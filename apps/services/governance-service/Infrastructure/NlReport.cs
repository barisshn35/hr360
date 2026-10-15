using System.Globalization;
using System.Text.RegularExpressions;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Doğal dilde rapor: "geçen ay departmanlara göre izin günleri" gibi Türkçe bir
/// soruyu ölçüt + kırılım + zaman aralığına ayrıştırır ve önceden yazılmış,
/// parametreli SQL şablonlarından birini çalıştırır.
///
/// DÜRÜSTLÜK NOTU: Bu bir dil modeli DEĞİLDİR. Kural tabanlı bir ayrıştırıcıdır;
/// anlamadığı soruda tahmin yürütmez, "anlamadım" der ve örnek sorular önerir.
/// Avantajı: SQL serbest üretilmez (enjeksiyon yok), her yanıtla birlikte
/// nasıl yorumlandığı ve çalıştırılan sorgu gösterilir, kiracı filtresi her
/// şablonda sabittir.
///
/// G3 (Dalga 5d): ücret dağılımı (yalnızca İK; 5 kişiden küçük gruplar gizlenir),
/// işe alım hunisi, departman ve tarih aralığı süzgeci, geçen yılla karşılaştırma
/// (çalışan sayısı / ayrılış / izin / işe alım). Küçük grup gizleme (k ≥ 5) tüm
/// toplu kırılımlara her yerde uygulanır (kişi bazında "ilk 10" yalnızca İK'ya açıktır).
/// </summary>
public static class NlReport
{
    /// <summary>Küçük grup eşiği: bu sayının altındaki kişiden oluşan grupların değeri gösterilmez.</summary>
    public const int MinGroup = 5;

    /// <summary>
    /// Çalıştırma seçenekleri. Kayıtlı raporun parametreleri (departman, tarih aralığı,
    /// karşılaştırma) sorudan çıkarılanı geçersiz kılar. Küçük grup gizleme (k ≥ 5)
    /// VARSAYILAN olarak açıktır: canlı rapor, kayıtlı/zamanlanmış rapor, İK asistanı ve
    /// sohbet botu aynı kuralı uygular (departman/tarih süzgeciyle tek kişiye inilemesin).
    /// </summary>
    public sealed record Options(Guid? DepartmentId = null, DateOnly? From = null, DateOnly? To = null, bool? Compare = null,
        bool Suppress = true, bool AllowSalary = false);

    /// <summary>Sorunun ayrıştırılmış hâli (veritabanına dokunmaz; birim testlerde doğrudan sınanır).</summary>
    public sealed record Query(string? Metric, string GroupBy, DateOnly From, DateOnly To, string PeriodLabel, bool Compare,
        Guid? DepartmentId, string? Department, bool Bands);

    public sealed record Result(
        bool Understood, string Interpretation, string Metric, string GroupBy, DateOnly From, DateOnly To,
        string[] Columns, List<object?[]> Rows, string Chart, string Sql, string[] Suggestions)
    {
        public Guid? DepartmentId { get; init; }
        public string? Department { get; init; }
        public bool Compare { get; init; }
        /// <summary>Küçük grup kuralıyla değeri gizlenen satır sayısı.</summary>
        public int Suppressed { get; init; }
        public string? Note { get; init; }
        /// <summary>Kişi bazında sonuç (zamanlanamaz).</summary>
        public bool PersonLevel => GroupBy == "person";
    }

    public static string[] ExamplesFor(string lang) => lang == "en" ? ExamplesEn : Examples;

    public static readonly string[] ExamplesEn =
    {
        "Leave days by department in the last 6 months",
        "Monthly hires this year",
        "Employees with the most overtime last month",
        "Headcount by department",
        "Monthly headcount in the last 12 months",
        "Leave by type this year",
        "Number of leavers last year",
        "Average performance score by department",
        "Monthly expense total this year",
        "Salary distribution by department",
        "Recruitment funnel by posting this year",
        "Leave days this year compared to last year",
    };

    public static readonly string[] Examples =
    {
        "Son 6 ayda departmanlara göre izin günleri",
        "Bu yıl aylık işe alım sayısı",
        "Geçen ay en çok fazla mesai yapan çalışanlar",
        "Departmanlara göre çalışan sayısı",
        "Son 12 ayda aylık çalışan sayısı",
        "Bu yıl izin türlerine göre dağılım",
        "Geçen yıl ayrılan çalışan sayısı",
        "Departmanlara göre ortalama performans puanı",
        "Bu yıl aylık masraf toplamı",
        "Departmanlara göre maaş dağılımı",
        "Bu yıl ilanlara göre işe alım hunisi",
        "Bu yıl izin günleri geçen yıla göre",
    };

    private static readonly string[] Months = { "ocak", "subat", "mart", "nisan", "mayis", "haziran", "temmuz", "agustos", "eylul", "ekim", "kasim", "aralik" };
    private static readonly string[] MonthsEn = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    /// <summary>Geçen yılla karşılaştırma ifadeleri (dönem ayrıştırmasından önce sorudan çıkarılır).</summary>
    private static readonly string[] ComparePhrases =
    {
        "gecen yila gore", "gecen yilla karsilastir", "gecen yilin ayni donemi", "gecen yilla", "onceki yila gore", "onceki yilla",
        "yillik karsilastirma", "yil bazinda karsilastir", "yildan yila", "gecen seneye gore", "gecen seneyle",
        "year over year", "year-over-year", " yoy ", "compared to last year", "compared with last year", "vs last year", "versus last year",
        "against last year", "vs. last year", "same period last year",
    };

