using NotificationService.Messaging;
using Xunit;

namespace Notification.Tests;

/// <summary>Onaycı bildirimi: "aşağıdaki düğme" cümlesi yalnızca e-postaya eklenir, kayıtlı gövdede yer almaz.</summary>
public class NotificationTextsTests
{
    [Fact]
    public void Kayitli_govdede_dugme_cumlesi_yok()
    {
        var (_, body) = NotificationTexts.Submitted("tr", "Mehmet", "Ayşe Yılmaz", "LeaveRequest", null, hasLink: true);
        Assert.DoesNotContain("Aşağıdaki", body);
        Assert.Contains("Onay kutusu", body);
    }

    [Fact]
    public void Eposta_ipucu_yalnizca_karar_baglantili_sablonda()
    {
        Assert.Contains("Aşağıdaki düğmeyle", NotificationTexts.EmailActionHint("workflow.submitted", "tr"));
        Assert.Contains("button below", NotificationTexts.EmailActionHint("workflow.submitted", "en"));
        Assert.Null(NotificationTexts.EmailActionHint("workflow.approved", "tr"));
    }
}
