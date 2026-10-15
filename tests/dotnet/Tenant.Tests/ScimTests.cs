using System.Text.Json;
using TenantService.Directory;
using Xunit;

namespace Tenant.Tests;

public class ScimFilterTests
{
    private static readonly string[] Attrs = { "userName", "externalId", "id", "emails.value" };

    [Fact]
    public void Parses_userName_eq()
    {
        var c = ScimFilter.Parse("userName eq \"ali.veli@acme.com\"", Attrs);
        Assert.NotNull(c);
        Assert.Equal("userName", c!.Attribute);
        Assert.Equal("ali.veli@acme.com", c.Value);
    }

    [Fact]
    public void Attribute_and_operator_are_case_insensitive_and_escapes_handled()
    {
        var c = ScimFilter.Parse("EXTERNALID EQ \"a\\\"b\"", Attrs);
        Assert.Equal("externalId", c!.Attribute);
        Assert.Equal("a\"b", c.Value);
    }

    [Fact]
    public void Empty_filter_is_null() => Assert.Null(ScimFilter.Parse("  ", Attrs));

    [Theory]
    [InlineData("userName co \"ali\"")]
    [InlineData("userName eq \"a\" and active eq true")]
    [InlineData("title eq \"x\"")]
    [InlineData("userName eq ali")]
    public void Unsupported_filters_are_rejected(string filter)
    {
        var ex = Assert.Throws<ScimException>(() => ScimFilter.Parse(filter, Attrs));
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalidFilter", ex.ScimType);
    }
}

public class ScimPatchTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static ScimUserDraft Base() => new()
    {
        UserName = "ali", Email = "ali@acme.com", GivenName = "Ali", FamilyName = "Veli", Active = true, Department = "Satış",
    };

    [Fact]
    public void Entra_style_pathless_replace_with_string_boolean_deactivates()
    {
        var ops = ScimPatch.ParseOperations(J("""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Replace","value":{"active":"False"}}]}"""));
        var d = ScimPatch.ApplyToUser(Base(), ops);
        Assert.False(d.Active);
        Assert.Equal("Ali", d.GivenName);
    }

    [Fact]
    public void Path_active_false()
    {
        var ops = ScimPatch.ParseOperations(J("""{"Operations":[{"op":"replace","path":"active","value":false}]}"""));
        Assert.False(ScimPatch.ApplyToUser(Base(), ops).Active);
    }

    [Fact]
    public void Name_email_and_department_paths()
    {
        var ops = ScimPatch.ParseOperations(J("""
        {"Operations":[
          {"op":"replace","path":"name.givenName","value":"Ayşe"},
          {"op":"replace","path":"emails[type eq \"work\"].value","value":"ayse@acme.com"},
          {"op":"add","path":"urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department","value":"Mühendislik"},
          {"op":"replace","value":{"name.familyName":"Kaya","title":"Kıdemli Mühendis"}}
        ]}
        """));
        var d = ScimPatch.ApplyToUser(Base(), ops);
        Assert.Equal("Ayşe", d.GivenName);
        Assert.Equal("Kaya", d.FamilyName);
        Assert.Equal("ayse@acme.com", d.Email);
        Assert.Equal("Mühendislik", d.Department);
        Assert.Equal("Kıdemli Mühendis", d.Title);
    }

    [Fact]
    public void Original_draft_is_not_mutated()
    {
        var original = Base();
        ScimPatch.ApplyToUser(original, ScimPatch.ParseOperations(J("""{"Operations":[{"op":"replace","path":"active","value":false}]}""")));
        Assert.True(original.Active);
    }

    [Fact]
    public void Unknown_attributes_are_ignored_for_data_minimisation()
    {
        var ops = ScimPatch.ParseOperations(J("""{"Operations":[{"op":"add","path":"addresses","value":[{"streetAddress":"Gizli sk."}]},{"op":"replace","path":"phoneNumbers","value":[{"value":"+905551112233"}]},{"op":"replace","path":"urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:manager","value":{"value":"m1"}}]}"""));
        var before = Base();
        var d = ScimPatch.ApplyToUser(before, ops);
        // Taslakta telefon/adres/yonetici alani hic yok; diger alanlar degismedi.
        Assert.Equal("Ali", d.GivenName);
        Assert.Equal("Satış", d.Department);
        Assert.Null(d.Title);
    }

    [Theory]
    [InlineData("""{"Operations":[{"op":"remove"}]}""")]
    [InlineData("""{"Operations":[{"op":"move","path":"active","value":true}]}""")]
    [InlineData("""{"Operations":[]}""")]
    [InlineData("""{"ops":[]}""")]
    [InlineData("""{"Operations":[{"op":"replace","path":"active"}]}""")]
    public void Invalid_patch_bodies_are_rejected(string json) =>
        Assert.Throws<ScimException>(() => ScimPatch.ParseOperations(J(json)));

    [Fact]
    public void Removing_email_is_rejected()
    {
        var ops = ScimPatch.ParseOperations(J("""{"Operations":[{"op":"remove","path":"emails"}]}"""));
        Assert.Throws<ScimException>(() => ScimPatch.ApplyToUser(Base(), ops));
    }

    [Fact]
    public void Non_boolean_active_is_rejected()
    {
        var ops = ScimPatch.ParseOperations(J("""{"Operations":[{"op":"replace","path":"active","value":"belki"}]}"""));
        Assert.Throws<ScimException>(() => ScimPatch.ApplyToUser(Base(), ops));
    }
}

