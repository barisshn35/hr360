using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure.Chat;

/* ======================================================================
 * Dalga 5e — iş özellikleri: fişten masraf (B6), izin iptali (B8), teşekkür
 * (B10), masa (B13), vardiya/takas (B14), duyuru (B15), belge (B16),
 * giriş-çıkış (B17), bordro özeti (BG13), İK vakası (BG18), toplu onay (BG5),
 * onay kartı zenginleştirme (BG4) ve düğme eylemleri.
 * ==================================================================== */
public sealed partial class ChatFeatures
{
    /// <summary>Bu sınıfın yanıtladığı komutlar (diğerleri ChatService'te).</summary>
    public static readonly HashSet<string> OwnCommands = new()
    {
        "cancelleave", "expense", "kudos", "desk", "shifts", "swaps", "announcements", "documents",
        "clockin", "clockout", "payslip", "hrcase", "forget", "anniversary", "code",
    };

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static string UserIdOf(Person p) => p.UserId ?? $"employee:{p.Id}";

    /// <summary>
    /// Uygulama içi bildirim (notification-service gelen kutusu, iç uç); yalnızca gerekli bilgi.
    /// En iyi çaba: bildirim oluşturulamazsa kullanıcı akışı bozulmaz, yalnızca log'a düşer.
    /// </summary>
    private async Task NotifyAsync(string tenant, Guid recipient, string subject, string body, string code, CancellationToken ct)
    {
        var r = await ChatInternal.PostAsync(_http, ChatInternal.NotificationBase, "/api/internal/chat/notify",
            new { tenantSlug = tenant, recipientEmployeeId = recipient, subject, body, templateCode = code, language = "tr" }, false, ct);
        if (!r.Ok) _log.LogDebug("Sohbet bildirimi oluşturulamadı (HTTP {Status}): {Message}", r.Status, r.Message);
    }

    // ================================================================== komutlar

