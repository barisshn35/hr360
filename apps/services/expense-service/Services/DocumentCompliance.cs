using ExpenseService.Models;

namespace ExpenseService.Services;

/// <summary>Eksik belge takibinin saf kuralı (birim testli).</summary>
public static class DocumentCompliance
{
    public const int SoonDays = 30;

    public sealed record Issue(DocumentType Type, string State, DateOnly? ValidUntil);

    /// <summary>
    /// Bir çalışan için eksik (Missing), süresi dolmuş (Expired) ve 30 gün içinde dolacak (ExpiringSoon)
    /// zorunlu belgeler. Aynı türde birden fazla gereksinim varsa en kısa süre geçerlidir; aynı türde
    /// birden fazla belge varsa geçerliliği en uzun olan dikkate alınır.
    /// </summary>
    public static List<Issue> Evaluate(IEnumerable<DocumentRequirement> requirements, Guid? departmentId,
        IEnumerable<Document> documents, DateOnly today)
    {
        var docs = documents.ToList();
        var issues = new List<Issue>();
        foreach (var g in requirements.Where(r => r.DepartmentId is null || r.DepartmentId == departmentId).GroupBy(r => r.Type))
        {
            var months = g.Where(r => r.ValidityMonths is > 0).Select(r => r.ValidityMonths!.Value).DefaultIfEmpty(0).Min();
            var ofType = docs.Where(d => d.Type == g.Key).ToList();
            if (ofType.Count == 0) { issues.Add(new(g.Key, "Missing", null)); continue; }
            DateOnly? Until(Document d) => d.ValidUntil ?? (months > 0 ? DateOnly.FromDateTime(d.UploadedAt.UtcDateTime).AddMonths(months) : null);
            var untils = ofType.Select(Until).ToList();
            if (untils.Any(u => u is null)) continue; // süresiz belge var
            var best = untils.Max()!.Value;
            if (best < today) issues.Add(new(g.Key, "Expired", best));
            else if (best <= today.AddDays(SoonDays)) issues.Add(new(g.Key, "ExpiringSoon", best));
        }
        return issues.OrderBy(i => i.State == "Missing" ? 0 : i.State == "Expired" ? 1 : 2).ThenBy(i => i.Type).ToList();
    }
}
