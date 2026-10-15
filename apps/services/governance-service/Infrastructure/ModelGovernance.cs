using System.Globalization;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Devir riski modelinin adillik denetimi girdisi (ML dalgası 1, madde 39).
///
/// İK, toplu ve dönemsel bir CSV yükler: <c>employee_id</c> + modelin 6 özelliği (+ isteğe bağlı
/// <c>label</c>: 1 = ayrıldı). Çalışan kimliği YALNIZCA burada, grup bilgisini (departman, kıdem
/// bandı) çalışan dizininden eşlemek için kullanılır; ml-inference'a kimlik gitmez. Cinsiyet ve
/// doğum tarihi veri modelinde tutulmadığından (KVKK veri en aza indirme) bu gruplar raporlanmaz.
/// Grup bilgisi modele özellik olarak GİRMEZ; yalnızca denetim içindir.
/// </summary>
public static class AttritionFairness
{
    public static readonly string[] Features =
    {
        "tenure_years", "compa_ratio", "last_rating", "months_since_promotion", "overtime_hours_month", "training_hours_year",
    };

    public const int MaxRows = 20000;
    public const int MinRows = 20;

    public sealed record CsvRow(Guid EmployeeId, double[] Features, int? Label);

    /// <summary>CSV'yi ayrıştırır. Başlık tam olarak employee_id + 6 özellik (+ label) olmalıdır;
    /// ek sütun (ad, cinsiyet, yaş...) kabul edilmez. Ondalık ayırıcı nokta.</summary>
    public static (List<CsvRow> Rows, List<string> Errors) ParseCsv(string? csv)
    {
        var rows = new List<CsvRow>();
        var errors = new List<string>();
        var lines = (csv ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) { errors.Add("CSV boş."); return (rows, errors); }
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var expected = new HashSet<string>(Features) { "employee_id" };
        var unexpected = header.Where(h => !expected.Contains(h) && h != "label").ToList();
        var missing = expected.Where(e => !header.Contains(e)).ToList();
        if (unexpected.Count > 0) errors.Add("Beklenmeyen sütun: " + string.Join(", ", unexpected));
        if (missing.Count > 0) errors.Add("Eksik sütun: " + string.Join(", ", missing));
        if (header.Distinct().Count() != header.Length) errors.Add("Tekrarlanan sütun var.");
        if (errors.Count > 0) return (rows, errors);
        if (lines.Count - 1 > MaxRows) { errors.Add($"En fazla {MaxRows} satır yüklenebilir."); return (rows, errors); }

        var idIdx = Array.IndexOf(header, "employee_id");
        var labelIdx = Array.IndexOf(header, "label");
        var featIdx = Features.Select(f => Array.IndexOf(header, f)).ToArray();
        for (var i = 1; i < lines.Count; i++)
        {
            var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length != header.Length) { errors.Add($"Satır {i + 1}: sütun sayısı başlıkla uyuşmuyor."); continue; }
            if (!Guid.TryParse(cells[idIdx], out var id)) { errors.Add($"Satır {i + 1}: employee_id geçersiz."); continue; }
            var values = new double[Features.Length];
            var ok = true;
            for (var f = 0; f < Features.Length; f++)
            {
                if (!double.TryParse(cells[featIdx[f]], NumberStyles.Float, CultureInfo.InvariantCulture, out values[f]) || !double.IsFinite(values[f]))
                { errors.Add($"Satır {i + 1}: {Features[f]} sayı olmalı."); ok = false; break; }
            }
            if (!ok) continue;
            int? label = null;
            if (labelIdx >= 0 && cells[labelIdx].Length > 0)
            {
                if (cells[labelIdx] is "0" or "1") label = cells[labelIdx] == "1" ? 1 : 0;
                else { errors.Add($"Satır {i + 1}: label 0 ya da 1 olmalı."); continue; }
            }
            rows.Add(new CsvRow(id, values, label));
        }
        if (errors.Count > 20) errors = errors.Take(20).Append($"… ve {errors.Count - 20} hata daha").ToList();
        return (rows, errors);
    }

    /// <summary>Kıdem bandı (işe giriş tarihinden): 0-1, 1-3, 3-5, 5-10, 10+ yıl.</summary>
    public static string? TenureBand(DateOnly hireDate, DateOnly today)
    {
        if (hireDate > today) return null;
        var years = (today.DayNumber - hireDate.DayNumber) / 365.25;
        return years switch { < 1 => "0-1", < 3 => "1-3", < 5 => "3-5", < 10 => "5-10", _ => "10+" };
    }

    /// <summary>
    /// ml-inference /model/fairness satırları: özellikler + (varsa) etiket + grup bilgisi.
    /// Kimlik eklenmez. Dizinde bulunmayan ya da tekrarlanan çalışanlar atlanır (sayısı döner).
    /// </summary>
    public static (List<Dictionary<string, object?>> Rows, int Skipped) BuildRows(
        IEnumerable<CsvRow> rows, IReadOnlyDictionary<Guid, Person> people, DateOnly today)
    {
        var output = new List<Dictionary<string, object?>>();
        var seen = new HashSet<Guid>();
        var skipped = 0;
        foreach (var r in rows)
        {
            if (!seen.Add(r.EmployeeId) || !people.TryGetValue(r.EmployeeId, out var p)) { skipped++; continue; }
            var row = new Dictionary<string, object?>();
            for (var f = 0; f < Features.Length; f++) row[Features[f]] = r.Features[f];
            if (r.Label is { } l) row["label"] = l;
            row["groups"] = new Dictionary<string, string?>
            {
                ["department"] = string.IsNullOrWhiteSpace(p.Department) ? null : p.Department,
                ["tenure_band"] = TenureBand(p.HireDate, today),
            };
            output.Add(row);
        }
        return (output, skipped);
    }

    /// <summary>İK'nın seçtiği inceleme eşiği 0,01–0,99 arasında, en fazla 3 ondalık.</summary>
    public static bool ValidThreshold(decimal t) => t is >= 0.01m and <= 0.99m && decimal.Round(t, 3) == t;
}
