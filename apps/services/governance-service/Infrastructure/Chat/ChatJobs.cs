using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure.Chat;

/* ======================================================================
 * Dalga 5e — zamanlanmış bot işleri: işe başlama mesajları (B9), doğum günü /
 * yıldönümü (B11, yalnızca açık izinle), nabız anketi (B12), duyuru kanalı ve
 * "Okudum" (B15), birebir hatırlatmaları (B18), çıkış anketi (B20), eski onay
 * kartlarının süresinin dolması (BG10) ve bağlam/bekleyen kayıt temizliği (BG16).
 * Kritik olmayan tüm DM'ler sessiz saate uyar (BG6). Tekrar governance_chat_job_log ile önlenir.
 * ==================================================================== */
public sealed partial class ChatFeatures
{
    public static readonly int JobsSeconds = int.TryParse(EnvVar.Or("CHAT_JOBS_SECONDS", "300"), out var j) && j > 0 ? j : 300;
    public static readonly int CelebrationHour = int.TryParse(EnvVar.Or("CHAT_CELEBRATION_HOUR", "9"), out var h) && h is >= 0 and <= 23 ? h : 9;

    /// <summary>İşi bir kez yapmak için anahtarı alır (zaten alınmışsa false).</summary>
    private async Task<bool> ClaimAsync(string tenant, string key, CancellationToken ct) =>
        await _sql.ExecuteAsync("INSERT INTO governance_chat_job_log (\"TenantSlug\",\"Key\",\"SentAt\") VALUES ($1,$2,now()) ON CONFLICT DO NOTHING", ct, tenant, key) > 0;

    private Task ReleaseAsync(string tenant, string key, CancellationToken ct) =>
        _sql.ExecuteAsync("DELETE FROM governance_chat_job_log WHERE \"TenantSlug\" = $1 AND \"Key\" = $2", ct, tenant, key);

    /// <summary>Anahtarı alıp DM gönderir; kişi eşleşmemişse anahtar geri bırakılır (sonradan bağlanınca alır).</summary>
    private async Task<bool> SendOnceAsync(GovernanceDbContext db, ChatApp app, string key, Guid emp, Func<bool, Task<ChatReply>> build, string template, CancellationToken ct)
    {
        if (!await ClaimAsync(app.TenantSlug, key, ct)) return false;
        try
        {
            var en = await _chat.EnAsync(app.TenantSlug, emp, ct);
            var outcome = await SendAsync(db, app, emp, await build(en), template, false, ct);
            if (outcome == SendOutcome.NoIdentity) { await ReleaseAsync(app.TenantSlug, key, ct); return false; }
            await db.SaveChangesAsync(ct);
            return outcome is SendOutcome.Sent or SendOutcome.Deferred;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReleaseAsync(app.TenantSlug, key, ct);
            await ErrorAsync(app, template, ex.Message);
            _log.LogInformation("Zamanlanmış sohbet mesajı gönderilemedi ({Key}): {Message}", key, ex.Message);
            return false;
        }
    }

    /// <summary>Kiracının (ya da tümünün) açık uygulamalarında tüm işleri çalıştırır; işe göre sayılar.</summary>
    public async Task<Dictionary<string, int>> RunAllJobsAsync(GovernanceDbContext db, string? onlyTenant, bool force, CancellationToken ct)
    {
        var totals = new Dictionary<string, int>();
        var apps = await db.ChatApps.IgnoreQueryFilters().Where(a => a.IsEnabled && (onlyTenant == null || a.TenantSlug == onlyTenant)).ToListAsync(ct);
        foreach (var app in apps)
        {
            if (!ChatHosts.Allowed(app, await TransferGuard.AllowedAsync(db, app.TenantSlug, ct))) continue;
            foreach (var (k, v) in await RunJobsAsync(db, app, force, ct)) totals[k] = totals.GetValueOrDefault(k) + v;
        }
        // Temizlik: 30 günden eski bağlam, süresi geçmiş bekleyen işlemler, biten çıkış anketi ilerlemesi.
        totals["contextPurged"] = await _sql.ExecuteAsync("DELETE FROM governance_chat_context WHERE \"CreatedAt\" < now() - make_interval(days => $1)", ct, ContextDays);
        await _sql.ExecuteAsync("DELETE FROM governance_chat_pending WHERE \"ExpiresAt\" < now() - interval '1 day'", ct);
        await _sql.ExecuteAsync("DELETE FROM governance_chat_exit_progress WHERE \"CompletedAt\" < now() - interval '1 day'", ct);
        await _sql.ExecuteAsync("DELETE FROM governance_chat_job_log WHERE \"SentAt\" < now() - interval '400 days'", ct);
        Limiter.Sweep(RateWindow, DateTime.UtcNow);
        return totals;
    }

