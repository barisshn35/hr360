namespace PerformanceService.Services;

/// <summary>OKR ağacının düğümü: şirket/departman amacı ya da kişisel hedef.</summary>
public sealed class OkrNode
{
    public Guid Id { get; init; }
    /// <summary>Company | Department | Goal</summary>
    public required string Kind { get; init; }
    public Guid? ParentId { get; init; }
    public int Weight { get; init; } = 100;
    /// <summary>Yalnızca hedefte: kendi gerçekleşmesi (0-100; iptalde null).</summary>
    public decimal? OwnProgress { get; init; }
    public Guid? EmployeeId { get; init; }
    public List<OkrNode> Children { get; } = new();
    /// <summary>Toplanmış ilerleme (RollUp sonrası).</summary>
    public decimal? Progress { get; set; }
    /// <summary>Alt ağaçtaki kişiler (hedef sahipleri).</summary>
    public HashSet<Guid> Employees { get; } = new();
}

/// <summary>
/// Dalga 11 / 80: OKR hizalama — saf hesap (birim testli).
///  * Hiyerarşi: Şirket amacı → Departman amacı → Kişisel hedef. Departman amacı yalnızca şirket
///    amacına, kişisel hedef şirket ya da departman amacına bağlanır (döngü oluşamaz).
///  * İlerleme: hedefte GoalProgress (ilerleme girilmemişse yok — %50 sayılmaz); amaçta ilerlemesi
///    olan çocukların ağırlıklı ortalaması. Çocuğu ya da ilerlemesi olan çocuğu olmayan amacın ilerlemesi yoktur.
///  * KVKK: yönetici olmayan izleyicide, alt ağacında kendisi dışında 1-4 kişinin hedefi olan amacın
///    ilerlemesi gizlenir (küçük grupta kişisel ilerleme çıkarılabilir).
/// </summary>
public static class OkrMath
{
    public const int MinGroup = 5;

    /// <summary>
    /// OKR ağacında hedefin ilerlemesi (0-100). Puan hesabındaki "devam ediyor = %50" varsayımı burada
    /// KULLANILMAZ: ilerleme girilmemiş (sayısal hedefi ya da güncel değeri olmayan, devam eden) hedef null
    /// döner ("ilerleme girilmedi") ve üst amacın ortalamasına katılmaz. İptal edilen hedef de null.
    /// </summary>
    public static decimal? GoalProgress(PerformanceService.Models.Goal g)
    {
        if (g.Status == PerformanceService.Models.GoalStatus.Cancelled) return null;
        if (g.TargetValue is > 0m && g.CurrentValue is not null) return Math.Round(ScoreCalculator.GoalAchievement(g), 1);
        return g.Status switch
        {
            PerformanceService.Models.GoalStatus.Achieved => 100m,
            PerformanceService.Models.GoalStatus.Missed => 0m,
            _ => null,
        };
    }

    public static string? ValidateParent(string childKind, string? parentKind)
    {
        return (childKind, parentKind) switch
        {
            ("Company", null) => null,
            ("Company", _) => "Şirket amacı başka bir amaca bağlanamaz",
            ("Department", null or "Company") => null,
            ("Department", _) => "Departman amacı yalnızca bir şirket amacına bağlanabilir",
            ("Goal", null or "Company" or "Department") => null,
            _ => "Geçersiz bağlantı",
        };
    }

    /// <summary>Düz listeden ağaç kurar; ebeveyni bulunamayan düğüm köke çıkar. Kökleri döner.</summary>
    public static List<OkrNode> Build(IEnumerable<OkrNode> nodes)
    {
        var list = nodes.ToList();
        var byId = list.ToDictionary(n => n.Id);
        var roots = new List<OkrNode>();
        foreach (var n in list)
        {
            if (n.ParentId is { } p && byId.TryGetValue(p, out var parent) && parent.Kind != "Goal") parent.Children.Add(n);
            else roots.Add(n);
        }
        foreach (var r in roots) RollUp(r);
        return roots;
    }

    public static void RollUp(OkrNode n)
    {
        if (n.Kind == "Goal")
        {
            n.Progress = n.OwnProgress;
            if (n.EmployeeId is { } e) n.Employees.Add(e);
        }
        decimal sum = 0, weight = 0;
        foreach (var c in n.Children)
        {
            RollUp(c);
            n.Employees.UnionWith(c.Employees);
            if (c.Progress is { } p && c.Weight > 0) { sum += p * c.Weight; weight += c.Weight; }
        }
        if (n.Kind != "Goal") n.Progress = weight > 0 ? Math.Round(sum / weight, 1) : null;
    }

    /// <summary>Yönetici olmayan izleyicide küçük grup ilerlemesi gizlenir mi (yalnızca bu düğüm)?</summary>
    public static bool SmallGroup(OkrNode n, Guid? viewer, bool privileged)
    {
        if (privileged || n.Kind == "Goal") return false;
        var others = n.Employees.Count(e => e != viewer);
        return others is > 0 and < MinGroup;
    }

    /// <summary>
    /// İlerlemesi gizlenecek amaçlar. Gizli bir alt amacın değeri, ebeveynin ortalamasından ve kardeşlerin
    /// görünen değerlerinden geri hesaplanabileceği için gizlilik yukarı doğru yayılır.
    /// </summary>
    public static HashSet<Guid> Suppressed(IEnumerable<OkrNode> roots, Guid? viewer, bool privileged)
    {
        var set = new HashSet<Guid>();
        if (privileged) return set;
        bool Visit(OkrNode n)
        {
            var hidden = SmallGroup(n, viewer, privileged);
            foreach (var c in n.Children) hidden |= Visit(c) && c.Kind != "Goal";
            if (hidden && n.Kind != "Goal") set.Add(n.Id);
            return hidden && n.Kind != "Goal";
        }
        foreach (var r in roots) Visit(r);
        return set;
    }
}
