using EngagementService.Controllers;
using EngagementService.Infrastructure;
using EngagementService.Models;
using Xunit;

namespace Engagement.Tests;

/// <summary>Org senaryo taslakları: sahip + İK erişimi; maaş görünürlüğü yoksa planlanan maaş çıkarılır.</summary>
public class OrgScenarioAccessTests
{
    private static UserInfo U(string id, params string[] roles) => new(id, "X", null, roles.ToHashSet());

    [Fact]
    public void Yonetici_yalnizca_kendi_taslagina_erisir()
    {
        var s = new OrgScenario { CreatedByUserId = "u-mehmet" };
        Assert.True(OrgScenariosController.CanAccess(s, U("u-mehmet", "manager")));
        Assert.False(OrgScenariosController.CanAccess(s, U("u-baska", "manager")));
    }

    [Fact]
    public void Ik_tum_taslaklara_erisir_sahipsiz_eski_satir_yalnizca_ik()
    {
        var legacy = new OrgScenario { CreatedByUserId = null };
        Assert.True(OrgScenariosController.CanAccess(legacy, U("u-ik", "hr-admin")));
        Assert.False(OrgScenariosController.CanAccess(legacy, U("", "manager")));
        Assert.True(OrgScenariosController.CanAccess(new OrgScenario { CreatedByUserId = "u-mehmet" }, U("u-ik", "tenant-admin")));
    }

    [Fact]
    public void Maas_gorunmuyorsa_planlanan_maas_cikarilir()
    {
        var moves = new List<OrgMove> { new() { Kind = "Hire", Name = "Yeni", PlannedSalary = 50000 }, new() { Kind = "Move", Name = "A" } };
        var hidden = OrgScenariosController.RedactMoves(moves, false);
        Assert.All(hidden, m => Assert.Null(m.PlannedSalary));
        Assert.Equal(50000, moves[0].PlannedSalary); // kaydın kendisi değişmez
        Assert.Equal(50000, OrgScenariosController.RedactMoves(moves, true)[0].PlannedSalary);
    }
}
