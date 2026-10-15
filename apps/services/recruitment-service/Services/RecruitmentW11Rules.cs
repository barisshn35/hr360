using System.Text.Json.Nodes;
using RecruitmentService.Models;

namespace RecruitmentService.Services;

/// <summary>
/// Dalga 11 (73): çalışan önerisi kuralları. Öneren yalnızca KABA durumu görür
/// (değerlendiriliyor / işe alındı / kapandı): aşama, mülakat, ret gerekçesi gösterilmez.
/// Ödül: işe girişten sonra deneme süresi dolunca "hak edildi" olur; onay/ödeme İK kararıdır.
/// </summary>
public static class ReferralRules
{
    public static (string Code, string Label) CoarseStatus(ApplicationStatus? status, bool postingClosed) => status switch
    {
        null => ("Closed", "Kapandı"),
        ApplicationStatus.Hired => ("Hired", "İşe alındı"),
        ApplicationStatus.Rejected or ApplicationStatus.Withdrawn => ("Closed", "Kapandı"),
        _ when postingClosed => ("Closed", "Kapandı"),
        _ => ("InReview", "Değerlendiriliyor"),
    };

    public static string RewardLabel(ReferralRewardStatus s) => s switch
    {
        ReferralRewardStatus.None => "Yok",
        ReferralRewardStatus.Waiting => "Deneme süresi bekleniyor",
        ReferralRewardStatus.Eligible => "Hak edildi (İK onayı bekleniyor)",
        ReferralRewardStatus.Approved => "Onaylandı",
        ReferralRewardStatus.Paid => "Ödendi",
        ReferralRewardStatus.Forfeited => "Düştü",
        ReferralRewardStatus.NotEligible => "Uygun değil",
        _ => s.ToString(),
    };

    /// <summary>
    /// Otomatik ödül adımı (yalnızca None → Waiting → Eligible ve geri düşme). İK kararları
    /// (Approved/Paid/Forfeited/NotEligible) asla otomatik değiştirilmez.
    /// <paramref name="stillEmployed"/>: null = bilinmiyor (çalışan kaydı eşleşmedi) → hak ediş engellenmez,
    /// İK onayında kontrol edilir; false = ayrılmış → Forfeited önerilmez, yalnızca Eligible yapılmaz ve "Waiting"te kalır.
    /// </summary>
    public static (ReferralRewardStatus Status, DateTimeOffset? HiredAt, DateTimeOffset? EligibleAt) Step(
        ReferralRewardStatus current, ApplicationStatus? appStatus, DateTimeOffset? hiredAt, int probationDays, DateTimeOffset now, bool? stillEmployed)
    {
        if (current is ReferralRewardStatus.Approved or ReferralRewardStatus.Paid or ReferralRewardStatus.Forfeited or ReferralRewardStatus.NotEligible)
            return (current, hiredAt, hiredAt?.AddDays(probationDays));
        if (appStatus != ApplicationStatus.Hired || hiredAt is null)
            return (ReferralRewardStatus.None, null, null);
        var eligibleAt = hiredAt.Value.AddDays(Math.Clamp(probationDays, 0, 365));
        if (now < eligibleAt || stillEmployed == false) return (ReferralRewardStatus.Waiting, hiredAt, eligibleAt);
        return (ReferralRewardStatus.Eligible, hiredAt, eligibleAt);
    }

    /// <summary>
    /// Deneme süresinin başladığı an: işe başlama tarihi. Öncelik çalışan kaydının işe giriş tarihi, yoksa kabul
    /// edilen teklifin başlama tarihi; ikisi de yoksa "İşe alındı" aşamasına geçiş anı (eski davranış). Teklif
    /// kabulü ile işe başlama arasında haftalar olabileceğinden deneme süresi kabul anından SAYILMAZ.
    /// Tarihler Türkiye saatiyle gün başı (UTC+3) alınır.
    /// </summary>
    public static DateTimeOffset? ProbationStart(DateOnly? employeeHireDate, DateOnly? offerStartDate, DateTimeOffset? hiredAt)
    {
        var d = employeeHireDate ?? offerStartDate;
        if (d is { } day) return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        return hiredAt;
    }

