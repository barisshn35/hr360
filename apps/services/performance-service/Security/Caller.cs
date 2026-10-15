using System.Security.Claims;

namespace PerformanceService.Security;

/// <summary>
/// Program.cs'teki politikalarla ayni rol kumeleri - denetimde bulunan kimlik/sahiplik
/// kontrollerinin (IDOR) tek yerden okunmasi icin.
/// </summary>
public static class Caller
{
    public static bool IsHr(this ClaimsPrincipal u) =>
        u.IsInRole("hr-admin") || u.IsInRole("tenant-admin") || u.IsInRole("platform-admin")
        || u.IsInRole("ext-performance-manage");

    public static bool IsManagerOrAbove(this ClaimsPrincipal u) => u.IsHr() || u.IsInRole("manager");
}
