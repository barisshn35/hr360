using System.Globalization;
using System.Text.Json.Nodes;

namespace GovernanceService.Infrastructure.Chat;

/// <summary>
/// Mesaj biçimleri: Slack Block Kit ve Teams Adaptive Card (1.4). Düğme değerleri
/// "talep|adım" biçimindedir; karar her zaman sunucuda yeniden yetkilendirilir.
/// </summary>
public static class ChatFormat
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    public const string ApproveAction = "hr360_approve";
    public const string RejectAction = "hr360_reject";

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    private static string T(bool en, string tr, string enText) => en ? enText : tr;

    private static string Title(PendingApproval p, bool en)
    {
        var label = ChatService.TypeLabel(p.Type, en);
        return en ? $"{char.ToUpper(label[0], En)}{label[1..]} request awaiting your approval"
                  : $"{char.ToUpper(label[0], Tr)}{label[1..]} talebi onayınızı bekliyor";
    }

    private static string? Sla(PendingApproval p, bool en) =>
        p.SlaDueAt is { } d ? d.AddHours(3).ToString(en ? "d MMMM HH:mm" : "d MMMM HH:mm", en ? En : Tr) : null;

    public static string ApprovalFallback(PendingApproval p, bool en = false) =>
        $"{Title(p, en)}: {p.Requester ?? T(en, "Bir çalışan", "An employee")}" + (p.Subject is null ? "" : $" — {p.Subject}");

    public static string StatusFallback(string status, PendingApproval p) =>
        p.Subject is null ? status : $"{status}: {p.Subject}";

    /// <summary>Slack mrkdwn kaçışı (&amp; &lt; &gt;).</summary>
    private static string E(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static JsonArray SlackApproval(PendingApproval p, string? status, bool en = false)
    {
        var lines = $"*{E(Title(p, en))}*\n*{E(p.Requester ?? T(en, "Bir çalışan", "An employee"))}*" + (p.Subject is null ? "" : $" · {E(p.Subject)}");
        if (p.Details is not null) lines += $"\n{E(p.Details)}";
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = lines } },
        };
        var context = new List<string>();
        if (Sla(p, en) is { } sla && status is null) context.Add(T(en, $"⏱ Son karar: {sla}", $"⏱ Decide by: {sla}"));
        if (status is not null) context.Add(E(status));
        if (context.Count > 0)
            blocks.Add(new JsonObject { ["type"] = "context", ["elements"] = new JsonArray(context.Select(c => (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = c }).ToArray()) });
        var value = $"{p.WorkflowId}|{p.StepId}";
        var elements = new JsonArray();
        if (status is null)
        {
            elements.Add(new JsonObject { ["type"] = "button", ["action_id"] = ApproveAction, ["style"] = "primary", ["value"] = value, ["text"] = PlainText(T(en, "Onayla", "Approve")) });
            // Reddet: gerekçe penceresi açılır (gerekçe yalnızca HR360'ta saklanır).
            elements.Add(new JsonObject { ["type"] = "button", ["action_id"] = RejectAction, ["style"] = "danger", ["value"] = value, ["text"] = PlainText(T(en, "Reddet", "Reject")) });
        }
        elements.Add(new JsonObject { ["type"] = "button", ["action_id"] = "hr360_open", ["url"] = ChatService.WorkflowUrl(p.WorkflowId), ["text"] = PlainText(T(en, "HR360'ta aç", "Open in HR360")) });
        blocks.Add(new JsonObject { ["type"] = "actions", ["block_id"] = $"wf_{p.StepId:N}", ["elements"] = elements });
        return blocks;
    }

    private static JsonObject PlainText(string t) => new() { ["type"] = "plain_text", ["text"] = t, ["emoji"] = true };

    public static JsonArray SlackText(string text, string? link, bool en = false)
    {
        var blocks = new JsonArray { new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = text } } };
        if (link is not null)
            blocks.Add(new JsonObject { ["type"] = "actions", ["elements"] = new JsonArray { new JsonObject { ["type"] = "button", ["action_id"] = "hr360_open", ["url"] = link, ["text"] = PlainText(T(en, "HR360'ta aç", "Open in HR360")) } } });
        return blocks;
    }

    public const string LeaveFormAction = "hr360_leave_form";

    /// <summary>Komut yanıtı: metin + her bekleyen onay için ayrı düğme bloğu (+ izin formu düğmesi).</summary>
    public static JsonArray SlackReply(ChatReply reply)
    {
        var blocks = SlackText(reply.Text, reply.Link, reply.En);
        if (reply.Form == ChatForm.Leave)
            blocks.Add(new JsonObject { ["type"] = "actions", ["elements"] = new JsonArray { new JsonObject { ["type"] = "button", ["action_id"] = LeaveFormAction, ["style"] = "primary", ["value"] = "leave", ["text"] = PlainText(T(reply.En, "İzin talebi oluştur", "Request leave")) } } });
        var all = new List<ChatButton>();
        if (reply.Form == ChatForm.Expense && reply.Expense is { } d)
        {
            all.Add(new ChatButton(T(reply.En, "Taslak oluştur", "Create draft"), "exp_confirm", d.PendingId.ToString(), "primary"));
            all.Add(new ChatButton(T(reply.En, "Vazgeç", "Cancel"), "exp_cancel", d.PendingId.ToString()));
        }
        if (reply.Buttons is { Count: > 0 } buttons) all.AddRange(buttons);
        if (all.Count > 0) blocks.Add(ChatCards.SlackButtons(all, DateTimeOffset.UtcNow));
        foreach (var p in reply.Approvals)
        {
            blocks.Add(new JsonObject { ["type"] = "divider" });
            foreach (var b in SlackApproval(p, null, reply.En)) blocks.Add(b!.DeepClone());
        }
        return blocks;
    }

    public static readonly string[] LeaveTypes = { "Annual", "Unpaid", "Sick", "Marriage", "Paternity", "Bereavement" };

    /// <summary>Slack izin talebi penceresi. Bakiye bilgisi bağlamda gösterilir.</summary>
    public static JsonObject SlackLeaveModal(IReadOnlyList<string> balanceLines, bool en)
    {
        var today = DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd");
        JsonObject Opt(string t) => new() { ["text"] = PlainText(ChatService.LeaveTypeLabel(t, en)), ["value"] = t };
        var blocks = new JsonArray();
        if (balanceLines.Count > 0)
            blocks.Add(new JsonObject { ["type"] = "context", ["elements"] = new JsonArray { new JsonObject { ["type"] = "mrkdwn", ["text"] = string.Join("\n", balanceLines) } } });
        blocks.Add(new JsonObject { ["type"] = "input", ["block_id"] = "type", ["label"] = PlainText(T(en, "İzin türü", "Leave type")),
            ["element"] = new JsonObject { ["type"] = "static_select", ["action_id"] = "v", ["initial_option"] = Opt("Annual"),
                ["options"] = new JsonArray(LeaveTypes.Select(t => (JsonNode)Opt(t)).ToArray()) } });
        blocks.Add(new JsonObject { ["type"] = "input", ["block_id"] = "start", ["label"] = PlainText(T(en, "Başlangıç", "Start date")),
            ["element"] = new JsonObject { ["type"] = "datepicker", ["action_id"] = "v", ["initial_date"] = today } });
        blocks.Add(new JsonObject { ["type"] = "input", ["block_id"] = "end", ["label"] = PlainText(T(en, "Bitiş", "End date")),
            ["element"] = new JsonObject { ["type"] = "datepicker", ["action_id"] = "v", ["initial_date"] = today } });
        blocks.Add(new JsonObject { ["type"] = "input", ["block_id"] = "reason", ["optional"] = true, ["label"] = PlainText(T(en, "Açıklama", "Note")),
            ["hint"] = PlainText(T(en, "Sağlık bilgisi yazmayın; rapor gerekiyorsa İK'ya iletin.", "Do not include health details; send any medical report to HR.")),
            ["element"] = new JsonObject { ["type"] = "plain_text_input", ["action_id"] = "v", ["max_length"] = 500 } });
        return new JsonObject
        {
            ["type"] = "modal", ["callback_id"] = "hr360_leave",
            ["title"] = PlainText(T(en, "İzin talebi", "Leave request")), ["submit"] = PlainText(T(en, "Gönder", "Submit")), ["close"] = PlainText(T(en, "Vazgeç", "Cancel")),
            ["blocks"] = blocks,
        };
    }

    /// <summary>Slack reddetme gerekçesi penceresi. Gerekçe bot mesajına yazılmaz, HR360'ta saklanır.</summary>
    public static JsonObject SlackRejectModal(string metadata, bool en) => new()
    {
        ["type"] = "modal", ["callback_id"] = "hr360_reject", ["private_metadata"] = metadata,
        ["title"] = PlainText(T(en, "Talebi reddet", "Reject request")), ["submit"] = PlainText(T(en, "Reddet", "Reject")), ["close"] = PlainText(T(en, "Vazgeç", "Cancel")),
        ["blocks"] = new JsonArray
        {
            new JsonObject { ["type"] = "input", ["block_id"] = "reason", ["optional"] = true, ["label"] = PlainText(T(en, "Gerekçe", "Reason")),
                ["hint"] = PlainText(T(en, "Talep sahibi gerekçeyi HR360'ta görür; sohbete yazılmaz.", "The requester sees the reason in HR360; it is not posted to chat.")),
                ["element"] = new JsonObject { ["type"] = "plain_text_input", ["action_id"] = "v", ["multiline"] = true, ["max_length"] = 500 } },
        },
    };

    // ------------------------------------------------------------------ Teams

    public static JsonObject TeamsActivity(JsonObject card, string fallback) => new()
    {
        ["type"] = "message",
        ["summary"] = fallback,
        ["attachments"] = new JsonArray { new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card } },
    };

    private static JsonObject Card(JsonArray body, JsonArray? actions = null)
    {
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
        };
        if (actions is { Count: > 0 }) card["actions"] = actions;
        return card;
    }

    private static JsonObject Text(string text, bool bold = false, string? color = null, bool subtle = false)
    {
        var o = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold) o["weight"] = "Bolder";
        if (color is not null) o["color"] = color;
        if (subtle) o["isSubtle"] = true;
        return o;
    }

    public static JsonObject TeamsApprovalCard(PendingApproval p, string? status, bool en = false)
    {
        var facts = new JsonArray
        {
            new JsonObject { ["title"] = T(en, "Talep eden", "Requested by"), ["value"] = p.Requester ?? "—" },
        };
        if (p.Subject is not null) facts.Add(new JsonObject { ["title"] = T(en, "Konu", "Subject"), ["value"] = p.Subject });
        if (p.Details is not null) facts.Add(new JsonObject { ["title"] = T(en, "Ayrıntı", "Details"), ["value"] = p.Details });
        if (Sla(p, en) is { } sla && status is null) facts.Add(new JsonObject { ["title"] = T(en, "Son karar", "Decide by"), ["value"] = sla });
        var body = new JsonArray { Text(Title(p, en), bold: true), new JsonObject { ["type"] = "FactSet", ["facts"] = facts } };
        if (status is not null) body.Add(Text(status, bold: true, color: status.StartsWith('⛔') ? "Attention" : "Good"));
        var actions = new JsonArray();
        if (status is null)
        {
            // Reddederken isteğe bağlı gerekçe: yalnızca HR360'ta saklanır, sohbete yazılmaz.
            body.Add(new JsonObject { ["type"] = "Input.Text", ["id"] = "reason", ["isMultiline"] = true, ["maxLength"] = 500,
                ["placeholder"] = T(en, "Reddetme gerekçesi (isteğe bağlı)", "Reason for rejection (optional)") });
            actions.Add(new JsonObject { ["type"] = "Action.Submit", ["title"] = T(en, "Onayla", "Approve"), ["style"] = "positive",
                ["data"] = new JsonObject { ["hr360"] = "decide", ["decision"] = "approve", ["wf"] = p.WorkflowId.ToString(), ["step"] = p.StepId.ToString() } });
            actions.Add(new JsonObject { ["type"] = "Action.Submit", ["title"] = T(en, "Reddet", "Reject"), ["style"] = "destructive",
                ["data"] = new JsonObject { ["hr360"] = "decide", ["decision"] = "reject", ["wf"] = p.WorkflowId.ToString(), ["step"] = p.StepId.ToString() } });
        }
        actions.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = T(en, "HR360'ta aç", "Open in HR360"), ["url"] = ChatService.WorkflowUrl(p.WorkflowId) });
        return Card(body, actions);
    }

    /// <summary>Teams metni: Slack'in *kalın* biçimi **kalın**'a çevrilir.</summary>
    private static string Md(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"(?<!\*)\*([^*\n]+)\*(?!\*)", "**$1**");

    public static JsonObject TeamsTextCard(string text, string? link, bool en = false)
    {
        var body = new JsonArray(Md(text).Split('\n').Select(l => (JsonNode)Text(l)).ToArray());
        var actions = link is null ? null : new JsonArray { new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = T(en, "HR360'ta aç", "Open in HR360"), ["url"] = link } };
        return Card(body, actions);
    }

    /// <summary>Teams izin talebi kartı (tür, tarihler, açıklama).</summary>
    public static JsonObject TeamsLeaveCard(IReadOnlyList<string> balanceLines, bool en)
    {
        var today = DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd");
        var body = new JsonArray { Text(T(en, "İzin talebi", "Leave request"), bold: true) };
        foreach (var l in balanceLines) body.Add(Text(Md(l), subtle: true));
        body.Add(new JsonObject { ["type"] = "Input.ChoiceSet", ["id"] = "type", ["label"] = T(en, "İzin türü", "Leave type"), ["value"] = "Annual",
            ["choices"] = new JsonArray(LeaveTypes.Select(t => (JsonNode)new JsonObject { ["title"] = ChatService.LeaveTypeLabel(t, en), ["value"] = t }).ToArray()) });
        body.Add(new JsonObject { ["type"] = "Input.Date", ["id"] = "start", ["label"] = T(en, "Başlangıç", "Start date"), ["value"] = today });
        body.Add(new JsonObject { ["type"] = "Input.Date", ["id"] = "end", ["label"] = T(en, "Bitiş", "End date"), ["value"] = today });
        body.Add(new JsonObject { ["type"] = "Input.Text", ["id"] = "reason", ["label"] = T(en, "Açıklama (sağlık bilgisi yazmayın)", "Note (no health details)"), ["maxLength"] = 500 });
        var actions = new JsonArray { new JsonObject { ["type"] = "Action.Submit", ["title"] = T(en, "Gönder", "Submit"), ["data"] = new JsonObject { ["hr360"] = "leave" } } };
        return Card(body, actions);
    }

    /// <summary>B6: fiş önerisi kartı (tutar, tarih, kategori düzenlenebilir; görüntü saklanmaz).</summary>
    public static JsonObject TeamsExpenseCard(ExpenseDraft d, string text, bool en)
    {
        var body = new JsonArray(Md(text).Split('\n').Select(l => (JsonNode)Text(l)).ToArray());
        body.Add(new JsonObject { ["type"] = "Input.Text", ["id"] = "amount", ["label"] = T(en, "Tutar (TL)", "Amount (TRY)"), ["value"] = d.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "" });
        body.Add(new JsonObject { ["type"] = "Input.Date", ["id"] = "date", ["label"] = T(en, "Harcama tarihi", "Expense date"), ["value"] = (d.Date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3))).ToString("yyyy-MM-dd") });
        body.Add(new JsonObject { ["type"] = "Input.ChoiceSet", ["id"] = "category", ["label"] = T(en, "Kategori", "Category"), ["value"] = d.Category,
            ["choices"] = new JsonArray(ReceiptParser.Categories.Select(c => (JsonNode)new JsonObject { ["title"] = ReceiptParser.CategoryLabel(c, en), ["value"] = c }).ToArray()) });
        var actions = new JsonArray
        {
            new JsonObject { ["type"] = "Action.Submit", ["title"] = T(en, "Taslak oluştur", "Create draft"), ["style"] = "positive",
                ["data"] = new JsonObject { ["hr360"] = "expense", ["id"] = d.PendingId.ToString() } },
            new JsonObject { ["type"] = "Action.Submit", ["title"] = T(en, "Vazgeç", "Cancel"),
                ["data"] = new JsonObject { ["hr360"] = "x", ["a"] = "exp_cancel", ["v"] = ButtonStamp.Encode(d.PendingId.ToString(), DateTimeOffset.UtcNow) } },
        };
        return Card(body, actions);
    }

    /// <summary>B6: Slack'te "Düzelt" penceresi (fiş önerisi).</summary>
    public static JsonObject SlackExpenseModal(ExpenseDraft d, bool en)
    {
        JsonObject Opt(string c) => new() { ["text"] = PlainText(ReceiptParser.CategoryLabel(c, en)), ["value"] = c };
        return new JsonObject
        {
            ["type"] = "modal", ["callback_id"] = "hr360_expense", ["private_metadata"] = d.PendingId.ToString(),
            ["title"] = PlainText(T(en, "Masraf taslağı", "Expense draft")), ["submit"] = PlainText(T(en, "Oluştur", "Create")), ["close"] = PlainText(T(en, "Vazgeç", "Cancel")),
            ["blocks"] = new JsonArray
            {
                new JsonObject { ["type"] = "input", ["block_id"] = "amount", ["label"] = PlainText(T(en, "Tutar (TL)", "Amount (TRY)")),
                    ["element"] = new JsonObject { ["type"] = "plain_text_input", ["action_id"] = "v", ["initial_value"] = d.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "" } },
                new JsonObject { ["type"] = "input", ["block_id"] = "date", ["label"] = PlainText(T(en, "Harcama tarihi", "Expense date")),
                    ["element"] = new JsonObject { ["type"] = "datepicker", ["action_id"] = "v", ["initial_date"] = (d.Date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3))).ToString("yyyy-MM-dd") } },
                new JsonObject { ["type"] = "input", ["block_id"] = "category", ["label"] = PlainText(T(en, "Kategori", "Category")),
                    ["element"] = new JsonObject { ["type"] = "static_select", ["action_id"] = "v", ["initial_option"] = Opt(d.Category),
                        ["options"] = new JsonArray(ReceiptParser.Categories.Select(c => (JsonNode)Opt(c)).ToArray()) } },
            },
        };
    }

    public static IEnumerable<JsonObject> TeamsReply(ChatReply reply)
    {
        if (reply.Form == ChatForm.Expense && reply.Expense is { } draft)
        {
            yield return TeamsActivity(TeamsExpenseCard(draft, reply.Text, reply.En), reply.Text);
            yield break;
        }
        var main = TeamsTextCard(reply.Text, reply.Link, reply.En);
        if (reply.Buttons is { Count: > 0 } buttons)
        {
            var acts = main["actions"] as JsonArray ?? new JsonArray();
            foreach (var a in ChatCards.TeamsActions(buttons, DateTimeOffset.UtcNow)) acts.Add(a!.DeepClone());
            main["actions"] = acts;
        }
        yield return TeamsActivity(main, reply.Text);
        if (reply.Form == ChatForm.Leave)
            yield return TeamsActivity(TeamsLeaveCard(reply.FormLines ?? Array.Empty<string>(), reply.En), reply.Text);
        foreach (var p in reply.Approvals)
            yield return TeamsActivity(TeamsApprovalCard(p, null, reply.En), ApprovalFallback(p, reply.En));
    }

    /// <summary>Gelen webhook (Teams Workflows / Power Automate) için kanal mesajı.</summary>
    public static JsonObject TeamsWebhookMessage(string text, string type) => new()
    {
        ["type"] = "message",
        ["attachments"] = new JsonArray
        {
            new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["contentUrl"] = null,
                ["content"] = Card(new JsonArray { Text("HR360", bold: true), Text(text), Text(type, subtle: true) }),
            },
        },
    };
}
