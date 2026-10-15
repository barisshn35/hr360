using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TenantService.Directory;

/// <summary>SCIM 2.0 (RFC 7643/7644) sabitleri - yalnizca desteklenen alt kume.</summary>
public static class ScimSchemas
{
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string EnterpriseUser = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
}

/// <summary>SCIM hatasi: HTTP durum + scimType (RFC 7644 3.12).</summary>
public sealed class ScimException : Exception
{
    public int Status { get; }
    public string? ScimType { get; }
    public ScimException(int status, string? scimType, string detail) : base(detail)
    {
        Status = status;
        ScimType = scimType;
    }

    public static ScimException Invalid(string detail) => new(400, "invalidValue", detail);
    public static ScimException Uniqueness(string detail) => new(409, "uniqueness", detail);
}

/// <summary>
/// Desteklenen filtre bicimi: <c>&lt;nitelik&gt; eq "&lt;deger&gt;"</c> (tek kosul).
/// IdP'lerin (Entra ID, Okta, OneLogin) kullanici eslestirmede gonderdigi bicim budur.
/// Diger operatorler/birlesik kosullar 400 invalidFilter doner.
/// </summary>
public static class ScimFilter
{
    public sealed record Condition(string Attribute, string Value);

    private static readonly Regex Pattern = new(
        @"^\s*(?<attr>[A-Za-z][A-Za-z0-9._:-]*)\s+(?<op>[A-Za-z]{2})\s+(?<val>""(?:[^""\\]|\\.)*""|true|false)\s*$",
        RegexOptions.CultureInvariant);