    public async Task<Dictionary<string, int>> RunJobsAsync(GovernanceDbContext db, ChatApp app, bool force, CancellationToken ct)
    {
        var r = new Dictionary<string, int>();
        async Task Run(string name, string feature, Func<Task<int>> job)
        {
            if (app.DisabledFeatures.Contains(feature)) return;
            try { r[name] = await job(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sohbet işi başarısız: {Job}", name);
                await ErrorAsync(app, name, ex.GetType().Name + ": " + ex.Message);
            }
        }
        await Run("onboarding", "onboarding", () => OnboardingJobAsync(db, app, ct));
        await Run("celebrations", "celebrations", () => CelebrationsJobAsync(app, force, ct));
        await Run("pulse", "pulse", () => PulseJobAsync(db, app, ct));
        await Run("announcements", "announcements", () => AnnouncementsJobAsync(db, app, ct));
        await Run("oneOnOne", "oneonone", () => OneOnOneJobAsync(db, app, ct));
        await Run("exit", "exit", () => ExitJobAsync(db, app, ct));
        await Run("expiredCards", "approvals", () => ExpireCardsAsync(db, app, ct));
        await db.SaveChangesAsync(ct);
        return r;
    }

    // ------------------------------------------------------------------ B9 işe başlama

    private async Task<List<(Guid Id, string Title)>> OpenTasksAsync(string tenant, Guid planId, Guid emp, CancellationToken ct) =>
        await _sql.QueryAsync("""
            SELECT "Id", "Title" FROM onboarding_tasks
            WHERE "TenantSlug" = $1 AND "PlanId" = $2 AND "Status" <> 'Done' AND ("AssigneeEmployeeId" = $3 OR "OwnerRole" = 'Employee')
            ORDER BY "Order", "DueDate" NULLS LAST LIMIT 8
            """, r => (r.GetGuid(0), r.GetString(1)), ct, tenant, planId, emp);

    private async Task<int> OnboardingJobAsync(GovernanceDbContext db, ChatApp app, CancellationToken ct)
    {
        var tenant = app.TenantSlug;
        var plans = await _sql.QueryAsync("""
            SELECT "Id", "EmployeeId", "StartDate", "BuddyEmployeeId" FROM onboarding_plans
            WHERE "TenantSlug" = $1 AND "Status" NOT IN ('Completed','Cancelled') AND "StartDate" BETWEEN $2 AND $3
            """, x => (Id: x.GetGuid(0), Emp: x.GetGuid(1), Start: x.GetFieldValue<DateOnly>(2), Buddy: x.GuidOrNull(3)), ct, tenant, Today.AddDays(-8), Today);
        var sent = 0;
        foreach (var p in plans)
        {
            var day = Today.DayNumber - p.Start.DayNumber;
            var hire = await _people.FindAsync(tenant, p.Emp, ct);
            if (hire is null || hire.Status == "Terminated") continue;
            var buddy = p.Buddy is { } b ? await _people.FindAsync(tenant, b, ct) : null;
            var first = hire.Name.Split(' ')[0];
            if (day <= 2 && await SendOnceAsync(db, app, $"{app.Id}:onb:{p.Id}:0", p.Emp, async en =>
                {
                    var tasks = await OpenTasksAsync(tenant, p.Id, p.Emp, ct);
                    var text = en
                        ? $"👋 Welcome to the team, {first}! " + (buddy is null ? "" : $"Your onboarding buddy is *{ChatService.ShortName(buddy.Name)}* — feel free to ask them anything. ")
                          + $"You have *{tasks.Count}* onboarding task(s); I will check in on day 1, 3 and 7. Type *help* to see what I can do."
                        : $"👋 Aramıza hoş geldin {first}! " + (buddy is null ? "" : $"Onboarding arkadaşın *{ChatService.ShortName(buddy.Name)}*; aklına takılan her şeyi sorabilirsin. ")
                          + $"*{tasks.Count}* işe başlama görevin var; 1., 3. ve 7. günlerde hatırlatacağım. Neler yapabildiğimi görmek için *yardım* yaz.";
                    return ChatReply.Of(text, en, ChatButton.Link(en ? "My onboarding" : "Panelde aç", $"{Origin}/panel/onboarding"), ChatButton.Say(en ? "Help" : "Yardım", en ? "help" : "yardım"));
                }, "onboarding.welcome", ct)) sent++;
            if (buddy is not null && day <= 2 && await SendOnceAsync(db, app, $"{app.Id}:onb:{p.Id}:buddy", buddy.Id, en => Task.FromResult(ChatReply.Of(en
                    ? $"🤝 *{ChatService.ShortName(hire.Name)}* starts on {p.Start:dd.MM.yyyy} and you are their onboarding buddy. A short hello on day one makes a big difference!"
                    : $"🤝 *{ChatService.ShortName(hire.Name)}* {p.Start:dd.MM.yyyy} tarihinde başlıyor ve onboarding arkadaşı sizsiniz. İlk gün kısa bir merhaba çok şey değiştirir!", en)),
                    "onboarding.buddy", ct)) sent++;
            foreach (var d in new[] { 1, 3, 7 })
            {
                if (day < d || day > d + 1) continue;
                if (await SendOnceAsync(db, app, $"{app.Id}:onb:{p.Id}:{d}", p.Emp, async en =>
                    {
                        var tasks = await OpenTasksAsync(tenant, p.Id, p.Emp, ct);
                        if (tasks.Count == 0)
                            return ChatReply.Of(en ? $"🎉 Day {d}: all your onboarding tasks are done. Great start!" : $"🎉 {d}. gün: tüm işe başlama görevlerin tamam. Harika başlangıç!", en);
                        var text = (en ? $"📋 Day {d} check-in — your open onboarding tasks:\n" : $"📋 {d}. gün — açık işe başlama görevlerin:\n")
                            + string.Join("\n", tasks.Select(t => "• " + t.Title)) + (en ? "\nTap a task when it is done." : "\nBiten görevin düğmesine bas.");
                        var buttons = tasks.Select(t => new ChatButton("✓ " + (t.Title.Length > 40 ? t.Title[..40] + "…" : t.Title), "onb_done", t.Id.ToString())).ToList();
                        buttons.Add(ChatButton.Link(en ? "My onboarding" : "Panelde aç", $"{Origin}/panel/onboarding"));
                        return ChatReply.Of(text, en, buttons.ToArray());
                    }, "onboarding.checklist", ct)) sent++;
            }
        }
        return sent;
    }

    /// <summary>
    /// "✓ görev": onboarding-service'in iç ucuyla tamamlanır (web ucuyla aynı kural: kendisine atanmış
    /// ya da — hukuki görevler hariç — kendi planındaki görev; plan ilerlemesi ve denetim kaydı orada).
    /// </summary>
    private async Task<ChatReply> OnboardingDoneAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        if (!Guid.TryParse(value, out var id)) return ChatReply.Of("—", en);
        var res = await ChatInternal.PostAsync(_http, ChatInternal.OnboardingBase, "/api/internal/chat/task-done",
            new { tenantSlug = tenant, employeeId = emp, taskId = id, platform = app.Platform }, en, ct);
        if (!res.Ok)
            return ChatReply.Of(res.Status is 404 or 409 ? (en ? "This task is already done or not yours." : "Bu görev zaten tamam ya da size ait değil.") : res.Message!, en);
        var title = res.Str("title") ?? "";
        return ChatReply.Of(en ? $"✅ Done: {title}" : $"✅ Tamamlandı: {title}", en);
    }

