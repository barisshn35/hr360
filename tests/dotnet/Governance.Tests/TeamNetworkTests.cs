using GovernanceService.Infrastructure;
using Xunit;
using static GovernanceService.Infrastructure.TeamNetwork;

namespace Governance.Tests;

/// <summary>Ekip ağı: küçük ekip birleştirme/gizleme ve küçük sayı bastırma (KVKK).</summary>
public class TeamNetworkTests
{
    private static Guid[] People(int n, int offset = 0) =>
        Enumerable.Range(offset, n).Select(i => new Guid(i + 1, 0, 0, new byte[8])).ToArray();

    private static TeamRow Team(int id, int size, int offset = 0, string? dept = "Mühendislik") =>
        new(new Guid(1000 + id, 0, 0, new byte[8]), $"Ekip {id}", dept, People(size, offset));

    [Fact]
    public void Bes_kisilik_ekip_gosterilir_dort_kisilik_tek_basina_gosterilmez()
    {
        var a = Team(1, 5);
        var b = Team(2, 4, 100);
        var r = Build(new[] { a, b }, Array.Empty<PairRow>());
        Assert.Single(r.Nodes);
        Assert.Equal(a.Id.ToString(), r.Nodes[0].Id);
        Assert.Equal(1, r.HiddenTeams);
    }

    [Fact]
    public void Kucuk_ekipler_bes_kisiye_ulasinca_Diger_dugumunde_birlesir()
    {
        var big = Team(1, 8);
        var s1 = Team(2, 3, 100);
        var s2 = Team(3, 2, 200);
        var r = Build(new[] { big, s1, s2 }, new[]
        {
            new PairRow(big.Id, s1.Id, Kinds.Kudos, 2),
            new PairRow(big.Id, s2.Id, Kinds.Kudos, 2),
        });
        var other = Assert.Single(r.Nodes, n => n.Id == OtherId);
        Assert.Equal(5, other.Members);
        Assert.Equal(2, other.MergedTeams);
        Assert.Equal(0, r.HiddenTeams);
        // İki küçük bağ (2 + 2) Diğer'de birleşince eşiği geçer.
        var e = Assert.Single(r.Edges);
        Assert.Equal(4, e.Counts.Kudos);
    }

    [Fact]
    public void Diger_dugumu_ortak_uyeler_tekil_sayilir()
    {
        // Aynı 3 kişi iki küçük ekipte: farklı kişi 3 → Diğer de gizlenir.
        var big = Team(1, 6);
        var s1 = Team(2, 3, 100);
        var s2 = Team(3, 3, 100);
        var r = Build(new[] { big, s1, s2 }, new[] { new PairRow(big.Id, s1.Id, Kinds.Kudos, 10) });
        Assert.DoesNotContain(r.Nodes, n => n.Id == OtherId);
        Assert.Equal(2, r.HiddenTeams);
        Assert.Empty(r.Edges);
    }

    [Fact]
    public void Ucten_kucuk_bag_gosterilmez()
    {
        var a = Team(1, 5);
        var b = Team(2, 5, 100);
        var r = Build(new[] { a, b }, new[] { new PairRow(a.Id, b.Id, Kinds.OneOnOne, 2) });
        Assert.Empty(r.Edges);
        Assert.Equal(1, r.HiddenEdges);
    }

    [Fact]
    public void Kucuk_tur_sifirlanir_agirlik_yalnizca_gosterilen_turlerin_toplami()
    {
        var a = Team(1, 5);
        var b = Team(2, 5, 100);
        var r = Build(new[] { a, b }, new[]
        {
            new PairRow(a.Id, b.Id, Kinds.Kudos, 4),
            new PairRow(b.Id, a.Id, Kinds.OneOnOne, 1), // ters sıra da aynı bağa toplanır
            new PairRow(a.Id, b.Id, Kinds.SharedGoal, 2),
        });
        var e = Assert.Single(r.Edges);
        Assert.Equal(4, e.Counts.Kudos);
        Assert.Equal(0, e.Counts.OneOnOnes);
        Assert.Equal(0, e.Counts.SharedGoals);
        // "Toplam − diğerleri" ile küçük sayı geri hesaplanamaz.
        Assert.Equal(4, e.Weight);
    }

    [Fact]
    public void Ekip_ici_sayac_da_bastirilir()
    {
        var a = Team(1, 6);
        var r = Build(new[] { a }, new[]
        {
            new PairRow(a.Id, a.Id, Kinds.Kudos, 7),
            new PairRow(a.Id, a.Id, Kinds.OneOnOne, 2),
        });
        var n = Assert.Single(r.Nodes);
        Assert.Equal(7, n.Internal.Kudos);
        Assert.Equal(0, n.Internal.OneOnOnes);
        Assert.Empty(r.Edges);
    }

    [Fact]
    public void Bilinmeyen_ekip_ve_sifir_sayilar_yok_sayilir()
    {
        var a = Team(1, 5);
        var b = Team(2, 5, 100);
        var r = Build(new[] { a, b }, new[]
        {
            new PairRow(a.Id, Guid.NewGuid(), Kinds.Kudos, 50),
            new PairRow(a.Id, b.Id, Kinds.Kudos, 0),
            new PairRow(a.Id, b.Id, "bilinmeyen", 9),
        });
        Assert.Empty(r.Edges);
        Assert.Equal(2, r.Nodes.Count);
    }

    [Fact]
    public void Suppress_esik_sinirlari()
    {
        Assert.Equal(new Counts(3, 0, 5), Suppress(new Counts(3, 2, 5)));
        Assert.Equal(Counts.Zero, Suppress(new Counts(1, 2, 0)));
    }
}
