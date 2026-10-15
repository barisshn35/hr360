using System.Text.RegularExpressions;

namespace TenantService.Directory;

/// <summary>
/// LDAP/AD nitelik eslemesi. KVKK veri minimizasyonu: dizinden YALNIZCA burada izin verilen
/// nitelikler istenir (sunucuya gonderilen nitelik listesi de bununla sinirli): kullanici adi,
/// e-posta, ad (givenName), soyad (sn), unvan, departman, pasiflik bayragi ve kalici kimlik.
/// Telefon, adres, yonetici, fotograf vb. hic okunmaz.
/// </summary>
public sealed record LdapAttributeMap(
    string UsernameAttr, string EmailAttr, string? DepartmentAttr, string? DisabledAttr, string? TitleAttr = null)
{
    public const string GivenNameAttr = "givenName";
    public const string SurnameAttr = "sn";

    public static readonly string[] AllowedUsername = { "uid", "sAMAccountName", "userPrincipalName", "cn" };
    public static readonly string[] AllowedEmail = { "mail", "userPrincipalName" };
    public static readonly string[] AllowedDepartment = { "department", "departmentNumber", "ou" };
    public static readonly string[] AllowedTitle = { "title" };
    /// <summary>userAccountControl (AD, bit 2 = kapali), nsAccountLock (389-DS), pwdAccountLockedTime (OpenLDAP ppolicy).</summary>
    public static readonly string[] AllowedDisabled = { "userAccountControl", "nsAccountLock", "pwdAccountLockedTime" };
    /// <summary>Kalici dis kimlik: OpenLDAP entryUUID, AD objectGUID; yoksa DN.</summary>
    public static readonly string[] IdentityAttrs = { "entryUUID", "objectGUID" };

    /// <summary>Gecersiz secim varsa Turkce hata, yoksa null.</summary>
    public string? Validate()
    {
        if (!AllowedUsername.Contains(UsernameAttr)) return "Kullanıcı adı niteliği uid, sAMAccountName, userPrincipalName ya da cn olmalı";
        if (!AllowedEmail.Contains(EmailAttr)) return "E-posta niteliği mail ya da userPrincipalName olmalı";
        if (DepartmentAttr is not null && !AllowedDepartment.Contains(DepartmentAttr)) return "Departman niteliği department, departmentNumber ya da ou olmalı";
        if (DisabledAttr is not null && !AllowedDisabled.Contains(DisabledAttr)) return "Pasiflik niteliği userAccountControl, nsAccountLock ya da pwdAccountLockedTime olmalı";
        if (TitleAttr is not null && !AllowedTitle.Contains(TitleAttr)) return "Unvan niteliği title olmalı";
        return null;
    }

    /// <summary>LDAP aramasinda istenecek nitelikler (fazlasi istenmez).</summary>
    public string[] RequestedAttributes()
    {
        var list = new List<string> { UsernameAttr, EmailAttr, GivenNameAttr, SurnameAttr };
        if (DepartmentAttr is not null) list.Add(DepartmentAttr);
        if (TitleAttr is not null) list.Add(TitleAttr);
        if (DisabledAttr is not null) list.Add(DisabledAttr);
        list.AddRange(IdentityAttrs);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>Dizinden okunmus tek kayit (yalnizca izinli nitelikler).</summary>
public sealed record LdapEntry(string Dn, IReadOnlyDictionary<string, string> Attributes);

/// <summary>Esleme sonucu: gecerli kullanici girdisi ya da atlama nedeni.</summary>
public sealed record LdapMappedUser(
    string Dn, string ExternalId, string UserName, string? Email, string? GivenName, string? FamilyName,
    string? Department, bool Disabled, string? SkipReason, string? Title = null);

public static class LdapMapper
{
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);

    public static LdapMappedUser Map(LdapEntry e, LdapAttributeMap map)
    {
        string? Get(string? name) =>
            name is null ? null
            : e.Attributes.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v.Trim() : null;

        var externalId = Get("entryUUID") ?? Get("objectGUID") ?? e.Dn;
        var userName = Get(map.UsernameAttr);
        var email = Get(map.EmailAttr)?.ToLowerInvariant();
        var given = Get(LdapAttributeMap.GivenNameAttr);
        var family = Get(LdapAttributeMap.SurnameAttr);
        var dept = Get(map.DepartmentAttr);
        var title = Get(map.TitleAttr);
        var disabled = IsDisabled(map.DisabledAttr, Get(map.DisabledAttr));

        string? skip = null;
        if (string.IsNullOrWhiteSpace(userName)) skip = $"'{map.UsernameAttr}' niteliği boş";
        else if (string.IsNullOrWhiteSpace(email) || !EmailRegex.IsMatch(email)) skip = "Geçerli e-posta yok";
        else if (string.IsNullOrWhiteSpace(given) || string.IsNullOrWhiteSpace(family)) skip = "Ad (givenName) ya da soyad (sn) eksik";

        return new LdapMappedUser(e.Dn, externalId, userName ?? "", email, given, family, dept, disabled, skip, title);
    }

    /// <summary>Pasif hesap bayragi: AD userAccountControl bit 0x2 (ACCOUNTDISABLE), nsAccountLock=true, pwdAccountLockedTime dolu.</summary>
    public static bool IsDisabled(string? attr, string? value)
    {
        if (attr is null || string.IsNullOrWhiteSpace(value)) return false;
        return attr switch
        {
            "userAccountControl" => long.TryParse(value, out var uac) && (uac & 0x2) != 0,
            "nsAccountLock" => value.Equals("true", StringComparison.OrdinalIgnoreCase),
            "pwdAccountLockedTime" => true,
            _ => false,
        };
    }
}

/// <summary>Esitleme plani (dry-run onizlemesi de budur).</summary>
public enum SyncActionKind { Create, Update, Disable, Enable, Skip, Unchanged }

public sealed record SyncAction(SyncActionKind Kind, string UserName, string? Email, string? Detail, Guid? DirectoryUserId, LdapMappedUser? Source);

public sealed record SyncPlan(List<SyncAction> Actions, bool DisableGuardTriggered, string? Warning)
{
    public int Count(SyncActionKind k) => Actions.Count(a => a.Kind == k);
}

/// <summary>Var olan dizin kullanicilarinin plan icin gerekli ozeti.</summary>
public sealed record ExistingDirectoryUser(
    Guid Id, string? ExternalId, string UserName, string Email, string? GivenName, string? FamilyName,
    string? Department, bool Active, string? Title = null);

public static class SyncPlanner
{
    /// <summary>
    /// LDAP kayitlariyla mevcut (Source=ldap) dizin kullanicilarini karsilastirir.
    /// Guvenlik: dizinde artik bulunmayan kullanicilar kapatilir - ama arama HIC sonuc
    /// dondurmediyse ya da kapatilacak sayi esigi asiyorsa (yanlis filtre/base DN ile toplu
    /// kapatma riski) kapatmalar yapilmaz, uyari doner; yonetici "forceDisable" ile onaylar.
    /// </summary>
    public static SyncPlan Build(IReadOnlyList<LdapMappedUser> entries, IReadOnlyList<ExistingDirectoryUser> existing, bool forceDisable)
    {
        var actions = new List<SyncAction>();
        var byExt = existing.Where(x => x.ExternalId is not null)
            .GroupBy(x => x.ExternalId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byName = existing.GroupBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            if (e.SkipReason is not null)
            {
                actions.Add(new SyncAction(SyncActionKind.Skip, string.IsNullOrEmpty(e.UserName) ? e.Dn : e.UserName, e.Email, e.SkipReason, null, e));
                // Gecersiz kayit mevcut bir kullaniciya aitse, onu "dizinde yok" sayip kapatma.
                if (byExt.TryGetValue(e.ExternalId, out var sk)) seen.Add(sk.Id);
                else if (!string.IsNullOrEmpty(e.UserName) && byName.TryGetValue(e.UserName, out var sk2)) seen.Add(sk2.Id);
                continue;
            }
            if (!seenNames.Add(e.UserName))
            {
                actions.Add(new SyncAction(SyncActionKind.Skip, e.UserName, e.Email, "Aynı kullanıcı adı dizinde birden fazla kez geçiyor", null, e));
                continue;
            }
            var match = byExt.TryGetValue(e.ExternalId, out var m1) ? m1 : byName.TryGetValue(e.UserName, out var m2) ? m2 : null;
            if (match is null)
            {
                actions.Add(new SyncAction(e.Disabled ? SyncActionKind.Skip : SyncActionKind.Create, e.UserName, e.Email,
                    e.Disabled ? "Dizinde pasif; oluşturulmadı" : null, null, e));
                continue;
            }
            seen.Add(match.Id);
            if (e.Disabled)
            {
                actions.Add(match.Active
                    ? new SyncAction(SyncActionKind.Disable, e.UserName, e.Email, "Dizinde pasif", match.Id, e)
                    : new SyncAction(SyncActionKind.Unchanged, e.UserName, e.Email, null, match.Id, e));
                continue;
            }
            var changes = new List<string>();
            if (!string.Equals(match.Email, e.Email, StringComparison.OrdinalIgnoreCase)) changes.Add("e-posta");
            if (!string.Equals(match.GivenName, e.GivenName, StringComparison.Ordinal)) changes.Add("ad");
            if (!string.Equals(match.FamilyName, e.FamilyName, StringComparison.Ordinal)) changes.Add("soyad");
            if (!string.Equals(match.Department ?? "", e.Department ?? "", StringComparison.Ordinal)) changes.Add("departman");
            if (!string.Equals(match.Title ?? "", e.Title ?? "", StringComparison.Ordinal)) changes.Add("unvan");
            if (!string.Equals(match.ExternalId, e.ExternalId, StringComparison.Ordinal)) changes.Add("dış kimlik");
            if (!match.Active)
                actions.Add(new SyncAction(SyncActionKind.Enable, e.UserName, e.Email, changes.Count > 0 ? string.Join(", ", changes) : null, match.Id, e));
            else if (changes.Count > 0)
                actions.Add(new SyncAction(SyncActionKind.Update, e.UserName, e.Email, string.Join(", ", changes), match.Id, e));
            else
                actions.Add(new SyncAction(SyncActionKind.Unchanged, e.UserName, e.Email, null, match.Id, e));
        }

        var missing = existing.Where(x => x.Active && !seen.Contains(x.Id)).ToList();
        var activeCount = existing.Count(x => x.Active);
        var guard = false;
        string? warning = null;
        if (missing.Count > 0)
        {
            if (entries.Count == 0)
            {
                guard = true;
                warning = "Dizin araması hiç kayıt döndürmedi; güvenlik için hiçbir hesap kapatılmadı. Base DN ve filtreyi kontrol edin.";
            }
            else if (!forceDisable && missing.Count > Math.Max(5, activeCount / 2))
            {
                guard = true;
                warning = $"{missing.Count} hesap dizinde bulunamadı (aktiflerin yarısından fazlası). Toplu kapatmayı onaylamadan kapatma yapılmaz.";
            }
        }
        foreach (var x in missing)
            actions.Add(new SyncAction(guard ? SyncActionKind.Skip : SyncActionKind.Disable, x.UserName, x.Email,
                guard ? "Dizinde yok (toplu kapatma koruması)" : "Dizinde artık yok", x.Id, null));

        return new SyncPlan(actions, guard, warning);
    }
}
