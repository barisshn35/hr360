using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeShiftService.Controllers;
using TimeShiftService.Data;
using TimeShiftService.Models;
using TimeShiftService.Tenancy;
using Xunit;

namespace TimeShift.Tests;

/// <summary>
/// Sohbet iç uçları (/api/internal/chat/*): anahtar denetimi ve ClockCore / takas çekirdeği kuralları.
/// INTERNAL_SERVICE_TOKEN ortam değişkenine dokunan testler aynı sınıfta (sıralı) çalışır.
/// </summary>
public class InternalChatTests
{
    private const string Token = "test-internal-token";
    private const string Tenant = "t-chat";

    private static (InternalChatController C, TimeShiftDbContext Db) Make(string? header, string? dbName = null)
    {
        var tenant = new TenantContext();
        var db = new TimeShiftDbContext(new DbContextOptionsBuilder<TimeShiftDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options, tenant);
        var http = new DefaultHttpContext();
        if (header is not null) http.Request.Headers["X-Internal-Token"] = header;
        var c = new InternalChatController(db, null!, tenant) { ControllerContext = new ControllerContext { HttpContext = http } };
        return (c, db);
    }

    private static string? Code(IActionResult r) =>
        (r as ObjectResult)?.Value?.GetType().GetProperty("code")?.GetValue(((ObjectResult)r).Value) as string;