    /// <summary>İK ödül işlemi: approve | pay | forfeit | not-eligible. Hata metni ya da hedef durum.</summary>
    public static (ReferralRewardStatus? Next, string? Error) Decide(ReferralRewardStatus current, string? action) => (action ?? "").ToLowerInvariant() switch
    {
        "approve" when current == ReferralRewardStatus.Eligible => (ReferralRewardStatus.Approved, null),
        "approve" => (null, "Yalnızca hak edilmiş ödül onaylanabilir"),
        "pay" when current == ReferralRewardStatus.Approved => (ReferralRewardStatus.Paid, null),
        "pay" => (null, "Yalnızca onaylanmış ödül ödendi olarak işaretlenebilir"),
        "forfeit" when current is ReferralRewardStatus.Waiting or ReferralRewardStatus.Eligible or ReferralRewardStatus.Approved => (ReferralRewardStatus.Forfeited, null),
        "forfeit" => (null, "Bu durumdaki ödül düşürülemez"),
        "not-eligible" when current is not ReferralRewardStatus.Paid => (ReferralRewardStatus.NotEligible, null),
        "not-eligible" => (null, "Ödenmiş ödül değiştirilemez"),
        _ => (null, "İşlem geçersiz"),
    };
}

/// <summary>Dalga 11 (74): aday durum bağlantısında gösterilen kaba aşama ve sonraki adım.</summary>
public static class StatusLinkRules
{
    public const int DefaultDays = 90, MaxDays = 180;

    public static (string Code, string Label, string NextStep) View(ApplicationStatus status, bool postingClosed, DateTimeOffset? nextInterviewAt, OfferStatus? offer)
    {
        if (status is ApplicationStatus.Hired) return ("Positive", "Olumlu sonuçlandı", "İşe giriş süreciniz için İnsan Kaynakları sizinle iletişime geçecek.");
        if (status is ApplicationStatus.Rejected) return ("Negative", "Olumsuz sonuçlandı", "Süreç tamamlandı. İlginiz için teşekkür ederiz.");
        if (status is ApplicationStatus.Withdrawn) return ("Withdrawn", "Geri çekildi", "Süreç tamamlandı.");
        if (postingClosed && status is not ApplicationStatus.Offer) return ("Closed", "İlan kapandı", "Bu ilan için süreç sona erdi.");
        if (status is ApplicationStatus.Offer || offer is OfferStatus.Sent)
            return ("Offer", "Teklif aşamasında", offer is OfferStatus.Sent
                ? "Size iletilen teklifi inceleyip yanıtlamanız bekleniyor."
                : "Teklifiniz hazırlanıyor; İnsan Kaynakları sizinle iletişime geçecek.");
        if (nextInterviewAt is not null) return ("InReview", "Değerlendiriliyor", "Planlanmış bir mülakatınız var; ayrıntılar e-posta ile iletildi.");
        if (status is ApplicationStatus.Interview) return ("InReview", "Değerlendiriliyor", "Mülakat değerlendirmesi sürüyor; sonuç size bildirilecek.");
        if (status is ApplicationStatus.Screening) return ("InReview", "Değerlendiriliyor", "Başvurunuz ön değerlendirmede; uygun görülürse mülakat için sizinle iletişime geçilecek.");
        return ("Received", "Başvurunuz alındı", "Başvurunuz sıraya alındı; değerlendirme başladığında burada görünecek.");
    }

    /// <summary>Bağlantı geçerli mi (iptal edilmemiş ve süresi dolmamış)?</summary>
    public static bool IsActive(DateTimeOffset? revokedAt, DateTimeOffset expiresAt, DateTimeOffset now) => revokedAt is null && now < expiresAt;
}

/// <summary>
/// Dalga 11 (77): değerlendiriciler arası tutarlılık. Her ölçüt için puan sayısı, ortalama,
/// standart sapma (popülasyon) ve açıklık (en yüksek − en düşük). 1-5 ölçeğinde açıklık ≥ 3 ya da
/// std ≥ 1,2 "yüksek görüş ayrılığı" sayılır; öneriler arasında hem olumlu hem olumsuz varsa da işaretlenir.
/// </summary>
public static class ScorecardConsistency
{
    public const decimal SpreadThreshold = 3m, StdThreshold = 1.2m;