    public static string Norm(string s)
    {
        var lower = s.ToLower(new CultureInfo("tr-TR"));
        var map = new Dictionary<char, char> { ['ı'] = 'i', ['ş'] = 's', ['ğ'] = 'g', ['ü'] = 'u', ['ö'] = 'o', ['ç'] = 'c', ['â'] = 'a', ['î'] = 'i' };
        return new string(lower.Select(c => map.TryGetValue(c, out var r) ? r : c).ToArray());
    }

    private const string DeptJoin = """
        LEFT JOIN LATERAL (SELECT x."DepartmentId" FROM employee_assignments x WHERE x."EmployeeId" = e."Id"
            AND x."EffectiveFrom" <= current_date AND (x."EffectiveTo" IS NULL OR x."EffectiveTo" >= current_date)
            ORDER BY x."EffectiveFrom" DESC LIMIT 1) asg ON true
        LEFT JOIN organization_departments d ON d."Id" = asg."DepartmentId"
        """;

    /// <summary>Departman süzgeci ($4; boşsa tüm şirket). Her şablonda bulunur.</summary>
    private const string DeptFilter = "($4::uuid IS NULL OR asg.\"DepartmentId\" = $4)";

    private const string ExitDate = """
        CASE WHEN e."Status" = 'Terminated' THEN coalesce((SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id"), e."CreatedAt"::date) END
        """;

    private static bool CompareSupported(string? metric) => metric is "headcount" or "exits" or "leave" or "hires";

    /* =================================================================== ayrıştırma */

    /// <summary>
    /// Soruyu ayrıştırır. Soru Türkçe ya da İngilizce olabilir; iki dilin anahtar sözcükleri
    /// birlikte aranır. <paramref name="en"/> yalnızca dönem etiketinin dilini belirler.
    /// </summary>
    public static Query Parse(string question, DateOnly today, IReadOnlyList<(Guid Id, string Name)>? departments, bool allowPersonLevel, bool en = false)
    {
        var q = " " + Norm(question).Replace('?', ' ').Replace(',', ' ') + " ";

        // Geçen yılla karşılaştırma: "geçen yıla göre" ifadesi dönemi "geçen yıl" yapmasın diye önce çıkarılır.
        var compare = false;
        foreach (var p in ComparePhrases)
            if (q.Contains(p)) { compare = true; q = q.Replace(p, " "); }

        // Departman süzgeci: kiracının departman adlarından biri geçiyorsa (en uzun eşleşme).
        Guid? deptId = null; string? deptName = null;
        if (departments is not null)
            foreach (var d in departments.Where(d => !string.IsNullOrWhiteSpace(d.Name)).OrderByDescending(d => d.Name.Length))
            {
                var n = Norm(d.Name).Trim();
                if (n.Length < 3) continue;
                var m = Regex.Match(q, $@"(?<![a-z0-9]){Regex.Escape(n)}[a-z]*(\s+(departman|birim|bolum|department|dept|team|ekib)[a-z]*)?");
                if (!m.Success) continue;
                deptId = d.Id; deptName = d.Name;
                q = q.Remove(m.Index, m.Length).Insert(m.Index, " ");
                break;
            }

        // Sıra önemli: "leavers" içinde "leave", "new employees" içinde "employees" geçer;
        // "işe alım hunisi" huni, "ücretsiz izin" izindir.
        string? metric =
            Has(q, "fazla mesai", "mesai", "overtime", "extra hours") ? "overtime" :
            Has(q, "huni", "funnel", "basvuru", " aday", "applicant", "application", "candidate", "pipeline") ? "funnel" :
            Has(q, "maas", "ucret dagilim", "ucret bant", "ucretler", "ucret seviye", "salary", "salaries", "pay band", "pay distribution", "compensation") ? "salary" :
            Has(q, "leaver", " left ", "attrition", "turnover", "resign", "termination", " exits", " exit ") ? "exits" :
            Has(q, "izin", " leave", "time off", "vacation", "absence") ? "leave" :
            Has(q, "ise alim", "ise giren", "yeni calisan", "yeni baslayan", "ise baslayan", "alim", " hire", "hiring", "new employee", "joiner", "new starter") ? "hires" :
            Has(q, "ayrilan", "cikis", "isten ayril", "devir") ? "exits" :
            Has(q, "masraf", "harcama", "expense", "spend") ? "expense" :
            Has(q, "performans", "puan", "performance", "score", "rating") ? "performance" :
            Has(q, "egitim", "kurs", "training", "course") ? "training" :
            Has(q, "calisan sayisi", "kac kisi", "kac calisan", "kadro", "headcount", "personel sayisi", "calisan",
                "employee count", "number of employees", "how many employees", "staff", "employees") ? "headcount" : null;

        var groupBy =
            Has(q, "departman", "birim", "bolum", "department") ? "department" :
            Has(q, "izin turu", "turlerine", "turune", "tipine", "tur bazinda", "turu", "by type", "per type", "leave type", " types") ? "type" :
            Has(q, "en cok", "en fazla", "kisi bazinda", "calisan bazinda", "kimler", " kim ", " most ", " top ", "per person", "by employee", " who ") ? "person" :
            Has(q, "aylik", "aya gore", "aylara", "her ay", "ay ay", "trend", "egilim", "monthly", "by month", "per month", "each month", "over time") ? "month" : "none";
        if (groupBy == "person" && !allowPersonLevel) groupBy = "department";
        if (groupBy == "type" && metric is not ("leave" or "training")) groupBy = "none";

        var bands = false;
        if (metric == "funnel")
        {
            if (Has(q, "ilan", "pozisyon", "posting", " job", " role", " position")) groupBy = "posting";
            else if (groupBy is "type" or "person") groupBy = "none";
        }
        if (metric == "salary")
        {
            bands = Has(q, "bant", "aralik", "aralig", "kademe", "band", "range", "histogram", "bucket");
            if (bands) groupBy = "band";
            else if (groupBy is "month" or "type" or "person") groupBy = "department";
        }
        if (!CompareSupported(metric)) compare = false;

        // "maaş aralıkları" (ranges) Aralık ayı değildir.
        q = Regex.Replace(q, @"aralik(lar|lari|larina|larin|larda)[a-z]*", " bant ");
        var (from, to, periodLabel) = ParsePeriod(q, today, groupBy == "month", en);
        return new Query(metric, groupBy, from, to, periodLabel, compare, deptId, deptName, bands);
    }