    // ------------------------------------------------------------------ B11 kutlamalar

    private async Task<int> CelebrationsJobAsync(ChatApp app, bool force, CancellationToken ct)
    {
        if (!app.CelebrationsEnabled || string.IsNullOrWhiteSpace(app.ChannelId)) return 0;
        if (!force && DateTime.UtcNow.AddHours(3).Hour < CelebrationHour) return 0;
        var tenant = app.TenantSlug;
        var today = Today;
        // 29 Şubat doğumlular artık yıllarda 28 Şubat'ta kutlanır.
        var leapCatchUp = today.Month == 2 && today.Day == 28 && !DateTime.IsLeapYear(today.Year);
        var birthdays = await _sql.QueryAsync("""
            SELECT e."FirstName" || ' ' || e."LastName" FROM engagement_profiles p JOIN employee_employees e ON e."Id" = p."EmployeeId"
            WHERE p."TenantSlug" = $1 AND p."ShowBirthday" AND p."BirthDate" IS NOT NULL AND e."Status" <> 'Terminated'
              AND ((extract(month FROM p."BirthDate") = $2 AND extract(day FROM p."BirthDate") = $3) OR ($4 AND extract(month FROM p."BirthDate") = 2 AND extract(day FROM p."BirthDate") = 29))
            ORDER BY 1
            """, r => r.GetString(0), ct, tenant, today.Month, today.Day, leapCatchUp);
        var anniversaries = await _sql.QueryAsync("""
            SELECT e."FirstName" || ' ' || e."LastName", $2 - extract(year FROM e."HireDate")::int FROM governance_chat_optins o JOIN employee_employees e ON e."Id" = o."EmployeeId"
            WHERE o."TenantSlug" = $1 AND o."ShowAnniversary" AND e."Status" <> 'Terminated'
              AND extract(month FROM e."HireDate") = $3 AND extract(day FROM e."HireDate") = $4 AND extract(year FROM e."HireDate")::int < $2
            ORDER BY 1
            """, r => (Name: r.GetString(0), Years: r.GetInt32(1)), ct, tenant, today.Year, today.Month, today.Day);
        if (birthdays.Count == 0 && anniversaries.Count == 0) return 0;
        var key = $"{app.Id}:cel:{today:yyyy-MM-dd}";
        if (!await ClaimAsync(tenant, key, ct)) return 0;
        // KVKK: yalnızca açık izin verenler; yaş ya da doğum yılı hiçbir zaman yazılmaz; soyadı kısaltılır.
        var lines = new List<string>();
        if (birthdays.Count > 0) lines.Add("🎂 Bugün doğum günü: " + string.Join(", ", birthdays.Select(n => ChatService.ShortName(n))) + " — iyi ki doğdunuz!");
        if (anniversaries.Count > 0) lines.Add("🎉 İş yıldönümü: " + string.Join(", ", anniversaries.Select(a => $"{ChatService.ShortName(a.Name)} ({a.Years}. yıl)")) + " — tebrikler!");
        try
        {
            await PostAsync(app, app.ChannelId!, null, ChatReply.Of(string.Join("\n", lines), false), ct);
            ChatMetrics.Sent.WithLabels(ChatMetrics.Provider(app.Platform), "channel", "sent").Inc();
            await CountAsync(app, "celebrations", "posted", ct);
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReleaseAsync(tenant, key, ct);
            throw;
        }
    }

