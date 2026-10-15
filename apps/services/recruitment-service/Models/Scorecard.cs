using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

/// <summary>Puan kartı ölçütü: anahtar, ad ve ağırlık (1-5). Puanlar 1-5 ölçeğinde verilir.</summary>
public record ScorecardCriterion(string Key, string Label, int Weight);

/// <summary>Ölçüt puanı; Evidence: puanı destekleyen gözlem/kanıt notu (Dalga 11, isteğe bağlı).</summary>
public record CriterionScore(string Key, int Score, string? Evidence = null);

/// <summary>Y17: ilan başına yapılandırılmış mülakat değerlendirme şablonu.</summary>
public class ScorecardTemplate : ITenantOwned
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobPostingId { get; set; }
    [JsonIgnore] public string CriteriaJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [NotMapped]
    public List<ScorecardCriterion> Criteria
    {
        get => JsonSerializer.Deserialize<List<ScorecardCriterion>>(CriteriaJson, Json) ?? new();
        set => CriteriaJson = JsonSerializer.Serialize(value, Json);
    }
}

/// <summary>Bir görüşmecinin bir mülakat için doldurduğu puan kartı.</summary>
public class Scorecard : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InterviewId { get; set; }
    public Guid InterviewerEmployeeId { get; set; }
    [JsonIgnore] public string ScoresJson { get; set; } = "[]";
    public decimal? OverallScore { get; set; }
    /// <summary>StrongNo | No | Yes | StrongYes (isteğe bağlı).</summary>
    public string? Recommendation { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;

    [NotMapped]
    public List<CriterionScore> Scores
    {
        get => JsonSerializer.Deserialize<List<CriterionScore>>(ScoresJson, ScorecardTemplate.Json) ?? new();
        set => ScoresJson = JsonSerializer.Serialize(value, ScorecardTemplate.Json);
    }
}
