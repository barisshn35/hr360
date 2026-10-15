using OrganizationService.Models;

namespace OrganizationService.Infrastructure;

/// <summary>
/// Matris bağı doğrulaması — veritabanından bağımsız, saf kurallar (birim testli:
/// tests/dotnet/Organization.Tests). Denetleyici departmanları kiracı filtresiyle okuyup buraya verir.
/// </summary>
public static class DepartmentLinkRules
{
    public const int NoteMaxLength = 500;

    /// <summary>Departmanın kural için gereken alanları.</summary>
    public sealed record DeptInfo(Guid Id, string TenantSlug);

    /// <summary>Var olan bağın kural için gereken alanları.</summary>
    public sealed record LinkInfo(Guid FromDepartmentId, Guid ToDepartmentId, string Kind);

    /// <summary>Türü standart yazıma çevirir ("project" → "Project"); tanınmıyorsa null.</summary>
    public static string? NormalizeKind(string? kind) =>
        DepartmentLinkKinds.All.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Hata iletisi (Türkçe; arayüz "@server:" anahtarıyla çevirir) ya da geçerliyse null.
    /// <paramref name="from"/>/<paramref name="to"/> null: departman bu kiracıda bulunamadı.
    /// <paramref name="tenantSlug"/>: isteğin kiracısı (platform yöneticisinde null olabilir).
    /// </summary>
    public static string? Validate(Guid fromId, Guid toId, string? kind, string? note,
        DeptInfo? from, DeptInfo? to, string? tenantSlug, IEnumerable<LinkInfo> existing)
    {
        if (fromId == Guid.Empty || toId == Guid.Empty) return "Her iki departman da seçilmeli.";
        if (fromId == toId) return "Bir departman kendisine bağlanamaz.";
        var k = NormalizeKind(kind);
        if (k is null) return "Bağ türü geçersiz (Functional ya da Project).";
        if (note is { Length: > NoteMaxLength }) return $"Not en fazla {NoteMaxLength} karakter olabilir.";
        if (from is null || to is null) return "Departman bulunamadı.";
        // Kiracı filtresi platform yöneticisinde uygulanmaz: iki uç ayrıca aynı kiracıda olmalı.
        if (from.TenantSlug != to.TenantSlug) return "Departmanlar aynı kiracıda olmalı.";
        if (!string.IsNullOrEmpty(tenantSlug) && from.TenantSlug != tenantSlug) return "Departman bulunamadı.";
        if (existing.Any(l => l.FromDepartmentId == fromId && l.ToDepartmentId == toId && l.Kind == k))
            return "Bu bağ zaten var.";
        return null;
    }
}
