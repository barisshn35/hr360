using System.Linq.Expressions;
using NotificationService.Models;

namespace NotificationService.Preferences;

/// <summary>Bir bildirim kategorisi: şablon kodu önekleriyle eşleşir.</summary>
/// <param name="Mandatory">Yasal/zorunlu: uygulama içi kanal kapatılamaz (e-posta/push kapatılabilir).</param>
/// <param name="Digestible">Acil değil: günlük özet açıksa tek tek e-posta yerine özete girer.</param>
/// <param name="Critical">Güvenlik açısından kritik: hiçbir kanal kapatılamaz, sessiz saat ve özet uygulanmaz.</param>
public sealed record CategoryDef(string Key, string Label, string LabelEn, string[] Prefixes, bool Mandatory, bool Digestible, bool Critical = false);

public sealed record ChannelPrefs(bool InApp, bool Email, bool Push, bool Chat)
{
    public static readonly ChannelPrefs AllOn = new(true, true, true, true);
}

public sealed record QuietHours(bool Enabled, TimeOnly Start, TimeOnly End, int DaysMask)
{
    public static readonly QuietHours Off = new(false, new(22, 0), new(8, 0), 127);
}

/// <summary>Bir kişinin gönderim anında geçerli tercihleri (kayıt yoksa varsayılanlar).</summary>
public sealed record EffectivePrefs(
    IReadOnlyDictionary<string, ChannelPrefs> Categories, QuietHours Quiet, bool DigestEnabled, int DigestHour)
{
    public static readonly EffectivePrefs Default = new(new Dictionary<string, ChannelPrefs>(), QuietHours.Off, false, 18);

    /// <summary>Kategori kanalları; zorunlu kategoride uygulama içi her zaman açık.</summary>
    public ChannelPrefs For(string category)
    {
        var def = NotificationCategories.Get(category);
        if (def.Critical) return ChannelPrefs.AllOn;
        var c = Categories.TryGetValue(category, out var v) ? v : ChannelPrefs.AllOn;
        return def.Mandatory ? c with { InApp = true } : c;
    }
}

public enum Delivery { Send, Suppress, Defer, Digest }

public static class NotificationCategories
{
    public const string System = "system";
    public const string Security = "security";
    public const string DigestCode = "digest.daily";

    /// <summary>
    /// GÜVENLİK AÇISINDAN KRİTİK bildirimler (belgelenmiş liste). Kişinin tercihlerinden bağımsızdır:
    /// kanal kapatma (opt-out), sessiz saat ve günlük özet UYGULANMAZ; e-posta ve anlık bildirim hemen gider,
    /// uygulama içi kayıt her zaman görünür.
    ///   privacy.breach*   — KVKK m.12/5 kişisel veri ihlali bildirimi (ilgili kişiye gecikmeksizin)
    ///   security.*        — hesap güvenliği uyarıları (şüpheli giriş, yetki değişikliği vb.)
    ///   account.*         — hesap kilitleme / açma, parola ve MFA değişikliği
    ///   auth.*            — kimlik doğrulama e-postaları (parola sıfırlama, SMTP deneme)
    ///   signature.otp     — e-imza tek kullanımlık doğrulama kodu (gecikirse süresi dolar)
    ///   *.otp             — diğer tek kullanımlık kodlar
    /// Ayrıca işlem e-postası employee.hired (hesap etkinleştirme/hoş geldin) e-postası da kapatılamaz/ertelenmez.
    /// </summary>
    public static readonly string[] SecurityCriticalPrefixes = ["privacy.breach", "security.", "account.", "auth.", "signature.otp"];

