using TimeShiftService.Services;
using Xunit;

namespace TimeShift.Tests;

/// <summary>Vardiya planlama/görünürlük kapsamı: yönetici yalnızca kendi bölümü, çalışan ekip arkadaşları, geçmiş gün yasak.</summary>
public class ShiftScopeTests
{
    private static readonly Guid Head = Guid.NewGuid(), OtherHead = Guid.NewGuid();
    private static readonly Guid DeptA = Guid.NewGuid(), DeptB = Guid.NewGuid();

    private static TsOps.PersonRow P(Guid id, Guid? dept, Guid? head, string status = "Active") =>
        new() { Id = id, DepartmentId = dept, HeadId = head, Status = status, FirstName = "X" };

    [Fact]
    public void Yonetici_yalnizca_basi_oldugu_bolumu_ve_kendini_planlar()
    {
        var mine = P(Guid.NewGuid(), DeptA, Head);
        var other = P(Guid.NewGuid(), DeptB, OtherHead);
        var self = P(Head, DeptB, OtherHead);
        Assert.True(ShiftScope.CanPlan(Head, mine));
        Assert.True(ShiftScope.CanPlan(Head, self));
        Assert.False(ShiftScope.CanPlan(Head, other));
        Assert.False(ShiftScope.CanPlan(null, mine));
        Assert.False(ShiftScope.CanPlan(Head, null));
    }

    [Fact]
    public void Gecmis_gun_yasak_bugun_serbest()
    {
        var today = new DateOnly(2026, 10, 14);
        Assert.True(ShiftScope.IsPast(today.AddDays(-1), today));
        Assert.False(ShiftScope.IsPast(today, today));
        Assert.False(ShiftScope.IsPast(today.AddDays(3), today));
    }

    [Fact]
    public void Ekip_uyesinin_arkadaslari_yalnizca_ayni_ekip()
    {
        Guid me = Guid.NewGuid(), mate = Guid.NewGuid(), stranger = Guid.NewGuid(), deptMate = Guid.NewGuid();
        Guid t1 = Guid.NewGuid(), t2 = Guid.NewGuid();
        var ms = new[] { (me, t1), (mate, t1), (stranger, t2) };
        var people = new[] { P(me, DeptA, Head), P(mate, DeptB, OtherHead), P(stranger, DeptA, Head), P(deptMate, DeptA, Head) };
        var peers = ShiftScope.Peers(me, ms, people);
        Assert.Equal(new HashSet<Guid> { mate }, peers);
    }

    [Fact]
    public void Ekipsiz_calisanin_arkadaslari_ayni_departmandaki_ekipsizler()
    {
        Guid me = Guid.NewGuid(), deptMate = Guid.NewGuid(), teamed = Guid.NewGuid(), other = Guid.NewGuid(), gone = Guid.NewGuid();
        var ms = new[] { (teamed, Guid.NewGuid()) };
        var people = new[] { P(me, DeptA, Head), P(deptMate, DeptA, Head), P(teamed, DeptA, Head), P(other, DeptB, OtherHead), P(gone, DeptA, Head, "Terminated") };
        Assert.Equal(new HashSet<Guid> { deptMate }, ShiftScope.Peers(me, ms, people));
    }

    [Fact]
    public void Departmansiz_ekipsiz_calisanin_arkadasi_yok()
    {
        var me = Guid.NewGuid();
        Assert.Empty(ShiftScope.Peers(me, Array.Empty<(Guid, Guid)>(), new[] { P(me, null, null), P(Guid.NewGuid(), null, null) }));
    }
}
