using TenantService.Controllers;
using TenantService.Services;
using Xunit;

namespace Tenant.Tests;

/// <summary>Çalışan kaydı olmayan organizasyon üyeleri (ör. demo.admin) üye listesine katılır.</summary>
public class MembersWithoutEmployeeTests
{
    private static KeycloakAdminClient.OrgMember M(string id) => new(id, id, $"{id}@x.test", null, null, true);

    [Fact]
    public void Calisan_kaydina_bagli_olmayan_uyeler_doner()
    {
        var extra = TeamMembersController.AccountsWithoutEmployee(new[] { "u1", null, "" }, new[] { M("u1"), M("admin"), M("admin"), M("") });
        Assert.Equal(new[] { "admin" }, extra.Select(m => m.Id));
    }

    [Fact]
    public void Tum_uyeler_bagliysa_ek_satir_yok()
    {
        Assert.Empty(TeamMembersController.AccountsWithoutEmployee(new[] { "u1", "u2" }, new[] { M("u1"), M("u2") }));
    }
}