    public static Condition? Parse(string? filter, IReadOnlyCollection<string> allowedAttributes)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        if (filter.Length > 512) throw new ScimException(400, "invalidFilter", "Filtre çok uzun");
        var m = Pattern.Match(filter);
        if (!m.Success)
            throw new ScimException(400, "invalidFilter", "Yalnızca '<nitelik> eq \"değer\"' biçimindeki filtreler destekleniyor");
        if (!m.Groups["op"].Value.Equals("eq", StringComparison.OrdinalIgnoreCase))
            throw new ScimException(400, "invalidFilter", "Yalnızca 'eq' operatörü destekleniyor");
        var attr = allowedAttributes.FirstOrDefault(a => a.Equals(m.Groups["attr"].Value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ScimException(400, "invalidFilter", $"'{m.Groups["attr"].Value}' niteliğine göre filtreleme desteklenmiyor");
        var raw = m.Groups["val"].Value;
        string value = raw.StartsWith('"') ? Unescape(raw[1..^1]) : raw;
        return new Condition(attr, value);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// SCIM User kaynaginin HR360'in sakladigi alt kumesi. KVKK veri minimizasyonu: YALNIZCA
/// userName, name.givenName, name.familyName, birincil e-posta (emails[primary]), active,
/// title ve (enterprise uzantisi) department okunur. externalId yalnizca IdP eslestirmesi icin
/// tutulan teknik bir kimliktir. Telefon, adres, fotograf, yonetici, calisan no, dogum tarihi vb.
/// gelen tum diger nitelikler YOK SAYILIR ve hicbir yerde saklanmaz.
/// </summary>
public sealed class ScimUserDraft
{
    public string? UserName { get; set; }
    public string? ExternalId { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Email { get; set; }
    public string? Title { get; set; }
    public string? Department { get; set; }
    public bool Active { get; set; } = true;

    public ScimUserDraft Clone() => (ScimUserDraft)MemberwiseClone();
}

public static class ScimUserMapper
{
    /// <summary>Saklanan nitelikler (ServiceProviderConfig ve Schemas'ta da bu liste ilan edilir).</summary>
    public static readonly string[] StoredAttributes =
    {
        "userName", "name.givenName", "name.familyName", "emails[primary].value", "active", "title",
        ScimSchemas.EnterpriseUser + ":department", "externalId",
    };

    public const string MinimisationNoticeTr =
        "KVKK veri minimizasyonu: yalnızca userName, name.givenName, name.familyName, birincil e-posta, active, " +
        "title ve department (enterprise uzantısı) saklanır. Telefon, adres, fotoğraf, yönetici, çalışan numarası " +
        "gibi diğer tüm nitelikler kabul edilir ancak yok sayılır ve saklanmaz.";

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);

    /// <summary>POST/PUT govdesinden taslak olusturur (bilinmeyen nitelikler yok sayilir).</summary>
    public static ScimUserDraft FromResource(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw ScimException.Invalid("Gövde bir JSON nesnesi olmalı");
        var d = new ScimUserDraft();
        foreach (var prop in body.EnumerateObject())
            ApplyAttribute(d, prop.Name, prop.Value, remove: false);
        return d;
    }

    /// <summary>
    /// Tek bir nitelik yolunu taslaga uygular. Yol nokta/uzanti bicimlerini kabul eder:
    /// "name.givenName", "emails[type eq \"work\"].value", "urn:...:enterprise:2.0:User:department".
    /// Bilinmeyen yollar sessizce yok sayilir (IdP'ler cok sayida nitelik gonderir).
    /// </summary>
    public static void ApplyAttribute(ScimUserDraft d, string path, JsonElement value, bool remove)
    {
        var p = path.Trim();
        var urn = ScimSchemas.EnterpriseUser;
        if (p.Equals(urn, StringComparison.OrdinalIgnoreCase))
        {
            if (remove) { d.Department = null; return; }
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var sub in value.EnumerateObject())
                    if (sub.Name.Equals("department", StringComparison.OrdinalIgnoreCase)) d.Department = AsString(sub.Value);
            return;
        }
        if (p.StartsWith(urn + ":", StringComparison.OrdinalIgnoreCase))
        {
            // Enterprise uzantisindaki employeeNumber, costCenter, manager vb. yok sayilir.
            if (p[(urn.Length + 1)..].Equals("department", StringComparison.OrdinalIgnoreCase))
                d.Department = remove ? null : AsString(value);
            return;
        }
        // Cekirdek sema URN'si ile nitelendirilmis yol: "urn:...:core:2.0:User:name.givenName".
        if (p.StartsWith(ScimSchemas.User + ":", StringComparison.OrdinalIgnoreCase))
            p = p[(ScimSchemas.User.Length + 1)..];

        var lower = p.ToLowerInvariant();
        switch (lower)
        {
            case "username":
                d.UserName = remove ? null : AsString(value);
                return;
            case "externalid":
                d.ExternalId = remove ? null : AsString(value);
                return;
            case "active":
                if (remove) throw ScimException.Invalid("'active' kaldırılamaz");
                d.Active = AsBool(value) ?? throw ScimException.Invalid("'active' true/false olmalı");
                return;
            case "title":
                d.Title = remove ? null : AsString(value);
                return;
            case "name":
                if (remove) { d.GivenName = null; d.FamilyName = null; return; }
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var sub in value.EnumerateObject())
                        if (sub.Name.Equals("givenName", StringComparison.OrdinalIgnoreCase) || sub.Name.Equals("familyName", StringComparison.OrdinalIgnoreCase))
                            ApplyAttribute(d, "name." + sub.Name, sub.Value, false);
                return;
            case "name.givenname":
                d.GivenName = remove ? null : AsString(value);
                return;
            case "name.familyname":
                d.FamilyName = remove ? null : AsString(value);
                return;
            case "emails":
                if (remove) throw ScimException.Invalid("E-posta zorunlu; kaldırılamaz");
                d.Email = PickPrimary(value) ?? d.Email;
                return;
        }

        // Cok degerli alt yol: emails[type eq "work"].value / emails[primary eq true].value
        if (lower.StartsWith("emails[") && (lower.EndsWith("].value") || lower.EndsWith("]")))
        {
            if (remove) throw ScimException.Invalid("E-posta zorunlu; kaldırılamaz");
            d.Email = value.ValueKind == JsonValueKind.Object ? PickPrimary(value) : AsString(value);
        }
        // Diger nitelikler (phoneNumbers, addresses, photos, nickName, x509...) yok sayilir: KVKK minimizasyon.
    }

    /// <summary>emails dizisinden birincil > work > ilk degeri secer (yalnizca TEK adres saklanir).</summary>
    public static string? PickPrimary(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object)
            return value.TryGetProperty("value", out var v) ? AsString(v) : null;
        if (value.ValueKind != JsonValueKind.Array) return null;
        JsonElement? primary = null, work = null, first = null;
        foreach (var el in value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("value", out _)) continue;
            first ??= el;
            if (el.TryGetProperty("primary", out var pr) && AsBool(pr) == true) primary ??= el;
            if (el.TryGetProperty("type", out var t) && string.Equals(t.GetString(), "work", StringComparison.OrdinalIgnoreCase)) work ??= el;
        }
        var pick = primary ?? work ?? first;
        return pick is { } e ? AsString(e.GetProperty("value")) : null;
    }

    private static string? AsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()?.Trim(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>Entra ID "active" degerini "False"/"True" metni olarak gonderir; ikisi de kabul edilir.</summary>
    public static bool? AsBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
        _ => null,
    };

    /// <summary>Zorunlu alanlari dogrular ve uzunluklari sinirlar.</summary>
    public static void Validate(ScimUserDraft d)
    {
        d.UserName = d.UserName?.Trim();
        if (string.IsNullOrWhiteSpace(d.UserName))
            throw ScimException.Invalid("userName zorunlu");
        if (d.UserName.Length > 256) throw ScimException.Invalid("userName çok uzun");
        if (string.IsNullOrWhiteSpace(d.Email) && EmailRegex.IsMatch(d.UserName)) d.Email = d.UserName;
        d.Email = d.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(d.Email) || !EmailRegex.IsMatch(d.Email) || d.Email.Length > 254)
            throw ScimException.Invalid("Geçerli bir iş e-postası (emails) zorunlu");
        if (string.IsNullOrWhiteSpace(d.GivenName) || string.IsNullOrWhiteSpace(d.FamilyName))
            throw ScimException.Invalid("name.givenName ve name.familyName zorunlu");
        d.GivenName = Trunc(d.GivenName.Trim(), 100);
        d.FamilyName = Trunc(d.FamilyName.Trim(), 100);
        d.Title = string.IsNullOrWhiteSpace(d.Title) ? null : Trunc(d.Title.Trim(), 200);
        d.Department = string.IsNullOrWhiteSpace(d.Department) ? null : Trunc(d.Department.Trim(), 200);
        d.ExternalId = string.IsNullOrWhiteSpace(d.ExternalId) ? null : Trunc(d.ExternalId.Trim(), 256);
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>
/// SCIM PATCH (RFC 7644 3.5.2) - add/replace/remove. Hem yollu ("path":"active") hem yolsuz
/// ("value":{"active":false,"name.givenName":"X"}) bicimler desteklenir (Entra ID yolsuz,
/// buyuk harfli "Replace" gonderir).
/// </summary>
public static class ScimPatch
{
    public sealed record Operation(string Op, string? Path, JsonElement Value);

