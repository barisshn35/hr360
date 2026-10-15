using EngagementService.Controllers;
using EngagementService.Models;
using Xunit;

namespace Engagement.Tests;

/// <summary>ML dalgası 2: anket konu analizine giden metinler (yalnızca o sorunun dolu metinleri, kimliksiz).</summary>
public class SurveyTopicsTests
{
    [Fact]
    public void Yalnizca_sorunun_dolu_metinleri_gider()
    {
        var responses = new[]
        {
            new SurveyResponse { RespondentKey = "k1", DepartmentName = "Mühendislik", Answers = new() { new SurveyAnswer { QuestionId = "q1", Text = " Yemekler kötü " }, new SurveyAnswer { QuestionId = "q2", Text = "Başka soru" } } },
            new SurveyResponse { RespondentKey = "k2", Answers = new() { new SurveyAnswer { QuestionId = "q1", Text = "   " } } },
            new SurveyResponse { RespondentKey = "k3", Answers = new() { new SurveyAnswer { QuestionId = "q1", Score = 4 } } },
            new SurveyResponse { RespondentKey = "k4", Answers = new() { new SurveyAnswer { QuestionId = "q1", Text = "Ekip iyi" } } },
        };
        var texts = SurveyTopics.Texts(responses, "q1");
        Assert.Equal(new[] { "Ekip iyi", "Yemekler kötü" }, texts.OrderBy(t => t).ToArray());
        Assert.DoesNotContain(texts, t => t.Contains("k1") || t.Contains("Mühendislik"));
    }
}
