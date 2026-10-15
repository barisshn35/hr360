using LearningService.Controllers;
using LearningService.Services;
using Xunit;

namespace Learning.Tests;

/// <summary>Zorunlu eğitim ataması: "departmandaki herkese" yöneticinin kendisini içermez; yönetici yalnızca kendi departmanı.</summary>
public class DueAssignScopeTests
{
    private static readonly Guid Dept = Guid.NewGuid(), Other = Guid.NewGuid(), Me = Guid.NewGuid();
    private static PersonRow P(Guid id, Guid? dept) => new() { Id = id, DepartmentId = dept, FirstName = "X" };

    private static readonly PersonRow[] People = { P(Me, Dept), P(Guid.NewGuid(), Dept), P(Guid.NewGuid(), Dept), P(Guid.NewGuid(), Other) };

    [Fact]
    public void Departman_modunda_yonetici_kendini_hedeflemez_ve_atama_izinli()
    {
        var t = LearningDueController.Targets(People, Dept, null, Me, isHr: false);
        Assert.Equal(2, t.Count);
        Assert.DoesNotContain(t, p => p.Id == Me);
        Assert.True(LearningDueController.ManagerMayAssign(t, new HashSet<Guid> { Dept }, Me));
    }

    [Fact]
    public void Ik_departman_modunda_herkesi_hedefler()
    {
        Assert.Equal(3, LearningDueController.Targets(People, Dept, null, Me, isHr: true).Count);
    }

    [Fact]
    public void Yonetici_baska_departmana_ya_da_kendine_atayamaz()
    {
        var other = LearningDueController.Targets(People, null, new[] { People[3].Id }, Me, false);
        Assert.False(LearningDueController.ManagerMayAssign(other, new HashSet<Guid> { Dept }, Me));
        var self = LearningDueController.Targets(People, null, new[] { Me }, Me, false);
        Assert.False(LearningDueController.ManagerMayAssign(self, new HashSet<Guid> { Dept }, Me));
        Assert.False(LearningDueController.ManagerMayAssign(new[] { People[1] }, new HashSet<Guid> { Dept }, null));
    }
}
