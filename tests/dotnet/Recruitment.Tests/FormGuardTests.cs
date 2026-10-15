using RecruitmentService.Services;
using Xunit;

namespace Recruitment.Tests;

/// <summary>Kariyer sayfası başvuru formu bot koruması (güvenlik dalgası 2B).</summary>
public class FormGuardTests
{
    [Fact]
    public void Basvuru_jetonu_en_az_3_sn_en_cok_2_saat_ve_tek_kullanim()
    {
        var g = new FormGuard(new byte[32]);
        var t0 = DateTimeOffset.UtcNow;
        var tok = g.Issue("career:demo", t0);
        Assert.Equal(FormTokenStatus.TooFast, g.Verify(tok, "career:demo", t0.AddSeconds(2)));
        Assert.Equal(FormTokenStatus.Invalid, g.Verify(tok, "career:diger", t0.AddSeconds(4)));
        Assert.Equal(FormTokenStatus.Ok, g.Verify(tok, "career:demo", t0.AddSeconds(4)));
        Assert.Equal(FormTokenStatus.Replayed, g.Verify(tok, "career:demo", t0.AddSeconds(5)));
        Assert.Equal(FormTokenStatus.Expired, g.Verify(g.Issue("career:demo", t0), "career:demo", t0.AddHours(2).AddSeconds(1)));
    }
}
