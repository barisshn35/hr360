using ExpenseService.Models;
using ExpenseService.Services;
using Xunit;

namespace Expense.Tests;

public class DocumentComplianceTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static Document Doc(DocumentType t, DateOnly? until = null, DateTime? uploaded = null) => new()
    {
        Type = t, FileName = "f", StorageKey = "", ValidUntil = until,
        UploadedAt = new DateTimeOffset(uploaded ?? new DateTime(2026, 1, 1), TimeSpan.Zero),
    };

    [Fact]
    public void Company_wide_requirement_reports_missing_and_department_requirement_only_applies_to_that_department()
    {
        Guid dep = Guid.NewGuid(), other = Guid.NewGuid();
        var reqs = new[] { new DocumentRequirement { Type = DocumentType.Contract }, new DocumentRequirement { Type = DocumentType.Health, DepartmentId = dep } };
        Assert.Equal([DocumentType.Contract, DocumentType.Health], DocumentCompliance.Evaluate(reqs, dep, [], Today).Select(i => i.Type));
        Assert.Equal([DocumentType.Contract], DocumentCompliance.Evaluate(reqs, other, [], Today).Select(i => i.Type));
        Assert.Empty(DocumentCompliance.Evaluate(reqs, other, [Doc(DocumentType.Contract)], Today));
    }

    [Fact]
    public void Validity_from_document_date_or_requirement_months_marks_expired_and_expiring()
    {
        var reqs = new[] { new DocumentRequirement { Type = DocumentType.Health, ValidityMonths = 12 } };
        var expired = DocumentCompliance.Evaluate(reqs, null, [Doc(DocumentType.Health, uploaded: new DateTime(2025, 9, 1))], Today);
        Assert.Equal("Expired", expired.Single().State);
        var soon = DocumentCompliance.Evaluate(reqs, null, [Doc(DocumentType.Health, until: Today.AddDays(10))], Today);
        Assert.Equal("ExpiringSoon", soon.Single().State);
        // Yenisi yüklenince eski süreli belge sorun çıkarmaz (en uzun geçerlilik).
        Assert.Empty(DocumentCompliance.Evaluate(reqs, null, [Doc(DocumentType.Health, until: Today.AddDays(-5)), Doc(DocumentType.Health, until: Today.AddYears(1))], Today));
    }
}