    public static readonly CategoryDef[] All =
    {
        new("approvals", "Onaylar", "Approvals", ["workflow.", "shift.swap.approval", "expense.", "travel."], false, false),
        new("leave", "İzin ve vardiya", "Leave and shifts", ["leave.", "shift.", "timeshift.", "attendance."], false, true),
        new("payroll", "Bordro ve ücret", "Payroll and compensation", ["payroll.", "compensation.", "benefit."], true, false),
        new("announcements", "Duyurular ve etkileşim", "Announcements and engagement", ["announcement.", "engagement.", "survey."], false, true),
        new("learning", "Eğitim ve oryantasyon", "Learning and onboarding", ["learning.", "onboarding."], false, true),
        new("recruitment", "İşe alım", "Recruitment", ["recruitment."], false, true),
        new("legal", "KVKK, disiplin ve yasal bildirimler", "Data protection, disciplinary and legal", ["privacy.", "kvkk.", "disciplinary.", "ethics.", "library."], true, false),
        new(Security, "Güvenlik ve hesap (kapatılamaz)", "Security and account (always on)", SecurityCriticalPrefixes, true, false, Critical: true),
        new(System, "Sistem ve hesap", "System and account", [], false, true),
    };

    static readonly Dictionary<string, CategoryDef> ByKey = All.ToDictionary(c => c.Key);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);
    public static CategoryDef Get(string key) => ByKey.TryGetValue(key, out var c) ? c : ByKey[System];

    /// <summary>En uzun eşleşen önekin kategorisi; eşleşme yoksa "system".</summary>
    public static string Categorize(string? templateCode)
    {
        if (string.IsNullOrEmpty(templateCode)) return System;
        string? best = null;
        var bestLen = -1;
        foreach (var c in All)
            foreach (var p in c.Prefixes)
                if (p.Length > bestLen && templateCode.StartsWith(p, StringComparison.Ordinal))
                {
                    best = c.Key;
                    bestLen = p.Length;
                }
        return best ?? System;
    }

    /// <summary>
    /// Hesap/işlem e-postaları (hoş geldin, parola, deneme) tercihlerden bağımsız hemen gider:
    /// kapatılamaz, ertelenmez, özete girmez.
    /// </summary>
    public static bool IsTransactional(string? templateCode) =>
        string.IsNullOrEmpty(templateCode) || templateCode == "employee.hired" || IsSecurityCritical(templateCode);

    /// <summary>Güvenlik açısından kritik mi (bkz. <see cref="SecurityCriticalPrefixes"/>): opt-out ve sessiz saat uygulanmaz.</summary>
    public static bool IsSecurityCritical(string? templateCode) =>
        !string.IsNullOrEmpty(templateCode)
        && (Get(Categorize(templateCode)).Critical || templateCode.EndsWith(".otp", StringComparison.Ordinal));

    /// <summary>
    /// SQL'e çevrilebilir koşul: bildirim verilen kategorilerden birine mi giriyor
    /// (Categorize ile aynı en-uzun-önek kuralı).
    /// </summary>
    public static Expression<Func<Notification, bool>> InCategories(IReadOnlyCollection<string> categories)
    {
        var n = Expression.Parameter(typeof(Notification), "n");
        return Expression.Lambda<Func<Notification, bool>>(InCategoriesBody(n, categories), n);
    }

    /// <summary>
    /// Kişinin uygulama içinde kapattığı kategorilerin bildirimlerini gizleyen koşul
    /// (diğer kanallar ve zorunlu kategoriler etkilenmez).
    /// </summary>
    public static Expression<Func<Notification, bool>> VisibleInApp(IReadOnlyCollection<string> mutedCategories)
    {
        var n = Expression.Parameter(typeof(Notification), "n");
        var muted = mutedCategories.Where(k => IsKnown(k) && !Get(k).Mandatory && !Get(k).Critical).ToList();
        var isInApp = Expression.Equal(Expression.Property(n, nameof(Notification.Channel)), Expression.Constant(NotificationChannel.InApp));
        // Tek kullanımlık kodlar (*.otp) hangi kategoride olursa olsun gizlenmez.
        var code = Expression.Property(n, nameof(Notification.TemplateCode));
        var endsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
        var isOtp = Expression.AndAlso(Expression.NotEqual(code, Expression.Constant(null, typeof(string))),
            Expression.Call(code, endsWith, Expression.Constant(".otp")));
        return Expression.Lambda<Func<Notification, bool>>(
            Expression.Not(Expression.AndAlso(Expression.AndAlso(isInApp, Expression.Not(isOtp)), InCategoriesBody(n, muted))), n);
    }

    static Expression InCategoriesBody(ParameterExpression n, IReadOnlyCollection<string> categories)
    {
        var code = Expression.Property(n, nameof(Notification.TemplateCode));
        var notNull = Expression.NotEqual(code, Expression.Constant(null, typeof(string)));
        var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        Expression Starts(string p) => Expression.AndAlso(notNull, Expression.Call(code, startsWith, Expression.Constant(p)));
        static Expression Or(IEnumerable<Expression> xs) => xs.Aggregate((Expression)Expression.Constant(false), Expression.OrElse);

        var allPrefixes = All.SelectMany(c => c.Prefixes.Select(p => (Cat: c.Key, P: p))).ToList();
        var parts = new List<Expression>();
        foreach (var key in categories.Distinct())
        {
            if (key == System)
            {
                // Hiçbir öneke uymayanlar (kod boş dahil).
                parts.Add(Expression.Not(Or(allPrefixes.Select(x => Starts(x.P)))));
                continue;
            }
            foreach (var p in Get(key).Prefixes)
            {
                // Başka kategorinin daha uzun (daha özgül) öneki bu öneki geçersiz kılar.
                var overrides = allPrefixes.Where(x => x.Cat != key && x.P.Length > p.Length && x.P.StartsWith(p, StringComparison.Ordinal)).ToList();
                Expression e = Starts(p);
                if (overrides.Count > 0) e = Expression.AndAlso(e, Expression.Not(Or(overrides.Select(x => Starts(x.P)))));
                parts.Add(e);
            }
        }
        return Or(parts);
    }
}

