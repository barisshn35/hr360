using RecruitmentService.Services;
using Xunit;

namespace Recruitment.Tests;

/// <summary>Kör değerlendirme kuralı (puan kartları ve kanban ortalaması aynı kuralı kullanır).</summary>
public class BlindPipelineTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), Outsider = Guid.NewGuid();

    [Fact]
    public void Panel_uyesi_panel_tamamlanmadan_ortalamayi_goremez()
    {
        Assert.True(ScorecardRules.IsBlind(new[] { A, B }, new HashSet<Guid> { A }, A));
        Assert.True(ScorecardRules.IsBlind(new[] { A, B }, new HashSet<Guid>(), B));
    }

    [Fact]
    public void Panel_tamamlaninca_acilir()
    {
        Assert.False(ScorecardRules.IsBlind(new[] { A, B }, new HashSet<Guid> { A, B }, A));
    }

    [Fact]
    public void Panel_disindaki_yonetici_ve_ik_icin_gizlilik_yok()
    {
        Assert.False(ScorecardRules.IsBlind(new[] { A, B }, new HashSet<Guid> { A }, Outsider));
        Assert.False(ScorecardRules.IsBlind(new[] { A, B }, new HashSet<Guid> { A }, null));
    }
}