    public sealed record CriterionStat(string Key, string Label, int Count, decimal? Mean, decimal? Std, int? Spread, bool HighDisagreement);
    public sealed record Result(List<CriterionStat> Criteria, bool RecommendationSplit, bool HighDisagreement, int Raters);

    public static Result Compute(IReadOnlyList<ScorecardCriterion> criteria, IReadOnlyList<(IReadOnlyList<CriterionScore> Scores, string? Recommendation)> cards)
    {
        var stats = new List<CriterionStat>();
        foreach (var c in criteria)
        {
            var xs = cards.SelectMany(k => k.Scores).Where(s => string.Equals(s.Key, c.Key, StringComparison.OrdinalIgnoreCase)).Select(s => (decimal)s.Score).ToList();
            if (xs.Count == 0) { stats.Add(new(c.Key, c.Label, 0, null, null, null, false)); continue; }
            var mean = xs.Average();
            var std = (decimal)Math.Sqrt((double)xs.Select(x => (x - mean) * (x - mean)).Average());
            var spread = (int)(xs.Max() - xs.Min());
            var high = xs.Count >= 2 && (spread >= SpreadThreshold || std >= StdThreshold);
            stats.Add(new(c.Key, c.Label, xs.Count, Math.Round(mean, 2), Math.Round(std, 2), spread, high));
        }
        var recs = cards.Select(k => k.Recommendation).Where(r => !string.IsNullOrEmpty(r)).ToList();
        var split = recs.Any(r => r is "Yes" or "StrongYes") && recs.Any(r => r is "No" or "StrongNo");
        return new(stats, split, split || stats.Any(s => s.HighDisagreement), cards.Count);
    }
}

/// <summary>Dalga 11 (75): Google for Jobs (schema.org JobPosting) yapılandırılmış verisi.</summary>
public static class JobPostingJsonLd
{
    public static string EmploymentType(EmploymentType t) => t switch
    {
        Models.EmploymentType.FullTime => "FULL_TIME",
        Models.EmploymentType.PartTime => "PART_TIME",
        Models.EmploymentType.Contract => "CONTRACTOR",
        Models.EmploymentType.Intern => "INTERN",
        _ => "OTHER",
    };

    private static readonly string[] Periods = { "HOUR", "DAY", "WEEK", "MONTH", "YEAR" };

    /// <summary>Ücret yalnızca <paramref name="publishSalary"/> açıkken ve ilan aralığı tanımlıyken eklenir.</summary>
    public static JsonObject Build(JobPosting p, string company, string? companyUrl, bool publishSalary, string? pageUrl)
    {
        var o = new JsonObject
        {
            ["@context"] = "https://schema.org/",
            ["@type"] = "JobPosting",
            ["title"] = p.Title,
            ["description"] = Html(p.Description ?? p.Title),
            ["datePosted"] = (p.PublishedAt ?? p.CreatedAt).ToString("yyyy-MM-dd"),
            ["employmentType"] = EmploymentType(p.EmploymentType),
            ["identifier"] = new JsonObject { ["@type"] = "PropertyValue", ["name"] = company, ["value"] = p.Id.ToString() },
            ["hiringOrganization"] = companyUrl is { Length: > 0 }
                ? new JsonObject { ["@type"] = "Organization", ["name"] = company, ["sameAs"] = companyUrl }
                : new JsonObject { ["@type"] = "Organization", ["name"] = company },
        };
        if (p.ValidThrough is { } vt) o["validThrough"] = vt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (pageUrl is { Length: > 0 }) o["url"] = pageUrl;
        var country = string.IsNullOrWhiteSpace(p.Country) ? "TR" : p.Country.Trim().ToUpperInvariant();
        var address = new JsonObject { ["@type"] = "PostalAddress", ["addressCountry"] = country };
        if (!string.IsNullOrWhiteSpace(p.Location)) address["addressLocality"] = p.Location.Trim();
        if (!string.IsNullOrWhiteSpace(p.Region)) address["addressRegion"] = p.Region.Trim();
        o["jobLocation"] = new JsonObject { ["@type"] = "Place", ["address"] = address };
        if (p.RemoteAllowed)
        {
            o["jobLocationType"] = "TELECOMMUTE";
            o["applicantLocationRequirements"] = new JsonObject { ["@type"] = "Country", ["name"] = country };
        }
        if (publishSalary && (p.SalaryMin is > 0 || p.SalaryMax is > 0))
        {
            var value = new JsonObject { ["@type"] = "QuantitativeValue", ["unitText"] = Periods.Contains(p.SalaryPeriod) ? p.SalaryPeriod : "MONTH" };
            if (p.SalaryMin is > 0 && p.SalaryMax is > 0 && p.SalaryMax != p.SalaryMin)
            {
                value["minValue"] = p.SalaryMin.Value;
                value["maxValue"] = p.SalaryMax.Value;
            }
            else value["value"] = (p.SalaryMin is > 0 ? p.SalaryMin : p.SalaryMax)!.Value;
            o["baseSalary"] = new JsonObject { ["@type"] = "MonetaryAmount", ["currency"] = string.IsNullOrWhiteSpace(p.SalaryCurrency) ? "TRY" : p.SalaryCurrency, ["value"] = value };
        }
        return o;
    }

