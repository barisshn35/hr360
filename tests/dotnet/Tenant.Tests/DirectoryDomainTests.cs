using TenantService.Directory;
using TenantService.Domains;
using Xunit;

namespace Tenant.Tests;

public class LdapMappingTests
{
    private static readonly LdapAttributeMap Ad = new("sAMAccountName", "mail", "department", "userAccountControl");
    private static readonly LdapAttributeMap OpenLdap = new("uid", "mail", "ou", null);

    private static LdapEntry E(string dn, params (string, string)[] attrs) =>
        new(dn, attrs.ToDictionary(a => a.Item1, a => a.Item2, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Maps_active_directory_entry_with_disabled_flag()
    {
        var m = LdapMapper.Map(E("CN=Ali,OU=Users,DC=acme,DC=local",
            ("sAMAccountName", "ali"), ("mail", "Ali@Acme.com"), ("givenName", "Ali"), ("sn", "Veli"),
            ("department", "Satış"), ("userAccountControl", "514"), ("objectGUID", "6f1c...")), Ad);
        Assert.Equal("ali", m.UserName);
        Assert.Equal("ali@acme.com", m.Email);
        Assert.Equal("Satış", m.Department);
        Assert.True(m.Disabled);
        Assert.Equal("6f1c...", m.ExternalId);
        Assert.Null(m.SkipReason);
    }

    [Theory]
    [InlineData("userAccountControl", "512", false)]
    [InlineData("userAccountControl", "514", true)]
    [InlineData("userAccountControl", "66050", true)]
    [InlineData("nsAccountLock", "TRUE", true)]
    [InlineData("nsAccountLock", "false", false)]
    [InlineData("pwdAccountLockedTime", "20260101000000Z", true)]
    [InlineData(null, "514", false)]
    public void Disabled_flag_interpretation(string? attr, string value, bool expected) =>
        Assert.Equal(expected, LdapMapper.IsDisabled(attr, value));

    [Fact]
    public void Entry_uuid_preferred_then_dn()
    {
        Assert.Equal("uuid-1", LdapMapper.Map(E("uid=a,dc=x", ("uid", "a"), ("mail", "a@x.com"), ("givenName", "A"), ("sn", "B"), ("entryUUID", "uuid-1")), OpenLdap).ExternalId);
        Assert.Equal("uid=a,dc=x", LdapMapper.Map(E("uid=a,dc=x", ("uid", "a"), ("mail", "a@x.com"), ("givenName", "A"), ("sn", "B")), OpenLdap).ExternalId);
    }

    [Fact]
    public void Missing_email_or_names_are_skipped()
    {
        Assert.NotNull(LdapMapper.Map(E("uid=a,dc=x", ("uid", "a"), ("givenName", "A"), ("sn", "B")), OpenLdap).SkipReason);
        Assert.NotNull(LdapMapper.Map(E("uid=a,dc=x", ("uid", "a"), ("mail", "a@x.com"), ("sn", "B")), OpenLdap).SkipReason);
    }

    [Fact]
    public void Only_minimal_attributes_are_requested()
    {
        var attrs = Ad.RequestedAttributes();
        Assert.Contains("sAMAccountName", attrs);
        Assert.Contains("userAccountControl", attrs);
        Assert.DoesNotContain("telephoneNumber", attrs);
        Assert.DoesNotContain("homePostalAddress", attrs);
        Assert.Equal(attrs.Length, attrs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Title_is_mapped_only_when_configured()
    {
        var withTitle = new LdapAttributeMap("uid", "mail", "ou", null, "title");
        var e = E("uid=a,dc=x", ("uid", "a"), ("mail", "a@x.com"), ("givenName", "A"), ("sn", "B"), ("title", "Uzman"), ("telephoneNumber", "123"));
        Assert.Equal("Uzman", LdapMapper.Map(e, withTitle).Title);
        Assert.Null(LdapMapper.Map(e, OpenLdap).Title);
        Assert.Contains("title", withTitle.RequestedAttributes());
        Assert.DoesNotContain("title", OpenLdap.RequestedAttributes());
        Assert.NotNull(new LdapAttributeMap("uid", "mail", null, null, "mobile").Validate());
    }

    [Fact]
    public void Attribute_map_rejects_non_whitelisted_attributes()
    {
        Assert.NotNull(new LdapAttributeMap("telephoneNumber", "mail", null, null).Validate());
        Assert.NotNull(new LdapAttributeMap("uid", "homePhone", null, null).Validate());
        Assert.NotNull(new LdapAttributeMap("uid", "mail", "manager", null).Validate());
        Assert.Null(new LdapAttributeMap("uid", "mail", "departmentNumber", "nsAccountLock").Validate());
    }
}

public class SyncPlannerTests
{
    private static LdapMappedUser U(string name, bool disabled = false, string? ext = null, string? given = "A") =>
        new("uid=" + name, ext ?? "id-" + name, name, name + "@x.com", given, "B", null, disabled, null);

    private static ExistingDirectoryUser X(string name, bool active = true, string? given = "A") =>
        new(Guid.NewGuid(), "id-" + name, name, name + "@x.com", given, "B", null, active);

    [Fact]
    public void Creates_updates_disables_and_enables()
    {
        var existing = new[] { X("a"), X("b", given: "Old"), X("c"), X("d", active: false), X("e"), X("f"), X("g"), X("h") };
        var entries = new[] { U("a"), U("b"), U("c", disabled: true), U("d"), U("new"), U("f"), U("g"), U("h") };
        var plan = SyncPlanner.Build(entries, existing, forceDisable: false);
        Assert.Equal(1, plan.Count(SyncActionKind.Create));
        Assert.Equal(1, plan.Count(SyncActionKind.Update));
        Assert.Equal(1, plan.Count(SyncActionKind.Enable));
        // c dizinde pasif + e dizinde yok
        Assert.Equal(2, plan.Count(SyncActionKind.Disable));
        Assert.False(plan.DisableGuardTriggered);
    }

    [Fact]
    public void Empty_search_never_disables()
    {
        var plan = SyncPlanner.Build(Array.Empty<LdapMappedUser>(), new[] { X("a"), X("b") }, forceDisable: true);
        Assert.True(plan.DisableGuardTriggered);
        Assert.Equal(0, plan.Count(SyncActionKind.Disable));
        Assert.NotNull(plan.Warning);
    }

    [Fact]
    public void Mass_disable_requires_confirmation()
    {
        var existing = Enumerable.Range(0, 20).Select(i => X("u" + i)).ToArray();
        var entries = new[] { U("u0"), U("u1") };
        var guarded = SyncPlanner.Build(entries, existing, forceDisable: false);
        Assert.True(guarded.DisableGuardTriggered);
        Assert.Equal(0, guarded.Count(SyncActionKind.Disable));
        var forced = SyncPlanner.Build(entries, existing, forceDisable: true);
        Assert.Equal(18, forced.Count(SyncActionKind.Disable));
    }

    [Fact]
    public void Matches_by_external_id_even_if_username_changed()
    {
        var existing = new[] { X("old") };
        var plan = SyncPlanner.Build(new[] { U("renamed", ext: "id-old") }, existing, false);
        Assert.Equal(0, plan.Count(SyncActionKind.Create));
        Assert.Equal(0, plan.Count(SyncActionKind.Disable));
    }

    [Fact]
    public void Disabled_new_entries_are_not_created()
    {
        var plan = SyncPlanner.Build(new[] { U("x", disabled: true) }, Array.Empty<ExistingDirectoryUser>(), false);
        Assert.Equal(0, plan.Count(SyncActionKind.Create));
        Assert.Equal(1, plan.Count(SyncActionKind.Skip));
    }
}

public class LdapUrlTests
{
    private static readonly string[] None = Array.Empty<string>();

    [Fact]
    public void Ldaps_default_port_without_warning()
    {
        var r = LdapUrl.Parse("ldaps://dc1.acme.com", None);
        Assert.Null(r.Error);
        Assert.True(r.Secure);
        Assert.Equal(636, r.Port);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void Plain_ldap_is_accepted_with_warning()
    {
        var r = LdapUrl.Parse("ldap://dc1.acme.com:389", None);
        Assert.Null(r.Error);
        Assert.False(r.Secure);
        Assert.NotNull(r.Warning);
    }

    [Theory]
    [InlineData("http://dc1.acme.com")]
    [InlineData("ldap://dc1.acme.com:5432")]
    [InlineData("ldaps://dc1.acme.com/dc=acme")]
    [InlineData("ldap://user:pw@dc1.acme.com")]
    [InlineData("dc1.acme.com")]
    public void Invalid_urls_are_rejected(string url) => Assert.NotNull(LdapUrl.Parse(url, None).Error);

    [Fact]
    public void Allowlisted_test_host_may_use_any_port()
    {
        Assert.Null(LdapUrl.Parse("ldap://ldaptest:1389", new[] { "ldaptest" }).Error);
        Assert.NotNull(LdapUrl.Parse("ldap://ldaptest:1389", None).Error);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("8.8.8.8", false)]
    public void Private_address_detection(string ip, bool expected) =>
        Assert.Equal(expected, LdapUrl.IsPrivate(System.Net.IPAddress.Parse(ip)));

    [Theory]
    [InlineData("(objectClass=person)", true)]
    [InlineData("(&(objectClass=user)(!(cn=x)))", true)]
    [InlineData("objectClass=person", false)]
    [InlineData("((objectClass=person)", false)]
    public void Filter_validation(string filter, bool ok) =>
        Assert.Equal(ok, LdapDirectoryReader.ValidateFilterAndDn("dc=acme,dc=com", filter, "cn=admin,dc=acme,dc=com") is null);
}

public class DomainValidatorTests
{
    private static readonly string[] Platform = { "hr360.example.com" };

    [Theory]
    [InlineData("ik.acme.com.tr", "ik.acme.com.tr")]
    [InlineData("  IK.Acme.COM.tr. ", "ik.acme.com.tr")]
    [InlineData("ik.şirket.com.tr", "ik.xn--irket-idb.com.tr")]
    [InlineData("acme.io", "acme.io")]
    public void Valid_domains_are_normalised(string input, string expected)
    {
        var (d, err) = DomainValidator.Normalize(input, Platform);
        Assert.Null(err);
        Assert.Equal(expected, d);
    }

    [Theory]
    [InlineData("https://ik.acme.com.tr")]
    [InlineData("ik.acme.com.tr/giris")]
    [InlineData("ik.acme.com.tr:8443")]
    [InlineData("*.acme.com.tr")]
    [InlineData("localhost")]
    [InlineData("intranet.local")]
    [InlineData("acme")]
    [InlineData("10.0.0.1")]
    [InlineData("-bad-.acme.com")]
    [InlineData("hr360.example.com")]
    [InlineData("tenant.hr360.example.com")]
    [InlineData("a_b.acme.com")]
    [InlineData("")]
    public void Invalid_domains_are_rejected(string input) => Assert.NotNull(DomainValidator.Normalize(input, Platform).Error);

    [Theory]
    [InlineData("IK.acme.com.tr:443", "ik.acme.com.tr")]
    [InlineData("ik.acme.com.tr", "ik.acme.com.tr")]
    [InlineData("[::1]:80", null)]
    [InlineData("localhost:8080", null)]
    public void Host_header_normalisation(string host, string? expected) =>
        Assert.Equal(expected, DomainValidator.NormalizeHost(host));

    [Fact]
    public void Txt_matching_tolerates_quotes_but_not_partial_values()
    {
        var token = DomainValidator.NewToken();
        Assert.StartsWith("hr360-verify=", token);
        Assert.True(DomainValidator.Matches(new[] { "v=spf1 -all", "\"" + token + "\"" }, token));
        Assert.False(DomainValidator.Matches(new[] { token + "x" }, token));
        Assert.False(DomainValidator.Matches(Array.Empty<string>(), token));
        Assert.Equal("_hr360-verify.ik.acme.com.tr", DomainValidator.TxtName("ik.acme.com.tr"));
    }

    [Fact]
    public void Scim_tokens_are_random_and_hash_is_stable()
    {
        var a = ScimTokenService.NewToken();
        var b = ScimTokenService.NewToken();
        Assert.NotEqual(a, b);
        Assert.StartsWith(ScimTokenService.Prefix, a);
        Assert.Equal(ScimTokenService.Hash(a), ScimTokenService.Hash(a));
        Assert.Equal(64, ScimTokenService.Hash(a).Length);
    }
}

public class DomainVerifierTests
{
    private sealed class FakeResolver : ITxtResolver
    {
        private readonly Dictionary<string, string[]> _map;
        public List<string> Queried { get; } = new();
        public bool Throw { get; init; }
        public FakeResolver(Dictionary<string, string[]> map) => _map = map;
        public Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct)
        {
            Queried.Add(name);
            if (Throw) throw new TimeoutException("dns");
            return Task.FromResult<IReadOnlyList<string>>(_map.TryGetValue(name, out var v) ? v : Array.Empty<string>());
        }
    }

    [Fact]
    public async Task Verifies_when_txt_record_matches_on_verify_subdomain()
    {
        var token = DomainValidator.NewToken();
        var dns = new FakeResolver(new() { ["_hr360-verify.ik.acme.com.tr"] = new[] { "other", token } });
        var r = await DomainVerifier.CheckAsync(dns, "ik.acme.com.tr", token, CancellationToken.None);
        Assert.True(r.Verified);
        Assert.Equal(new[] { "_hr360-verify.ik.acme.com.tr" }, dns.Queried);
    }

    [Fact]
    public async Task Record_on_apex_or_wrong_value_does_not_verify()
    {
        var token = DomainValidator.NewToken();
        var apex = new FakeResolver(new() { ["ik.acme.com.tr"] = new[] { token } });
        Assert.Equal("TXT kaydı bulunamadı", (await DomainVerifier.CheckAsync(apex, "ik.acme.com.tr", token, default)).Error);
        var wrong = new FakeResolver(new() { ["_hr360-verify.ik.acme.com.tr"] = new[] { DomainValidator.NewToken() } });
        var r = await DomainVerifier.CheckAsync(wrong, "ik.acme.com.tr", token, default);
        Assert.False(r.Verified);
        Assert.Equal("TXT kaydı doğrulama değeriyle eşleşmiyor", r.Error);
    }

    [Fact]
    public async Task Dns_failure_is_treated_as_not_found()
    {
        var r = await DomainVerifier.CheckAsync(new FakeResolver(new()) { Throw = true }, "ik.acme.com.tr", "t", default);
        Assert.False(r.Verified);
    }
}