/// <summary>Sessiz saat ve özet zamanlaması (Europe/Istanbul). Saf mantık; birim testli.</summary>
public static class QuietHoursCalc
{
    public static readonly TimeZoneInfo Istanbul = ResolveIstanbul();

    static TimeZoneInfo ResolveIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (Exception)
        {
            // tzdata yoksa: Türkiye 2016'dan beri sabit UTC+3 (yaz saati yok).
            return TimeZoneInfo.CreateCustomTimeZone("Europe/Istanbul", TimeSpan.FromHours(3), "Europe/Istanbul", "TRT");
        }
    }

    public static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo? tz = null) =>
        TimeZoneInfo.ConvertTime(utc, tz ?? Istanbul).DateTime;

    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo? tz = null)
    {
        tz ??= Istanbul;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    public static bool DayOn(int mask, DayOfWeek d) => (mask & (1 << (int)d)) != 0;

    /// <summary>
    /// Yerel saat sessiz penceredeyse pencerenin bittiği yerel an; değilse null.
    /// Gece yarısını aşan pencere (22:00–08:00) başladığı günün ayarına tabidir.
    /// Başlangıç = bitiş ise pencere boştur.
    /// </summary>
    public static DateTime? QuietUntilLocal(DateTime local, TimeOnly start, TimeOnly end, int daysMask)
    {
        if (start == end) return null;
        var t = TimeOnly.FromDateTime(local);
        if (start < end)
            return t >= start && t < end && DayOn(daysMask, local.DayOfWeek) ? local.Date + end.ToTimeSpan() : null;
        if (t >= start && DayOn(daysMask, local.DayOfWeek)) return local.Date.AddDays(1) + end.ToTimeSpan();
        if (t < end && DayOn(daysMask, local.Date.AddDays(-1).DayOfWeek)) return local.Date + end.ToTimeSpan();
        return null;
    }

    /// <summary>Şu an sessiz saatteyse sessizliğin bittiği UTC an; değilse null.</summary>
    public static DateTimeOffset? QuietUntil(DateTimeOffset nowUtc, QuietHours q, TimeZoneInfo? tz = null)
    {
        if (!q.Enabled) return null;
        var until = QuietUntilLocal(ToLocal(nowUtc, tz), q.Start, q.End, q.DaysMask);
        return until is null ? null : ToUtc(until.Value, tz);
    }

    /// <summary>
    /// Günlük özet zamanı geldi mi: bugünün (saat henüz gelmediyse dünün) özet anı,
    /// son gönderimden sonraysa evet.
    /// </summary>
    public static bool DigestDue(DateTimeOffset nowUtc, int hour, DateTimeOffset? lastSentUtc, TimeZoneInfo? tz = null)
    {
        var local = ToLocal(nowUtc, tz);
        var slot = local.Date.AddHours(Math.Clamp(hour, 0, 23));
        if (local < slot) slot = slot.AddDays(-1);
        return lastSentUtc is null || lastSentUtc.Value < ToUtc(slot, tz);
    }
}

