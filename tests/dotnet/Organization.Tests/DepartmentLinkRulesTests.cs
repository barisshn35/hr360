using OrganizationService.Infrastructure;
using OrganizationService.Models;
using static OrganizationService.Infrastructure.DepartmentLinkRules;
using Xunit;

namespace Organization.Tests;

/// <summary>Matris bağı doğrulaması (DepartmentLinksController.Create bu kurallara dayanır).</summary>
public class DepartmentLinkRulesTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static DeptInfo D(Guid id, string tenant = "demo") => new(id, tenant);

    private static string? Check(Guid from, Guid to, string? kind = "Functional", string? note = null,
        DeptInfo? f = null, DeptInfo? t = null, string? tenant = "demo", IEnumerable<LinkInfo>? existing = null) =>
        Validate(from, to, kind, note, f ?? D(from), t ?? D(to), tenant, existing ?? Array.Empty<LinkInfo>());

    [Fact]
    public void Gecerli_bag_kabul_edilir() => Assert.Null(Check(A, B));

    [Fact]
    public void Tur_buyuk_kucuk_harf_duyarsiz() => Assert.Null(Check(A, B, kind: "project"));

    [Fact]
    public void Kendine_bag_reddedilir() => Assert.Equal("Bir departman kendisine bağlanamaz.", Check(A, A));

    [Fact]
    public void Bos_kimlik_reddedilir() => Assert.NotNull(Check(Guid.Empty, B));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Solid")]
    public void Gecersiz_tur_reddedilir(string? kind) => Assert.NotNull(Check(A, B, kind: kind));

    [Fact]
    public void Uzun_not_reddedilir() => Assert.NotNull(Check(A, B, note: new string('x', NoteMaxLength + 1)));

    [Fact]
    public void Bulunamayan_departman_reddedilir()
    {
        Assert.Equal("Departman bulunamadı.", Validate(A, B, "Functional", null, D(A), null, "demo", Array.Empty<LinkInfo>()));
        Assert.Equal("Departman bulunamadı.", Validate(A, B, "Functional", null, null, D(B), "demo", Array.Empty<LinkInfo>()));
    }

    [Fact]
    public void Farkli_kiraci_reddedilir()
    {
        Assert.Equal("Departmanlar aynı kiracıda olmalı.", Check(A, B, f: D(A, "demo"), t: D(B, "acme")));
        // İsteğin kiracısı farklıysa (filtre dışı kalmış kayıt) bulunamadı gibi davranılır.
        Assert.Equal("Departman bulunamadı.", Check(A, B, f: D(A, "acme"), t: D(B, "acme"), tenant: "demo"));
        // Platform yöneticisi (kiracısız) aynı kiracıdaki iki departmanı bağlayabilir.
        Assert.Null(Check(A, B, f: D(A, "acme"), t: D(B, "acme"), tenant: null));
    }

    [Fact]
    public void Ayni_bag_ikinci_kez_eklenemez()
    {
        var existing = new[] { new LinkInfo(A, B, DepartmentLinkKinds.Functional) };
        Assert.Equal("Bu bağ zaten var.", Check(A, B, existing: existing));
        // Farklı tür ya da ters yön ayrı bağdır.
        Assert.Null(Check(A, B, kind: "Project", existing: existing));
        Assert.Null(Check(B, A, existing: existing));
    }
}
