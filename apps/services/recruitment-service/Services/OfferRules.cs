using System.Globalization;
using System.Text.RegularExpressions;
using RecruitmentService.Models;

namespace RecruitmentService.Services;

/// <summary>Y18: teklif mektubu şablonu ve durum geçişleri (saf mantık, birim testli).</summary>
public static class OfferRules
{
    public static readonly string[] Placeholders =
    {
        "adayAdi", "pozisyon", "brutMaas", "baslangicTarihi", "yanHaklar", "sonGecerlilik", "sirketAdi", "bugun",
    };

    public const string DefaultTemplate = """
        {sirketAdi}
        {bugun}

        Sayın {adayAdi},

        Şirketimizdeki işe alım sürecinize gösterdiğiniz ilgi için teşekkür ederiz. Sizi {pozisyon} pozisyonunda
        ekibimize katılmaya davet etmekten memnuniyet duyarız.

        Teklifimizin ayrıntıları:
          • Pozisyon: {pozisyon}
          • Aylık brüt ücret: {brutMaas}
          • İşe başlama tarihi: {baslangicTarihi}
          • Yan haklar: {yanHaklar}

        Bu teklif {sonGecerlilik} tarihine kadar geçerlidir. Yanıtınızı bu tarihe kadar iletmenizi rica ederiz.
        İş sözleşmeniz işe başlama tarihinde düzenlenecektir.

        Saygılarımızla,
        {sirketAdi} İnsan Kaynakları
        """;

    private static readonly CultureInfo Tr = new("tr-TR");

    public static string Money(decimal amount, string currency) => currency.ToUpperInvariant() switch
    {
        "TRY" => amount.ToString("#,##0.00", Tr) + " TL",
        var c => amount.ToString("#,##0.00", Tr) + " " + c,
    };

    public static string Date(DateOnly d) => d.ToString("dd.MM.yyyy", Tr);

    /// <summary>{yer tutucu} değerlerini doldurur; bilinmeyen yer tutucular olduğu gibi kalır.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(template, @"\{([A-Za-z]+)\}", m =>
            values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static Dictionary<string, string> Values(string candidateName, string position, decimal gross, string currency,
        DateOnly start, string? benefits, DateOnly expires, string company, DateOnly today) => new()
    {
        ["adayAdi"] = candidateName,
        ["pozisyon"] = position,
        ["brutMaas"] = Money(gross, currency),
        ["baslangicTarihi"] = Date(start),
        ["yanHaklar"] = string.IsNullOrWhiteSpace(benefits) ? "Şirket politikası kapsamındaki standart yan haklar" : benefits.Trim(),
        ["sonGecerlilik"] = Date(expires),
        ["sirketAdi"] = company,
        ["bugun"] = Date(today),
    };

    /// <summary>Şablonu doğrular: boş olamaz, 20.000 karakteri aşamaz, bilinmeyen yer tutucu içeremez.</summary>
    public static string? ValidateTemplate(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Şablon boş olamaz";
        if (body.Length > 20_000) return "Şablon en fazla 20.000 karakter olabilir";
        var unknown = Regex.Matches(body, @"\{([A-Za-z]+)\}").Select(m => m.Groups[1].Value)
            .Where(k => !Placeholders.Contains(k)).Distinct().ToList();
        return unknown.Count > 0 ? $"Bilinmeyen yer tutucu: {string.Join(", ", unknown.Select(u => "{" + u + "}"))}" : null;
    }

    /// <summary>Durum geçişi geçerli mi? Hata metni ya da null.</summary>
    public static string? CheckTransition(OfferStatus from, OfferStatus to) => (from, to) switch
    {
        (OfferStatus.PendingApproval, OfferStatus.Approved or OfferStatus.Rejected) => null,
        (OfferStatus.Approved, OfferStatus.Sent) => null,
        (OfferStatus.Sent, OfferStatus.Accepted or OfferStatus.Declined or OfferStatus.Expired) => null,
        (OfferStatus.PendingApproval or OfferStatus.Approved or OfferStatus.Sent, OfferStatus.Withdrawn) => null,
        (_, OfferStatus.Sent) => "Yalnızca onaylanmış teklif adaya gönderilebilir",
        (_, OfferStatus.Accepted or OfferStatus.Declined) => "Yalnızca adaya gönderilmiş teklif yanıtlanabilir",
        (_, OfferStatus.Approved or OfferStatus.Rejected) => "Teklif onay beklemiyor",
        _ => "Bu durum geçişi yapılamaz",
    };