    /* =================================================================== çalıştırma */

    public static async Task<Result> RunAsync(Sql sql, string tenant, string question, bool allowPersonLevel, CancellationToken ct, string lang = "tr", Options? opt = null)
    {
        opt ??= new Options();
        var en = lang == "en";
        string L(string tr, string e) => en ? e : tr;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        var departments = await sql.QueryAsync("SELECT \"Id\", \"Name\" FROM organization_departments WHERE \"TenantSlug\" = $1",
            r => (r.GetGuid(0), r.GetString(1)), ct, tenant);
        var pq = Parse(question, today, departments, allowPersonLevel, en);

        // Kayıtlı rapor parametreleri sorudan çıkarılanı geçersiz kılar.
        var from = pq.From; var to = pq.To; var periodLabel = pq.PeriodLabel;
        if (opt.From is not null || opt.To is not null)
        {
            from = opt.From ?? pq.From; to = opt.To ?? pq.To;
            if (from > to) (from, to) = (to, from);
            periodLabel = RangeLabel(from, to, en);
        }
        var deptId = opt.DepartmentId ?? pq.DepartmentId;
        var deptName = opt.DepartmentId is { } od ? departments.FirstOrDefault(d => d.Item1 == od).Item2 : pq.Department;
        var compare = (opt.Compare ?? pq.Compare) && CompareSupported(pq.Metric);
        var metric = pq.Metric;
        var groupBy = pq.GroupBy;

        if (metric is null)
            return new Result(false, L("Soruyu anlayamadım. İzin, işe alım, ayrılış, çalışan sayısı, fazla mesai, masraf, performans, eğitim, ücret dağılımı veya işe alım hunisi hakkında sorabilirsiniz.",
                    "I couldn't understand the question. You can ask about leave, hiring, leavers, headcount, overtime, expenses, performance, training, salary distribution or the recruitment funnel."),
                "", groupBy, from, to, Array.Empty<string>(), new(), "none", "", ExamplesFor(lang));

        if (metric == "salary" && !opt.AllowSalary)
            return new Result(false, L("Ücret dağılımı yalnızca İK yetkilisine açıktır.", "Salary distribution is available to HR only."),
                metric, groupBy, from, to, Array.Empty<string>(), new(), "none", "", ExamplesFor(lang).Where(e => !Norm(e).Contains("maas") && !e.Contains("Salary")).Take(4).ToArray());

        string[] columns; List<object?[]> rows; string chart, label, shownSql; var suppressed = 0; string? note = null;

        if (metric == "salary")
        {
            var salarySql = SalarySql(en);
            var raw = await sql.QueryAsync(salarySql, r => (Group: r.GetString(0), Salary: r.GetDecimal(1), Currency: r.GetString(2)), ct, tenant, from, to, (object?)deptId);
            (columns, rows, suppressed, note) = SalaryStats(raw, pq.Bands, groupBy == "department", en);
            chart = pq.Bands ? "bar" : groupBy == "department" ? "bar" : "number";
            label = pq.Bands ? L("ücret bantlarına göre çalışan dağılımı", "employees by salary band") : L("ücret dağılımı (çeyrekler)", "salary distribution (quartiles)");
            shownSql = salarySql;
        }
        else
        {
            var (cols, select, ch, lb, nIndex) = Build(metric, groupBy, en, compare);
            columns = cols; chart = ch; label = lb; shownSql = select;
            rows = await RunRowsAsync(sql, select, tenant, from, to, deptId, ct);
            if (compare)
            {
                var prev = await RunRowsAsync(sql, select, tenant, from.AddYears(-1), to.AddYears(-1), deptId, ct);
                rows = MergeYoY(rows, prev, groupBy == "month");
                columns = new[] { columns[0], columns[1], L("Geçen yıl", "Previous year"), L("Değişim %", "Change %") };
                nIndex = 4;
                chart = groupBy == "month" ? "line" : "bar";
            }
            // Küçük gruplar her yerde gizlenir (kişi bazında rapor zaten zamanlanamaz ve yalnızca İK'ya açıktır).
            if (opt.Suppress && groupBy != "person" && nIndex is not null)
                suppressed = Suppress(rows, nIndex.Value, Enumerable.Range(1, nIndex.Value - 1).ToArray());
            if (nIndex is not null) rows = rows.Select(r => r.Take(nIndex.Value).ToArray()).ToList();
            if (suppressed > 0) note = SuppressedNote(en);
        }

        string interpretation;
        var isCurrent = metric is "headcount" or "performance" or "salary" && groupBy != "month" && !compare;
        if (en)
        {
            var by = groupBy switch
            {
                "department" => " by department", "month" => " by month", "type" => " by type", "person" => " per person (top 10)", "posting" => " by posting", _ => "",
            };
            interpretation = isCurrent ? $"{label}{by} (current)" : $"{label}{by}, {periodLabel}";
            if (deptName is not null) interpretation += $" — {deptName} department";
            if (compare) interpretation += " (compared with the same period last year)";
        }
        else
        {
            var groupText = groupBy switch
            {
                "department" => "departmanlara göre ", "month" => "aylara göre ", "type" => "türe göre ", "person" => "kişi bazında (ilk 10) ", "posting" => "ilanlara göre ", _ => "",
            };
            interpretation = isCurrent ? $"{Capitalize(groupText)}{label} (güncel)" : $"{periodLabel} {groupText}{label}";
            if (deptName is not null) interpretation += $" — {deptName} departmanı";
            if (compare) interpretation += " (geçen yılın aynı dönemiyle karşılaştırmalı)";
        }
        var examples = ExamplesFor(lang);
        return new Result(true, Capitalize(interpretation.Trim(), en), metric, groupBy, from, to, columns, rows, chart,
            Regex.Replace(shownSql, @"\s+", " ").Trim(), examples.Where(e => !Norm(e).Contains(metric)).Take(4).ToArray())
        {
            DepartmentId = deptId, Department = deptName, Compare = compare, Suppressed = suppressed, Note = note,
        };
    }

