using System.Globalization;
using Microsoft.EntityFrameworkCore;
using GovernanceService.Data;
using GovernanceService.Infrastructure.Ai;

namespace GovernanceService.Infrastructure;

/// <summary>Soran kişi: web'de jetondan, sohbet botunda doğrulanmış sohbet hesabından gelir.</summary>
/// <param name="ReportDepartmentId">Sohbet botu (B19): yönetici rapor soruları bu departmanla sınırlanır (İK değilse).</param>
public sealed record AssistantAsker(string Tenant, Person? Me, string? UserId, bool IsManager, bool IsHr, bool En, Guid? ReportDepartmentId = null);

public sealed record AssistantLink(string Label, string Path);

public sealed record AssistantAnswer(string Reply, string Source, IReadOnlyList<AssistantLink> Links,
    NlReport.Result? Report = null, IReadOnlyList<string>? Related = null);

/// <summary>
/// İK asistanı: izin bakiyesi, bekleyen talepler, tatiller, kim izinde, masraflar, bordro,
/// yöneticinin analitik soruları (rapor motoru), yapay zekâ (açıksa) ve bilgi bankası.
/// Uygulama içi asistan ve Slack/Teams botu aynı mantığı kullanır.
/// </summary>
public sealed class HrAssistant(Sql db, PeopleDirectory people, GovernanceDbContext gdb, AiGateway ai)
{
    private static readonly CultureInfo TrCulture = new("tr-TR");

