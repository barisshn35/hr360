using GovernanceService.Controllers;
using Xunit;

namespace Governance.Tests;

/// <summary>Erişim gözden geçirme: çalışan kaydı olmayan yönetici hesapları da kampanyaya girer (gözden geçiren İK).</summary>
public class AccessReviewAccountsTests
{
    [Fact]
    public void Calisan_kaydi_olmayan_yetkili_hesap_kampanyaya_girer()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var rows = new List<AccessReviewController.MemberRow>
        {
            new(known, "k1", true, new() { "manager" }, new()),
            new(unknown, "k2", true, new() { "hr-admin" }, new()),                       // kiracının çalışan listesinde yok
            new(null, "admin", true, new() { "tenant-admin" }, new(), false, "Demo", "Yönetici"),
            new(null, "plain", true, new() { "employee" }, new(), false, "Sade", "Hesap"),  // yetkisi yok
            new(null, "off", false, new() { "tenant-admin" }, new(), false),               // kapalı hesap
        };
        var r = AccessReviewController.CampaignRows(rows, new HashSet<Guid> { known });
        Assert.Equal(new[] { "k1", "admin" }, r.Select(x => x.KeycloakUserId));
        Assert.Equal("Demo Yönetici", AccessReviewController.AccountName(r[1]));
        Assert.Null(AccessReviewController.AccountName(r[0]));
    }
}
