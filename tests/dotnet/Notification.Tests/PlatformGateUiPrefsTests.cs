using NotificationService.Tenancy;
using Xunit;

namespace Notification.Tests;

/// <summary>Platform yöneticisinin kendi arayüz tercihleri kiracı izni gerektirmez; bildirimler gerektirir.</summary>
public class PlatformGateUiPrefsTests
{
    [Theory]
    [InlineData("/api/notifications/ui-prefs", true)]
    [InlineData("/api/notifications/ui-prefs/dashboard", true)]
    [InlineData("/api/notifications", false)]
    [InlineData("/api/notifications/unread-count", false)]
    [InlineData("/api/notifications/ui-prefsx", false)]
    public void Ui_prefs_muaf(string path, bool exempt) => Assert.Equal(exempt, PlatformAccessGate.IsExempt(path));
}
