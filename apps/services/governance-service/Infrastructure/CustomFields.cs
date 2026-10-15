using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GovernanceService.Infrastructure;

/// <summary>
/// Y24 özel alanlar (form tasarımcısı). İK çalışan profiline alan ekler; her alanın KVKK
/// üst verisi (özel nitelikli mi, hukuki sebep, amaç, saklama süresi) oluştururken ZORUNLUDUR.
/// Özel nitelikli alan onaylı gizlilik etki değerlendirmesi (governance_privacy_assessments,
/// Kind=CustomField) ister, değeri şifreli tutulur ve yalnızca İK / kişinin kendisi görebilir.
/// Her alan KVKK işleme envanterinde kendiliğinden bir faaliyet olarak görünür.
/// </summary>
public static class CustomFields
{
    public static readonly string[] Types = { "text", "number", "date", "select", "boolean" };
    /// <summary>G20 alan düzeyinde yetki düzeyleri (engagement-service FieldPolicies ile aynı anlam).</summary>
    public static readonly string[] Levels = { "everyone", "manager", "hr", "self" };
    public const string EncPrefix = "enc1:";

    public sealed record Basis(string Code, string Label, bool Special);

    /// <summary>KVKK m.5 (genel) ve m.6 (özel nitelikli; 7499 s. K. ile değişik) işleme şartları.</summary>
    public static readonly Basis[] LegalBases =
    {
        new("m5-1", "m.5/1 açık rıza", false),
        new("m5-2-a", "m.5/2-a kanunlarda açıkça öngörülmesi", false),
        new("m5-2-b", "m.5/2-b fiili imkânsızlık (hayatın korunması)", false),
        new("m5-2-c", "m.5/2-c sözleşmenin kurulması veya ifası", false),
        new("m5-2-ch", "m.5/2-ç hukuki yükümlülüğün yerine getirilmesi", false),
        new("m5-2-d", "m.5/2-d ilgili kişinin kendisince alenileştirilmesi", false),
        new("m5-2-e", "m.5/2-e bir hakkın tesisi, kullanılması veya korunması", false),
        new("m5-2-f", "m.5/2-f veri sorumlusunun meşru menfaati", false),
        new("m6-2", "m.6/2 açık rıza (özel nitelikli)", true),
        new("m6-3-a", "m.6/3-a kanunlarda açıkça öngörülmesi", true),
        new("m6-3-b", "m.6/3-b fiili imkânsızlık (hayatın korunması)", true),
        new("m6-3-c", "m.6/3-c ilgili kişinin alenileştirmesi", true),
        new("m6-3-ch", "m.6/3-ç bir hakkın tesisi, kullanılması veya korunması", true),
        new("m6-3-d", "m.6/3-d kamu sağlığı / tıbbi teşhis (sır saklama yükümlülüğü altındakilerce)", true),
        new("m6-3-e", "m.6/3-e istihdam, iş sağlığı ve güvenliği, sosyal güvenlik yükümlülükleri", true),
    };

    public static string BasisLabel(string code) => LegalBases.FirstOrDefault(b => b.Code == code)?.Label ?? code;

    public sealed record Definition(string Key, string Label, string Type, List<string>? Options, bool Required, string Visibility,
        bool SelfEditable, bool IsSpecialCategory, string LegalBasis, string Purpose, int RetentionMonths);

