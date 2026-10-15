using TimeShiftService.Services;
using Xunit;

public class VisibleTeamsTests
{
    private static readonly Guid Dept = Guid.NewGuid(), OtherDept = Guid.NewGuid();
    private static readonly Guid Mgr = Guid.NewGuid(), Emp = Guid.NewGuid(), Stranger = Guid.NewGuid();
    private static readonly Guid TeamA = Guid.NewGuid(), TeamB = Guid.NewGuid(), TeamC = Guid.NewGuid();

    private static (Guid, Guid?)[] Teams => new (Guid, Guid?)[] { (TeamA, Dept), (TeamB, null), (TeamC, OtherDept) };
    private static (Guid, Guid)[] Members => new[] { (Emp, TeamB), (Stranger, TeamC) };
    private static TsOps.PersonRow[] People => new[]
    {
        new TsOps.PersonRow { Id = Mgr, DepartmentId = Dept, HeadId = Mgr },
        new TsOps.PersonRow { Id = Emp, DepartmentId = Dept, HeadId = Mgr },
        new TsOps.PersonRow { Id = Stranger, DepartmentId = OtherDept, HeadId = null },
    };

    [Fact]
    public void Ik_tum_ekipleri_gorur() => Assert.Null(ShiftScope.VisibleTeams(true, null, Teams, Members, People));

    [Fact]
    public void Yonetici_bolumunun_ve_ekibinin_ekiplerini_gorur()
    {
        var v = ShiftScope.VisibleTeams(false, Mgr, Teams, Members, People)!;
        Assert.Contains(TeamA, v);   // başı olduğu bölümün ekibi
        Assert.Contains(TeamB, v);   // yönettiği kişinin üye olduğu ekip
        Assert.DoesNotContain(TeamC, v);
    }

    [Fact]
    public void Calisan_yalnizca_uyesi_oldugu_ekibi_gorur()
    {
        var v = ShiftScope.VisibleTeams(false, Emp, Teams, Members, People)!;
        Assert.Equal(new[] { TeamB }, v.ToArray());
    }

    [Fact]
    public void Kimligi_bilinmeyen_hicbir_ekibi_goremez() => Assert.Empty(ShiftScope.VisibleTeams(false, null, Teams, Members, People)!);
}