    public async Task<ChatReply> CommandAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, ResolvedCommand rc, bool en, CancellationToken ct)
    {
        var tenant = who.TenantSlug;
        var emp = who.EmployeeId!.Value;
        string L(string a, string b) => en ? b : a;
        switch (rc.Cmd)
        {
            case "cancelleave": return await CancelLeaveListAsync(tenant, emp, en, ct);
            case "expense": return await ExpenseCommandAsync(app, emp, rc.Args, en, ct);
            case "kudos": return await KudosAsync(db, app, who, rc.Args, en, ct);
            case "desk": return await DeskListAsync(tenant, emp, ParseDay(rc.Args) ?? Today, en, ct);
            case "shifts": return await ShiftsAsync(tenant, emp, en, ct);
            case "swaps": return await SwapsAsync(tenant, emp, en, ct);
            case "announcements": return await AnnouncementsAsync(tenant, emp, en, ct);
            case "documents": return await DocumentsAsync(tenant, emp, en, ct);
            case "clockin": return await ClockAsync(app, tenant, emp, true, en, ct);
            case "clockout": return await ClockAsync(app, tenant, emp, false, en, ct);
            case "payslip":
                return await StepUpAsync(app, emp, "payslip", new { }, L("Son bordro özetinizi görüntüleme", "View your latest payslip summary"), en, ct);
            case "hrcase": return await HrCaseOfferAsync(app, emp, rc.Args, en, ct);
            case "forget":
            {
                var n = await ForgetAsync(tenant, emp, ct);
                await CountAsync(app, "context", "forget", ct);
                return ChatReply.Of(L($"🧹 Asistan konuşma geçmişiniz silindi ({n / 2} soru). Bundan sonraki sorular yine 30 gün hatırlanır; istediğinizde *geçmişimi sil* yazabilirsiniz.",
                    $"🧹 Your assistant conversation history was deleted ({n / 2} question(s)). New questions are remembered for 30 days; type *forget me* any time."), en);
            }
            case "anniversary": return await AnniversaryAsync(tenant, emp, rc.Args, en, ct);
            case "code": return await CodeAsync(db, app, who, rc.Args, en, ct);
        }
        return ChatReply.Of(ChatService.HelpOf(en), en);
    }

    // ================================================================== düğme eylemleri

    /// <summary>Düğme eyleminin özellik anahtarı (BG20: kapalı özelliğin düğmesi de çalışmaz).</summary>
    public static string? ActionFeature(string action) => action switch
    {
        "decide" or "approveall" or "approveall_yes" => "approvals",
        "leave_cancel" or "leave_cancel_yes" => "leavecancel",
        "exp_confirm" or "exp_cancel" or "exp_edit" => "expense",
        "desk_book" or "desk_cancel" or "desk_day" => "desk",
        "swap_accept" or "swap_decline" or "swap_approve" or "swap_reject" => "shifts",
        "ann_ack" => "announcements",
        "doc_req" => "documents",
        "clock" => "clock",
        "pulse" => "pulse",
        "onb_done" => "onboarding",
        "exit" => "exit",
        "hrcase_open" => "hrcase",
        "anniv" => "celebrations",
        _ => null,
    };

    public async Task<ChatReply> ActionAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, string action, string stamped, CancellationToken ct, HrAssistant? assistant = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var provider = ChatMetrics.Provider(app.Platform);
        var (value, issued) = ButtonStamp.Decode(stamped);
        var en = await _chat.EnAsync(app.TenantSlug, who.EmployeeId, ct);
        string L(string a, string b) => en ? b : a;
        ChatReply Done(ChatReply r, string outcome)
        {
            ChatMetrics.Commands.WithLabels(provider, "btn_" + ChatMetrics.Intent(action), outcome).Inc();
            ChatMetrics.Latency.WithLabels(provider).Observe(sw.Elapsed.TotalSeconds);
            return r;
        }

        // BG10: eski düğmeler — nazik bir mesajla geçersiz.
        if (ButtonStamp.Expired(issued, DateTimeOffset.UtcNow, app.ButtonTtlDays))
        {
            await CountAsync(app, "button", "expired", ct);
            return Done(ChatReply.Of(L($"⌛ Bu düğmenin süresi doldu ({app.ButtonTtlDays} günden eski). Güncel hâli için komutu yeniden yazın ya da *yardım* deyin.",
                $"⌛ This button has expired (older than {app.ButtonTtlDays} days). Type the command again or say *help*."), en,
                ChatButton.Say(L("Yardım", "Help"), L("yardım", "help"))) with { Replace = true }, "expired");
        }
        if (action == "say") return Done(await _chat.CommandAsync(db, app, who, value, ct, assistant), "ok");
        if (action == "cancel")
        {
            if (Guid.TryParse(value, out var pid)) await PendingCloseAsync(pid, "Cancelled", ct);
            return Done(ChatReply.Of(L("Vazgeçildi.", "Cancelled."), en) with { Replace = true }, "ok");
        }
        if (!await _chat.EnsureActiveAsync(db, who, ct))
            return Done(ChatReply.Of(L("Hesabınız etkin bir çalışan kaydıyla eşleşmedi.", "Your account is not matched to an active employee record."), en), "unlinked");
        if (!ChatService.Trusted(app, who))
            return Done(ChatReply.Of(ChatService.LinkPrompt(await _chat.LinkUrlAsync(db, who, TimeSpan.FromHours(24), ct), en: en), en), "unverified");
        if (ActionFeature(action) is { } feature && app.DisabledFeatures.Contains(feature))
            return Done(ChatReply.Of(L("Bu komut şirketinizde kapalı.", "This command is turned off in your company."), en) with { Replace = true }, "disabled");

        var tenant = who.TenantSlug;
        var emp = who.EmployeeId!.Value;
        ChatReply reply;
        try
        {
            reply = action switch
            {
                "decide" => await DecideButtonAsync(db, app, who, value, en, ct),
                "approveall" => await ApproveAllAsync(app, emp, value, en, ct),
                "approveall_yes" => await ApproveAllConfirmAsync(app, emp, value, en, ct),
                "leave_cancel" => await CancelLeaveConfirmAsync(tenant, emp, value, en, ct),
                "leave_cancel_yes" => await CancelLeaveAsync(app, tenant, emp, value, en, ct),
                "exp_confirm" => await ExpenseConfirmAsync(app, emp, value, null, null, null, en, ct),
                "exp_cancel" => await ExpenseCancelAsync(tenant, emp, value, en, ct),
                "exp_edit" => ChatReply.Of(L("Düzeltmek için *masraf <tutar> <gg.aa.yyyy> <kategori>* yazın (ör. *masraf 245,50 01.10.2026 yemek*).",
                    "To correct, type *expense <amount> <dd.mm.yyyy> <category>* (e.g. *expense 245.50 01.10.2026 meal*)."), en),
                "desk_book" => await DeskBookAsync(app, tenant, emp, value, en, ct),
                "desk_cancel" => await DeskCancelAsync(app, tenant, emp, value, en, ct),
                "desk_day" => await DeskListAsync(tenant, emp, DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var day) ? day : Today, en, ct) with { Replace = true },
                "swap_accept" => await SwapRespondAsync(app, tenant, emp, value, true, en, ct),
                "swap_decline" => await SwapRespondAsync(app, tenant, emp, value, false, en, ct),
                "swap_approve" => await SwapDecideAsync(app, tenant, emp, value, true, en, ct),
                "swap_reject" => await SwapDecideAsync(app, tenant, emp, value, false, en, ct),
                "ann_ack" => await AnnouncementAckAsync(app, tenant, emp, value, en, ct),
                "doc_req" => await DocumentRequestAsync(db, app, tenant, emp, value, en, ct),
                "clock" => await ClockAsync(app, tenant, emp, value == "in", en, ct) with { Replace = true },
                "pulse" => await PulseAnswerAsync(app, tenant, emp, value, en, ct),
                "onb_done" => await OnboardingDoneAsync(app, tenant, emp, value, en, ct),
                "exit" => await ExitAnswerAsync(app, tenant, emp, value, en, ct),
                "hrcase_open" => await HrCaseOpenAsync(app, tenant, emp, value, en, ct),
                "anniv" => await AnniversaryAsync(tenant, emp, value, en, ct) with { Replace = true },
                _ => ChatReply.Of(L("Bu düğme artık desteklenmiyor.", "This button is no longer supported."), en) with { Replace = true },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ChatApiException)
        {
            _log.LogWarning(ex, "Sohbet eylemi işlenemedi ({Action})", action);
            await ErrorAsync(app, "btn_" + action, ex.GetType().Name + ": " + ex.Message);
            return Done(ChatReply.Of(L("⚠️ İşlem tamamlanamadı; HR360 üzerinden deneyin.", "⚠️ The action could not be completed; please try in HR360."), en), "error");
        }
        await CountAsync(app, ActionFeature(action) ?? action, "ok", ct);
        return Done(reply, "ok");
    }

    // ================================================================== BG4 onay kartı ayrıntısı

    /// <summary>
    /// İzin onay kartına: talep edenin kalan bakiyesi ve aynı günlerde ekipten kaç kişinin izinde
    /// olduğu (yalnızca sayı; başkalarının adı hiçbir zaman yazılmaz).
    /// </summary>
    public async Task<PendingApproval> EnrichAsync(string tenant, PendingApproval p, bool en, CancellationToken ct)
    {
        if (p.Type != "LeaveRequest") return p;
        try
        {
            var lr = (await _sql.QueryAsync("""
                SELECT "EmployeeId", "Type", "StartDate", "EndDate" FROM leave_requests WHERE "TenantSlug" = $1 AND "WorkflowRequestId" = $2 LIMIT 1
                """, r => (Emp: r.GetGuid(0), Type: r.GetString(1), S: r.GetFieldValue<DateOnly>(2), E: r.GetFieldValue<DateOnly>(3)), ct, tenant, p.WorkflowId)).FirstOrDefault();
            if (lr.Emp == Guid.Empty) return p;
            var extra = new List<string>();
            var remaining = await _sql.ScalarAsync("""
                SELECT "EntitledDays" - "UsedDays" - "PendingDays" FROM leave_balances WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Year" = $3 AND "Type" = $4
                """, ct, tenant, lr.Emp, lr.S.Year, lr.Type);
            if (remaining is not null)
                extra.Add(en ? $"Balance left: {Convert.ToDecimal(remaining):0.#} day(s)" : $"Kalan bakiye: {Convert.ToDecimal(remaining):0.#} gün");
            var me = await _people.FindAsync(tenant, lr.Emp, ct);
            if (me?.DepartmentId is { } dept)
            {
                var off = Convert.ToInt64(await _sql.ScalarAsync("""
                    SELECT count(DISTINCT l."EmployeeId") FROM leave_requests l
                    JOIN employee_assignments a ON a."EmployeeId" = l."EmployeeId" AND a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
                    WHERE l."TenantSlug" = $1 AND l."Status" = 'Approved' AND l."StartDate" <= $3 AND l."EndDate" >= $2 AND a."DepartmentId" = $4 AND l."EmployeeId" <> $5
                    """, ct, tenant, lr.S, lr.E, dept, lr.Emp));
                extra.Add(en ? $"{off} from the same team already off on those days" : $"Aynı günlerde ekipten {off} kişi izinde");
            }
            return extra.Count == 0 ? p : p with { Details = (p.Details is null ? "" : p.Details + " · ") + string.Join(" · ", extra) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Onay kartı zenginleştirilemedi");
            return p;
        }
    }

    // ================================================================== BG5 toplu onay + BG13

    /// <summary>Ücretle ilgili onaylar (pozisyon/ücret değişikliği, teklif) sohbetten ek doğrulama ister.</summary>
    public async Task<bool> SensitiveWorkflowAsync(string tenant, Guid wfId, CancellationToken ct)
    {
        var row = (await _sql.QueryAsync("SELECT \"Type\", coalesce(\"Payload\", '') FROM workflow_requests WHERE \"TenantSlug\" = $1 AND \"Id\" = $2",
            r => (Type: r.GetString(0), Payload: r.GetString(1)), ct, tenant, wfId)).FirstOrDefault();
        if (row.Type is null) return false;
        if (row.Type is "PositionChange" or "OfferApproval") return true;
        var p = row.Payload.ToLowerInvariant();
        return p.Contains("\"salary") || p.Contains("maas") || p.Contains("payroll") || p.Contains("\"wage");
    }

    private async Task<ChatReply> DecideButtonAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, string value, bool en, CancellationToken ct)
    {
        var parts = value.Split('|');
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out var wf) || !Guid.TryParse(parts[1], out var step))
            return ChatReply.Of(en ? "Invalid button." : "Geçersiz düğme.", en);
        var r = await _chat.DecideAsync(db, app, who, wf, step, parts[2] == "a", ct);
        if (r.StepUp is { } stepUp) return stepUp;
        return ChatReply.Of((r.Ok ? (parts[2] == "a" ? "✅ " : "⛔ ") : "⚠️ ") + r.Message, en, ChatButton.Link(en ? "Open in HR360" : "HR360'ta aç", ChatService.WorkflowUrl(wf)))
            with { Replace = r.Ok };
    }

    /// <summary>Toplu onay; <paramref name="type"/> doluysa yalnızca o türdeki talepler ("tüm izinleri onayla").</summary>
    private async Task<ChatReply> ApproveAllAsync(ChatApp app, Guid emp, string type, bool en, CancellationToken ct)
    {
        var items = (await _chat.MyPendingAsync(app.TenantSlug, emp, 50, ct)).Where(i => string.IsNullOrEmpty(type) || i.Type == type).ToList();
        if (items.Count == 0) return ChatReply.Of(en ? "Nothing is awaiting your decision. 🎉" : "Karar bekleyen talebiniz yok. 🎉", en) with { Replace = true };
        var byType = items.GroupBy(i => ChatService.TypeLabel(i.Type, en)).Select(g => $"{g.Count()} {g.Key}");
        var id = await PendingAddAsync(app, emp, "bulk", new { items = items.Select(i => new { wf = i.WorkflowId, step = i.StepId }).ToList() }, TimeSpan.FromMinutes(15), null, ct);
        return ChatReply.Of(en
                ? $"You are about to approve *{items.Count}* request(s) ({string.Join(", ", byType)}). Are you sure?"
                : $"*{items.Count}* talebi ({string.Join(", ", byType)}) onaylamak üzeresiniz. Emin misiniz?", en,
            new ChatButton(en ? $"Yes, approve {items.Count}" : $"Evet, {items.Count} talebi onayla", "approveall_yes", id.ToString(), "primary"),
            new ChatButton(en ? "Cancel" : "Vazgeç", "cancel", id.ToString())) with { Replace = true };
    }

    private async Task<ChatReply> ApproveAllConfirmAsync(ChatApp app, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id) || await PendingGetAsync(app.TenantSlug, emp, id, ct) is not { Kind: "bulk" } p)
            return ChatReply.Of(en ? "This confirmation has expired; type *approvals* again." : "Bu onayın süresi doldu; *onaylarım* yazarak yeniden başlayın.", en) with { Replace = true };
        await PendingCloseAsync(id, "Done", ct);
        var n = p.Payload.GetProperty("items").GetArrayLength();
        // Toplu onay her zaman ek doğrulama ister (BG13).
        return await StepUpAsync(app, emp, "bulk", JsonSerializer.Deserialize<object>(p.Payload.GetRawText())!, en ? $"Approve {n} request(s) at once" : $"{n} talebi toplu onaylama", en, ct);
    }

    private async Task<string> BulkApproveAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, JsonElement payload, bool en, CancellationToken ct)
    {
        int ok = 0, fail = 0;
        foreach (var it in payload.GetProperty("items").EnumerateArray())
        {
            var r = await _chat.DecideAsync(db, app, who, it.GetProperty("wf").GetGuid(), it.GetProperty("step").GetGuid(), true, ct, null, stepUpDone: true);
            if (r.Ok) ok++; else fail++;
        }
        await CountAsync(app, "approvals", "bulk", ct);
        return en
            ? $"✅ {ok} request(s) approved." + (fail > 0 ? $" {fail} could not be approved (already decided or not yours); see HR360." : "")
            : $"✅ {ok} talep onaylandı." + (fail > 0 ? $" {fail} talep onaylanamadı (karara bağlanmış ya da size ait değil); HR360'ta bakın." : "");
    }

    private async Task<string> PayslipTextAsync(string tenant, Guid emp, bool en, CancellationToken ct)
    {
        var row = (await _sql.QueryAsync("""
            SELECT s."Year", s."Month", s."Net", s."Gross", s."Currency" FROM compensation_payslips s
            JOIN compensation_payroll_periods p ON p."Id" = s."PeriodId"
            WHERE s."TenantSlug" = $1 AND s."EmployeeId" = $2 AND p."Status" = 'Closed'
            ORDER BY s."Year" DESC, s."Month" DESC LIMIT 1
            """, r => (Y: r.GetInt32(0), M: r.GetInt32(1), Net: r.GetDecimal(2), Gross: r.GetDecimal(3), Cur: r.GetString(4)), ct, tenant, emp)).FirstOrDefault();
        await AuditAsync(tenant, emp, "Payslip", emp.ToString(), "SensitiveViewed", new { field = "payslipSummary", channel = "chat" }, "chat");
        if (row.Y == 0) return en ? "You have no closed payslip yet." : "Henüz kapanmış bir bordronuz yok.";
        var month = new DateTime(row.Y, row.M, 1).ToString("MMMM yyyy", en ? CultureInfo.GetCultureInfo("en-GB") : Tr);
        return en
            ? $"🧾 Your {month} payslip: net *{row.Net.ToString("N2", CultureInfo.GetCultureInfo("en-GB"))} {row.Cur}* (gross {row.Gross.ToString("N2", CultureInfo.GetCultureInfo("en-GB"))}). Details: {Origin}/panel/bordrolarim"
            : $"🧾 {month} bordronuz: net *{row.Net.ToString("N2", Tr)} {row.Cur}* (brüt {row.Gross.ToString("N2", Tr)}). Ayrıntı: {Origin}/panel/bordrolarim";
    }

    // ================================================================== B8 izin iptali

    private async Task<ChatReply> CancelLeaveListAsync(string tenant, Guid emp, bool en, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync("""
            SELECT "Id", "Type", "StartDate", "EndDate", "Status" FROM leave_requests
            WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Status" IN ('Submitted','Draft','Approved') AND "EndDate" >= $3
            ORDER BY ("Status" = 'Approved'), "StartDate" LIMIT 200
            """, r => (Id: r.GetGuid(0), Type: r.GetString(1), S: r.GetFieldValue<DateOnly>(2), E: r.GetFieldValue<DateOnly>(3), St: r.GetString(4)), ct, tenant, emp, Today);
        if (rows.Count == 0) return ChatReply.Of(en ? "You have no pending or upcoming leave to cancel." : "İptal edilebilecek bekleyen ya da yaklaşan izniniz yok.", en);
        // Slack bir blokta en çok 25 düğme gösterir: en yakın 20 bekleyen talep.
        var pending = rows.Where(r => r.St is "Submitted" or "Draft").Take(20).ToList();
        var approved = rows.Where(r => r.St == "Approved" && r.S > Today).ToList();
        var lines = new List<string>();
        if (pending.Count > 0) lines.Add(en ? "Pending requests you can cancel:" : "İptal edebileceğiniz bekleyen talepler:");
        lines.AddRange(pending.Select(r => $"• {HrAssistant.LeaveLabel(r.Type, en)}: {r.S:dd.MM} – {r.E:dd.MM}"));
        if (approved.Count > 0)
            lines.Add(en
                ? $"{approved.Count} approved leave(s) cannot be cancelled from chat (decided requests follow the cancellation rules in HR360; contact HR)."
                : $"Onaylanmış {approved.Count} izin sohbetten iptal edilemez (karara bağlanmış talepler HR360'taki iptal kurallarına tabidir; İK ile görüşün).");
        var buttons = pending.Select(r => new ChatButton(en ? $"Cancel {r.S:dd.MM}–{r.E:dd.MM}" : $"İptal: {r.S:dd.MM}–{r.E:dd.MM}", "leave_cancel", r.Id.ToString(), "danger")).ToList();
        buttons.Add(ChatButton.Link(en ? "Leave in HR360" : "Panelde aç", $"{Origin}/panel/izin"));
        return ChatReply.Of(string.Join("\n", lines), en, buttons.ToArray());
    }

    private async Task<ChatReply> CancelLeaveConfirmAsync(string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var r = (await _sql.QueryAsync("SELECT \"Type\", \"StartDate\", \"EndDate\", \"Status\" FROM leave_requests WHERE \"TenantSlug\" = $1 AND \"Id\" = $2 AND \"EmployeeId\" = $3",
            x => (Type: x.GetString(0), S: x.GetFieldValue<DateOnly>(1), E: x.GetFieldValue<DateOnly>(2), St: x.GetString(3)), ct, tenant, id, emp)).FirstOrDefault();
        if (r.Type is null) return ChatReply.Of(en ? "Leave request not found." : "İzin talebi bulunamadı.", en) with { Replace = true };
        if (r.St is not ("Submitted" or "Draft"))
            return ChatReply.Of(en ? "A decided request cannot be cancelled from chat." : "Sonuçlanmış talep sohbetten iptal edilemez.", en) with { Replace = true };
        return ChatReply.Of(en
                ? $"Your {HrAssistant.LeaveLabel(r.Type, true)} request {r.S:dd.MM} – {r.E:dd.MM} will be cancelled. Are you sure?"
                : $"{r.S:dd.MM} – {r.E:dd.MM} {HrAssistant.LeaveLabel(r.Type)} talebiniz iptal edilecek. Emin misiniz?", en,
            new ChatButton(en ? "Yes, cancel it" : "Evet, iptal et", "leave_cancel_yes", id.ToString(), "danger"),
            new ChatButton(en ? "No" : "Vazgeç", "cancel")) with { Replace = true };
    }

    /// <summary>
    /// İptal leave-service'in iç ucundan (POST /api/internal/chat/leave-cancel): web ucuyla aynı kural
    /// (yalnızca talep sahibi, yalnızca sonuçlanmamış talep; bekleyen gün bakiyeden düşülür, onay akışı
    /// kapatılır) ve denetim kaydı orada yazılır.
    /// </summary>
    private async Task<ChatReply> CancelLeaveAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var r = await ChatInternal.PostAsync(_http, ChatInternal.LeaveBase, "/api/internal/chat/leave-cancel",
            new { tenantSlug = tenant, employeeId = emp, leaveId = id, channel = app.Platform }, en, ct);
        if (!r.Ok)
        {
            var msg = r.Status switch
            {
                409 when r.Str("code") == "not_cancellable" => en ? "This request can no longer be cancelled (already decided or cancelled)." : "Bu talep artık iptal edilemez (karara bağlanmış ya da iptal edilmiş).",
                404 => en ? "Leave request not found." : "İzin talebi bulunamadı.",
                400 or 409 when en => "The leave request could not be cancelled; please try again.",
                _ => r.Message!,
            };
            return ChatReply.Of(msg, en) with { Replace = true };
        }
        DateOnly.TryParseExact(r.Str("startDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s);
        DateOnly.TryParseExact(r.Str("endDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e);
        return ChatReply.Of(en ? $"✅ Your leave request {s:dd.MM} – {e:dd.MM} was cancelled." : $"✅ {s:dd.MM} – {e:dd.MM} izin talebiniz iptal edildi.", en,
            ChatButton.Link(en ? "Leave in HR360" : "Panelde aç", $"{Origin}/panel/izin")) with { Replace = true };
    }

    // ================================================================== B6 fişten masraf

    private static readonly string MlBase = EnvVar.Or("ML_INFERENCE_URL", "http://ml-inference:8000").TrimEnd('/');
    private static readonly string KeycloakToken = EnvVar.Or("OCR_TOKEN_URL", EnvVar.Or("KEYCLOAK_AUTHORITY", "http://keycloak:8080/auth/realms/hr360").TrimEnd('/') + "/protocol/openid-connect/token");
    private static (string Token, DateTime Exp)? _ocrToken;

    /// <summary>ml-inference fiş okuması için hizmet hesabı jetonu (OCR_CLIENT_ID / OCR_CLIENT_SECRET, client_credentials).</summary>
    private async Task<string?> OcrTokenAsync(CancellationToken ct)
    {
        if (_ocrToken is { } c && c.Exp > DateTime.UtcNow.AddMinutes(1)) return c.Token;
        var secret = Environment.GetEnvironmentVariable("OCR_CLIENT_SECRET");
        if (string.IsNullOrEmpty(secret)) return null;
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var res = await client.PostAsync(KeycloakToken, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = EnvVar.Or("OCR_CLIENT_ID", "hr360-ml-inference"), ["client_secret"] = secret,
        }), ct);
        if (!res.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        var token = doc.GetProperty("access_token").GetString()!;
        _ocrToken = (token, DateTime.UtcNow.AddSeconds(doc.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 60));
        return token;
    }

    /// <summary>Yerel OCR (ml-inference/Tesseract). Görüntü bellekte işlenir; ne burada ne orada saklanır.</summary>
    public async Task<string?> OcrAsync(byte[] image, string contentType, CancellationToken ct)
    {
        var token = await OcrTokenAsync(ct);
        if (token is null) return null;
        using var content = new MultipartFormDataContent();
        var bc = new ByteArrayContent(image);
        bc.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(bc, "file", "receipt");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(40);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{MlBase}/ocr/receipt") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Görüntü indirme yalnızca sağlayıcının dosya sunucusundan (SSRF'e karşı izinli ana bilgisayarlar).</summary>
    public static bool DownloadAllowed(ChatApp app, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        string? Host(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } b && Uri.TryCreate(b, UriKind.Absolute, out var x) ? x.Host : null;
        var test = new[] { Host("SLACK_API_BASE"), Host("TEAMS_LOGIN_BASE") }.Where(h => h is not null).ToHashSet();
        if (test.Contains(host)) return true;
        if (u.Scheme != "https") return false;
        return app.Platform switch
        {
            "Slack" => host == "files.slack.com" || host.EndsWith(".slack.com") || host.EndsWith(".slack-edge.com"),
            "Teams" => host.EndsWith(".trafficmanager.net") || host.EndsWith(".botframework.com") || host.EndsWith(".sharepoint.com")
                || host.EndsWith(".teams.microsoft.com") || host.EndsWith(".skype.com") || host == "graph.microsoft.com",
            _ => false,
        };
    }

    /// <summary>B6: gelen fiş görüntüsünü indirir (en çok 5 MB), okur ve öneri kartı döner. Görüntü saklanmaz.</summary>
    public async Task<ChatReply> ReceiptFromUrlAsync(ChatApp app, ChatIdentity who, string url, string? contentType, string? bearer, CancellationToken ct)
    {
        var en = await _chat.EnAsync(app.TenantSlug, who.EmployeeId, ct);
        string L(string a, string b) => en ? b : a;
        if (app.DisabledFeatures.Contains("expense")) return ChatReply.Of(L("Bu komut şirketinizde kapalı.", "This command is turned off in your company."), en);
        if (!DownloadAllowed(app, url)) return ChatReply.Of(L("Bu dosya adresinden indirme yapılamıyor.", "Downloads from this file address are not allowed."), en);
        byte[] bytes;
        string type;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) throw new ChatApiException($"dosya indirilemedi: HTTP {(int)res.StatusCode}");
            if (res.Content.Headers.ContentLength > 5_000_000) return ChatReply.Of(L("Görüntü en fazla 5 MB olabilir.", "The image can be at most 5 MB."), en);
            type = (res.Content.Headers.ContentType?.MediaType ?? contentType ?? "").ToLowerInvariant();
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > 5_000_000) return ChatReply.Of(L("Görüntü en fazla 5 MB olabilir.", "The image can be at most 5 MB."), en);
            }
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ChatApiException)
        {
            await ErrorAsync(app, "expense", "Fiş indirilemedi: " + ex.Message);
            return ChatReply.Of(L("Fiş görüntüsü alınamadı; yeniden gönderin ya da *masraf <tutar> <tarih>* yazın.", "The receipt image could not be fetched; send it again or type *expense <amount> <date>*."), en);
        }
        // Gerçek tür denetimi: sağlayıcının bildirdiği tür içerikle (magic bytes) uyuşmalı.
        var verdict = FileSniffer.Check(bytes, type, null, [FileSniffer.Jpeg, FileSniffer.Png, FileSniffer.Webp], out var detected);
        if (verdict != FileSniffer.Verdict.Ok || type is not ("image/jpeg" or "image/png" or "image/webp" or "image/jpg"))
        {
            Array.Clear(bytes);
            return ChatReply.Of(L("Fiş için JPEG, PNG ya da WEBP fotoğraf gönderin.", "Send a JPEG, PNG or WEBP photo of the receipt."), en);
        }
        var text = await OcrAsync(bytes, detected!, ct);
        Array.Clear(bytes); // görüntü bellekte bile tutulmaz
        var (amount, date, _) = text is null ? (null, null, null) : ReceiptParser.Parse(text);
        var category = text is null ? "Other" : ReceiptParser.GuessCategory(text);
        await CountAsync(app, "expense", text is null ? "ocr_unavailable" : "ocr", ct);
        return await ExpenseDraftCardAsync(app, who.EmployeeId!.Value, amount, date, category, text is null, en, ct);
    }

    private async Task<ChatReply> ExpenseDraftCardAsync(ChatApp app, Guid emp, decimal? amount, DateOnly? date, string category, bool ocrFailed, bool en, CancellationToken ct)
    {
        var id = await PendingAddAsync(app, emp, "expense", new { amount, date = date?.ToString("yyyy-MM-dd"), category }, TimeSpan.FromHours(2), null, ct);
        string F(decimal? a) => a is null ? "—" : a.Value.ToString("N2", en ? CultureInfo.GetCultureInfo("en-GB") : Tr) + " TL";
        var head = ocrFailed
            ? (en ? "🧾 The receipt could not be read automatically. Enter the amount and date, then confirm." : "🧾 Fiş otomatik okunamadı. Tutarı ve tarihi girip onaylayın.")
            : (en ? "🧾 Read from the receipt — please check:" : "🧾 Fişten okunanlar — lütfen kontrol edin:");
        var text = $"{head}\n" + (en
            ? $"Amount: *{F(amount)}* · Date: *{date?.ToString("dd.MM.yyyy") ?? "—"}* · Category: *{ReceiptParser.CategoryLabel(category, true)}*\nThe image was not stored. Confirming creates a *draft* claim; you submit it in HR360."
            : $"Tutar: *{F(amount)}* · Tarih: *{date?.ToString("dd.MM.yyyy") ?? "—"}* · Kategori: *{ReceiptParser.CategoryLabel(category, false)}*\nGörüntü saklanmadı. Onaylayınca *taslak* beyan oluşur; onaya HR360'tan gönderirsiniz.");
        var buttons = new List<ChatButton>();
        if (app.Platform == "Slack") buttons.Add(new ChatButton(en ? "Correct" : "Düzelt", "exp_edit", id.ToString()));
        else if (app.Platform != "Teams") buttons.Add(new ChatButton(en ? "How to correct" : "Nasıl düzeltirim?", "exp_edit", id.ToString()));
        return new ChatReply(text, Array.Empty<PendingApproval>())
        {
            En = en, Form = ChatForm.Expense, Expense = new ExpenseDraft(id, amount, date, category), Buttons = buttons.Count == 0 ? null : buttons,
        };
    }

    /// <summary>"masraf" (yönerge) ya da "masraf 245,50 01.10.2026 yemek" (elle öneri kartı).</summary>
    private async Task<ChatReply> ExpenseCommandAsync(ChatApp app, Guid emp, string args, bool en, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args))
            return ChatReply.Of(en
                ? "📸 Send me a photo of the receipt (Slack: share the image in this DM; Teams: attach it). I read it locally, the image is not stored, and you confirm the amount before a draft claim is created.\nOr type *expense <amount> <dd.mm.yyyy> <category>*."
                : "📸 Fişin fotoğrafını bana gönderin (Slack: bu yazışmaya görüntü olarak; Teams: ek olarak). Fiş yerel olarak okunur, görüntü saklanmaz; taslak oluşmadan önce tutarı siz onaylarsınız.\nYa da *masraf <tutar> <gg.aa.yyyy> <kategori>* yazın.", en,
                ChatButton.Link(en ? "Expenses in HR360" : "Panelde aç", $"{Origin}/panel/masraf"));
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        decimal? amount = null; DateOnly? date = null; var category = "Other";
        foreach (var p in parts)
        {
            if (amount is null && ReceiptParser.ParseAmount(p) is { } a && Regex.IsMatch(p, @"^\d")) { amount = a; continue; }
            if (date is null && ParseDay(p) is { } d && !Regex.IsMatch(p, @"^\d+([.,]\d{1,2})?$")) { date = d; continue; }
            var g = ReceiptParser.GuessCategory(p);
            if (g != "Other") category = g;
            else if (ReceiptParser.Categories.FirstOrDefault(c => string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) is { } exact) category = exact;
        }
        return await ExpenseDraftCardAsync(app, emp, amount, date ?? Today, category, false, en, ct);
    }

    private async Task<ChatReply> ExpenseCancelAsync(string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (Guid.TryParse(value, out var id) && await PendingGetAsync(tenant, emp, id, ct) is not null) await PendingCloseAsync(id, "Cancelled", ct);
        return ChatReply.Of(en ? "Cancelled; nothing was saved." : "Vazgeçildi; hiçbir şey kaydedilmedi.", en) with { Replace = true };
    }

    /// <summary>Onaylanan öneriden taslak masraf beyanı (expense-service Create kuralları: tutar 0–1.000.000, gelecek tarih yok, kendi adına).</summary>
    public async Task<ChatReply> ExpenseConfirmAsync(ChatApp app, Guid emp, string value, string? amountText, string? dateText, string? categoryText, bool en, CancellationToken ct)
    {
        string L(string a, string b) => en ? b : a;
        if (!Guid.TryParse(value, out var id) || await PendingGetAsync(app.TenantSlug, emp, id, ct) is not { Kind: "expense" } p)
            return ChatReply.Of(L("Bu öneri artık geçerli değil; fişi yeniden gönderin.", "This suggestion is no longer valid; send the receipt again."), en) with { Replace = true };
        decimal? amount = amountText is not null ? ReceiptParser.ParseAmount(amountText)
            : p.Payload.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : null;
        DateOnly? date = dateText is not null ? ParseDay(dateText)
            : p.Payload.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var dd) ? dd : null;
        var category = categoryText ?? (p.Payload.TryGetProperty("category", out var c) ? c.GetString() : null) ?? "Other";
        if (!ReceiptParser.Categories.Contains(category)) category = "Other";
        if (amount is not (> 0 and <= 1_000_000m))
            return ChatReply.Of(L("Tutar okunamadı ya da geçersiz (0–1.000.000). *Düzelt* ile girin ya da *masraf <tutar> <tarih>* yazın.",
                "The amount is missing or invalid (0–1,000,000). Use *Correct* or type *expense <amount> <date>*."), en);
        date ??= Today;
        if (date > Today.AddDays(1)) return ChatReply.Of(L("Harcama tarihi gelecekte olamaz.", "The expense date cannot be in the future."), en);
        if (!await PendingCloseAsync(id, "Done", ct)) return ChatReply.Of(L("Bu öneri zaten kullanıldı.", "This suggestion was already used."), en) with { Replace = true };
        var title = en ? $"Receipt {date:dd.MM.yyyy}" : $"Fiş {date:dd.MM.yyyy}";
        // Taslak beyan expense-service'in iç ucuyla açılır (web Create kuralları + denetim kaydı orada).
        var r = await ChatInternal.PostAsync(_http, ChatInternal.ExpenseBase, "/api/internal/chat/expense-draft", new
        {
            tenantSlug = app.TenantSlug, employeeId = emp, title, currency = "TRY", amount = amount.Value, date = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            category, description = en ? "From chat (receipt reading)" : "Sohbetten (fiş okuma)", platform = app.Platform,
        }, en, ct);
        if (!r.Ok)
        {
            // Öneri tükenmesin: düzeltip yeniden onaylanabilsin.
            await _sql.ExecuteAsync("UPDATE governance_chat_pending SET \"State\" = 'Pending', \"ConfirmedAt\" = NULL WHERE \"Id\" = $1 AND \"State\" = 'Done'", ct, id);
            return ChatReply.Of(r.Message!, en);
        }
        return ChatReply.Of(en
                ? $"✅ Draft expense claim created: {amount.Value.ToString("N2", CultureInfo.GetCultureInfo("en-GB"))} TL ({ReceiptParser.CategoryLabel(category, true)}, {date:dd.MM.yyyy}). Review and submit it in HR360."
                : $"✅ Taslak masraf beyanı oluşturuldu: {amount.Value.ToString("N2", Tr)} TL ({ReceiptParser.CategoryLabel(category, false)}, {date:dd.MM.yyyy}). HR360'ta kontrol edip onaya gönderin.", en,
            ChatButton.Link(en ? "Open in HR360" : "Panelde aç", $"{Origin}/panel/masraf")) with { Replace = true };
    }

    /// <summary>Slack "Düzelt" penceresi için öneri (yalnızca sahibine).</summary>
    public async Task<ExpenseDraft?> ExpenseDraftAsync(string tenant, Guid emp, string value, CancellationToken ct)
    {
        var (v, _) = ButtonStamp.Decode(value);
        if (!Guid.TryParse(v, out var id) || await PendingGetAsync(tenant, emp, id, ct) is not { Kind: "expense" } p) return null;
        decimal? amount = p.Payload.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : null;
        DateOnly? date = p.Payload.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var dd) ? dd : null;
        return new ExpenseDraft(id, amount, date, p.Payload.TryGetProperty("category", out var c) ? c.GetString() ?? "Other" : "Other");
    }

    // ================================================================== B10 teşekkür

    /// <summary>"teşekkür @kişi mesaj": takdir (engagement kudos) oluşturur; alıcıya DM.</summary>
    private async Task<ChatReply> KudosAsync(GovernanceDbContext db, ChatApp app, ChatIdentity who, string args, bool en, CancellationToken ct)
    {
        string L(string a, string b) => en ? b : a;
        var usage = L("Kullanım: *teşekkür @kişi mesajınız* (ör. *teşekkür @ayse.yilmaz sunum için çok teşekkürler*).",
            "Usage: *thanks @person your message* (e.g. *thanks @ayse.yilmaz great presentation*).");
        args = (args ?? "").Trim();
        if (args.Length == 0) return ChatReply.Of(usage, en);
        string target, message;
        var m = Regex.Match(args, @"^(<@([A-Z0-9_]+)(\|[^>]*)?>|<at>(.*?)</at>|@(\S+))\s*(.*)$", RegexOptions.Singleline);
        if (!m.Success) return ChatReply.Of(usage, en);
        message = m.Groups[6].Value.Trim();
        Person? to = null;
        var tenant = who.TenantSlug;
        if (m.Groups[2].Success)
        {
            // Slack mention: <@U123>
            var ext = m.Groups[2].Value;
            var id = await db.ChatIdentities.FirstOrDefaultAsync(i => i.AppId == app.Id && i.ExternalUserId == ext, ct);
            if (id?.EmployeeId is { } eid) to = await _people.FindAsync(tenant, eid, ct);
            else if (app.Platform == "Slack")
            {
                try
                {
                    var (email, _) = await _slack.UserInfoAsync(SecretBox.Unprotect(app.SlackBotTokenEnc)!, ext, ct);
                    if (email is not null && await _chat.EmployeeIdByEmailAsync(tenant, email, ct) is { } e2) to = await _people.FindAsync(tenant, e2, ct);
                }
                catch (ChatApiException) { }
            }
            target = ext;
        }
        else
        {
            target = (m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value).Trim();
            to = await FindPersonAsync(tenant, target, ct);
        }
        if (to is null) return ChatReply.Of(L($"“{Trim(target, 40)}” adlı tek bir çalışan bulunamadı. E-posta adının başını yazın (ör. *@ayse.yilmaz*).",
            $"Could not find a single employee matching “{Trim(target, 40)}”. Use the start of their email (e.g. *@ayse.yilmaz*)."), en);
        // Kurallar (rozet, kendine takdir, 1–500 karakter), uygulama içi bildirim ve denetim kaydı engagement-service'te.
        var r = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/kudos",
            new { tenantSlug = tenant, employeeId = who.EmployeeId!.Value, toEmployeeId = to.Id, message, badge = "thanks", platform = app.Platform }, en, ct);
        if (!r.Ok)
            return ChatReply.Of(r.Str("code") switch
            {
                "self" => L("Kendinize takdir gönderemezsiniz.", "You cannot send kudos to yourself."),
                "message_length" => L("Mesaj 1–500 karakter olmalı. ", "The message must be 1–500 characters. ") + usage,
                _ => r.Message!,
            }, en);
        var fromName = r.Str("fromName") ?? who.DisplayName ?? "";
        // Alıcıya DM (yalnızca alıcının kendisine; sessiz saate uyar).
        var toEn = await _chat.EnAsync(tenant, to.Id, ct);
        var dm = toEn ? $"🙌 *{fromName}* thanked you: “{message}”" : $"🙌 *{fromName}* size teşekkür etti: “{message}”";
        try { await SendAsync(db, app, to.Id, ChatReply.Of(dm, toEn, ChatButton.Link(toEn ? "Kudos wall" : "Takdir duvarı", $"{Origin}/panel/takdir")), "engagement.kudos", false, ct); await db.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is ChatApiException or HttpRequestException) { _log.LogInformation("Takdir DM'i gönderilemedi: {Message}", ex.Message); }
        await CountAsync(app, "kudos", "ok", ct);
        return ChatReply.Of(L($"🙌 Teşekkürünüz {ChatService.ShortName(to.Name)} kişisine iletildi ve takdir duvarına eklendi.", $"🙌 Your thanks was sent to {ChatService.ShortName(to.Name)} and added to the kudos wall."), en);
    }

    /// <summary>Ad, ad soyad ya da e-posta yerel kısmıyla TEK etkin çalışan (belirsizse null).</summary>
    public async Task<Person?> FindPersonAsync(string tenant, string token, CancellationToken ct)
    {
        var t = ChatText.Fold(token.Replace('.', ' ').Replace('_', ' '));
        if (t.Length < 2) return null;
        var all = await _people.ListAsync(tenant, ct);
        var byEmail = all.Where(p => p.Email is not null && ChatText.Fold(p.Email.Split('@')[0].Replace('.', ' ').Replace('_', ' ')) == t).ToList();
        if (byEmail.Count == 1) return byEmail[0];
        var byName = all.Where(p => ChatText.Fold(p.Name) == t).ToList();
        if (byName.Count == 1) return byName[0];
        var byFirst = all.Where(p => ChatText.Fold(p.Name).Split(' ')[0] == t).ToList();
        return byFirst.Count == 1 ? byFirst[0] : null;
    }

    // ================================================================== B13 masa

    /// <summary>"bugün", "yarın", "12.10", "12.10.2026", "2026-10-12".</summary>
    public static DateOnly? ParseDay(string? s)
    {
        var f = ChatText.Fold(s);
        if (f.Length == 0) return null;
        var today = Today;
        if (f is "bugun" or "today") return today;
        if (f is "yarin" or "tomorrow") return today.AddDays(1);
        var raw = (s ?? "").Trim();
        if (DateOnly.TryParseExact(raw, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateOnly.TryParseExact(raw, new[] { "dd.MM", "d.M" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dm))
        {
            var x = new DateOnly(today.Year, dm.Month, dm.Day);
            return x < today.AddDays(-7) ? x.AddYears(1) : x;
        }
        return null;
    }

    private async Task<ChatReply> DeskListAsync(string tenant, Guid emp, DateOnly day, bool en, CancellationToken ct)
    {
        var me = await _people.FindAsync(tenant, emp, ct);
        var mine = (await _sql.QueryAsync("""
            SELECT b."Id", d."Code", d."Name" FROM engagement_desk_bookings b JOIN engagement_desks d ON d."Id" = b."DeskId"
            WHERE b."TenantSlug" = $1 AND b."Date" = $2 AND d."Kind" = 'Desk' AND (b."EmployeeId" = $3 OR b."UserId" = $4)
            """, r => (Id: r.GetGuid(0), Code: r.GetString(1), Name: r.GetString(2)), ct, tenant, day, emp, me is null ? "" : UserIdOf(me))).FirstOrDefault();
        var date = day.ToString("dd.MM.yyyy");
        var next = new ChatButton(en ? "Next day" : "Sonraki gün", "desk_day", day.AddDays(1).ToString("yyyy-MM-dd"));
        if (mine.Id != Guid.Empty)
            return ChatReply.Of(en ? $"🪑 You have desk *{mine.Code}* ({mine.Name}) on {date}." : $"🪑 {date} için *{mine.Code}* ({mine.Name}) masanız var.", en,
                new ChatButton(en ? "Cancel booking" : "Rezervasyonu iptal et", "desk_cancel", mine.Id.ToString(), "danger"), next,
                ChatButton.Link(en ? "Office in HR360" : "Panelde aç", $"{Origin}/panel/ofis"));
        if (day < Today || day > Today.AddDays(30))
            return ChatReply.Of(en ? "Desks can be booked from today up to 30 days ahead." : "Masa bugünden itibaren en fazla 30 gün sonrası için ayrılabilir.", en);
        var free = await _sql.QueryAsync("""
            SELECT d."Id", d."Code", coalesce(d."Floor", '') FROM engagement_desks d
            WHERE d."TenantSlug" = $1 AND d."IsActive" AND d."Kind" = 'Desk'
              AND NOT EXISTS (SELECT 1 FROM engagement_desk_bookings b WHERE b."DeskId" = d."Id" AND b."Date" = $2 AND b."StartMinute" < 1080 AND 540 < b."EndMinute")
            ORDER BY d."Floor", d."Code" LIMIT 8
            """, r => (Id: r.GetGuid(0), Code: r.GetString(1), Floor: r.GetString(2)), ct, tenant, day);
        if (free.Count == 0) return ChatReply.Of(en ? $"No free desks on {date}." : $"{date} için boş masa yok.", en, next);
        var buttons = free.Select(f => new ChatButton($"{f.Code}{(f.Floor.Length > 0 ? " · " + f.Floor : "")}", "desk_book", $"{f.Id}|{day:yyyy-MM-dd}")).ToList();
        buttons.Add(next);
        return ChatReply.Of(en ? $"🪑 Free desks on *{date}* (09:00–18:00). Pick one:" : $"🪑 *{date}* için boş masalar (09:00–18:00). Birini seçin:", en, buttons.ToArray());
    }

    /// <summary>engagement-service rezervasyon kuralları: bugün–30 gün, çakışma yok, kişi aynı saatte tek masa; masa ayıran o gün "ofiste" görünür.</summary>
    private async Task<ChatReply> DeskBookAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        var parts = value.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var deskId) || !DateOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out var day))
            return ChatReply.Of("—", en);
        // Kurallar, "kim nerede" güncellemesi ve denetim kaydı engagement-service'te.
        var r = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/desk-book",
            new { tenantSlug = tenant, employeeId = emp, deskId, date = day.ToString("yyyy-MM-dd"), startMinute = 540, endMinute = 1080, platform = app.Platform }, en, ct);
        if (!r.Ok)
        {
            switch (r.Str("code"))
            {
                case "date_range":
                    return ChatReply.Of(en ? "That day can no longer be booked." : "Bu güne artık rezervasyon yapılamaz.", en) with { Replace = true };
                case "taken" or "has_desk" or "not_found":
                    var again = await DeskListAsync(tenant, emp, day, en, ct);
                    return again with { Text = (en ? "⚠️ That desk was just taken (or you already have one). " : "⚠️ Bu masa az önce doldu (ya da o gün bir masanız var). ") + again.Text, Replace = true };
                default:
                    return ChatReply.Of(r.Message!, en) with { Replace = true };
            }
        }
        var id = r.Str("id");
        var code = r.Str("code");
        return ChatReply.Of(en ? $"✅ Desk *{code}* is booked for {day:dd.MM.yyyy} (09:00–18:00)." : $"✅ *{code}* masası {day:dd.MM.yyyy} için ayrıldı (09:00–18:00).", en,
            new ChatButton(en ? "Cancel booking" : "Rezervasyonu iptal et", "desk_cancel", id ?? "", "danger")) with { Replace = true };
    }

    private async Task<ChatReply> DeskCancelAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var r = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/desk-cancel",
            new { tenantSlug = tenant, employeeId = emp, bookingId = id, platform = app.Platform }, en, ct);
        if (!r.Ok && r.Status != 404) return ChatReply.Of(r.Message!, en) with { Replace = true };
        return ChatReply.Of(r.Ok ? (en ? "Booking cancelled." : "Rezervasyon iptal edildi.") : (en ? "Booking not found." : "Rezervasyon bulunamadı."), en) with { Replace = true };
    }

    // ================================================================== B14 vardiya ve takas

    private sealed record Assignment(Guid Id, Guid EmployeeId, DateOnly Date, string Shift, TimeOnly Start, TimeOnly End, int Break);

    private static Assignment MapAssignment(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetString(3), r.GetFieldValue<TimeOnly>(4), r.GetFieldValue<TimeOnly>(5), r.GetInt32(6));

    private const string AssignmentSql = """
        SELECT a."Id", a."EmployeeId", a."Date", s."Name", s."StartTime", s."EndTime", s."BreakMinutes"
        FROM timeshift_assignments a JOIN timeshift_shifts s ON s."Id" = a."ShiftId"
        """;

    private static string ShiftLine(Assignment a, bool en) =>
        $"{a.Date.ToString(en ? "ddd dd.MM" : "dd.MM ddd", en ? CultureInfo.GetCultureInfo("en-GB") : Tr)} · {a.Shift} {a.Start:HH\\:mm}–{a.End:HH\\:mm}";

    private async Task<ChatReply> ShiftsAsync(string tenant, Guid emp, bool en, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync(AssignmentSql + " WHERE a.\"TenantSlug\" = $1 AND a.\"EmployeeId\" = $2 AND a.\"Date\" >= $3 ORDER BY a.\"Date\" LIMIT 7",
            MapAssignment, ct, tenant, emp, Today);
        if (rows.Count == 0) return ChatReply.Of(en ? "You have no upcoming shifts." : "Yaklaşan vardiyanız yok.", en, ChatButton.Link(en ? "Shifts in HR360" : "Panelde aç", $"{Origin}/panel/puantaj"));
        return ChatReply.Of((en ? "*Your next shifts*\n" : "*Sonraki vardiyalarınız*\n") + string.Join("\n", rows.Select(r => "• " + ShiftLine(r, en))), en,
            ChatButton.Say(en ? "Swap requests" : "Takas talepleri", en ? "swaps" : "takas"),
            ChatButton.Link(en ? "Shift swaps in HR360" : "Panelde aç", $"{Origin}/panel/vardiya-takasi"));
    }

    private async Task<ChatReply> SwapsAsync(string tenant, Guid emp, bool en, CancellationToken ct)
    {
        var team = (await _people.ListAsync(tenant, ct)).Where(p => p.DepartmentHeadId == emp && p.Id != emp).Select(p => p.Id).ToArray();
        var rows = await _sql.QueryAsync("""
            SELECT "Id", "Status", "RequesterEmployeeId", "TargetEmployeeId", "RequesterAssignmentId", "TargetAssignmentId" FROM timeshift_swap_requests
            WHERE "TenantSlug" = $1 AND "Status" IN ('PendingPeer','PendingApproval')
              AND ("RequesterEmployeeId" = $2 OR "TargetEmployeeId" = $2 OR "RequesterEmployeeId" = ANY($3))
            ORDER BY "CreatedAt" DESC LIMIT 10
            """, r => (Id: r.GetGuid(0), St: r.GetString(1), Req: r.GetGuid(2), Tgt: r.GetGuid(3), RA: r.GetGuid(4), TA: r.GuidOrNull(5)), ct, tenant, emp, team);
        if (rows.Count == 0) return ChatReply.Of(en ? "No open shift swap requests." : "Açık vardiya takas talebi yok.", en, ChatButton.Link(en ? "Shift swaps in HR360" : "Panelde aç", $"{Origin}/panel/vardiya-takasi"));
        var ids = rows.Select(r => r.RA).Concat(rows.Where(r => r.TA != null).Select(r => r.TA!.Value)).Distinct().ToArray();
        var asg = (await _sql.QueryAsync(AssignmentSql + " WHERE a.\"TenantSlug\" = $1 AND a.\"Id\" = ANY($2)", MapAssignment, ct, tenant, ids)).ToDictionary(a => a.Id);
        var names = (await _people.ListAsync(tenant, ct)).ToDictionary(p => p.Id, p => ChatService.ShortName(p.Name) ?? "");
        var lines = new List<string> { en ? "*Shift swap requests*" : "*Vardiya takas talepleri*" };
        var buttons = new List<ChatButton>();
        var i = 0;
        foreach (var r in rows)
        {
            i++;
            var mine = asg.GetValueOrDefault(r.RA);
            var theirs = r.TA is { } ta ? asg.GetValueOrDefault(ta) : null;
            var what = (mine is null ? "?" : ShiftLine(mine, en)) + (theirs is null ? (en ? " (give away)" : " (devir)") : " ⇄ " + ShiftLine(theirs, en));
            string role;
            if (r.Tgt == emp && r.St == "PendingPeer")
            {
                role = en ? $"{names.GetValueOrDefault(r.Req)} asks you" : $"{names.GetValueOrDefault(r.Req)} sizden istiyor";
                buttons.Add(new ChatButton(en ? $"Accept #{i}" : $"Kabul #{i}", "swap_accept", r.Id.ToString(), "primary"));
                buttons.Add(new ChatButton(en ? $"Decline #{i}" : $"Reddet #{i}", "swap_decline", r.Id.ToString()));
            }
            else if (r.St == "PendingApproval" && team.Contains(r.Req) && r.Req != emp && r.Tgt != emp)
            {
                role = en ? $"{names.GetValueOrDefault(r.Req)} ⇄ {names.GetValueOrDefault(r.Tgt)}, awaiting your approval" : $"{names.GetValueOrDefault(r.Req)} ⇄ {names.GetValueOrDefault(r.Tgt)}, onayınızı bekliyor";
                buttons.Add(new ChatButton(en ? $"Approve #{i}" : $"Onayla #{i}", "swap_approve", r.Id.ToString(), "primary"));
                buttons.Add(new ChatButton(en ? $"Reject #{i}" : $"Reddet #{i}", "swap_reject", r.Id.ToString(), "danger"));
            }
            else role = r.St == "PendingPeer" ? (en ? "waiting for the other person" : "karşı tarafın yanıtı bekleniyor") : (en ? "waiting for manager approval" : "yönetici onayı bekleniyor");
            lines.Add($"{i}. {what} — {role}");
        }
        buttons.Add(ChatButton.Link(en ? "Shift swaps in HR360" : "Panelde aç", $"{Origin}/panel/vardiya-takasi"));
        return ChatReply.Of(string.Join("\n", lines), en, buttons.ToArray());
    }

    /// <summary>
    /// Takas yanıtı timeshift-service iç ucundan (/api/internal/chat/swap-respond): yalnızca hedef kişi ve
    /// yalnızca PendingPeer iken; bildirim ve denetim kaydı orada yazılır.
    /// </summary>
    private async Task<ChatReply> SwapRespondAsync(ChatApp app, string tenant, Guid emp, string value, bool accept, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var r = await ChatInternal.PostAsync(_http, ChatInternal.TimeshiftBase, "/api/internal/chat/swap-respond",
            new { tenantSlug = tenant, employeeId = emp, swapId = id, accept, platform = app.Platform }, en, ct);
        if (!r.Ok)
            return r.Status is 404 or 409
                ? ChatReply.Of(en ? "This request is no longer awaiting your answer." : "Bu talep artık yanıtınızı beklemiyor.", en) with { Replace = true }
                : ChatReply.Of(r.Message!, en);
        return ChatReply.Of(accept ? (en ? "✅ You accepted the swap; it now awaits manager approval." : "✅ Takası kabul ettiniz; yönetici onayı bekleniyor.")
            : (en ? "You declined the swap." : "Takası reddettiniz."), en) with { Replace = true };
    }

    /// <summary>
    /// Takas onayı/reddi timeshift-service iç ucundan (/api/internal/chat/swap-decide): yetki (talep edenin
    /// bölüm başı, taraflar hariç), durum ve takas kuralları (aynı ekip, çakışma, 11 saat dinlenme, haftalık
    /// 45 saat) web ile aynı çekirdekte uygulanır; değişim, bildirim ve denetim kaydı orada yapılır.
    /// </summary>
    private async Task<ChatReply> SwapDecideAsync(ChatApp app, string tenant, Guid emp, string value, bool approve, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var r = await ChatInternal.PostAsync(_http, ChatInternal.TimeshiftBase, "/api/internal/chat/swap-decide",
            new { tenantSlug = tenant, employeeId = emp, swapId = id, approve, platform = app.Platform }, en, ct);
        if (r.Ok)
            return ChatReply.Of(approve ? (en ? "✅ Swap approved; the schedule was updated." : "✅ Takas onaylandı; program güncellendi.")
                : (en ? "⛔ Swap rejected." : "⛔ Takas reddedildi."), en) with { Replace = true };
        return r.Status switch
        {
            400 when r.Str("code") == "rule_violation" =>
                ChatReply.Of(en ? $"⛔ Swap rejected by the rules: {r.Message}" : $"⛔ Takas kurallara uymadığı için reddedildi: {r.Message}", en) with { Replace = true },
            404 => ChatReply.Of(en ? "Swap request not found." : "Takas talebi bulunamadı.", en) with { Replace = true },
            403 => ChatReply.Of(en ? "You are not allowed to decide on this swap." : "Bu takası onaylama yetkiniz yok.", en),
            409 when r.Str("code") == "not_pending" => ChatReply.Of(en ? "This swap is not awaiting approval." : "Takas onay beklemiyor.", en) with { Replace = true },
            _ => ChatReply.Of(r.Message!, en),
        };
    }

    // ================================================================== B15 duyurular

    private async Task<List<(Guid Id, string Title, string Body, bool Ack, bool Read)>> VisibleAnnouncementsAsync(string tenant, Guid emp, CancellationToken ct)
    {
        var me = await _people.FindAsync(tenant, emp, ct);
        var rows = await _sql.QueryAsync("""
            SELECT a."Id", a."Title", a."Body", a."RequiresAck", a."Audience", a."DepartmentIds",
                   EXISTS (SELECT 1 FROM governance_acknowledgements k WHERE k."TenantSlug" = a."TenantSlug" AND k."SubjectType" = 'Announcement' AND k."SubjectId" = a."Id"
                           AND (k."EmployeeId" = $2 OR k."UserId" = $3))
            FROM governance_announcements a
            WHERE a."TenantSlug" = $1 AND a."PublishAt" <= now() AND (a."ExpireAt" IS NULL OR a."ExpireAt" > now())
            ORDER BY a."PublishAt" DESC LIMIT 30
            """, r => (Id: r.GetGuid(0), Title: r.GetString(1), Body: r.GetString(2), Ack: r.GetBoolean(3), Aud: r.GetString(4), Depts: r.GetFieldValue<Guid[]>(5), Read: r.GetBoolean(6)),
            ct, tenant, emp, me is null ? "" : UserIdOf(me));
        return rows.Where(a => a.Aud == Audience.All || (a.Aud == Audience.Departments && me?.DepartmentId is { } d && a.Depts.Contains(d)))
            .Select(a => (a.Id, a.Title, a.Body, a.Ack, a.Read)).ToList();
    }

    private async Task<ChatReply> AnnouncementsAsync(string tenant, Guid emp, bool en, CancellationToken ct)
    {
        var list = (await VisibleAnnouncementsAsync(tenant, emp, ct)).Take(5).ToList();
        if (list.Count == 0) return ChatReply.Of(en ? "There are no current announcements." : "Güncel duyuru yok.", en);
        var lines = list.Select((a, i) => $"{i + 1}. *{a.Title}*{(a.Read ? (en ? " ✓ read" : " ✓ okundu") : "")}\n   {Trim(a.Body.Replace('\n', ' '), 160)}");
        var buttons = list.Select((a, i) => (a, i)).Where(x => !x.a.Read).Select(x => new ChatButton(en ? $"Read #{x.i + 1}" : $"Okudum #{x.i + 1}", "ann_ack", x.a.Id.ToString(), x.a.Ack ? "primary" : null)).ToList();
        buttons.Add(ChatButton.Link(en ? "Announcements in HR360" : "Panelde aç", $"{Origin}/panel/duyurular"));
        return ChatReply.Of((en ? "*Announcements*\n" : "*Duyurular*\n") + string.Join("\n", lines), en, buttons.ToArray());
    }

    /// <summary>"Okudum": duyuru kişiye görünür olmalı; kayıt rıza değildir (governance_acknowledgements).</summary>
    public async Task<ChatReply> AnnouncementAckAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var a = (await VisibleAnnouncementsAsync(tenant, emp, ct)).FirstOrDefault(x => x.Id == id);
        if (a.Id == Guid.Empty) return ChatReply.Of(en ? "Announcement not found or expired." : "Duyuru bulunamadı ya da süresi doldu.", en) with { Replace = true };
        var me = await _people.FindAsync(tenant, emp, ct);
        await _sql.ExecuteAsync("""
            INSERT INTO governance_acknowledgements ("Id","TenantSlug","SubjectType","SubjectId","Version","UserId","EmployeeId","PersonName","AcknowledgedAt")
            VALUES ($1,$2,'Announcement',$3,0,$4,$5,$6,now())
            ON CONFLICT ("TenantSlug","SubjectType","SubjectId","Version","UserId") DO NOTHING
            """, ct, Guid.NewGuid(), tenant, id, me is null ? $"employee:{emp}" : UserIdOf(me), emp, me?.Name ?? "");
        return ChatReply.Of(en ? $"✓ Marked as read: *{a.Title}*" : $"✓ Okundu olarak işaretlendi: *{a.Title}*", en) with { Replace = true };
    }

    // ================================================================== B16 belge talebi

    private async Task<ChatReply> DocumentsAsync(string tenant, Guid emp, bool en, CancellationToken ct)
    {
        var templates = await _sql.QueryAsync("""
            SELECT "Id", "Name", "RequiresApproval" FROM governance_doc_templates WHERE "TenantSlug" = $1 AND "SelfService" ORDER BY "Name" LIMIT 8
            """, r => (Id: r.GetGuid(0), Name: r.GetString(1), Appr: r.GetBoolean(2)), ct, tenant);
        var recent = await _sql.QueryAsync("""
            SELECT "TemplateName", "Status", "CreatedAt" FROM governance_document_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" DESC LIMIT 3
            """, r => (Name: r.GetString(0), St: r.GetString(1), At: r.GetFieldValue<DateTime>(2)), ct, tenant, emp);
        var lines = new List<string>();
        if (templates.Count == 0) lines.Add(en ? "No documents can be requested via self-service." : "Kendiniz talep edebileceğiniz belge tanımlı değil.");
        else lines.Add(en ? "Which document do you need? The document itself is never sent in chat; you download it in HR360." : "Hangi belge gerekiyor? Belgenin kendisi sohbete gönderilmez; HR360'tan indirirsiniz.");
        if (recent.Count > 0)
        {
            lines.Add(en ? "Your recent requests:" : "Son talepleriniz:");
            lines.AddRange(recent.Select(r => $"• {r.Name} — {StatusLabel(r.St, en)} ({r.At.AddHours(3):dd.MM})"));
        }
        var buttons = templates.Select(t => new ChatButton(t.Name, "doc_req", t.Id.ToString())).ToList();
        buttons.Add(ChatButton.Link(en ? "Documents in HR360" : "Panelde aç", $"{Origin}/panel/belge-talebi"));
        return ChatReply.Of(string.Join("\n", lines), en, buttons.ToArray());
    }

    private static string StatusLabel(string s, bool en) => (s, en) switch
    {
        ("Issued", false) => "hazır", ("Issued", true) => "ready",
        ("Pending", false) => "İK onayında", ("Pending", true) => "awaiting HR",
        ("Rejected", false) => "reddedildi", ("Rejected", true) => "rejected",
        _ => s,
    };

    /// <summary>Belge talebi (web ile aynı kurallar: self-servis şablon, günde en çok 10). Yanıtta yalnızca bağlantı.</summary>
    private async Task<ChatReply> DocumentRequestAsync(GovernanceDbContext db, ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var tid)) return ChatReply.Of("—", en);
        var t = await db.DocTemplates.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid && x.TenantSlug == tenant && x.SelfService, ct);
        if (t is null) return ChatReply.Of(en ? "This document cannot be requested." : "Bu belge talep edilemiyor.", en) with { Replace = true };
        var since = DateTime.UtcNow.AddDays(-1);
        if (await db.DocumentRequests.IgnoreQueryFilters().CountAsync(r => r.TenantSlug == tenant && r.EmployeeId == emp && r.CreatedAt > since, ct) >= 10)
            return ChatReply.Of(en ? "At most 10 documents can be requested per day." : "Günde en fazla 10 belge talep edilebilir.", en);
        var r = new DocumentRequest { TenantSlug = tenant, EmployeeId = emp, TemplateId = t.Id, TemplateName = t.Name };
        db.DocumentRequests.Add(r);
        if (!t.RequiresApproval) await Controllers.DocumentRequestsController.IssueCoreAsync(_sql, _people, tenant, r, t, "otomatik", ct);
        await db.SaveChangesAsync(ct);
        await AuditAsync(tenant, emp, "DocumentRequest", r.Id.ToString(), "Created", new { template = t.Name, source = "chat" }, app.Platform);
        var panel = ChatButton.Link(en ? "Open in HR360" : "Panelde aç", $"{Origin}/panel/belge-talebi");
        if (r.Status == "Issued" && r.VerificationCode is { } code)
            return ChatReply.Of(en
                    ? $"✅ Your *{t.Name}* is ready. Download it in HR360 (sign-in required). Third parties can verify it at the verification page with code *{code}*."
                    : $"✅ *{t.Name}* belgeniz hazır. HR360'tan (giriş yaparak) indirebilirsiniz. Üçüncü kişiler *{code}* koduyla doğrulama sayfasından doğrulayabilir.", en,
                panel, ChatButton.Link(en ? "Verification page" : "Doğrulama sayfası", $"{Origin}/belge-dogrula/{code}")) with { Replace = true };
        return ChatReply.Of(en ? $"✅ Your *{t.Name}* request was sent to HR. You will be notified when it is ready; download it in HR360."
            : $"✅ *{t.Name}* talebiniz İK'ya iletildi. Hazır olunca bildirilecek; HR360'tan indirirsiniz.", en, panel) with { Replace = true };
    }

    // ================================================================== B17 giriş-çıkış

    private static readonly TimeZoneInfo Zone = ResolveZone();
    private static TimeZoneInfo ResolveZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("HR360_TIMEZONE") ?? "Europe/Istanbul"); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Giriş-çıkış timeshift-service iç ucundan (/api/internal/chat/punch): web/QR/terminal ile aynı ClockCore
    /// kuralları (açık kayıt varken yeni giriş yok; günde bir giriş; çıkış açık kaydı kapatır, mesai hesaplanır).
    /// Yöntem "Chat", konum denetimi yok (OnSite boş).
    /// </summary>
    private async Task<ChatReply> ClockAsync(ChatApp app, string tenant, Guid emp, bool goingIn, bool en, CancellationToken ct)
    {
        var r = await ChatInternal.PostAsync(_http, ChatInternal.TimeshiftBase, "/api/internal/chat/punch",
            new { tenantSlug = tenant, employeeId = emp, kind = goingIn ? "in" : "out", platform = app.Platform }, en, ct);
        if (!r.Ok)
            return r.Str("code") switch
            {
                "open_entry" => ChatReply.Of(en ? "You already have an open clock-in; clock out first." : "Açık bir giriş kaydınız var; önce çıkış yapın.", en,
                    new ChatButton(en ? "Clock out" : "Çıkış yap", "clock", "out")),
                "already_in" => ChatReply.Of(en ? "There is already a clock-in for today." : "Bu gün için giriş kaydı zaten var.", en),
                "no_open_entry" => ChatReply.Of(en ? "Clock in first." : "Önce giriş kaydı oluşturulmalı.", en, new ChatButton(en ? "Clock in" : "Giriş yap", "clock", "in")),
                _ => ChatReply.Of(r.Message!, en),
            };
        var body = r.Body.ValueKind == JsonValueKind.Object ? r.Body : default;
        var at = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("at", out var atEl) && atEl.TryGetDateTimeOffset(out var atVal) ? atVal : DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(at, Zone);
        await CountAsync(app, "clock", goingIn ? "in" : "out", ct);
        if (goingIn)
            return ChatReply.Of(en ? $"🟢 Clocked in at {local:HH:mm}. Have a good day!" : $"🟢 Giriş kaydedildi: {local:HH:mm}. İyi çalışmalar!", en,
                new ChatButton(en ? "Clock out" : "Çıkış yap", "clock", "out"));
        var minutes = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("workedMinutes", out var wEl) && wEl.TryGetInt32(out var w) ? w : 0;
        return ChatReply.Of(en ? $"🔴 Clocked out at {local:HH:mm} ({minutes / 60}h {minutes % 60}m today)." : $"🔴 Çıkış kaydedildi: {local:HH:mm} (bugün {minutes / 60} sa {minutes % 60} dk).", en);
    }

    // ================================================================== BG18 İK vakası

    private async Task<ChatReply> HrCaseOfferAsync(ChatApp app, Guid emp, string question, bool en, CancellationToken ct)
    {
        question = (question ?? "").Trim();
        if (question.Length < 5)
            return ChatReply.Of(en ? "Write your question after the command: *hrcase <your question>*." : "Sorunuzu komuttan sonra yazın: *ik vakası <sorunuz>*.", en);
        return await HrCaseConfirmCardAsync(app, emp, question, en, ct);
    }

    public async Task<ChatReply> HrCaseConfirmCardAsync(ChatApp app, Guid emp, string question, bool en, CancellationToken ct)
    {
        var id = await PendingAddAsync(app, emp, "hrcase", new { q = Trim(question, 1000) }, TimeSpan.FromHours(1), null, ct);
        return ChatReply.Of(en
                ? $"Shall I send this question to HR as an *HR case*? “{Trim(question, 200)}”\nOnly HR sees it; do not include health or other sensitive details."
                : $"Bu soruyu İK'ya *İK vakası* olarak ileteyim mi? “{Trim(question, 200)}”\nYalnızca İK görür; sağlık gibi hassas ayrıntı yazmayın.", en,
            new ChatButton(en ? "Yes, open an HR case" : "Evet, İK vakası aç", "hrcase_open", id.ToString(), "primary"),
            new ChatButton(en ? "No" : "Hayır", "cancel", id.ToString()));
    }

    private async Task<ChatReply> HrCaseOpenAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var pid) || await PendingGetAsync(tenant, emp, pid, ct) is not { Kind: "hrcase" } p || !await PendingCloseAsync(pid, "Done", ct))
            return ChatReply.Of(en ? "This offer has expired; ask again." : "Bu önerinin süresi doldu; yeniden sorun.", en) with { Replace = true };
        var q = p.Payload.GetProperty("q").GetString() ?? "";
        // Vaka expense-service'in iç ucuyla açılır (web Create kuralları + denetim kaydı orada).
        var r = await ChatInternal.PostAsync(_http, ChatInternal.ExpenseBase, "/api/internal/chat/hr-case", new
        {
            tenantSlug = tenant, employeeId = emp, subject = (en ? "Question from chat: " : "Sohbetten soru: ") + Trim(q.Replace('\n', ' '), 80),
            description = q, category = "Other", platform = app.Platform,
        }, en, ct);
        if (!r.Ok)
        {
            // Öneri tükenmesin: yeniden denenebilsin.
            await _sql.ExecuteAsync("UPDATE governance_chat_pending SET \"State\" = 'Pending', \"ConfirmedAt\" = NULL WHERE \"Id\" = $1 AND \"State\" = 'Done'", ct, pid);
            return ChatReply.Of(r.Message!, en);
        }
        await CountAsync(app, "hrcase", "created", ct);
        return ChatReply.Of(en ? "✅ Your HR case was opened; HR will get back to you." : "✅ İK vakanız açıldı; İK size dönecek.", en,
            ChatButton.Link(en ? "My HR cases" : "Panelde aç", $"{Origin}/panel/ik-vakalari")) with { Replace = true };
    }

    // ================================================================== B11 yıldönümü izni

    private async Task<ChatReply> AnniversaryAsync(string tenant, Guid emp, string args, bool en, CancellationToken ct)
    {
        var f = ChatText.Fold(args);
        bool? set = f switch { "ac" or "acik" or "on" or "evet" or "yes" => true, "kapat" or "kapali" or "off" or "hayir" or "no" => false, _ => null };
        if (set is { } v)
            await _sql.ExecuteAsync("""
                INSERT INTO governance_chat_optins ("TenantSlug","EmployeeId","ShowAnniversary","UpdatedAt") VALUES ($1,$2,$3,now())
                ON CONFLICT ("TenantSlug","EmployeeId") DO UPDATE SET "ShowAnniversary" = $3, "UpdatedAt" = now()
                """, ct, tenant, emp, v);
        var on = set ?? (await _sql.ScalarAsync("SELECT \"ShowAnniversary\" FROM governance_chat_optins WHERE \"TenantSlug\" = $1 AND \"EmployeeId\" = $2", ct, tenant, emp) as bool? ?? false);
        return ChatReply.Of(on
                ? (en ? "🎉 Your work anniversary will be celebrated in the company channel (years of service only; never age or birth date)." : "🎉 İş yıldönümünüz şirket kanalında kutlanacak (yalnızca kıdem yılı; yaş ya da doğum tarihi asla yazılmaz).")
                : (en ? "Your work anniversary is not announced. Birthdays follow the “show my birthday” setting in your profile." : "İş yıldönümünüz duyurulmuyor. Doğum günü için profilinizdeki “doğum günümü göster” ayarı geçerlidir."), en,
            on ? new ChatButton(en ? "Turn off" : "Kapat", "anniv", "kapat") : new ChatButton(en ? "Turn on" : "Aç", "anniv", "ac", "primary"));
    }
}