    public static string SuppressedNote(bool en) => en
        ? $"Groups with fewer than {MinGroup} people are hidden (privacy)."
        : $"{MinGroup} kişiden az olan grupların değeri gizlendi (KVKK veri en aza indirme).";

    private static Task<List<object?[]>> RunRowsAsync(Sql sql, string select, string tenant, DateOnly from, DateOnly to, Guid? dept, CancellationToken ct) =>
        sql.QueryAsync(select, r =>
        {
            var arr = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
                arr[i] = r.IsDBNull(i) ? null : r.GetValue(i) switch
                {
                    DateOnly d => d.ToString("yyyy-MM"),
                    DateTime dt => dt.ToString("yyyy-MM"),
                    decimal m => Math.Round(m, 1),
                    double db => Math.Round(db, 1),
                    var v => v,
                };
            return arr;
        }, ct, tenant, from, to, (object?)dept);

    /* =================================================================== saf yardımcılar (birim testli) */

    /// <summary>
    /// Küçük grup gizleme: <paramref name="nIndex"/> sütunundaki kişi sayısı 1..MinGroup-1 olan
    /// satırların değer sütunları boşaltılır. Gizlenen satır sayısını döner.
    /// </summary>
    public static int Suppress(List<object?[]> rows, int nIndex, int[] valueIndexes)
    {
        var count = 0;
        foreach (var r in rows)
        {
            var n = r.Length > nIndex && r[nIndex] is not null ? Convert.ToInt64(r[nIndex], CultureInfo.InvariantCulture) : 0;
            if (n is <= 0 or >= MinGroup) continue;
            foreach (var i in valueIndexes) if (i < r.Length) r[i] = null;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Geçen yılla birleştirme. Satırlar [anahtar, değer, kişi sayısı]; sonuç
    /// [anahtar, bu dönem, geçen yıl, değişim %, kişi sayısı (ikisinin küçüğü)].
    /// Aylık kırılımda geçen yılın "2025-03" anahtarı "2026-03" ile eşlenir.
    /// </summary>
    public static List<object?[]> MergeYoY(List<object?[]> current, List<object?[]> previous, bool monthly)
    {
        string Key(object? k, bool shift)
        {
            var s = k?.ToString() ?? "";
            if (shift && monthly && s.Length >= 7 && int.TryParse(s[..4], out var y)) return (y + 1).ToString(CultureInfo.InvariantCulture) + s[4..];
            return s;
        }
        decimal? Num(object? v) => v is null ? null : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        long N(object?[] r) => r.Length > 2 && r[2] is not null ? Convert.ToInt64(r[2], CultureInfo.InvariantCulture) : 0;
        var prev = previous.GroupBy(r => Key(r[0], true)).ToDictionary(g => g.Key, g => g.First());
        var keys = current.Select(r => Key(r[0], false)).Concat(prev.Keys.Where(k => current.All(c => Key(c[0], false) != k))).ToList();
        var cur = current.GroupBy(r => Key(r[0], false)).ToDictionary(g => g.Key, g => g.First());
        var outRows = new List<object?[]>();
        foreach (var k in keys)
        {
            cur.TryGetValue(k, out var c); prev.TryGetValue(k, out var p);
            var cv = c is null ? 0m : Num(c[1]) ?? 0m;
            var pv = p is null ? (decimal?)null : Num(p[1]);
            decimal? change = pv is null or 0m ? null : Math.Round((cv - pv.Value) / pv.Value * 100m, 1);
            var n = c is null ? N(p!) : p is null ? N(c) : Math.Min(N(c), N(p));
            outRows.Add(new object?[] { c?[0] ?? k, cv, pv ?? 0m, change, n });
        }
        if (monthly) outRows = outRows.OrderBy(r => r[0]?.ToString()).ToList();
        return outRows;
    }

    /// <summary>Doğrusal aralıklı çeyrek (R tip 7). Liste sıralı olmalı.</summary>
    public static decimal Quantile(IReadOnlyList<decimal> sorted, double p)
    {
        if (sorted.Count == 0) throw new ArgumentException("boş liste");
        if (sorted.Count == 1) return sorted[0];
        var h = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(h);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (decimal)(h - lo) * (sorted[hi] - sorted[lo]);
    }

    private static decimal Round100(decimal v) => Math.Round(v / 100m, MidpointRounding.AwayFromZero) * 100m;

    public static readonly (decimal Min, decimal? Max)[] SalaryBands =
    {
        (0m, 25_000m), (25_000m, 40_000m), (40_000m, 60_000m), (60_000m, 80_000m), (80_000m, 120_000m), (120_000m, null),
    };

    /// <summary>
    /// Ücret dağılımı. Kişi düzeyinde değer DÖNMEZ: departman (ve para birimi) başına
    /// kişi sayısı ve çeyrekler (100'e yuvarlanmış); 5 kişiden az grubun değerleri gizlenir.
    /// Bant modunda TRY ücretler bantlara sayılır; toplam 5'ten azsa hiçbir bant gösterilmez.
    /// </summary>
    public static (string[] Columns, List<object?[]> Rows, int Suppressed, string? Note) SalaryStats(
        IEnumerable<(string Group, decimal Salary, string Currency)> data, bool bands, bool byDepartment, bool en)
    {
        string L(string tr, string e) => en ? e : tr;
        var list = data.ToList();
        if (bands)
        {
            var tl = list.Where(x => x.Currency is "TRY" or "TL").Select(x => x.Salary).ToList();
            var cols = new[] { L("Ücret bandı (aylık brüt, TRY)", "Salary band (monthly gross, TRY)"), L("Çalışan", "Employees") };
            var nf = CultureInfo.GetCultureInfo(en ? "en-GB" : "tr-TR");
            var rowsB = SalaryBands.Select(b => new object?[]
            {
                b.Max is null ? $"{b.Min.ToString("N0", nf)}+" : b.Min == 0 ? $"< {b.Max.Value.ToString("N0", nf)}" : $"{b.Min.ToString("N0", nf)}–{b.Max.Value.ToString("N0", nf)}",
                (long)tl.Count(s => s >= b.Min && (b.Max is null || s < b.Max)),
            }).ToList();
            var note = list.Count > tl.Count ? L("TRY dışındaki ücretler bantlara dahil edilmedi.", "Non-TRY salaries are not included in the bands.") : null;
            if (tl.Count is > 0 and < MinGroup)
            {
                foreach (var r in rowsB) r[1] = null;
                return (cols, rowsB, rowsB.Count, SuppressedNote(en));
            }
            return (cols, rowsB, 0, note);
        }

        var columns = new[]
        {
            byDepartment ? L("Departman", "Department") : L("Kapsam", "Scope"), L("Çalışan", "Employees"),
            L("Alt çeyrek (P25)", "Lower quartile (P25)"), L("Medyan", "Median"), L("Üst çeyrek (P75)", "Upper quartile (P75)"),
        };
        var whole = L("Tüm şirket", "Whole company");
        var rows = new List<object?[]>();
        var suppressed = 0;
        foreach (var g in list.GroupBy(x => (Group: byDepartment ? x.Group : whole, x.Currency)).OrderBy(g => g.Key.Group, StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), false)))
        {
            var sorted = g.Select(x => x.Salary).OrderBy(x => x).ToList();
            var name = g.Key.Currency is "TRY" or "TL" ? g.Key.Group : $"{g.Key.Group} ({g.Key.Currency})";
            if (sorted.Count < MinGroup)
            {
                rows.Add(new object?[] { name, null, null, null, null });
                suppressed++;
                continue;
            }
            rows.Add(new object?[] { name, (long)sorted.Count, Round100(Quantile(sorted, 0.25)), Round100(Quantile(sorted, 0.5)), Round100(Quantile(sorted, 0.75)) });
        }
        return (columns, rows, suppressed, suppressed > 0 ? SuppressedNote(en) : null);
    }

