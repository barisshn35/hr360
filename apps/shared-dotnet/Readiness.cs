using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace HR360.Platform;

/// <summary>
/// Hazırlık denetimi: <c>/health</c> yalnızca sürecin ayakta olduğunu söyler; <c>/health/ready</c> veritabanına
/// gerçekten bağlanılabildiğini de doğrular (3 sn sınır). update.sh güncellemeden sonra bunu bekler.
/// İç ağdan çağrılır; ağ geçidi /health uçlarını dışarı açmaz.
/// </summary>
public static class Readiness
{
    public static void MapReadiness<TDb>(this WebApplication app, string service) where TDb : DbContext =>
        app.MapGet("/health/ready", async (TDb db, CancellationToken ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            bool ok;
            try { ok = await db.Database.CanConnectAsync(cts.Token); }
            catch (Exception) { ok = false; }
            return ok
                ? Results.Ok(new { status = "ok", service, db = "ok" })
                : Results.Json(new { status = "unavailable", service, db = "down" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
}
