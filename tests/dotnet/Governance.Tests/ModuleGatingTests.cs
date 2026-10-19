using GovernanceService.Controllers;
using GovernanceService.Infrastructure;
using GovernanceService.Infrastructure.Chat;
using HR360.Platform;
using Xunit;

namespace Governance.Tests;

/// <summary>Kurulumda kapalı modüller (HR360_MODULES): sohbet komutları, işe alım sagası ve ML çağrıları.</summary>
public class ModuleGatingTests
{
    [Fact]
    public void Hepsi_acikken_sohbet_ozelligi_kapali_sayilmaz_yardim_aynen_kalir()
    {
        using var _ = Modules.Override(null);
        Assert.All(ChatFeatureCatalog.Keys, k => Assert.False(ChatFeatureCatalog.ModuleOff(k)));
        Assert.Equal(ChatService.Help, ChatService.HelpOf(false));
        Assert.Equal(ChatService.HelpEn, ChatService.HelpOf(true));
        Assert.Equal(ChatFeatureCatalog.All.Length, ChatFeatureCatalog.Available().Count());
        Assert.Contains(ChatFeatures.HelpReply(false).Buttons!, b => b.Label == "Vardiyam");
    }

    [Fact]
    public void Kapali_modulun_komutlari_dugmeleri_ve_yardimi_gizlenir()
    {
        using var _ = Modules.Override("recruitment,performance,learning,ml");
        foreach (var cmd in new[] { "shifts", "swaps", "clockin", "clockout", "expense", "payslip" })
            Assert.True(ChatFeatureCatalog.ModuleOff(ChatFeatureCatalog.FeatureOf(cmd)), cmd);
        foreach (var action in new[] { "swap_accept", "clock", "exp_confirm", "onb_done" })
            Assert.True(ChatFeatureCatalog.ModuleOff(ChatFeatures.ActionFeature(action)), action);
        // Çekirdek ve belge/vaka eylemleri açık kalır (expense-service belge ve İK vakalarını da taşır).
        foreach (var cmd in new[] { "balance", "leave", "documents", "hrcase", "desk", "announcements", "approvals" })
            Assert.False(ChatFeatureCatalog.ModuleOff(ChatFeatureCatalog.FeatureOf(cmd)), cmd);
        Assert.False(ChatFeatureCatalog.ModuleOff(ChatFeatures.ActionFeature("doc_req")));
        Assert.False(ChatFeatureCatalog.ModuleOff(ChatFeatures.ActionFeature("hrcase_open")));

        var tr = ChatService.HelpOf(false);
        Assert.DoesNotContain("*vardiyam*", tr);
        Assert.DoesNotContain("*geldim*", tr);
        Assert.DoesNotContain("*masraf*", tr);
        Assert.DoesNotContain("*bordrom*", tr);
        Assert.Contains("*bakiye*", tr);
        Assert.Contains("*belge*", tr);
        Assert.Contains("*ik vakası*", tr);
        var en = ChatService.HelpOf(true);
        Assert.DoesNotContain("*my shifts*", en);
        Assert.DoesNotContain("*clock in*", en);
        Assert.Contains("*documents*", en);

        Assert.DoesNotContain(ChatFeatures.HelpReply(false).Buttons!, b => b.Label == "Vardiyam");
        var keys = ChatFeatureCatalog.Available().Select(f => f.Key).ToList();
        Assert.DoesNotContain("shifts", keys);
        Assert.DoesNotContain("onboarding", keys);
        Assert.Contains("documents", keys);
        Assert.Equal("Bu modül bu kurulumda açık değil.", ChatFeatureCatalog.ModuleOffText(false));
    }

    [Fact]
    public void Yalniz_masraf_ekranlari_kapaliyken_yalniz_masraf_komutu_kapanir()
    {
        using var _ = Modules.Override("recruitment,onboarding,timeshift,performance,learning,compensation,ml");
        Assert.True(ChatFeatureCatalog.ModuleOff("expense"));
        Assert.False(ChatFeatureCatalog.ModuleOff("shifts"));
        Assert.False(ChatFeatureCatalog.ModuleOff("documents"));
        Assert.False(ChatFeatureCatalog.ModuleOff("hrcase"));
    }

    [Theory]
    [InlineData("Offer", false, false, true, "Offer")]
    [InlineData("Hired", false, false, true, "AwaitingEmployee")]
    [InlineData("Hired", true, false, true, "AwaitingOnboarding")]
    [InlineData("Hired", true, true, true, "Completed")]
    [InlineData("Hired", false, false, false, "AwaitingEmployee")]
    [InlineData("Hired", true, false, false, "Completed")] // işe alıştırma kapalı: plan adımı atlanır
    public void Saga_asamasi(string status, bool employee, bool plan, bool onboarding, string want) =>
        Assert.Equal(want, SagaController.Stage(status, employee, plan, onboarding));

    [Fact]
    public void Saga_ise_alim_kapaliyken_modul_yok_der()
    {
        using (Modules.Override("onboarding")) Assert.Equal("recruitment", SagaController.MissingModule());
        using (Modules.Override("recruitment")) Assert.Null(SagaController.MissingModule());
        using (Modules.Override(null)) Assert.Null(SagaController.MissingModule());
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP çağrısı yapılmamalı");
    }

    [Fact]
    public async Task Ml_kapaliyken_cagri_yapilmadan_hata_sonucu_doner()
    {
        using var _ = Modules.Override("timeshift");
        var (status, body) = await MlCall.PostAsync(new NoHttp(), "/forecast/leave-daily", new { }, null, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, status);
        Assert.Null(body);
    }
}