    /* =================================================================== SQL şablonları */

    private static string SalarySql(bool en) => $"""
        SELECT DISTINCT ON (r."EmployeeId") coalesce(d."Name", '{(en ? "Unassigned" : "Atanmamış")}'), r."BaseSalary", r."Currency"
        FROM compensation_records r JOIN employee_employees e ON e."Id" = r."EmployeeId" {DeptJoin}
        WHERE r."TenantSlug" = $1 AND e."TenantSlug" = $1 AND e."Status" <> 'Terminated' AND $2::date IS NOT NULL
          AND r."EffectiveFrom" <= $3 AND (r."EffectiveTo" IS NULL OR r."EffectiveTo" >= $3) AND {DeptFilter}
        ORDER BY r."EmployeeId", r."EffectiveFrom" DESC
        """;

    /// <summary>
    /// Sorgu şablonu. Parametreler: $1 kiracı, $2 başlangıç, $3 bitiş, $4 departman (boş olabilir).
    /// Kişi bazında olmayan sorgularda son sütun gruptaki kişi sayısıdır (NIndex) — küçük grup
    /// gizlemesi için kullanılır, yanıttan çıkarılır.
    /// </summary>
    private static (string[] Columns, string Sql, string Chart, string Label, int? NIndex) Build(string metric, string groupBy, bool en, bool asOf = false)
    {
        string L(string tr, string e) => en ? e : tr;
        var unassigned = L("Atanmamış", "Unassigned");
        const string distinctPeople = "count(DISTINCT e.\"Id\")";
        string Group(string valueExpr, string from, string dateExpr, string label, string typeExpr = "NULL")
        {
            var where = $"WHERE e.\"TenantSlug\" = $1 AND {dateExpr} BETWEEN $2 AND $3 AND {DeptFilter}";
            return groupBy switch
            {
                "department" => $"SELECT coalesce(d.\"Name\", '{unassigned}'), {valueExpr}, {distinctPeople} FROM {from} {DeptJoin} {where} GROUP BY 1 ORDER BY 2 DESC",
                "month" => $"SELECT date_trunc('month', {dateExpr})::date, {valueExpr}, {distinctPeople} FROM {from} {DeptJoin} {where} GROUP BY 1 ORDER BY 1",
                "type" => $"SELECT {typeExpr}, {valueExpr}, {distinctPeople} FROM {from} {DeptJoin} {where} GROUP BY 1 ORDER BY 2 DESC",
                "person" => $"SELECT e.\"FirstName\" || ' ' || e.\"LastName\", {valueExpr}, 1 FROM {from} {DeptJoin} {where} GROUP BY 1 ORDER BY 2 DESC LIMIT 10",
                _ => $"SELECT '{label}', {valueExpr}, {distinctPeople} FROM {from} {DeptJoin} {where}",
            };
        }
        string First() => groupBy switch
        {
            "department" => L("Departman", "Department"), "month" => L("Ay", "Month"), "type" => L("Tür", "Type"), "person" => L("Çalışan", "Employee"),
            "posting" => L("İlan", "Posting"), _ => L("Ölçüt", "Measure"),
        };
        var chart = groupBy switch { "month" => "line", "none" => "number", _ => "bar" };

        switch (metric)
        {
            case "leave":
                return (new[] { First(), L("İzin günü", "Leave days") }, Group("sum(l.\"Days\")",
                    "leave_requests l JOIN employee_employees e ON e.\"Id\" = l.\"EmployeeId\" AND l.\"Status\" = 'Approved'",
                    "l.\"StartDate\"", L("onaylı izin günü", "Approved leave days"), "l.\"Type\""), chart, L("onaylı izin günleri", "approved leave days"), 2);
            case "overtime":
                return (new[] { First(), L("Fazla mesai (saat)", "Overtime (hours)") }, Group("round(sum(t.\"OvertimeMinutes\") / 60.0, 1)",
                    "timeshift_time_entries t JOIN employee_employees e ON e.\"Id\" = t.\"EmployeeId\"", "t.\"Date\"", L("fazla mesai saati", "Overtime hours")), chart, L("fazla mesai saatleri", "overtime hours"), 2);
            case "expense":
                return (new[] { First(), L("Tutar", "Amount") }, Group("sum(c.\"TotalAmount\")",
                    "expense_claims c JOIN employee_employees e ON e.\"Id\" = c.\"EmployeeId\" AND c.\"Status\" IN ('Approved','Paid')",
                    "coalesce(c.\"SubmittedAt\", c.\"CreatedAt\")::date", L("masraf toplamı", "Expense total")), chart, L("onaylı masraf toplamı", "approved expense total"), 2);
            case "hires":
                return (new[] { First(), L("İşe alım", "Hires") }, Group("count(*)", "employee_employees e", "e.\"HireDate\"", L("işe alım", "Hires")), chart, L("işe alım sayısı", "number of hires"), 2);
            case "exits":
                return (new[] { First(), L("Ayrılış", "Leavers") }, Group("count(*)", "employee_employees e", $"({ExitDate})", L("ayrılış", "Leavers")), chart, L("ayrılan çalışan sayısı", "number of leavers"), 2);
            case "training":
                return (new[] { First(), L("Tamamlanan eğitim", "Completed trainings") }, Group("count(*)",
                    "learning_enrollments n JOIN employee_employees e ON e.\"Id\" = n.\"EmployeeId\" AND n.\"Status\" = 'Completed' JOIN learning_courses co ON co.\"Id\" = n.\"CourseId\"",
                    "n.\"CompletedAt\"::date", L("tamamlanan eğitim", "Completed trainings"), "co.\"Category\""), chart, L("tamamlanan eğitimler", "completed trainings"), 2);
            case "funnel":
            {
                var key = groupBy switch
                {
                    "posting" => "a.\"Title\"",
                    "month" => "date_trunc('month', a.\"AppliedAt\")::date",
                    "department" => $"coalesce(d.\"Name\", '{unassigned}')",
                    _ => $"'{L("Tüm ilanlar", "All postings")}'",
                };
                var join = groupBy == "department" ? "LEFT JOIN organization_departments d ON d.\"Id\" = a.\"DepartmentId\"" : "";
                var tail = groupBy switch { "none" => "", "month" => "GROUP BY 1 ORDER BY 1", _ => "GROUP BY 1 ORDER BY 2 DESC" };
                var fsql = $"""
                    WITH a AS (
                        SELECT a."Status", a."AppliedAt", p."Title", p."DepartmentId",
                               EXISTS (SELECT 1 FROM recruitment_interviews i WHERE i."ApplicationId" = a."Id") AS iv,
                               EXISTS (SELECT 1 FROM recruitment_offers o WHERE o."ApplicationId" = a."Id") AS ofr
                        FROM recruitment_applications a JOIN recruitment_job_postings p ON p."Id" = a."JobPostingId"
                        WHERE a."TenantSlug" = $1 AND a."AppliedAt"::date BETWEEN $2 AND $3 AND ($4::uuid IS NULL OR p."DepartmentId" = $4))
                    SELECT {key}, count(*),
                           count(*) FILTER (WHERE a."Status" IN ('Screening','Interview','Offer','Hired') OR iv OR ofr),
                           count(*) FILTER (WHERE a."Status" IN ('Interview','Offer','Hired') OR iv OR ofr),
                           count(*) FILTER (WHERE a."Status" IN ('Offer','Hired') OR ofr),
                           count(*) FILTER (WHERE a."Status" = 'Hired'),
                           count(*) FILTER (WHERE a."Status" IN ('Rejected','Withdrawn')),
                           count(*)
                    FROM a {join} {tail}
                    """;
                return (new[]
                {
                    First(), L("Başvuru", "Applications"), L("Ön eleme+", "Screening+"), L("Mülakat+", "Interview+"), L("Teklif+", "Offer+"),
                    L("İşe alınan", "Hired"), L("Reddedilen/çekilen", "Rejected/withdrawn"),
                }, fsql, groupBy == "none" ? "funnel" : groupBy == "month" ? "line" : "bar", L("işe alım hunisi", "recruitment funnel"), 7);
            }
            case "performance":
                const string latest = "(SELECT DISTINCT ON (\"EmployeeId\") * FROM performance_snapshots WHERE \"TenantSlug\" = $1 ORDER BY \"EmployeeId\", \"CapturedAt\" DESC) s JOIN employee_employees e ON e.\"Id\" = s.\"EmployeeId\"";
                var perf = groupBy switch
                {
                    "person" => $"SELECT e.\"FirstName\" || ' ' || e.\"LastName\", round(avg(s.\"Score\"), 1), 1 FROM {latest} {DeptJoin} WHERE $2::date IS NOT NULL AND $3::date IS NOT NULL AND {DeptFilter} GROUP BY 1 ORDER BY 2 DESC LIMIT 10",
                    "month" => $"SELECT date_trunc('month', s.\"CapturedAt\")::date, round(avg(s.\"Score\"), 1), count(DISTINCT s.\"EmployeeId\") FROM performance_snapshots s JOIN employee_employees e ON e.\"Id\" = s.\"EmployeeId\" {DeptJoin} WHERE s.\"TenantSlug\" = $1 AND s.\"CapturedAt\"::date BETWEEN $2 AND $3 AND {DeptFilter} GROUP BY 1 ORDER BY 1",
                    "department" => $"SELECT coalesce(d.\"Name\", '{unassigned}'), round(avg(s.\"Score\"), 1), count(*) FROM {latest} {DeptJoin} WHERE $2::date IS NOT NULL AND $3::date IS NOT NULL AND {DeptFilter} GROUP BY 1 ORDER BY 2 DESC",
                    _ => $"SELECT '{L("Ortalama puan", "Average score")}', round(avg(s.\"Score\"), 1), count(*) FROM {latest} {DeptJoin} WHERE $2::date IS NOT NULL AND $3::date IS NOT NULL AND {DeptFilter}",
                };
                return (new[] { First(), L("Ortalama puan", "Average score") }, perf, chart, L("ortalama performans puanı", "average performance score"), 2);
            default: // headcount
                // Karşılaştırmada "güncel" yerine dönem sonundaki ($3) kadro sayılır.
                var alive = asOf
                    ? $"e.\"HireDate\" <= $3 AND coalesce({ExitDate}, '9999-12-31'::date) > $3 AND $2::date IS NOT NULL"
                    : "e.\"Status\" <> 'Terminated' AND $2::date IS NOT NULL AND $3::date IS NOT NULL";
                var hc = groupBy switch
                {
                    "month" => $"SELECT m::date, c.n, c.n FROM generate_series(date_trunc('month', $2::date), date_trunc('month', $3::date), interval '1 month') m, LATERAL (SELECT count(*) AS n FROM employee_employees e {DeptJoin} WHERE e.\"TenantSlug\" = $1 AND {DeptFilter} AND e.\"HireDate\" <= (m + interval '1 month - 1 day')::date AND coalesce({ExitDate}, '9999-12-31'::date) >= (m + interval '1 month - 1 day')::date) c ORDER BY 1",
                    "department" or "person" => $"SELECT coalesce(d.\"Name\", '{unassigned}'), count(*), count(*) FROM employee_employees e {DeptJoin} WHERE e.\"TenantSlug\" = $1 AND {alive} AND {DeptFilter} GROUP BY 1 ORDER BY 2 DESC",
                    _ => $"SELECT '{L("Aktif çalışan", "Active employees")}', count(*), count(*) FROM employee_employees e {DeptJoin} WHERE e.\"TenantSlug\" = $1 AND {alive} AND {DeptFilter}",
                };
                return (new[] { groupBy == "person" ? L("Departman", "Department") : First(), L("Çalışan", "Employees") }, hc, groupBy == "person" ? "bar" : chart, L("çalışan sayısı", "headcount"), 2);
        }
    }