    /// <summary>Düz metin açıklamayı Google'ın beklediği basit HTML'e çevirir (kaçışlı paragraflar).</summary>
    public static string Html(string text) =>
        string.Join("", text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + System.Net.WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>") + "</p>"));

    /// <summary>İlan ücret alanlarının doğrulaması (İK girişi).</summary>
    public static string? Validate(decimal? min, decimal? max, string? currency, string? period, string? country)
    {
        if (min is < 0 or > 100_000_000 || max is < 0 or > 100_000_000) return "Ücret aralığı geçersiz";
        if (min is { } a && max is { } b && b < a) return "Üst sınır alt sınırdan küçük olamaz";
        if (currency is { Length: > 0 } c && !new[] { "TRY", "USD", "EUR", "GBP" }.Contains(c.ToUpperInvariant())) return "Para birimi TRY, USD, EUR ya da GBP olmalı";
        if (period is { Length: > 0 } p && !Periods.Contains(p.ToUpperInvariant())) return "Ücret dönemi HOUR, DAY, WEEK, MONTH ya da YEAR olmalı";
        if (country is { Length: > 0 } k && (k.Trim().Length != 2 || !k.Trim().All(char.IsLetter))) return "Ülke kodu iki harfli olmalı (ör. TR)";
        return null;
    }
}

/// <summary>
/// Dalga 11 (78): işe alım hunisi analizi. KVKK: kişi düzeyinde çıkarım yapılabilecek küçük gruplar
/// (n &lt; 5) gizlenir — işe alım süresi, aşamada geçen süre, kaynak dönüşümü ve teklif kabul oranı için.
/// </summary>
public static class FunnelAnalytics
{
    public const int MinGroup = 5;

    public static readonly ApplicationStatus[] Funnel =
        { ApplicationStatus.Applied, ApplicationStatus.Screening, ApplicationStatus.Interview, ApplicationStatus.Offer, ApplicationStatus.Hired };

    public sealed record App(Guid Id, DateTimeOffset AppliedAt, ApplicationStatus Status, string Source, IReadOnlyList<(string? From, string To, DateTimeOffset At)> Events);
    public sealed record OfferRow(OfferStatus Status);

    public sealed record Duration(int Count, bool Suppressed, double? MedianDays, double? AverageDays, double? P75Days);
    public sealed record StageRow(string Stage, int Reached, double? ConversionFromPrevious, Duration TimeInStage);
    public sealed record SourceRow(string Source, int? Applications, int? Hired, double? HireRate, bool Suppressed);
    public sealed record OfferStats(int Sent, int? Accepted, int? Declined, int? Expired, double? AcceptanceRate, bool Suppressed);
    public sealed record Result(int Applications, List<StageRow> Stages, Duration TimeToHire, List<SourceRow> Sources, OfferStats Offers, int MinGroup);

