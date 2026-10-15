using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GovernanceService.Infrastructure;

/* ======================================================================
 * Dalga 10 — ML:
 *   36) Devir modelinin kiracı verisiyle (izinle) eğitimi: toplu, kimliksiz özellik çıkarımı,
 *       zaman bazlı doğrulama, asgari veri eşikleri.
 *   51) Aday–ilan uygunluğu için aday metninden adın çıkarılması (bias guard'ın çağıran tarafı).
 * ==================================================================== */

public static class AttritionTraining
{
    public const int MinTrainRows = 200;
    public const int MinTrainLeavers = 20;
    public const int MinTrainStayers = 20;
    public const int MinEvalRows = 50;
    public const int MinEvalLeavers = 5;
    public const int LabelMonths = 12;

    public static readonly string[] Features = AttritionFairness.Features;

    /// <summary>Ham çıkarım satırı (eksik değer null). Kimlik yok: satırlar karıştırılarak gönderilir.</summary>
    public sealed record RawRow(double Tenure, double? Compa, double? Rating, double MonthsSincePromotion, double Overtime, double Training, bool Left);

    public sealed record Prepared(List<Dictionary<string, object>> Rows, int Leavers, Dictionary<string, int> Imputed);

    /// <summary>
    /// Anlık görüntü T'de şirkette olan herkes için T'deki özellikler ve T'den sonraki 12 ayda ayrılıp ayrılmadığı.
    /// $1 kiracı, $2 T (date). Ayrılış tarihi bilinmeyen ayrılmış çalışan satırı dışarıda kalır (etiket belirsiz).
    /// </summary>
    public const string ExtractSql = """
        WITH emp AS (
            SELECT e."Id", e."HireDate", e."Status",
                   CASE WHEN e."Status" = 'Terminated' THEN coalesce(
                       (SELECT max(o."LastWorkingDay") FROM engagement_offboarding_cases o WHERE o."TenantSlug" = $1 AND o."EmployeeId" = e."Id"),
                       (SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id")) END AS left_on
            FROM employee_employees e
            WHERE e."TenantSlug" = $1 AND e."HireDate" <= $2
        ), pop AS (
            SELECT * FROM emp WHERE NOT ("Status" = 'Terminated' AND left_on IS NULL) AND (left_on IS NULL OR left_on > $2)
        )
        SELECT ($2 - p."HireDate")::float8 / 365.25,
               (SELECT (r."BaseSalary" / NULLIF(b."MidAmount", 0))::float8 FROM compensation_records r
                  LEFT JOIN LATERAL (SELECT sb."MidAmount" FROM compensation_salary_bands sb
                                     WHERE sb."TenantSlug" = $1 AND sb."Grade" = r."Grade" AND coalesce(sb."EffectiveFrom", make_date(sb."Year", 1, 1)) <= $2
                                     ORDER BY coalesce(sb."EffectiveFrom", make_date(sb."Year", 1, 1)) DESC LIMIT 1) b ON true
                 WHERE r."TenantSlug" = $1 AND r."EmployeeId" = p."Id" AND r."EffectiveFrom" <= $2 ORDER BY r."EffectiveFrom" DESC LIMIT 1),
               (SELECT s."Score"::float8 FROM performance_snapshots s
                 WHERE s."TenantSlug" = $1 AND s."EmployeeId" = p."Id" AND NOT s."IsProvisional" AND s."CapturedAt" < ($2 + 1)::timestamptz
                 ORDER BY s."CapturedAt" DESC LIMIT 1),
               ($2 - coalesce((SELECT a."EffectiveFrom" FROM (
                     SELECT x."EffectiveFrom", x."PositionTitle", lag(x."PositionTitle") OVER (ORDER BY x."EffectiveFrom") AS prev
                     FROM employee_assignments x WHERE x."EmployeeId" = p."Id" AND x."EffectiveFrom" <= $2) a
                   WHERE a.prev IS NOT NULL AND a.prev IS DISTINCT FROM a."PositionTitle" ORDER BY a."EffectiveFrom" DESC LIMIT 1), p."HireDate"))::float8 / 30.44,
               coalesce((SELECT sum(t."OvertimeMinutes") FROM timeshift_time_entries t
                          WHERE t."TenantSlug" = $1 AND t."EmployeeId" = p."Id" AND t."Date" > $2 - 91 AND t."Date" <= $2), 0)::float8 / 60.0 / 3.0,
               coalesce((SELECT sum(c."DurationHours") FROM learning_enrollments en JOIN learning_courses c ON c."Id" = en."CourseId"
                          WHERE en."TenantSlug" = $1 AND en."EmployeeId" = p."Id" AND en."Status" = 'Completed'
                            AND en."CompletedAt" > ($2 - 365)::timestamptz AND en."CompletedAt" < ($2 + 1)::timestamptz), 0)::float8,
               (p.left_on IS NOT NULL AND p.left_on <= $2 + 365)
        FROM pop p
        """;

