using NotificationService.Email;
using Xunit;

namespace Notification.Tests;

public class UndeliverableAddressTests
{
    [Theory]
    [InlineData("berk.aslan@ornek.invalid", true)]
    [InlineData("X@ORNEK.INVALID", true)]
    [InlineData("a@ornek.invalid.", true)]
    [InlineData("a@invalid-domain.com", false)]
    [InlineData("a@example.com", false)]
    [InlineData(null, false)]
    public void Reserved_invalid_tld_is_never_sent(string? email, bool undeliverable) =>
        Assert.Equal(undeliverable, EmailSenderWorker.IsUndeliverable(email));
}