    public async Task<AssistantAnswer> AskAsync(AssistantAsker a, string raw, CancellationToken ct)
    {
        string L(string tr, string en) => a.En ? en : tr;
        AssistantAnswer Reply(string text, string source, params (string Label, string Path)[] links) =>
            new(text, source, links.Select(l => new AssistantLink(l.Label, l.Path)).ToList());
        var q = " " + NlReport.Norm(raw) + " ";
        var me = a.Me;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var tr = TrCulture;

        if (Has(q, "merhaba", "selam", "yardim", "neler yapabilirsin", "ne yapabilirsin", " hello", " hi ", " hey ", " help", "what can you do"))
            return Reply(L("Merhaba! Şunları sorabilirsiniz: **izin bakiyem**, **bekleyen taleplerim**, **sonraki resmî tatil**, **bugün kim izinde**, **masraflarım**",
                    "Hello! You can ask: **my leave balance**, **my pending requests**, **next public holiday**, **who is on leave today**, **my expenses**") +
                (a.IsManager ? L(", ya da *\"son 6 ayda departmanlara göre izin günleri\"* gibi rapor soruları.", ", or report questions like *\"leave days by department in the last 6 months\"*.")
                              : L(". Şirket politikalarını da sorabilirsiniz (ör. *uzaktan çalışma*).", ". You can also ask about company policies (e.g. *remote work*).")), "help");

        // KVKK: kişisel veri soruları yalnızca soranın kendisi için yanıtlanır. "Ayşe'nin izin bakiyesi"
        // gibi başka birine ait soru kendi verisiyle yanıtlanmaz; ilgili ekrana yönlendirilir.
        var personalTopic = PersonalTopic(q);
        if (personalTopic is not null && await AboutSomeoneElseAsync(a, raw, q, ct))
            return Reply(L("Başka çalışanların bilgilerini asistan paylaşmaz. Yöneticiyseniz ekibinizin bilgilerini ilgili ekranda görebilirsiniz.",
                    "The assistant does not share other employees' information. If you are a manager, you can see your team's information on the relevant screen."),
                "help", (L(personalTopic.Value.Label.Tr, personalTopic.Value.Label.En), personalTopic.Value.Path));

        if ((Has(q, "izin") && Has(q, "bakiye", "kac gun", "kalan", "hakkim", "ne kadar")) || (Has(q, " leave", "time off", "vacation") && Has(q, "balance", "how many days", " left", "remaining")))
        {
            if (me is null) return Reply(L("Hesabınız bir çalışan kaydına bağlı olmadığı için izin bakiyesi yok.", "Your account is not linked to an employee record, so there is no leave balance."), "data");
            var rows = await db.QueryAsync("""
                SELECT "Type", "EntitledDays", "UsedDays", "PendingDays" FROM leave_balances
                WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 AND "Year" = $3 ORDER BY 1
                """, r => (T: r.GetString(0), E: r.GetDecimal(1), U: r.GetDecimal(2), P: r.GetDecimal(3)), ct, a.Tenant, me.Id, today.Year);
            if (rows.Count == 0) return Reply(L($"{today.Year} için tanımlı izin bakiyeniz yok. İK ile görüşebilirsiniz.", $"You have no leave balance defined for {today.Year}. Please contact HR."), "data", (L("İzin ekranı", "Leave"), "/panel/izin"));
            var lines = rows.Select(r => a.En
                ? $"• **{LeaveLabel(r.T, true)}**: {r.E - r.U - r.P:0.#} days available ({r.U:0.#} used, {r.P:0.#} pending, {r.E:0.#} total)"
                : $"• **{LeaveLabel(r.T)}**: {r.E - r.U - r.P:0.#} gün kullanılabilir ({r.U:0.#} kullanıldı, {r.P:0.#} onay bekliyor, toplam {r.E:0.#})");
            return Reply(L($"{today.Year} izin durumunuz:\n", $"Your leave for {today.Year}:\n") + string.Join("\n", lines), "data", (L("İzin talebi oluştur", "Request leave"), "/panel/izin"));
        }

        if (Has(q, "bekleyen", "onay bekleyen", "taleplerim", "talebim", "pending", "my request", "awaiting"))
        {
            if (me is null) return Reply(L("Hesabınız bir çalışan kaydına bağlı değil.", "Your account is not linked to an employee record."), "data");
            // Sayı gerçek toplamdır (COUNT); liste yalnızca en yeni 10 talebi gösterir.
            var mineTotal = Convert.ToInt64(await db.ScalarAsync("""
                SELECT count(*) FROM workflow_requests WHERE "TenantSlug" = $1 AND "RequesterEmployeeId" = $2 AND "Status" = 'Pending'
                """, ct, a.Tenant, me.Id));
            var mine = await db.QueryAsync("""
                SELECT "Subject", "CreatedAt" FROM workflow_requests WHERE "TenantSlug" = $1 AND "RequesterEmployeeId" = $2 AND "Status" = 'Pending' ORDER BY 2 DESC LIMIT 10
                """, r => (S: r.GetString(0), At: r.GetFieldValue<DateTime>(1)), ct, a.Tenant, me.Id);
            var waiting = Convert.ToInt64(await db.ScalarAsync("""
                SELECT count(DISTINCT s."WorkflowRequestId") FROM workflow_approval_steps s JOIN workflow_requests w ON w."Id" = s."WorkflowRequestId"
                WHERE s."TenantSlug" = $1 AND w."Status" = 'Pending' AND s."Decision" = 'Pending' AND coalesce(s."DelegatedToEmployeeId", s."ApproverEmployeeId") = $2
                """, ct, a.Tenant, me.Id));
            var text = mineTotal == 0 ? L("Onay bekleyen talebiniz yok.", "You have no requests awaiting approval.")
                : L($"Onay bekleyen **{mineTotal}** talebiniz var", $"You have **{mineTotal}** request(s) awaiting approval")
                  + (mineTotal > mine.Count ? L($" (en yeni {mine.Count} tanesi):\n", $" (latest {mine.Count}):\n") : ":\n")
                  + string.Join("\n", mine.Select(m => $"• {m.S} ({m.At.AddHours(3):dd.MM})"));
            if (waiting > 0) text += L($"\n\nAyrıca **sizin kararınızı bekleyen {waiting} talep** var.", $"\n\nThere are also **{waiting} request(s) awaiting your decision**.");
            return Reply(text, "data", (L("Onay kutusu", "Approvals"), "/panel/onaylar"));
        }

        if (Has(q, "tatil", "bayram", "holiday"))
        {
            var next = await db.QueryAsync("SELECT \"Date\", \"Name\" FROM leave_public_holidays WHERE \"TenantSlug\" = $1 AND \"Date\" >= $2 ORDER BY 1 LIMIT 5",
                r => (D: r.GetFieldValue<DateOnly>(0), N: r.GetString(1)), ct, a.Tenant, today);
            return Reply(next.Count == 0 ? L("Tanımlı yaklaşan resmî tatil yok.", "No upcoming public holidays are defined.") :
                L("Yaklaşan resmî tatiller:\n", "Upcoming public holidays:\n") + string.Join("\n", next.Select(n => a.En
                    ? $"• **{n.N}** — {n.D.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))} (in {n.D.DayNumber - today.DayNumber} days)"
                    : $"• **{n.N}** — {n.D.ToString("d MMMM yyyy, dddd", tr)} ({n.D.DayNumber - today.DayNumber} gün sonra)")), "data");
        }

        if (Has(q, "kim izinde", "izindekiler", "izinde kim", "bugun izinde", "who is on leave", "who's on leave", "who is off", "on leave today"))
        {
            var rows = await db.QueryAsync("""
                SELECT l."EmployeeId", e."FirstName" || ' ' || e."LastName", l."EndDate" FROM leave_requests l JOIN employee_employees e ON e."Id" = l."EmployeeId"
                WHERE l."TenantSlug" = $1 AND l."Status" = 'Approved' AND l."StartDate" <= $2 AND l."EndDate" >= $2 ORDER BY 2
                """, r => (Id: r.GetGuid(0), N: r.GetString(1), E: r.GetFieldValue<DateOnly>(2)), ct, a.Tenant, today);
            // KVKK veri en aza indirme: İK herkesi, yönetici yalnızca ekibini görür; diğerleri sayıyı.
            var visible = a.IsHr ? rows
                : me is null ? new()
                : (await people.TeamOfAsync(a.Tenant, me.Id, ct)).Select(p => p.Id).ToHashSet() is { Count: > 0 } team
                    ? rows.Where(r => team.Contains(r.Id)).ToList() : new();
            string Lines() => string.Join("\n", visible.Select(r => a.En ? $"• {r.N} — until {r.E:dd.MM}" : $"• {r.N} — {r.E:dd.MM}'e kadar"));
            string text;
            if (rows.Count == 0) text = L("Bugün izinde olan kimse yok.", "Nobody is on leave today.");
            else if (a.IsHr) text = L("Bugün izinde olanlar:\n", "On leave today:\n") + Lines();
            else if (visible.Count > 0) text = L("Ekibinizden bugün izinde olanlar:\n", "On leave today from your team:\n") + Lines()
                + (rows.Count > visible.Count ? L($"\nŞirket genelinde toplam {rows.Count} kişi izinde.", $"\n{rows.Count} people are on leave company-wide.") : "");
            else text = L($"Bugün şirkette {rows.Count} kişi izinde.", $"{rows.Count} people are on leave today.");
            return Reply(text, "data", (L("Kim nerede", "Who's where"), "/panel/ofis"));
        }

        if (Has(q, "masraf", "expense"))
        {
            if (me is null) return Reply(L("Hesabınız bir çalışan kaydına bağlı değil.", "Your account is not linked to an employee record."), "data");
            var rows = await db.QueryAsync("""
                SELECT "Title", "TotalAmount", "Currency", "Status" FROM expense_claims WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" DESC LIMIT 5
                """, r => (T: r.GetString(0), A: r.GetDecimal(1), C: r.GetString(2), S: r.GetString(3)), ct, a.Tenant, me.Id);
            if (rows.Count > 0 || !Has(q, "politika", "limit", "kural", "nasil", "policy", "rule", " how "))
                return Reply(rows.Count == 0 ? L("Henüz masraf beyanınız yok.", "You have no expense claims yet.") :
                    L("Son masraflarınız:\n", "Your recent expenses:\n") + string.Join("\n", rows.Select(r => $"• {r.T}: {r.A.ToString("N2", tr)} {r.C} — {ExpenseLabel(r.S, a.En)}")), "data", (L("Masraf ekranı", "Expenses"), "/panel/masraf"));
        }

        // "Maaş ne zaman yatar?" → bilgi bankasındaki ödeme günü makalesi (simülasyon değil).
        if (Has(q, "maas", "bordro", "salary", "payroll", " pay ") && Has(q, "ne zaman", "hangi gun", "yatar", "yatiyor", "yatacak", "odenir", "odeniyor", "odenecek", "odeme gunu",
                " when ", "pay day", "payday", "paid"))
        {
            var kb = await gdb.KbArticles.AsNoTracking().ToListAsync(ct);
            var payDay = kb.FirstOrDefault(x => NlReport.Norm(x.Title).Contains("maas odeme"))
                ?? kb.FirstOrDefault(x => x.Tags.Select(NlReport.Norm).Contains("odeme") && x.Tags.Select(NlReport.Norm).Any(t => t is "maas" or "bordro"));
            if (payDay is not null)
                return new($"**{payDay.Title}**\n\n{payDay.Body}", "kb", Array.Empty<AssistantLink>());
            return Reply(L("Maaş ödeme günü bilgi bankasında tanımlı değil. Sorunuzu İK'ya bir **İK vakası** olarak iletebilirsiniz.",
                    "The salary payment day is not defined in the knowledge base. You can send your question to HR as an **HR case**."),
                "fallback", (L("İK vakası aç", "Open an HR case"), "/panel/ik-vakalari"));
        }

        // İK'nın "maaş dağılımı" sorusu rapor motoruna gider (G3); diğerleri bordro simülasyonuna.
        var salaryReport = a.IsHr && Has(q, "dagilim", "distribution", "bant", "band", "medyan", "median", "ceyrek", "quartile");
        if (!salaryReport && Has(q, "maas", "bordro", "net ucret", "brut", "salary", "payroll", "gross", "net pay"))
            return Reply(L("Brütten nete hesap için **Bordro simülasyonu** ekranını kullanabilirsiniz (2026 SGK, gelir vergisi dilimleri ve asgari ücret istisnası dahil).",
                    "Use the **Payroll simulation** screen for gross-to-net calculations (2026 SGK, income tax brackets and minimum wage exemption included)."),
                "help", (L("Bordro simülasyonu", "Payroll simulation"), "/panel/bordro-simulasyonu"));

        // Yönetici analitik sorusu → rapor motoru
        if (a.IsManager && Has(q, "kac", "sayisi", "toplam", "ortalama", "dagilim", "gore", "aylik", "en cok", "trend",
                "how many", "number of", "total", "average", " by ", "monthly", " most ", "count", "headcount"))
        {
            var report = await NlReport.RunAsync(db, a.Tenant, raw, a.IsHr, ct, a.En ? "en" : "tr", new NlReport.Options(DepartmentId: a.IsHr ? null : a.ReportDepartmentId, AllowSalary: a.IsHr));
            if (report.Understood)
                return new(report.Interpretation + ":", "report", new[] { new AssistantLink(L("Doğal dilde rapor", "Report assistant"), "/panel/rapor-asistani") }, report);
        }

        // Yapay zekâ açıksa: bilgi bankası bağlamıyla model yanıtı (kişisel veri gönderilmez).
        if (await ai.EnabledAsync(ct))
        {
            var (reply, related, failure) = await ai.AssistantAsync(a.UserId, raw, ct, a.En);
            if (failure is null && !string.IsNullOrWhiteSpace(reply))
                return new(reply, "llm", Array.Empty<AssistantLink>(), Related: related);
        }

        // Bilgi bankası
        var words = q.Split(new[] { ' ', ',', '.', '?', '!', ':', ';' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToHashSet();
        var articles = await gdb.KbArticles.AsNoTracking().ToListAsync(ct);
        var best = articles.Select(x =>
            {
                var t = NlReport.Norm(x.Title); var b = NlReport.Norm(x.Body); var tags = x.Tags.Select(NlReport.Norm).ToList();
                var score = words.Sum(w => (t.Contains(w) ? 3 : 0) + (tags.Any(g => g.Contains(w) || w.Contains(g)) ? 3 : 0) + (b.Contains(w) ? 1 : 0));
                return (x, score);
            })
            .Where(x => x.score >= 3).OrderByDescending(x => x.score).Take(2).ToList();
        if (best.Count > 0)
            return new($"**{best[0].x.Title}**\n\n{best[0].x.Body}", "kb", Array.Empty<AssistantLink>(), Related: best.Skip(1).Select(x => x.x.Title).ToList());

        return Reply(L("Bunu yanıtlayacak bir bilgiye sahip değilim. Sorunuzu İK'ya bir **İK vakası** olarak iletebilirsiniz; ilgili kişi size dönecektir.",
                "I don't have information to answer that. You can send your question to HR as an **HR case**; the right person will get back to you."),
            "fallback", (L("İK vakası aç", "Open an HR case"), "/panel/ik-vakalari"));
    }

    private static bool Has(string q, params string[] words) => words.Any(q.Contains);

    /// <summary>Soru kişisel veri konusu mu (izin bakiyesi, talepler, masraf, maaş)? Öyleyse yönlendirme ekranı.</summary>
    private static ((string Tr, string En) Label, string Path)? PersonalTopic(string q)
    {
        if (Has(q, "izin", " leave", "time off", "vacation") && Has(q, "bakiye", "kac gun", "kalan", "hakki", "ne kadar", "balance", "how many days", " left", "remaining"))
            return (("İzin ekranı", "Leave"), "/panel/izin");
        if (Has(q, "masraf", "expense")) return (("Masraf ekranı", "Expenses"), "/panel/masraf");
        if (Has(q, "maas", "bordro", "ucret", "salary", "payroll", "payslip")) return (("Çalışanlar", "Employees"), "/panel/calisanlar");
        if (Has(q, "talep", "request")) return (("Onay kutusu", "Approvals"), "/panel/onaylar");
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex Possessive = new(
        @"(?<![\p{L}])(\p{Lu}[\p{L}]+)\s*['’`]\s*(n[ıiuü]n|[ıiuü]n|s)(?![\p{L}])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SelfWords = new() { "ben", "benim", "kendi", "kendim", "my", "i", "me", "hr", "ik", "sgk", "sirket", "sirketin", "company", "turkiye" };

    /// <summary>
    /// Soru başka bir kişiye mi ait? "Ayşe'nin…", "Mehmet's…" gibi iyelik eki almış özel ad ya da
    /// kiracıdaki başka bir çalışanın adının iyelik ekiyle (ayşenin) geçmesi. Soranın kendi adı sayılmaz.
    /// </summary>
    private async Task<bool> AboutSomeoneElseAsync(AssistantAsker a, string raw, string q, CancellationToken ct)
    {
        var ownFirst = a.Me is null ? null : NlReport.Norm(a.Me.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
        foreach (System.Text.RegularExpressions.Match m in Possessive.Matches(raw))
        {
            var name = NlReport.Norm(m.Groups[1].Value);
            if (!SelfWords.Contains(name) && name != ownFirst) return true;
        }
        // Kesme işaretsiz yazım (ör. "aysenin izin bakiyesi"): yalnızca kiracıdaki gerçek ad + iyelik eki.
        var names = await db.QueryAsync("""
            SELECT DISTINCT lower("FirstName") FROM employee_employees WHERE "TenantSlug" = $1 AND "FirstName" IS NOT NULL
            """, r => NlReport.Norm(r.GetString(0)), ct, a.Tenant);
        var tokens = q.Split(new[] { ' ', ',', '.', '?', '!', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var n in names.Where(n => n.Length >= 3 && n != ownFirst))
            if (tokens.Any(t => t.Length > n.Length && t.StartsWith(n) && t[n.Length..] is "nin" or "in" or "un" or "nun" or "s")) return true;
        return false;
    }

    public static string LeaveLabel(string t, bool en = false) => en ? t switch
    {
        "Annual" => "Annual leave", "Sick" => "Sick leave", "Unpaid" => "Unpaid leave", "Maternity" => "Maternity leave", "Paternity" => "Paternity leave",
        "Marriage" => "Marriage leave", "Bereavement" => "Bereavement leave", _ => t,
    } : t switch
    {
        "Annual" => "Yıllık izin", "Sick" => "Hastalık izni", "Unpaid" => "Ücretsiz izin", "Maternity" => "Doğum izni", "Paternity" => "Babalık izni",
        "Marriage" => "Evlilik izni", "Bereavement" => "Vefat izni", _ => t,
    };

    public static string ExpenseLabel(string s, bool en = false) => en ? s switch
    {
        "Draft" => "draft", "Submitted" => "in approval", "Approved" => "approved", "Rejected" => "rejected", "Paid" => "paid", _ => s,
    } : s switch
    {
        "Draft" => "taslak", "Submitted" => "onayda", "Approved" => "onaylandı", "Rejected" => "reddedildi", "Paid" => "ödendi", _ => s,
    };
}