    /// <summary>Tanım doğrulaması. Hata varsa (tr, en) ileti, yoksa null.</summary>
    public static (string Tr, string En)? Validate(Definition d)
    {
        if (string.IsNullOrWhiteSpace(d.Key) || !Regex.IsMatch(d.Key, "^[a-z][a-z0-9_]{1,40}$"))
            return ("Anahtar küçük harfle başlamalı; yalnızca a-z, 0-9 ve _ (2–41 karakter).", "The key must start with a lowercase letter; only a-z, 0-9 and _ (2–41 characters).");
        if (string.IsNullOrWhiteSpace(d.Label) || d.Label.Trim().Length > 120)
            return ("Etiket 1–120 karakter olmalı.", "The label must be 1–120 characters.");
        if (!Types.Contains(d.Type)) return ("Geçersiz alan türü.", "Invalid field type.");
        if (d.Type == "select")
        {
            var opts = (d.Options ?? new()).Select(o => o?.Trim() ?? "").Where(o => o.Length > 0).ToList();
            if (opts.Count < 2 || opts.Count > 50 || opts.Any(o => o.Length > 80) || opts.Distinct().Count() != opts.Count)
                return ("Seçim alanı 2–50 farklı seçenek ister (en fazla 80 karakter).", "A select field needs 2–50 distinct options (max 80 characters).");
        }
        if (!Levels.Contains(d.Visibility)) return ("Geçersiz görünürlük düzeyi.", "Invalid visibility level.");
        if (string.IsNullOrWhiteSpace(d.LegalBasis) || LegalBases.All(b => b.Code != d.LegalBasis))
            return ("KVKK hukuki sebebi seçilmeli (m.5 / m.6).", "A KVKK legal basis must be chosen (art. 5 / art. 6).");
        var basis = LegalBases.First(b => b.Code == d.LegalBasis);
        if (d.IsSpecialCategory && !basis.Special)
            return ("Özel nitelikli veri yalnızca KVKK m.6 şartlarıyla işlenebilir.", "Special category data may only be processed under KVKK art. 6 conditions.");
        if (!d.IsSpecialCategory && basis.Special)
            return ("m.6 şartları yalnızca özel nitelikli alanlar içindir; m.5 şartı seçin.", "Art. 6 conditions are for special category fields only; choose an art. 5 condition.");
        if (string.IsNullOrWhiteSpace(d.Purpose) || d.Purpose.Trim().Length < 5 || d.Purpose.Trim().Length > 500)
            return ("İşleme amacı 5–500 karakter olmalı.", "The processing purpose must be 5–500 characters.");
        if (d.RetentionMonths is < 1 or > 240)
            return ("Saklama süresi 1–240 ay olmalı.", "Retention must be 1–240 months.");
        // Veri en aza indirme: özel nitelikli veri tüm çalışanlara ya da yöneticiye açılamaz.
        if (d.IsSpecialCategory && d.Visibility is not ("hr" or "self"))
            return ("Özel nitelikli alan yalnızca İK ya da kişinin kendisi tarafından görülebilir.", "A special category field can only be visible to HR or the person.");
        return null;
    }

    /// <summary>viewer: self | hr | manager | other</summary>
    public static bool CanSee(string fieldLevel, string viewer) => viewer switch
    {
        "self" => true,
        "hr" => fieldLevel is "everyone" or "manager" or "hr",
        "manager" => fieldLevel is "everyone" or "manager",
        _ => fieldLevel == "everyone",
    };

    /// <summary>Değeri türüne göre doğrular ve kanonik metne çevirir. Boş değer = sil.</summary>
    public static (bool Ok, string? Value, string? ErrTr, string? ErrEn) Normalize(string type, IReadOnlyList<string>? options, JsonElement? raw)
    {
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return (true, null, null, null);
        var v = raw.Value;
        string? s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        if (s is null) return (false, null, "Geçersiz değer.", "Invalid value.");
        s = s.Trim();
        if (s.Length == 0) return (true, null, null, null);
        switch (type)
        {
            case "text":
                return s.Length > 500 ? (false, null, "Metin en fazla 500 karakter.", "Text must be at most 500 characters.") : (true, s, null, null);
            case "number":
                var norm = s.Replace(" ", "");
                if (norm.Contains(',') && !norm.Contains('.')) norm = norm.Replace(',', '.');
                return decimal.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < 1_000_000_000_000m
                    ? (true, d.ToString(CultureInfo.InvariantCulture), null, null)
                    : (false, null, "Geçerli bir sayı girin.", "Enter a valid number.");
            case "date":
                return DateOnly.TryParseExact(s.Length > 10 ? s[..10] : s, "yyyy-MM-dd", out var dt) && dt.Year is > 1900 and < 2200
                    ? (true, dt.ToString("yyyy-MM-dd"), null, null)
                    : (false, null, "Tarih YYYY-AA-GG biçiminde olmalı.", "The date must be YYYY-MM-DD.");
            case "boolean":
                return s.ToLowerInvariant() switch
                {
                    "true" or "1" or "evet" or "yes" => (true, "true", null, null),
                    "false" or "0" or "hayir" or "hayır" or "no" => (true, "false", null, null),
                    _ => (false, null, "Evet/hayır değeri bekleniyor.", "A yes/no value is expected."),
                };
            case "select":
                return options is not null && options.Contains(s) ? (true, s, null, null) : (false, null, "Seçeneklerden biri seçilmeli.", "Choose one of the options.");
            default:
                return (false, null, "Bilinmeyen alan türü.", "Unknown field type.");
        }
    }

    /// <summary>Özel nitelikli değer: "enc1:" + şifreli (anahtar yenilendikten sonra "enc2:&lt;kimlik&gt;:"), bkz. Security.KeyRing.</summary>
    public static string Seal(bool special, string value) => special ? GovernanceService.Security.KeyRing.Seal(value, EncPrefix) : value;
    public static string Open(string stored) => stored == EncPrefix ? "" : GovernanceService.Security.KeyRing.IsSealed(stored) ? SecretBox.Unprotect(stored) ?? "" : stored;

