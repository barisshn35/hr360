using System.Text.Json;
using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>Olay yükünden gizli alanların (e-posta karar jetonu vb.) ayıklanması.</summary>
public class EventSanitizeTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Removes_action_token_and_nested_secrets()
    {
        var p = Parse("""{"TenantSlug":"demo","Subject":"x","ActionToken":"abc.def","Inner":{"apiSecret":"s","Password":"p","Keep":1},"List":[{"refreshToken":"t","Id":2}]}""");
        var raw = EventHub.Sanitize(p)!.Value.GetRawText();
        Assert.DoesNotContain("ActionToken", raw);
        Assert.DoesNotContain("apiSecret", raw);
        Assert.DoesNotContain("Password", raw);
        Assert.DoesNotContain("refreshToken", raw);
        Assert.Contains("\"Keep\":1", raw);
        Assert.Contains("\"Id\":2", raw);
        Assert.Equal("demo", EventHub.Field(EventHub.Sanitize(p), "TenantSlug"));
    }

    [Fact]
    public void Removes_personal_fields_only_when_asked()
    {
        var p = Parse("""{"ApproverEmail":"a@b.c","ApproverEmployeeId":"1"}""");
        Assert.Contains("ApproverEmail", EventHub.Sanitize(p)!.Value.GetRawText());
        var stored = EventHub.Sanitize(p, EventHub.RadarPersonalFields)!.Value.GetRawText();
        Assert.DoesNotContain("ApproverEmail", stored);
        Assert.Contains("ApproverEmployeeId", stored);
    }

    [Fact]
    public void Leaves_clean_and_null_payloads_untouched()
    {
        Assert.Null(EventHub.Sanitize(null));
        var p = Parse("""{"Subject":"x"}""");
        Assert.Equal(p.GetRawText(), EventHub.Sanitize(p)!.Value.GetRawText());
    }
}