    public static Task<List<RawRow>> ExtractAsync(Sql sql, string tenant, DateOnly snapshot, CancellationToken ct) =>
        sql.QueryAsync(ExtractSql, r => new RawRow(
            r.GetDouble(0), r.IsDBNull(1) ? null : r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2),
            r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetBoolean(6)), ct, tenant, snapshot);

    /// <summary>Performans puanı 1-5 ölçeğine (0-100 ise doğrusal dönüşüm).</summary>
    public static double RatingTo5(double score) => score <= 5 ? Math.Clamp(score, 1, 5) : 1 + 4 * Math.Clamp(score, 0, 100) / 100.0;

    /// <summary>Model aralıklarına kırpar, eksik değeri nötr değerle doldurur (sayısı raporlanır), sırayı karıştırır.</summary>
    public static Prepared Prepare(IEnumerable<RawRow> raw, int seed)
    {
        var imputed = new Dictionary<string, int> { ["compa_ratio"] = 0, ["last_rating"] = 0 };
        var rows = new List<Dictionary<string, object>>();
        var leavers = 0;
        foreach (var r in raw)
        {
            if (r.Compa is null) imputed["compa_ratio"]++;
            if (r.Rating is null) imputed["last_rating"]++;
            if (r.Left) leavers++;
            rows.Add(new Dictionary<string, object>
            {
                ["tenure_years"] = Math.Round(Math.Clamp(r.Tenure, 0, 45), 3),
                ["compa_ratio"] = Math.Round(Math.Clamp(r.Compa ?? 1.0, 0.3, 2.0), 4),
                ["last_rating"] = Math.Round(r.Rating is { } s ? RatingTo5(s) : 3.0, 3),
                ["months_since_promotion"] = Math.Round(Math.Clamp(r.MonthsSincePromotion, 0, 360), 2),
                ["overtime_hours_month"] = Math.Round(Math.Clamp(r.Overtime, 0, 200), 2),
                ["training_hours_year"] = Math.Round(Math.Clamp(r.Training, 0, 500), 2),
                ["label"] = r.Left ? 1 : 0,
            });
        }
        var rng = new Random(seed);
        for (var i = rows.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (rows[i], rows[j]) = (rows[j], rows[i]); }
        return new Prepared(rows, leavers, imputed);
    }

    /// <summary>Eşik denetimi; boş liste = eğitim yapılabilir.</summary>
    public static List<string> Refusals(Prepared train, Prepared eval)
    {
        var list = new List<string>();
        if (train.Rows.Count < MinTrainRows) list.Add($"Eğitim anlık görüntüsünde en az {MinTrainRows} çalışan gerekir (şu an {train.Rows.Count}).");
        if (train.Leavers < MinTrainLeavers) list.Add($"Eğitim döneminde en az {MinTrainLeavers} ayrılan gerekir (şu an {train.Leavers}).");
        if (train.Rows.Count - train.Leavers < MinTrainStayers) list.Add($"Eğitim döneminde en az {MinTrainStayers} kalan çalışan gerekir.");
        if (eval.Rows.Count < MinEvalRows) list.Add($"Değerlendirme anlık görüntüsünde en az {MinEvalRows} çalışan gerekir (şu an {eval.Rows.Count}).");
        if (eval.Leavers < MinEvalLeavers || eval.Rows.Count - eval.Leavers < MinEvalLeavers)
            list.Add($"Değerlendirme döneminde her iki sınıftan (ayrılan/kalan) en az {MinEvalLeavers} kişi gerekir (ayrılan {eval.Leavers}).");
        return list;
    }

    /// <summary>Zaman bazlı doğrulama: eğitim T1 = bugün − 24 ay (etiket penceresi T1..T1+12 ay), değerlendirme T2 = bugün − 12 ay.</summary>
    public static (DateOnly Train, DateOnly Eval) Snapshots(DateOnly today) => (today.AddMonths(-2 * LabelMonths), today.AddMonths(-LabelMonths));

    /// <summary>ML servisine kiracı adı değil özeti gider (model kartında kaynağı ayırt etmek için).</summary>
    public static string TenantRef(string tenant) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hr360-tenant:" + tenant))).ToLowerInvariant()[..16];
}

public static class CandidateText
{
    /// <summary>
    /// Adayın adı/soyadı (ve e-postasının yerel kısmı) özgeçmiş metninden çıkarılır; ML servisi ayrıca
    /// iletişim/demografik ifadeleri temizler. Büyük/küçük harf ve Türkçe i/ı farkı gözetmez.
    /// </summary>
    public static string StripName(string? text, string? firstName, string? lastName, string? email)
    {
        var t = text ?? "";
        var parts = new List<string>();
        foreach (var n in new[] { firstName, lastName })
            if (!string.IsNullOrWhiteSpace(n)) parts.AddRange(n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 2));
        if (!string.IsNullOrWhiteSpace(email) && email.Contains('@')) parts.Add(email[..email.IndexOf('@')]);
        foreach (var p in parts.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length))
        {
            var rx = new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(p) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            t = rx.Replace(t, " ▒ ");
            // Türkçe büyük harf yazımı ("AYŞE YILMAZ", "İLKER") değişmez kültürle eşleşmeyebilir.
            t = t.Replace(p.ToUpper(new CultureInfo("tr-TR")), " ▒ ");
        }
        return t;
    }
}