    /* =================================================================== dönem */

    private static string RangeLabel(DateOnly from, DateOnly to, bool en) => en
        ? $"between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}"
        : $"{from:dd.MM.yyyy}–{to:dd.MM.yyyy} arasında";

    private static int? MonthIndex(string word)
    {
        for (var i = 0; i < 12; i++)
            if (word.StartsWith(Months[i]) || word == MonthsEn[i] || (word.Length == 3 && MonthsEn[i].StartsWith(word)))
                return i;
        return null;
    }

    /// <summary>Açık tarih aralığı: iki tarih (gg.aa.yyyy / yyyy-aa-gg) ya da "ocak-mart 2026", "january to march".</summary>
    public static (DateOnly From, DateOnly To)? ParseRange(string q, DateOnly today)
    {
        var dates = new List<DateOnly>();
        foreach (Match m in Regex.Matches(q, @"\b(\d{1,2})[./](\d{1,2})[./](20\d{2})\b"))
            if (DateOnly.TryParseExact($"{int.Parse(m.Groups[3].Value):0000}-{int.Parse(m.Groups[2].Value):00}-{int.Parse(m.Groups[1].Value):00}", "yyyy-MM-dd", out var d)) dates.Add(d);
        foreach (Match m in Regex.Matches(q, @"\b(20\d{2})-(\d{2})-(\d{2})\b"))
            if (DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", out var d)) dates.Add(d);
        if (dates.Count >= 2)
        {
            var a = dates[0]; var b = dates[1];
            return a <= b ? (a, b) : (b, a);
        }
        if (dates.Count == 1) return dates[0] <= today ? (dates[0], today) : (today, dates[0]);

        var mr = Regex.Match(q, @"\b([a-z]+)\s*(?:-|–|ile|to|through|until|thru)\s*([a-z]+)(?:\s+(?:ayi\s+)?(?:arasi[a-z]*\s+)?(20\d{2}))?");
        while (mr.Success)
        {
            var i1 = MonthIndex(mr.Groups[1].Value); var i2 = MonthIndex(mr.Groups[2].Value);
            if (i1 is not null && i2 is not null)
            {
                var yearM = Regex.Match(q, @"\b(20\d{2})\b");
                var y = yearM.Success ? int.Parse(yearM.Value) : today.Year;
                var f = new DateOnly(y, i1.Value + 1, 1);
                var t = new DateOnly(i2.Value < i1.Value ? y + 1 : y, i2.Value + 1, 1).AddMonths(1).AddDays(-1);
                return (f, t);
            }
            mr = mr.NextMatch();
        }
        return null;
    }

    private static (DateOnly From, DateOnly To, string Label) ParsePeriod(string q, DateOnly today, bool monthly, bool en = false)
    {
        string L(string tr, string e) => en ? e : tr;
        if (ParseRange(q, today) is { } range) return (range.From, range.To, RangeLabel(range.From, range.To, en));

        var m = Regex.Match(q, @"son\s+(\d{1,3})\s*(gun|hafta|ay|yil)");
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value);
            return Last(n, m.Groups[2].Value switch { "gun" => "d", "hafta" => "w", "yil" => "y", _ => "m" });
        }
        m = Regex.Match(q, @"(?:last|past)\s+(\d{1,3})\s*(day|week|month|year)s?");
        if (m.Success)
            return Last(int.Parse(m.Groups[1].Value), m.Groups[2].Value[..1]);