    // ------------------------------------------------------------------ B12 nabız anketi

    private static readonly string[] PulseFaces = { "1 😞", "2 🙁", "3 😐", "4 🙂", "5 😄" };

    private async Task<int> PulseJobAsync(GovernanceDbContext db, ChatApp app, CancellationToken ct)
    {
        var tenant = app.TenantSlug;
        // Süresi dolan nabızlar kapanır (anket de engagement-service'te yanıta kapanır; kapatılamazsa sonraki turda yeniden denenir).
        var expired = await _sql.QueryAsync("""
            SELECT "Id", "SurveyId" FROM governance_chat_pulses WHERE "TenantSlug" = $1 AND "Status" <> 'Closed' AND "ClosesAt" IS NOT NULL AND "ClosesAt" < now()
            """, r => (Id: r.GetGuid(0), Survey: r.GetGuid(1)), ct, tenant);
        foreach (var e in expired)
            if (await ClosePulseSurveyAsync(tenant, e.Survey, ct))
                await _sql.ExecuteAsync("UPDATE governance_chat_pulses SET \"Status\" = 'Closed' WHERE \"Id\" = $1", ct, e.Id);
        var pulses = await _sql.QueryAsync("""
            SELECT "Id", "Question" FROM governance_chat_pulses WHERE "TenantSlug" = $1 AND "Status" IN ('Scheduled','Sent') AND "SendAt" <= now()
              AND ("ClosesAt" IS NULL OR "ClosesAt" >= now())
            """, r => (Id: r.GetGuid(0), Q: r.GetString(1)), ct, tenant);
        var sent = 0;
        foreach (var p in pulses)
        {
            var recipients = await db.ChatIdentities.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.AppId == app.Id && i.EmployeeId != null).Select(i => i.EmployeeId!.Value).Distinct().ToListAsync(ct);
            var n = 0;
            foreach (var emp in recipients)
            {
                if (await _sql.ScalarAsync("SELECT 1 FROM governance_chat_pulse_answered WHERE \"PulseId\" = $1 AND \"EmployeeId\" = $2", ct, p.Id, emp) is not null) continue;
                if (await SendOnceAsync(db, app, $"pulse:{p.Id}:{emp}", emp, en => Task.FromResult(ChatReply.Of(
                        (en ? "📊 *Quick pulse* (anonymous — your name is never stored with the answer):\n" : "📊 *Kısa nabız* (anonim — yanıtınız adınızla saklanmaz):\n") + p.Q, en,
                        PulseFaces.Select((f, i) => new ChatButton(f, "pulse", $"{p.Id}|{i + 1}")).ToArray())), "survey.pulse", ct))
                    n++;
            }
            sent += n;
            await _sql.ExecuteAsync("UPDATE governance_chat_pulses SET \"Status\" = 'Sent', \"SentCount\" = \"SentCount\" + $2 WHERE \"Id\" = $1 AND \"Status\" <> 'Closed'", ct, p.Id, n);
        }
        return sent;
    }

    /// <summary>
    /// Nabız yanıtı: yanıt engagement anketine KİMLİKSİZ (rastgele anahtar, departman yok, gün hassasiyetinde tarih)
    /// yazılır; tekrarı önlemek için yalnızca "yanıtladı" bilgisi ayrı tabloda tutulur.
    /// </summary>
    private async Task<ChatReply> PulseAnswerAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        var parts = value.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var score) || score is < 1 or > 5) return ChatReply.Of("—", en);
        var pulse = (await _sql.QueryAsync("""
            SELECT p."SurveyId" FROM governance_chat_pulses p JOIN engagement_surveys s ON s."Id" = p."SurveyId"
            WHERE p."TenantSlug" = $1 AND p."Id" = $2 AND p."Status" = 'Sent' AND s."Status" = 'Open' AND (p."ClosesAt" IS NULL OR p."ClosesAt" > now())
            """, r => r.GetGuid(0), ct, tenant, pid)).FirstOrDefault();
        if (pulse == Guid.Empty) return ChatReply.Of(en ? "This pulse is closed." : "Bu nabız anketi kapandı.", en) with { Replace = true };
        var first = await _sql.ExecuteAsync("""
            INSERT INTO governance_chat_pulse_answered ("TenantSlug","PulseId","EmployeeId","AnsweredOn") VALUES ($1,$2,$3,current_date) ON CONFLICT DO NOTHING
            """, ct, tenant, pid, emp) > 0;
        if (!first) return ChatReply.Of(en ? "You have already answered this pulse. Thank you!" : "Bu ankete zaten yanıt verdiniz. Teşekkürler!", en) with { Replace = true };
        // Yanıt engagement-service'e KİMLİKSİZ gider (çalışan kimliği gönderilmez; servis rastgele anahtar,
        // departmansız ve gün hassasiyetinde saklar). Yazılamazsa "yanıtladı" işareti geri alınır.
        var res = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/pulse-answer",
            new { tenantSlug = tenant, surveyId = pulse, score }, en, ct);
        if (!res.Ok)
        {
            await _sql.ExecuteAsync("DELETE FROM governance_chat_pulse_answered WHERE \"TenantSlug\" = $1 AND \"PulseId\" = $2 AND \"EmployeeId\" = $3", ct, tenant, pid, emp);
            return (res.Str("code") == "closed" ? ChatReply.Of(en ? "This pulse is closed." : "Bu nabız anketi kapandı.", en) : ChatReply.Of(res.Message!, en)) with { Replace = true };
        }
        await CountAsync(app, "pulse", "answered", ct);
        return ChatReply.Of(en ? "🙏 Thank you! Your answer was recorded anonymously." : "🙏 Teşekkürler! Yanıtınız anonim olarak kaydedildi.", en) with { Replace = true };
    }

    /// <summary>
    /// İK'nın nabız planı: engagement'ta tek soruluk anonim anket (Scale 1–5; /api/internal/chat/pulse-survey) + gönderim planı.
    /// Anket oluşturulamazsa Id null, Status/Error servisin yanıtıdır.
    /// </summary>
    public async Task<(Guid? Id, int Status, string? Error)> SchedulePulseAsync(string tenant, string question, DateTime sendAt, DateTime? closesAt, string createdBy, CancellationToken ct)
    {
        var r = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/pulse-survey",
            new { tenantSlug = tenant, question, sendAt, closesAt, createdBy }, false, ct);
        if (!r.Ok || !Guid.TryParse(r.Str("id"), out var surveyId)) return (null, r.Status, r.Message ?? "Anket oluşturulamadı.");
        var id = Guid.NewGuid();
        await _sql.ExecuteAsync("""
            INSERT INTO governance_chat_pulses ("Id","TenantSlug","SurveyId","Question","SendAt","ClosesAt","Status","SentCount","CreatedBy","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,'Scheduled',0,$7,now())
            """, ct, id, tenant, surveyId, question, sendAt, closesAt, createdBy);
        return (id, 200, null);
    }

    /// <summary>Nabzın engagement anketini yanıta kapatır (/api/internal/chat/pulse-close). Anket artık yoksa (404) da kapalı sayılır.</summary>
    public async Task<bool> ClosePulseSurveyAsync(string tenant, Guid surveyId, CancellationToken ct)
    {
        var r = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/pulse-close",
            new { tenantSlug = tenant, surveyId }, false, ct);
        // Gövdesiz 404 = iç anahtar tutmadı (uç "yok"); yalnızca servisin "not_found" yanıtı anketin silindiği anlamına gelir.
        var gone = r.Status == 404 && r.Str("code") == "not_found";
        if (!r.Ok && !gone) _log.LogWarning("Nabız anketi kapatılamadı ({Survey}): {Message}", surveyId, r.Message);
        return r.Ok || gone;
    }

    /// <summary>Nabız sonuçları: en az 5 yanıt yoksa dağılım gizlenir (KVKK küçük grup kuralı).</summary>
    public async Task<List<object>> PulsesAsync(string tenant, CancellationToken ct)
    {
        var rows = await _sql.QueryAsync("""
            SELECT p."Id", p."Question", p."SendAt", p."ClosesAt", p."Status", p."SentCount", p."CreatedAt", p."SurveyId",
                   (SELECT count(*) FROM engagement_survey_responses r WHERE r."SurveyId" = p."SurveyId")
            FROM governance_chat_pulses p WHERE p."TenantSlug" = $1 ORDER BY p."CreatedAt" DESC LIMIT 50
            """, r => (Id: r.GetGuid(0), Q: r.GetString(1), Send: r.GetFieldValue<DateTime>(2), Close: r.Ts(3), St: r.GetString(4), Sent: r.GetInt32(5), At: r.GetFieldValue<DateTime>(6), Survey: r.GetGuid(7), N: r.GetInt64(8)), ct, tenant);
        var list = new List<object>();
        foreach (var p in rows)
        {
            int[]? dist = null; double? avg = null;
            if (p.N >= NlReport.MinGroup)
            {
                var scores = await _sql.QueryAsync("""
                    SELECT (a->>'Score')::int FROM engagement_survey_responses r, jsonb_array_elements(r."Answers") a
                    WHERE r."SurveyId" = $1 AND a->>'QuestionId' = 'pulse' AND a->>'Score' IS NOT NULL
                    """, r => r.GetInt32(0), ct, p.Survey);
                dist = Enumerable.Range(1, 5).Select(i => scores.Count(s => s == i)).ToArray();
                avg = scores.Count == 0 ? null : Math.Round(scores.Average(), 2);
            }
            list.Add(new { id = p.Id, question = p.Q, sendAt = p.Send, closesAt = p.Close, status = p.St, sentCount = p.Sent, createdAt = p.At,
                responses = p.N, hidden = p.N < NlReport.MinGroup, threshold = NlReport.MinGroup, distribution = dist, average = avg });
        }
        return list;
    }

    // ------------------------------------------------------------------ B15 duyurular

    private async Task<int> AnnouncementsJobAsync(GovernanceDbContext db, ChatApp app, CancellationToken ct)
    {
        var tenant = app.TenantSlug;
        var rows = await _sql.QueryAsync("""
            SELECT "Id", "Title", "Body", "Audience", "DepartmentIds", "RequiresAck" FROM governance_announcements
            WHERE "TenantSlug" = $1 AND "PublishAt" <= now() AND "PublishAt" > now() - interval '7 days' AND ("ExpireAt" IS NULL OR "ExpireAt" > now())
            ORDER BY "PublishAt"
            """, r => (Id: r.GetGuid(0), Title: r.GetString(1), Body: r.GetString(2), Aud: r.GetString(3), Depts: r.GetFieldValue<Guid[]>(4), Ack: r.GetBoolean(5)), ct, tenant);
        var sent = 0;
        foreach (var a in rows)
        {
            var link = ChatButton.Link("Duyurular", $"{Origin}/panel/duyurular");
            // Kanal: yalnızca "herkes" kitleli duyuru (departman duyurusu kanala yazılmaz).
            if (a.Aud == Audience.All && !string.IsNullOrWhiteSpace(app.ChannelId) && await ClaimAsync(tenant, $"{app.Id}:ann:{a.Id}:ch", ct))
            {
                try
                {
                    await PostAsync(app, app.ChannelId!, null, ChatReply.Of($"📢 *{a.Title}*\n{Trim(a.Body, 600)}", false, link), ct);
                    ChatMetrics.Sent.WithLabels(ChatMetrics.Provider(app.Platform), "channel", "sent").Inc();
                    sent++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ReleaseAsync(tenant, $"{app.Id}:ann:{a.Id}:ch", ct);
                    await ErrorAsync(app, "announcements", ex.Message);
                }
            }
            if (!a.Ack) continue;
            // "Okudum" gerektiren duyuru: kitledeki her kişiye kendi DM'inde düğme (okuyana gitmez).
            var all = await _people.ListAsync(tenant, ct);
            var members = a.Aud == Audience.All ? all : all.Where(p => p.DepartmentId is { } d && a.Depts.Contains(d)).ToList();
            var linked = (await db.ChatIdentities.IgnoreQueryFilters().AsNoTracking().Where(i => i.AppId == app.Id && i.EmployeeId != null)
                .Select(i => i.EmployeeId!.Value).ToListAsync(ct)).ToHashSet();
            foreach (var m in members.Where(m => linked.Contains(m.Id)))
            {
                var read = await _sql.ScalarAsync("""
                    SELECT 1 FROM governance_acknowledgements WHERE "TenantSlug" = $1 AND "SubjectType" = 'Announcement' AND "SubjectId" = $2 AND ("EmployeeId" = $3 OR "UserId" = $4)
                    """, ct, tenant, a.Id, m.Id, UserIdOf(m));
                if (read is not null) continue;
                if (await SendOnceAsync(db, app, $"ann:{a.Id}:{m.Id}", m.Id, en => Task.FromResult(ChatReply.Of(
                        (en ? "📢 New announcement — please confirm you have read it:\n" : "📢 Yeni duyuru — okuduğunuzu onaylayın:\n") + $"*{a.Title}*\n{Trim(a.Body, 400)}", en,
                        new ChatButton(en ? "I have read it" : "Okudum", "ann_ack", a.Id.ToString(), "primary"),
                        ChatButton.Link(en ? "Announcements" : "Duyurular", $"{Origin}/panel/duyurular"))), "announcement.published", ct))
                    sent++;
            }
        }
        return sent;
    }

    // ------------------------------------------------------------------ B18 birebir hatırlatmaları

    private async Task<int> OneOnOneJobAsync(GovernanceDbContext db, ChatApp app, CancellationToken ct)
    {
        var tenant = app.TenantSlug;
        var rows = await _sql.QueryAsync("""
            SELECT o."Id", o."EmployeeId", m."Id", o."ScheduledAt", jsonb_array_length(coalesce(o."Agenda", '[]'::jsonb))
            FROM engagement_one_on_ones o LEFT JOIN employee_employees m ON m."TenantSlug" = o."TenantSlug" AND m."KeycloakUserId" = o."ManagerUserId"
            WHERE o."TenantSlug" = $1 AND o."Status" = 'Planned' AND o."ScheduledAt" > now() AND o."ScheduledAt" <= now() + interval '24 hours'
            """, r => (Id: r.GetGuid(0), Emp: r.GetGuid(1), Mgr: r.GuidOrNull(2), At: r.GetFieldValue<DateTime>(3), Agenda: r.GetInt32(4)), ct, tenant);
        var sent = 0;
        foreach (var o in rows)
        {
            var left = o.At - DateTime.UtcNow;
            var window = left <= TimeSpan.FromHours(1) ? "1h" : "24h";
            var local = o.At.AddHours(3);
            var emp = await _people.FindAsync(tenant, o.Emp, ct);
            var mgr = o.Mgr is { } mid ? await _people.FindAsync(tenant, mid, ct) : null;
            foreach (var (who, other) in new[] { (o.Emp, mgr?.Name), (o.Mgr ?? Guid.Empty, emp?.Name) })
            {
                if (who == Guid.Empty) continue;
                // Gündem içeriği yazılmaz; yalnızca madde sayısı.
                if (await SendOnceAsync(db, app, $"1on1:{o.Id}:{window}:{who}", who, en => Task.FromResult(ChatReply.Of(en
                        ? $"📅 Reminder: your 1:1{(other is null ? "" : " with *" + ChatService.ShortName(other) + "*")} is {(window == "1h" ? "in about an hour" : "tomorrow")} at {local:HH:mm} — {o.Agenda} agenda item(s)."
                        : $"📅 Hatırlatma: {(other is null ? "" : "*" + ChatService.ShortName(other) + "* ile ")}birebir görüşmeniz {(window == "1h" ? "yaklaşık bir saat sonra" : $"{local:dd.MM}")} saat {local:HH:mm}'de — gündemde {o.Agenda} madde var.", en,
                        ChatButton.Link(en ? "Open agenda" : "Gündemi aç", $"{Origin}/panel/birebir"))), "engagement.oneonone", ct))
                    sent++;
            }
        }
        return sent;
    }

    // ------------------------------------------------------------------ B20 çıkış anketi

    private static readonly string[] ExitReasons = { "Kariyer fırsatı", "Ücret", "Yönetici ilişkisi", "İş yükü", "Taşınma", "Kişisel", "Diğer" };
    private static readonly string[] ExitReasonsEn = { "Career opportunity", "Pay", "Manager relationship", "Workload", "Relocation", "Personal", "Other" };
    private static readonly string[] ExitScoreKeys = { "ManagerScore", "CultureScore", "GrowthScore", "CompensationScore" };

    private static ChatReply ExitQuestion(Guid caseId, int step, bool en)
    {
        ChatButton[] Scale() => Enumerable.Range(1, 5).Select(i => new ChatButton(i.ToString(), "exit", $"{caseId}|{step}|{i}")).ToArray();
        return step switch
        {
            0 => ChatReply.Of(en ? "1/6 — What is the main reason you are leaving?" : "1/6 — Ayrılmanızın ana nedeni nedir?", en,
                ExitReasons.Select((r, i) => new ChatButton(en ? ExitReasonsEn[i] : r, "exit", $"{caseId}|0|{i}")).ToArray()),
            1 => ChatReply.Of(en ? "2/6 — How satisfied were you with your manager? (1 = not at all, 5 = very)" : "2/6 — Yöneticinizden ne kadar memnundunuz? (1 = hiç, 5 = çok)", en, Scale()),
            2 => ChatReply.Of(en ? "3/6 — How would you rate the company culture?" : "3/6 — Şirket kültürünü nasıl değerlendirirsiniz?", en, Scale()),
            3 => ChatReply.Of(en ? "4/6 — How were your growth opportunities?" : "4/6 — Gelişim olanaklarınız nasıldı?", en, Scale()),
            4 => ChatReply.Of(en ? "5/6 — How fair was your pay?" : "5/6 — Ücretiniz ne kadar adildi?", en, Scale()),
            _ => ChatReply.Of(en ? "6/6 — Would you recommend us as an employer?" : "6/6 — Bizi bir işveren olarak önerir misiniz?", en,
                new ChatButton(en ? "Yes" : "Evet", "exit", $"{caseId}|5|1", "primary"), new ChatButton(en ? "No" : "Hayır", "exit", $"{caseId}|5|0")),
        };
    }

    private async Task<int> ExitJobAsync(GovernanceDbContext db, ChatApp app, CancellationToken ct)
    {
        var tenant = app.TenantSlug;
        var cases = await _sql.QueryAsync("""
            SELECT c."Id", c."EmployeeId" FROM engagement_offboarding_cases c JOIN employee_employees e ON e."Id" = c."EmployeeId"
            WHERE c."TenantSlug" = $1 AND c."Status" = 'Open' AND c."ExitInterview" IS NULL AND e."Status" <> 'Terminated'
              AND c."LastWorkingDay" BETWEEN $2 AND $3
            """, r => (Id: r.GetGuid(0), Emp: r.GetGuid(1)), ct, tenant, Today.AddDays(-3), Today.AddDays(14));
        var sent = 0;
        foreach (var c in cases)
        {
            await _sql.ExecuteAsync("""
                INSERT INTO governance_chat_exit_progress ("Id","TenantSlug","CaseId","EmployeeId","Step","AnswersEnc","UpdatedAt") VALUES ($1,$2,$3,$4,0,NULL,now())
                ON CONFLICT ("CaseId") DO NOTHING
                """, ct, Guid.NewGuid(), tenant, c.Id, c.Emp);
            if (await SendOnceAsync(db, app, $"exit:{c.Id}", c.Emp, en =>
                {
                    var q = ExitQuestion(c.Id, 0, en);
                    return Task.FromResult(q with
                    {
                        Text = (en
                            ? "👋 Before you go, would you answer a short, confidential exit survey? 6 questions; only HR sees your answers and they are not shared with your manager. You can skip it.\n\n"
                            : "👋 Ayrılmadan önce kısa ve gizli bir çıkış anketine yanıt verir misiniz? 6 soru; yanıtlarınızı yalnızca İK görür, yöneticinizle paylaşılmaz. İsterseniz yanıtlamayabilirsiniz.\n\n") + q.Text,
                    });
                }, "engagement.exit", ct)) sent++;
        }
        return sent;
    }

    /// <summary>Çıkış anketi yanıtı: ara yanıtlar şifreli; bitince ayrılış kaydının ExitInterview alanına (İK'nın girdiği yoksa) yazılır.</summary>
    private async Task<ChatReply> ExitAnswerAsync(ChatApp app, string tenant, Guid emp, string value, bool en, CancellationToken ct)
    {
        var parts = value.Split('|');
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out var caseId) || !int.TryParse(parts[1], out var step) || !int.TryParse(parts[2], out var answer)) return ChatReply.Of("—", en);
        var progRows = await _sql.QueryAsync("""
            SELECT g."Step", g."AnswersEnc" FROM governance_chat_exit_progress g JOIN engagement_offboarding_cases c ON c."Id" = g."CaseId"
            WHERE g."TenantSlug" = $1 AND g."CaseId" = $2 AND g."EmployeeId" = $3 AND g."CompletedAt" IS NULL AND c."Status" = 'Open'
            """, r => (Step: r.GetInt32(0), Enc: r.Str(1)), ct, tenant, caseId, emp);
        if (progRows.Count == 0) return ChatReply.Of(en ? "This survey is closed. Thank you!" : "Bu anket kapandı. Teşekkürler!", en) with { Replace = true };
        var prog = progRows[0];
        if (prog.Step != step) return ExitQuestion(caseId, prog.Step, en) with { Replace = true };
        var answers = prog.Enc is null ? new JsonObject() : JsonNode.Parse(SecretBox.Unprotect(prog.Enc) ?? "{}")!.AsObject();
        switch (step)
        {
            case 0: if (answer is < 0 or > 6) return ChatReply.Of("—", en); answers["PrimaryReason"] = ExitReasons[answer]; break;
            case >= 1 and <= 4: if (answer is < 1 or > 5) return ChatReply.Of("—", en); answers[ExitScoreKeys[step - 1]] = answer; break;
            case 5: answers["WouldRecommend"] = answer == 1; break;
            default: return ChatReply.Of("—", en);
        }
        if (step < 5)
        {
            await _sql.ExecuteAsync("UPDATE governance_chat_exit_progress SET \"Step\" = $2, \"AnswersEnc\" = $3, \"UpdatedAt\" = now() WHERE \"CaseId\" = $1 AND \"Step\" = $4",
                ct, caseId, step + 1, SecretBox.Protect(answers.ToJsonString())!, step);
            return ExitQuestion(caseId, step + 1, en) with { Replace = true };
        }
        answers["Comments"] = null;
        // engagement-service yazar (yalnızca çalışanın kendi açık kaydı; İK'nın yüz yüze girdiği görüşme varsa üzerine yazılmaz → 409 "exists") ve denetler.
        var res = await ChatInternal.PostAsync(_http, ChatInternal.EngagementBase, "/api/internal/chat/exit-interview",
            new { tenantSlug = tenant, caseId, employeeId = emp, answers, platform = app.Platform }, en, ct);
        var code = res.Str("code");
        if (!res.Ok && code is not ("exists" or "not_found"))
        {
            // Geçici hata: ilerleme 5. adımda kalır, son soru yeniden gösterilir.
            var retry = ExitQuestion(caseId, step, en);
            return retry with { Text = $"⚠️ {res.Message}\n\n{retry.Text}", Replace = true };
        }
        await _sql.ExecuteAsync("UPDATE governance_chat_exit_progress SET \"Step\" = 6, \"AnswersEnc\" = NULL, \"CompletedAt\" = now(), \"UpdatedAt\" = now() WHERE \"CaseId\" = $1", ct, caseId);
        if (code == "not_found") return ChatReply.Of(en ? "This survey is closed. Thank you!" : "Bu anket kapandı. Teşekkürler!", en) with { Replace = true };
        await CountAsync(app, "exit", "completed", ct);
        return ChatReply.Of(en ? "🙏 Thank you. Your answers were sent to HR only. We wish you all the best!" : "🙏 Teşekkürler. Yanıtlarınız yalnızca İK'ya iletildi. Yolunuz açık olsun!", en) with { Replace = true };
    }

    // ------------------------------------------------------------------ BG10 eski onay kartları

    private async Task<int> ExpireCardsAsync(GovernanceDbContext db, ChatApp app, CancellationToken ct)
    {
        if (app.ButtonTtlDays <= 0) return 0;
        var limit = DateTime.UtcNow.AddDays(-app.ButtonTtlDays);
        var old = await db.ChatMessages.IgnoreQueryFilters().Where(m => m.AppId == app.Id && m.State == "Open" && m.CreatedAt < limit).Take(50).ToListAsync(ct);
        foreach (var m in old)
        {
            m.State = "Expired";
            m.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _chat.UpdateMessageAsync(app, m, en => en
                    ? "⌛ This card has expired. Type *approvals* for the current list or open it in HR360."
                    : "⌛ Bu kartın süresi doldu. Güncel liste için *onaylarım* yazın ya da HR360'ta açın.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogInformation("Eski kart güncellenemedi: {Message}", ex.Message); }
        }
        return old.Count;
    }
}