    public static Duration Summarize(IReadOnlyList<double> days)
    {
        if (days.Count < MinGroup) return new(days.Count, days.Count > 0, null, null, null);
        var s = days.OrderBy(x => x).ToList();
        return new(s.Count, false, Math.Round(Quantile(s, 0.5), 1), Math.Round(s.Average(), 1), Math.Round(Quantile(s, 0.75), 1));
    }

    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var pos = (sorted.Count - 1) * q;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static int Rank(ApplicationStatus s) => Array.IndexOf(Funnel, s);

    public static Result Compute(IReadOnlyList<App> apps, IReadOnlyList<OfferRow> offers, DateTimeOffset now)
    {
        // Bir aşamaya ulaşmış sayılır: geçmişte o aşamaya giriş var ya da şu anki durumu o aşama veya ötesi.
        var reached = Funnel.ToDictionary(s => s, _ => 0);
        var inStage = Funnel.ToDictionary(s => s, _ => new List<double>());
        var tth = new List<double>();
        foreach (var a in apps)
        {
            var seen = a.Events.Select(e => Enum.TryParse<ApplicationStatus>(e.To, out var st) ? st : (ApplicationStatus?)null)
                .Where(x => x is not null).Select(x => x!.Value).ToHashSet();
            seen.Add(ApplicationStatus.Applied);
            var maxRank = Rank(a.Status);
            foreach (var st in seen) maxRank = Math.Max(maxRank, Rank(st));
            foreach (var st in Funnel) if (Rank(st) <= maxRank || seen.Contains(st)) reached[st]++;

            var ev = a.Events.OrderBy(e => e.At).ToList();
            for (var i = 0; i < ev.Count; i++)
            {
                if (!Enum.TryParse<ApplicationStatus>(ev[i].To, out var st) || st == ApplicationStatus.Hired || !inStage.ContainsKey(st)) continue;
                // Yalnızca tamamlanmış aşama süreleri (bir sonraki olay varsa) sayılır.
                if (i + 1 < ev.Count) inStage[st].Add(Math.Max(0, (ev[i + 1].At - ev[i].At).TotalDays));
            }
            var hired = ev.LastOrDefault(e => e.To == nameof(ApplicationStatus.Hired));
            if (a.Status == ApplicationStatus.Hired && hired.To is not null)
                tth.Add(Math.Max(0, (hired.At - a.AppliedAt).TotalDays));
        }

        var stages = new List<StageRow>();
        for (var i = 0; i < Funnel.Length; i++)
        {
            var st = Funnel[i];
            double? conv = i == 0 || reached[Funnel[i - 1]] == 0 ? null : Math.Round(100.0 * reached[st] / reached[Funnel[i - 1]], 1);
            stages.Add(new(st.ToString(), reached[st], conv, st == ApplicationStatus.Hired ? new(0, false, null, null, null) : Summarize(inStage[st])));
        }

        var sources = apps.GroupBy(a => string.IsNullOrWhiteSpace(a.Source) ? "Other" : a.Source)
            .Select(g =>
            {
                var n = g.Count();
                var h = g.Count(a => a.Status == ApplicationStatus.Hired);
                return n < MinGroup
                    ? new SourceRow(g.Key, null, null, null, true)
                    : new SourceRow(g.Key, n, h, Math.Round(100.0 * h / n, 1), false);
            })
            .OrderBy(r => r.Suppressed).ThenByDescending(r => r.Applications).ToList();

        var sent = offers.Count(o => o.Status is OfferStatus.Sent or OfferStatus.Accepted or OfferStatus.Declined or OfferStatus.Expired);
        var acc = offers.Count(o => o.Status == OfferStatus.Accepted);
        var dec = offers.Count(o => o.Status == OfferStatus.Declined);
        var exp = offers.Count(o => o.Status == OfferStatus.Expired);
        var decided = acc + dec + exp;
        var offerStats = decided < MinGroup
            ? new OfferStats(sent, null, null, null, null, decided > 0)
            : new OfferStats(sent, acc, dec, exp, Math.Round(100.0 * acc / decided, 1), false);

        return new(apps.Count, stages, Summarize(tth), sources, offerStats, MinGroup);
    }
}