public static class DeliveryRules
{
    /// <summary>Bekleyen bir e-posta için karar: gönder, kapat, ertele ya da özete al.</summary>
    public static (Delivery Decision, DateTimeOffset? Until) DecideEmail(
        string? templateCode, bool hasAction, EffectivePrefs? prefs, DateTimeOffset nowUtc)
    {
        if (NotificationCategories.IsTransactional(templateCode) || prefs is null) return (Delivery.Send, null);
        if (templateCode != NotificationCategories.DigestCode)
        {
            var cat = NotificationCategories.Categorize(templateCode);
            if (!prefs.For(cat).Email) return (Delivery.Suppress, null);
            // Tek kullanımlık eylem bağlantısı olan e-posta özete girmez (bağlantı kaybolurdu).
            if (prefs.DigestEnabled && NotificationCategories.Get(cat).Digestible && !hasAction) return (Delivery.Digest, null);
        }
        var until = QuietHoursCalc.QuietUntil(nowUtc, prefs.Quiet);
        return until is null ? (Delivery.Send, null) : (Delivery.Defer, until);
    }

    /// <summary>Uygulama içi bildirimin anlık bildirim (Web Push) kararı. Uygulama içi kayıt etkilenmez.</summary>
    public static (Delivery Decision, DateTimeOffset? Until) DecidePush(string? templateCode, EffectivePrefs? prefs, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrEmpty(templateCode) || prefs is null || NotificationCategories.IsSecurityCritical(templateCode)) return (Delivery.Send, null);
        var ch = prefs.For(NotificationCategories.Categorize(templateCode));
        if (!ch.InApp || !ch.Push) return (Delivery.Suppress, null);
        var until = QuietHoursCalc.QuietUntil(nowUtc, prefs.Quiet);
        return until is null ? (Delivery.Send, null) : (Delivery.Defer, until);
    }
}

public sealed record DigestItem(string? TemplateCode, string? Subject, DateTimeOffset CreatedAt);

public static class DigestBuilder
{
    public const int MaxLines = 100;

    /// <summary>
    /// Günlük özet e-postası: yalnızca konu satırları, kategoriye göre gruplu (KVKK: gövde metinleri yok).
    /// </summary>
    public static (string Subject, string Body) Build(IReadOnlyCollection<DigestItem> items, string lang, TimeZoneInfo? tz = null)
    {
        var en = lang == "en";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(en
            ? "Summary of your notifications. Sign in to HR360 for details."
            : "Bildirimlerinizin özeti. Ayrıntılar için HR360'a giriş yapın.");
        var shown = 0;
        var order = NotificationCategories.All.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i);
        foreach (var g in items.GroupBy(i => NotificationCategories.Categorize(i.TemplateCode)).OrderBy(g => order[g.Key]))
        {
            if (shown >= MaxLines) break;
            var def = NotificationCategories.Get(g.Key);
            sb.AppendLine();
            sb.AppendLine($"{(en ? def.LabelEn : def.Label)} ({g.Count()})");
            foreach (var i in g.OrderBy(i => i.CreatedAt))
            {
                if (shown >= MaxLines) break;
                var subject = string.IsNullOrWhiteSpace(i.Subject) ? (en ? "Notification" : "Bildirim") : i.Subject.Trim();
                sb.AppendLine($"• {QuietHoursCalc.ToLocal(i.CreatedAt, tz):HH:mm} {subject}");
                shown++;
            }
        }
        if (items.Count > shown)
        {
            sb.AppendLine();
            sb.AppendLine(en ? $"…and {items.Count - shown} more." : $"…ve {items.Count - shown} bildirim daha.");
        }
        var title = en ? $"Daily notification summary ({items.Count})" : $"Günlük bildirim özeti ({items.Count})";
        return (title, sb.ToString().TrimEnd());
    }
}