    public static List<Operation> ParseOperations(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !TryGet(body, "Operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
            throw ScimException.Invalid("PATCH gövdesinde 'Operations' dizisi olmalı");
        var list = new List<Operation>();
        foreach (var op in ops.EnumerateArray())
        {
            if (!TryGet(op, "op", out var o) || o.ValueKind != JsonValueKind.String)
                throw ScimException.Invalid("Her işlemde 'op' olmalı");
            var name = o.GetString()!.Trim().ToLowerInvariant();
            if (name is not ("add" or "replace" or "remove"))
                throw ScimException.Invalid($"Desteklenmeyen işlem: {name}");
            var path = TryGet(op, "path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            TryGet(op, "value", out var v);
            if (name != "remove" && v.ValueKind == JsonValueKind.Undefined)
                throw ScimException.Invalid("'value' zorunlu");
            if (name == "remove" && string.IsNullOrWhiteSpace(path))
                throw new ScimException(400, "noTarget", "'remove' için 'path' zorunlu");
            list.Add(new Operation(name, path, v));
        }
        if (list.Count == 0) throw ScimException.Invalid("En az bir işlem gerekli");
        if (list.Count > 100) throw ScimException.Invalid("Tek istekte en fazla 100 işlem");
        return list;
    }

    /// <summary>Islemleri kullanici taslagina uygular (yeni taslak doner, girdi degismez).</summary>
    public static ScimUserDraft ApplyToUser(ScimUserDraft current, IEnumerable<Operation> ops)
    {
        var d = current.Clone();
        foreach (var op in ops)
        {
            if (string.IsNullOrWhiteSpace(op.Path))
            {
                if (op.Value.ValueKind != JsonValueKind.Object)
                    throw ScimException.Invalid("Yolsuz işlemde 'value' bir nesne olmalı");
                foreach (var prop in op.Value.EnumerateObject())
                    ScimUserMapper.ApplyAttribute(d, prop.Name, prop.Value, remove: false);
            }
            else
            {
                ScimUserMapper.ApplyAttribute(d, op.Path!, op.Value, remove: op.Op == "remove");
            }
        }
        return d;
    }

    private static bool TryGet(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}