public class ScimUserMapperTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private const string Resource = """
    {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"Zeynep@Acme.com","externalId":"ext-1",
     "name":{"givenName":"Zeynep","familyName":"Ak"},"emails":[{"value":"home@x.com","type":"home"},{"value":"Zeynep@Acme.com","type":"work","primary":true}],
     "phoneNumbers":[{"value":"+90 555 111 22 33","type":"work"}],"addresses":[{"locality":"İstanbul"}],"title":"Uzman","active":true,
     "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User":{"department":"İK","manager":{"value":"x"}}}
    """;

    [Fact]
    public void Maps_only_minimal_fields_primary_email_title_department()
    {
        var d = ScimUserMapper.FromResource(J(Resource));
        ScimUserMapper.Validate(d);
        Assert.Equal("zeynep@acme.com", d.Email);
        Assert.Equal("İK", d.Department);
        Assert.Equal("Uzman", d.Title);
        Assert.Equal("ext-1", d.ExternalId);
        Assert.Equal("Zeynep", d.GivenName);
        Assert.Equal("Ak", d.FamilyName);
        Assert.True(d.Active);
    }

    [Fact]
    public void Draft_type_has_no_slot_for_unminimised_attributes()
    {
        // KVKK: telefon, adres, foto, yonetici vb. icin taslakta alan bile yok.
        var props = typeof(ScimUserDraft).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Active", "Department", "Email", "ExternalId", "FamilyName", "GivenName", "Title", "UserName" }, props);
    }

    [Fact]
    public void Primary_email_wins_over_work_and_first()
    {
        var d = ScimUserMapper.FromResource(J("""{"userName":"u","name":{"givenName":"A","familyName":"B"},"emails":[{"value":"w@x.com","type":"work"},{"value":"p@x.com","primary":"True"}]}"""));
        ScimUserMapper.Validate(d);
        Assert.Equal("p@x.com", d.Email);
    }

    [Fact]
    public void Email_falls_back_to_userName()
    {
        var d = ScimUserMapper.FromResource(J("""{"userName":"a@b.com","name":{"givenName":"A","familyName":"B"}}"""));
        ScimUserMapper.Validate(d);
        Assert.Equal("a@b.com", d.Email);
    }

    [Theory]
    [InlineData("""{"userName":"a@b.com"}""")]
    [InlineData("""{"userName":"ab","name":{"givenName":"A","familyName":"B"}}""")]
    [InlineData("""{"name":{"givenName":"A","familyName":"B"},"emails":[{"value":"a@b.com"}]}""")]
    public void Missing_required_fields_are_rejected(string json)
    {
        var d = ScimUserMapper.FromResource(J(json));
        Assert.Throws<ScimException>(() => ScimUserMapper.Validate(d));
    }
}
