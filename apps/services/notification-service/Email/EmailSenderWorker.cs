using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using NotificationService.Data;
using NotificationService.Models;
using NotificationService.Services;
using NotificationService.Tenancy;

namespace NotificationService.Email;

/// <summary>
/// Pending durumundaki Email kanalli bildirimleri periyodik olarak tarar
/// ve gonderir. Bu, tum tenant'lar icin calisan sistem geneli bir arka
/// plan isi oldugundan, kendi DbContext scope'unda
/// TenantContext.IsPlatformAdmin=true ayarlanir - boylece tenant filtresi
/// devre disi kalir ve hangi tenant'a ait olursa olsun butun bekleyen
/// bildirimler gorunur (bkz. TenantDbContextExtensions.ApplyTenantFilters).
///
/// Her bildirim, KENDI kiracisinin ozel SMTP sunucusu ayarlanmissa
/// (Ayarlar > Marka) o sunucu uzerinden; yoksa
/// platformun varsayilan SMTP'si uzerinden gonderilir. Bir turdaki
/// bildirimler, gereksiz yere baglanti acip kapatmamak icin hedef SMTP'ye
/// gore gruplanir (ayni kiracinin ardisik bildirimleri TEK baglantidan
/// gider).
///
/// Basarisiz gonderimler 3 denemeye kadar Pending'de birakilir (bir
/// sonraki turda tekrar denenir); 3. denemeden sonra Failed'e duser.
/// </summary>
public class EmailSenderWorker : BackgroundService
{
    /// <summary>Ayrılmış ".invalid" üst alanındaki adresler (RFC 2606) asla gönderilmez.</summary>
    public static bool IsUndeliverable(string? email) =>
        email is not null && email.Trim().TrimEnd('.').EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);

    private const int BatchSize = 20;
    private const int MaxAttempts = 3;
    // EMAIL_POLL_SECONDS: testte kısaltılır (varsayılan 30 sn).
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("EMAIL_POLL_SECONDS"), out var s) ? Math.Max(1, s) : 30);

    private readonly IServiceProvider _services;
    private readonly ILogger<EmailSenderWorker> _logger;
    private readonly EmailOptions _options;

    public EmailSenderWorker(IServiceProvider services, ILogger<EmailSenderWorker> logger, EmailOptions options)
    {
        _services = services;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "EmailSenderWorker basladi. Varsayilan SMTP: {Host}:{Port}, gonderen: {From}",
            _options.SmtpHost, _options.SmtpPort, _options.FromAddress);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EmailSenderWorker turu basarisiz oldu, bir sonraki turda tekrar denenecek");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>Bir bildirimin hangi SMTP sunucusundan, hangi marka bilgisiyle gonderilecegini tasir.</summary>
    private record SendPlan(
        string Host, int Port, string? User, string? Password,
        string FromAddress, string FromName,
        string? LogoUrl, string? CompanyName, bool IsTenantSmtp = false);

    /// <summary>
    /// GUVENLIK (SSRF / DNS rebinding): Kiracinin ozel SMTP adresi kayit aninda
    /// dogrulaniyor, ama ad cozumlemesi baglanti aninda tekrar yapiliyor - kayitta genel
    /// IP'ye, gonderimde ic aga (postgres, keycloak...) cozulen bir alan adi kontrolu
    /// atlatabilirdi; bu duzeltmeden once kaydedilmis ayarlar da hic kontrol edilmemisti.
    /// Baglanmadan hemen once cozulen adreslerin hicbiri ic ag olmamali.
    /// </summary>
    private static async Task<System.Net.IPAddress?> ResolvePublicAddressAsync(string host, CancellationToken ct)
    {
        System.Net.IPAddress[] addresses;
        try { addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception) { return null; }
        if (addresses.Length == 0) return null;
        foreach (var ip in addresses)
        {
            var a = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            if (System.Net.IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal)
                return null;
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                if (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127))
                    return null;
            }
        }
        return addresses[0];
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();

        // Sistem geneli is: tum tenant'lari gormek icin filtreyi devre disi birak.
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.IsPlatformAdmin = true;

        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var branding = scope.ServiceProvider.GetRequiredService<TenantBrandingClient>();

        var now = DateTimeOffset.UtcNow;
        var pending = await db.Notifications
            .Where(n => n.Channel == NotificationChannel.Email && n.Status == NotificationStatus.Pending
                        && (n.DeferredUntil == null || n.DeferredUntil <= now))
            // Az denenmis olanlar once: bir kiracinin bozuk SMTP'sine takilan eski
            // kayitlar, saglikli kiracilarin yeni e-postalarini bekletmesin.
            .OrderBy(n => n.AttemptCount).ThenBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        // G11 bildirim tercihleri: kapatılan kategori (Suppressed), günlük özete alınan (DigestQueued)
        // ve sessiz saatte ertelenen (DeferredUntil) e-postalar bu turda gönderilmez.
        pending = await ApplyPreferencesAsync(db, pending, now, ct);
        if (pending.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogInformation("{Count} bekleyen e-posta bulundu, gonderiliyor", pending.Count);

        // Her bildirim icin gonderim plani (SMTP hedefi + marka) onceden
        // cikarilir - hem gruplamak hem de baglanti asamasinda ayri bir
        // cross-service cagriya ihtiyac duymamak icin.
        var plans = new Dictionary<Guid, SendPlan>();
        // Platform SMTP'si: arayüzden girildiyse o (Sistem ayarları), değilse .env (EmailOptions).
        var platformSmtp = await branding.GetPlatformSmtpAsync(ct);
        foreach (var n in pending)
        {
            var tenantBranding = await branding.GetBrandingAsync(n.TenantSlug, ct);
            var tenantSmtp = await branding.GetSmtpConfigAsync(n.TenantSlug, ct);

            plans[n.Id] = tenantSmtp is not null
                ? new SendPlan(
                    tenantSmtp.Host, tenantSmtp.Port, tenantSmtp.User, tenantSmtp.Password,
                    FromAddress: string.IsNullOrWhiteSpace(tenantSmtp.FromAddress) ? _options.FromAddress : tenantSmtp.FromAddress,
                    FromName: string.IsNullOrWhiteSpace(tenantSmtp.FromName) ? _options.FromName : tenantSmtp.FromName,
                    LogoUrl: tenantBranding?.LogoUrl, CompanyName: tenantBranding?.Name,
                    IsTenantSmtp: true)
                : platformSmtp is not null
                ? new SendPlan(
                    platformSmtp.Host, platformSmtp.Port, platformSmtp.User, platformSmtp.Password,
                    FromAddress: string.IsNullOrWhiteSpace(platformSmtp.FromAddress) ? _options.FromAddress : platformSmtp.FromAddress,
                    FromName: string.IsNullOrWhiteSpace(platformSmtp.FromName) ? _options.FromName : platformSmtp.FromName,
                    LogoUrl: tenantBranding?.LogoUrl, CompanyName: tenantBranding?.Name)
                : new SendPlan(
                    _options.SmtpHost, _options.SmtpPort, _options.SmtpUser, _options.SmtpPassword,
                    FromAddress: _options.FromAddress, FromName: _options.FromName,
                    LogoUrl: tenantBranding?.LogoUrl, CompanyName: tenantBranding?.Name);
        }

        // Ayni SMTP hedefine (host+port+user) sahip bildirimleri grupla -
        // tek baglanti/kimlik dogrulama ile hepsi gonderilsin. Sozluk
        // sirasi .NET'te "ekleme sirasi" olma egiliminde olsa da garanti
        // degildir; burada onemli degil, her grup bagimsiz islenir.
        // GUVENLIK: Gruplama onceden yalnizca (Host, Port, User) idi ve grup, ILK
        // bildirimin parolasiyla dogrulaniyordu - iki kiraci ayni sunucu/kullanici adini
        // girerse, biri digerinin kimligi dogrulanmis oturumundan (kendi sectigi
        // gonderen adresiyle) e-posta gonderebiliyordu. Grup artik kiraci bazli.
        var groups = pending.GroupBy(n => (n.TenantSlug, plans[n.Id].Host, plans[n.Id].Port, plans[n.Id].User));

        foreach (var group in groups)
        {
            await SendGroupAsync(group.ToList(), plans, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<List<Notification>> ApplyPreferencesAsync(
        NotificationDbContext db, List<Notification> pending, DateTimeOffset now, CancellationToken ct)
    {
        var prefs = await Preferences.PreferenceStore.LoadAsync(db, pending.Select(n => (n.TenantSlug, n.RecipientEmployeeId)), ct);
        var send = new List<Notification>();
        foreach (var n in pending)
        {
            // ".invalid" alanı hiçbir zaman teslim edilemez (RFC 2606); örnek veri kişileri bu alandadır.
            if (IsUndeliverable(n.RecipientEmail))
            {
                n.Status = NotificationStatus.Suppressed;
                n.FailureReason = "Teslim edilemez adres (.invalid; örnek veri)";
                continue;
            }
            prefs.TryGetValue((n.TenantSlug, n.RecipientEmployeeId), out var p);
            var (decision, until) = Preferences.DeliveryRules.DecideEmail(n.TemplateCode, !string.IsNullOrEmpty(n.ActionUrl), p, now);
            switch (decision)
            {
                case Preferences.Delivery.Suppress:
                    n.Status = NotificationStatus.Suppressed;
                    n.FailureReason = "Kişi bu kategori için e-postayı kapatmış";
                    break;
                case Preferences.Delivery.Digest:
                    n.Status = NotificationStatus.DigestQueued;
                    break;
                case Preferences.Delivery.Defer:
                    n.DeferredUntil = until;
                    break;
                default:
                    send.Add(n);
                    break;
            }
        }
        return send;
    }

    private async Task SendGroupAsync(
        List<Notification> group, Dictionary<Guid, SendPlan> plans, CancellationToken ct)
    {
        var target = plans[group[0].Id];

        using var smtp = new SmtpClient();

        try
        {
            System.Net.IPAddress? vetted = null;
            if (target.IsTenantSmtp)
            {
                vetted = await ResolvePublicAddressAsync(target.Host, ct)
                    ?? throw new InvalidOperationException("Kiracı SMTP sunucusu iç ağ adresine çözümleniyor; bağlantı reddedildi");
            }

            // Auto: sunucu STARTTLS destekliyorsa kullanir, desteklemiyorsa
            // (orn. varsayilan kurulumdaki Mailpit - sertifika tanimlanmadan
            // STARTTLS sunmaz) duz baglantiya duser. Sabit StartTls burada
            // KULLANILMAZ - gercek bir SMTP saglayicisi (Brevo vb.) ile
            // calisirken sorun cikarmaz, ama Mailpit'e karsi baglantiyi
            // TAMAMEN reddederdi.
            if (vetted is not null)
            {
                // Kontrol edilen IP'ye baglanilir; ad TLS dogrulamasi icin ayrica verilir.
                // Aksi halde ad baglanti aninda yeniden cozulur ve kontrol ile baglanti
                // arasinda ic aga donen bir DNS yaniti (rebinding) kontrolu atlatabilirdi.
                var socket = new System.Net.Sockets.Socket(vetted.AddressFamily,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new System.Net.IPEndPoint(vetted, target.Port), ct);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                await smtp.ConnectAsync(socket, target.Host, target.Port, SecureSocketOptions.Auto, ct);
            }
            else
            {
                await smtp.ConnectAsync(target.Host, target.Port, SecureSocketOptions.Auto, ct);
            }

            // Sunucu kimlik dogrulama desteklemiyorsa (yine Mailpit'in
            // varsayilan hali) AuthenticateAsync cagirmak istisna firlatir -
            // bu yuzden sadece sunucu gercekten destekliyorsa deneriz. Parola
            // da null olabilir (User doluyken Password bos gelirse) - ikisi
            // de doluysa deneriz, aksi halde kimlik dogrulamasiz devam ederiz.
            if (smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication)
                && !string.IsNullOrEmpty(target.User)
                && !string.IsNullOrEmpty(target.Password))
            {
                await smtp.AuthenticateAsync(target.User, target.Password, ct);
            }
        }
        catch (Exception ex)
        {
            // Baglanti/kimlik dogrulama hatasi bu grubu etkiler - hicbir
            // notification'i Failed olarak isaretlemeden atla, bir sonraki
            // turda (30sn sonra) tekrar denenir. DB'ye hic yazilmadigi icin
            // AttemptCount de artmaz - yanlislikla Failed'e dusmezler.
            // NOT: Onceden baglanti hatasi deneme sayilmiyordu - erisilemeyen ozel SMTP
            // sunucusu tanimlamis tek bir kiracinin 20+ bekleyen e-postasi her turda
            // ilk 20'yi doldurup TUM platformun e-postalarini suresiz durduruyordu.
            // Artik baglanti hatasi da deneme sayilir; MaxAttempts sonrasi Failed.
            _logger.LogError(ex,
                "SMTP baglantisi/kimlik dogrulamasi basarisiz ({Host}:{Port}), {Count} e-posta icin deneme sayildi",
                target.Host, target.Port, group.Count);
            // Yalnizca KIRACININ ozel SMTP'sinde deneme sayilir. Platformun varsayilan
            // sunucusundaki kisa bir kesinti (orn. dagitim sirasinda Mailpit yeniden
            // baslarken) tum bekleyen davet/parola e-postalarini 90 sn icinde kalici
            // olarak Failed yapmasin - onlar bir sonraki turda yeniden denenir.
            if (target.IsTenantSmtp)
            {
                foreach (var n in group)
                {
                    n.AttemptCount++;
                    n.FailureReason = "SMTP sunucusuna bağlanılamadı";
                    if (n.AttemptCount >= MaxAttempts) n.Status = NotificationStatus.Failed;
                }
            }
            if (smtp.IsConnected)
                await smtp.DisconnectAsync(true, ct);
            return;
        }

        foreach (var notification in group)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(notification.RecipientEmail))
                {
                    notification.Status = NotificationStatus.Failed;
                    notification.FailureReason = "Alici e-posta adresi yok";
                    continue;
                }

                var message = BuildMessage(notification, plans[notification.Id]);
                await smtp.SendAsync(message, ct);

                notification.Status = NotificationStatus.Sent;
                notification.SentAt = DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "E-posta gonderildi: {NotificationId} -> {Email}",
                    notification.Id, notification.RecipientEmail);
            }
            catch (Exception ex)
            {
                notification.AttemptCount++;
                notification.FailureReason = ex.Message;

                if (notification.AttemptCount >= MaxAttempts)
                {
                    notification.Status = NotificationStatus.Failed;
                    _logger.LogError(ex,
                        "E-posta gonderimi {MaxAttempts} denemede de basarisiz, Failed olarak isaretlendi: {NotificationId}",
                        MaxAttempts, notification.Id);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "E-posta gonderimi basarisiz (deneme {Attempt}/{MaxAttempts}), tekrar denenecek: {NotificationId}",
                        notification.AttemptCount, MaxAttempts, notification.Id);
                }

                // Baglanti koptuysa (tek bir gonderim hatasindan farkli olarak)
                // bu grubun geri kalanini da atla - hepsi ayni sebeple basarisiz olur.
                if (!smtp.IsConnected)
                {
                    _logger.LogError("SMTP baglantisi koptu, bu grubun geri kalani atlaniyor");
                    break;
                }
            }
        }

        if (smtp.IsConnected)
            await smtp.DisconnectAsync(true, ct);
    }

    private static MimeMessage BuildMessage(Notification notification, SendPlan plan)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(plan.FromName, plan.FromAddress));
        message.To.Add(MailboxAddress.Parse(notification.RecipientEmail!));
        message.Subject = notification.Subject ?? (notification.Language == "en" ? "HR360 Enterprise notification" : "HR360 Enterprise Bildirimi");

        var safeUrl = notification.ActionUrl is { } au && Uri.TryCreate(au, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? au : null;
        // Düğme açıklaması yalnızca e-postada (uygulama içi bildirimde düğme yok).
        var hint = safeUrl is null ? null : Messaging.NotificationTexts.EmailActionHint(notification.TemplateCode, notification.Language);
        var body = hint is null ? notification.Body : $"{notification.Body}\n\n{hint}";
        var html = EmailTemplateRenderer.Render(
            subject: message.Subject,
            bodyPlainText: body,
            actionUrl: safeUrl is null ? null : System.Net.WebUtility.HtmlEncode(safeUrl),
            actionLabel: notification.ActionLabel,
            logoUrl: plan.LogoUrl,
            companyName: plan.CompanyName,
            lang: notification.Language);

        message.Body = new BodyBuilder
        {
            HtmlBody = html,
            TextBody = safeUrl is null ? body : $"{body}\n{safeUrl}",
        }.ToMessageBody();

        return message;
    }
}