    [Fact]
    public async Task Anahtar_yoksa_ya_da_yanlissa_uc_yok_sayilir()
    {
        var body = new InternalChatController.PunchBody(Tenant, Guid.NewGuid(), "in", "slack");
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null);
        Assert.IsType<NotFoundResult>(await Make(Token).C.Punch(body, default));

        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);
        try
        {
            Assert.IsType<NotFoundResult>(await Make(null).C.Punch(body, default));
            Assert.IsType<NotFoundResult>(await Make("yanlis").C.Punch(body, default));
            Assert.IsType<NotFoundResult>(await Make(Token + "x").C.SwapRespond(new(Tenant, Guid.NewGuid(), Guid.NewGuid(), true, null), default));
            Assert.IsType<NotFoundResult>(await Make("").C.SwapDecide(new(Tenant, Guid.NewGuid(), Guid.NewGuid(), true, null, null), default));
            // Doğru anahtar ama kiracı / işlem eksik.
            Assert.IsType<BadRequestObjectResult>(await Make(Token).C.Punch(body with { TenantSlug = " " }, default));
            Assert.IsType<BadRequestObjectResult>(await Make(Token).C.Punch(body with { Kind = "auto" }, default));
            Assert.IsType<BadRequestObjectResult>(await Make(Token).C.Punch(body with { EmployeeId = Guid.Empty }, default));
        }
        finally { Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null); }
    }

    [Fact]
    public async Task Sohbet_giris_cikis_ClockCore_kurallari()
    {
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);
        try
        {
            var dbName = Guid.NewGuid().ToString();
            var emp = Guid.NewGuid();
            var p = new InternalChatController.PunchBody(Tenant, emp, "in", "teams");

            Assert.IsType<OkObjectResult>(await Make(Token, dbName).C.Punch(p, default));
            var second = await Make(Token, dbName).C.Punch(p, default);
            Assert.IsType<ConflictObjectResult>(second);
            Assert.Equal("open_entry", Code(second));

            Assert.IsType<OkObjectResult>(await Make(Token, dbName).C.Punch(p with { Kind = "out" }, default));
            var again = await Make(Token, dbName).C.Punch(p with { Kind = "out" }, default);
            Assert.Equal("no_open_entry", Code(again));
            // Aynı gün ikinci giriş: günde bir giriş kuralı.
            Assert.Equal("already_in", Code(await Make(Token, dbName).C.Punch(p, default)));

            var (_, db) = Make(null, dbName);
            var entry = await db.TimeEntries.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(Tenant, entry.TenantSlug);
            Assert.Equal(TimeEntrySource.Chat, entry.Source);
            Assert.NotNull(entry.ClockOut);
            Assert.Equal(0, entry.OvertimeMinutes);
            var punches = await db.TimeClockPunches.IgnoreQueryFilters().OrderBy(x => x.At).ToListAsync();
            Assert.Equal(new[] { PunchKind.In, PunchKind.Out }, punches.Select(x => x.Kind));
            Assert.All(punches, x => { Assert.Equal(TimeEntrySource.Chat, x.Method); Assert.Null(x.OnSite); Assert.Null(x.SiteId); Assert.Equal(emp, x.EmployeeId); });
        }
        finally { Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null); }
    }

    [Fact]
    public async Task Takas_yaniti_ve_karari_durum_ve_taraf_kurallari()
    {
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", Token);
        try
        {
            var dbName = Guid.NewGuid().ToString();
            Guid req = Guid.NewGuid(), tgt = Guid.NewGuid();
            var pendingPeer = new ShiftSwapRequest { TenantSlug = Tenant, RequesterEmployeeId = req, TargetEmployeeId = tgt, RequesterAssignmentId = Guid.NewGuid() };
            var approved = new ShiftSwapRequest { TenantSlug = Tenant, RequesterEmployeeId = req, TargetEmployeeId = tgt, RequesterAssignmentId = Guid.NewGuid(), Status = SwapStatus.Approved };
            var otherTenant = new ShiftSwapRequest { TenantSlug = "baska", RequesterEmployeeId = req, TargetEmployeeId = tgt, RequesterAssignmentId = Guid.NewGuid() };
            var (_, seed) = Make(null, dbName);
            seed.ShiftSwapRequests.AddRange(pendingPeer, approved, otherTenant);
            await seed.SaveChangesAsync();

            // Yalnızca hedef kişi yanıtlar; başka kiracının talebi görünmez.
            Assert.IsType<NotFoundObjectResult>(await Make(Token, dbName).C.SwapRespond(new(Tenant, req, pendingPeer.Id, true, "slack"), default));
            Assert.IsType<NotFoundObjectResult>(await Make(Token, dbName).C.SwapRespond(new(Tenant, tgt, otherTenant.Id, true, "slack"), default));
            var notPending = await Make(Token, dbName).C.SwapRespond(new(Tenant, tgt, approved.Id, true, "slack"), default);
            Assert.IsType<ConflictObjectResult>(notPending);
            Assert.Equal("not_pending", Code(notPending));

            // Taraflar kendi takasına karar veremez; bulunmayan talep 404.
            var self = await Make(Token, dbName).C.SwapDecide(new(Tenant, req, approved.Id, true, null, "slack"), default);
            Assert.Equal(403, Assert.IsType<ObjectResult>(self).StatusCode);
            Assert.IsType<NotFoundObjectResult>(await Make(Token, dbName).C.SwapDecide(new(Tenant, Guid.NewGuid(), Guid.NewGuid(), false, null, null), default));

            var (_, check) = Make(null, dbName);
            Assert.Equal(SwapStatus.PendingPeer, (await check.ShiftSwapRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == pendingPeer.Id)).Status);
        }
        finally { Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", null); }
    }

    [Fact]
    public void Karar_yetkisi_bolum_basi_ve_taraf_degil()
    {
        Guid req = Guid.NewGuid(), tgt = Guid.NewGuid(), head = Guid.NewGuid();
        var s = new ShiftSwapRequest { RequesterEmployeeId = req, TargetEmployeeId = tgt };
        Assert.True(ShiftSwapsController.IsHeadDecider(s, head, head));
        Assert.False(ShiftSwapsController.IsHeadDecider(s, Guid.NewGuid(), head));
        Assert.False(ShiftSwapsController.IsHeadDecider(s, head, null));
        // Bölüm başı aynı zamanda taraf ise karar veremez.
        Assert.False(ShiftSwapsController.IsHeadDecider(new ShiftSwapRequest { RequesterEmployeeId = req, TargetEmployeeId = head }, head, head));
        Assert.False(ShiftSwapsController.IsHeadDecider(s, req, req));
        Assert.True(InternalChatController.TokenOk("abc", "abc"));
        Assert.False(InternalChatController.TokenOk("", ""));
        Assert.False(InternalChatController.TokenOk(null, null));
        Assert.False(InternalChatController.TokenOk("abc", "abd"));
    }
}