    /// <summary>JSON değer: number/boolean türleri tipli döner.</summary>
    public static object? Typed(string type, string? value) => value is null ? null : type switch
    {
        "number" => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : value,
        "boolean" => value == "true",
        _ => value,
    };

    public sealed record FieldRow(Guid Id, string Key, string Label, string Type, List<string> Options, bool Required, string Visibility,
        bool SelfEditable, bool IsSpecialCategory, string LegalBasis, string Purpose, int RetentionMonths, Guid? AssessmentId,
        bool IsActive, int SortOrder, string CreatedBy, DateTime CreatedAt);

    public const string Columns = "\"Id\",\"Key\",\"Label\",\"Type\",\"Options\"::text,\"Required\",\"Visibility\",\"SelfEditable\",\"IsSpecialCategory\",\"LegalBasis\",\"Purpose\",\"RetentionMonths\",\"AssessmentId\",\"IsActive\",\"SortOrder\",\"CreatedBy\",\"CreatedAt\"";

    public static FieldRow Map(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? new(), r.GetBoolean(5), r.GetString(6), r.GetBoolean(7), r.GetBoolean(8),
        r.GetString(9), r.GetString(10), r.GetInt32(11), r.GuidOrNull(12), r.GetBoolean(13), r.GetInt32(14), r.GetString(15), r.GetFieldValue<DateTime>(16));

    public static Task<List<FieldRow>> ListAsync(Sql sql, string tenant, CancellationToken ct, bool activeOnly = false) =>
        sql.QueryAsync($"SELECT {Columns} FROM governance_custom_fields WHERE \"TenantSlug\" = $1 AND \"Target\" = 'Employee'{(activeOnly ? " AND \"IsActive\"" : "")} ORDER BY \"SortOrder\", \"CreatedAt\"",
            Map, ct, tenant);

    /// <summary>KVKK işleme envanterine eklenecek dinamik faaliyet (alan başına bir satır).</summary>
    public static ProcessingActivity Activity(FieldRow f)
    {
        var recipients = f.Visibility switch
        {
            "everyone" => new[] { "Tüm çalışanlar (şirket içi)" },
            "manager" => new[] { "Bölüm yöneticisi", "İK" },
            "hr" => new[] { "İK" },
            _ => new[] { "Yalnızca kişinin kendisi (İK yönetir)" },
        };
        return new ProcessingActivity(
            $"custom-field-{f.Key}", "Özel alanlar", $"Özel alan: {f.Label}", new[] { "Çalışanlar" },
            new[] { f.IsSpecialCategory ? $"{f.Label} (özel nitelikli)" : f.Label },
            f.Purpose, BasisLabel(f.LegalBasis), f.IsSpecialCategory,
            $"İşten ayrılıştan {f.RetentionMonths} ay sonra otomatik silinir", "CustomFieldValues",
            recipients, Array.Empty<string>(),
            (f.IsSpecialCategory ? "Değer AES-256-GCM şifreli; onaylı gizlilik etki değerlendirmesi var; İK görüntülemeleri kaydedilir; " : "")
            + $"Görünürlük düzeyi: {f.Visibility}{(f.IsActive ? "" : " (alan pasif)")}");
    }

    /// <summary>
    /// Saklama: ayrılmış çalışanların değerleri, alanın saklama süresi (ay) dolunca silinir.
    /// Ayrılış tarihi = son görevlendirmenin bitişi (yoksa kayıt tarihi), diğer imha işleriyle aynı.
    /// </summary>
    public static async Task<List<(string Tenant, int Count)>> PurgeAsync(Sql sql, string? tenant, CancellationToken ct)
    {
        var rows = await sql.QueryAsync("""
            WITH del AS (
                DELETE FROM governance_custom_field_values v USING governance_custom_fields f, employee_employees e
                WHERE v."FieldId" = f."Id" AND e."Id" = v."EmployeeId" AND e."Status" = 'Terminated'
                  AND ($1::text IS NULL OR v."TenantSlug" = $1)
                  AND coalesce((SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id"), e."CreatedAt"::date)
                      < (now() - make_interval(months => f."RetentionMonths"))::date
                RETURNING v."TenantSlug")
            SELECT "TenantSlug", count(*)::int FROM del GROUP BY 1
            """, r => (r.GetString(0), r.GetInt32(1)), ct, tenant);
        return rows;
    }
}