    public static string StatusLabel(OfferStatus s) => s switch
    {
        OfferStatus.PendingApproval => "Onay bekliyor",
        OfferStatus.Approved => "Onaylandı",
        OfferStatus.Rejected => "Reddedildi",
        OfferStatus.Sent => "Adaya gönderildi",
        OfferStatus.Accepted => "Kabul edildi",
        OfferStatus.Declined => "Aday reddetti",
        OfferStatus.Withdrawn => "Geri çekildi",
        OfferStatus.Expired => "Süresi doldu",
        _ => s.ToString(),
    };
}

/// <summary>G13: kanban aşama sırası ve geçiş kuralları (ApplicationsController ile ortak).</summary>
public static class PipelineRules
{
    /// <summary>Panodaki sıralı aşamalar; Rejected/Withdrawn sonlandırıcıdır (panoda ayrı sütun).</summary>
    public static readonly ApplicationStatus[] Stages =
    {
        ApplicationStatus.Applied, ApplicationStatus.Screening, ApplicationStatus.Interview,
        ApplicationStatus.Offer, ApplicationStatus.Hired, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn,
    };

    public static bool IsTerminal(ApplicationStatus s) =>
        s is ApplicationStatus.Hired or ApplicationStatus.Rejected or ApplicationStatus.Withdrawn;

    /// <summary>Geçiş kuralı; hata metni ya da null. postingClosed: ilan kapalı mı.</summary>
    public static string? CheckMove(ApplicationStatus from, ApplicationStatus to, bool postingClosed)
    {
        if (IsTerminal(from)) return "Sonuclanmis basvurunun durumu degistirilemez";
        if (from == to) return "Başvuru zaten bu aşamada";
        if (to == ApplicationStatus.Hired)
        {
            if (from != ApplicationStatus.Offer) return "İşe alım yalnızca teklif aşamasındaki başvurudan yapılabilir";
            if (postingClosed) return "Kapalı ilana işe alım yapılamaz";
        }
        return null;
    }
}

/// <summary>Y16 / KVKK: aday verisinin imha zamanı (saf mantık, birim testli).</summary>
public static class RetentionRules
{
    public sealed record AppSnapshot(ApplicationStatus Status, DateTimeOffset AppliedAt, DateTimeOffset? StatusChangedAt, DateTimeOffset? PostingClosedAt);

    /// <summary>
    /// Adayın verisinin imha (anonimleştirme) zamanı; süreç devam ediyorsa ya da işe alındıysa null.
    ///  * Tüm başvurular sonuçlanmış (Reddedildi/Geri çekildi) ya da ilanı kapanmış olmalı.
    ///  * Açık rıza YOKSA: son kapanıştan <paramref name="retentionDays"/> gün sonra.
    ///  * Açık rıza VARSA (aday havuzu): rıza tarihi ile son kapanıştan geç olanın
    ///    <paramref name="poolMonths"/> ay sonrası.
    ///  * Başvurusu olmayan (İK'nın elle eklediği) aday: kayıt tarihi kapanış sayılır.
    /// </summary>
    public static DateTimeOffset? DueAt(DateTimeOffset createdAt, bool consent, DateTimeOffset? consentAt,
        IReadOnlyList<AppSnapshot> apps, int retentionDays, int poolMonths)
    {
        // Başvurusu yoksa kayıt tarihi; varsa en son kapanış.
        DateTimeOffset closedAt = apps.Count == 0 ? createdAt : DateTimeOffset.MinValue;
        foreach (var a in apps)
        {
            if (a.Status == ApplicationStatus.Hired) return null;
            DateTimeOffset? closed = a.Status is ApplicationStatus.Rejected or ApplicationStatus.Withdrawn
                ? a.StatusChangedAt ?? a.AppliedAt
                : a.PostingClosedAt;
            if (closed is null) return null; // süreç devam ediyor
            if (closed.Value > closedAt) closedAt = closed.Value;
        }
        if (!consent) return closedAt.AddDays(retentionDays);
        var from = consentAt is { } c && c > closedAt ? c : closedAt;
        return from.AddMonths(poolMonths);
    }
}