        (DateOnly, DateOnly, string) Last(int n, string unit)
        {
            var from = unit switch { "d" => today.AddDays(-n), "w" => today.AddDays(-7 * n), "y" => today.AddYears(-n), _ => today.AddMonths(-n) };
            var trUnit = unit switch { "d" => "günde", "w" => "haftada", "y" => "yılda", _ => "ayda" };
            var enUnit = unit switch { "d" => "day", "w" => "week", "y" => "year", _ => "month" } + (n == 1 ? "" : "s");
            return (from, today, L($"Son {n} {trUnit}", $"last {n} {enUnit}"));
        }

        if (Has(q, "gecen ay", "last month")) { var f = new DateOnly(today.Year, today.Month, 1).AddMonths(-1); return (f, f.AddMonths(1).AddDays(-1), L("Geçen ay", "last month")); }
        if (Has(q, "bu ay", "this month")) return (new DateOnly(today.Year, today.Month, 1), today, L("Bu ay", "this month"));
        if (Has(q, "gecen yil", "gecen sene", "last year")) return (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31), L("Geçen yıl", "last year"));
        if (Has(q, "bu hafta", "this week")) return (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today, L("Bu hafta", "this week"));
        if (Has(q, "bugun", "today")) return (today, today, L("Bugün", "today"));

        var year = Regex.Match(q, @"\b(20\d{2})\b");
        for (var i = 0; i < 12; i++)
        {
            if (!Regex.IsMatch(q, $@"\b{Months[i]}") && !Regex.IsMatch(q, $@"\b{MonthsEn[i]}\b")) continue;
            var y = year.Success ? int.Parse(year.Value) : (i + 1 > today.Month ? today.Year - 1 : today.Year);
            var f = new DateOnly(y, i + 1, 1);
            var name = CultureInfo.GetCultureInfo(en ? "en-GB" : "tr-TR").DateTimeFormat.GetMonthName(i + 1);
            return (f, f.AddMonths(1).AddDays(-1), en ? $"in {name} {y}" : $"{name} {y}");
        }
        if (year.Success) { var y = int.Parse(year.Value); return (new DateOnly(y, 1, 1), new DateOnly(y, 12, 31), L($"{y} yılında", $"in {y}")); }
        if (Has(q, "bu yil", "bu sene", "this year")) return (new DateOnly(today.Year, 1, 1), today, L("Bu yıl", "this year"));
        return monthly ? (today.AddMonths(-11), today, L("Son 12 ayda", "last 12 months")) : (today.AddMonths(-12), today, L("Son 12 ayda", "last 12 months"));
    }

    private static bool Has(string q, params string[] words) => words.Any(q.Contains);
    private static string Capitalize(string s, bool en = false) =>
        s.Length == 0 ? s : char.ToUpper(s[0], en ? CultureInfo.InvariantCulture : new CultureInfo("tr-TR")) + s[1..];
}
