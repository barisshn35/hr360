using System.Text.Json.Nodes;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure.Chat;

/// <summary>
/// Sağlayıcıdan bağımsız düğme. <see cref="Url"/> doluysa bağlantı düğmesidir ("Panelde aç");
/// değilse tıklama sunucuya <see cref="Action"/> + <see cref="Value"/> olarak döner ve HER ZAMAN
/// yeniden yetkilendirilir (değer yalnızca kimlik taşır, yetki taşımaz).
/// </summary>
public sealed record ChatButton(string Label, string Action, string Value = "", string? Style = null, string? Url = null)
{
    public static ChatButton Link(string label, string url) => new(label, "open", "", null, url);
    /// <summary>Hızlı yanıt: tıklanınca bu metin komut olarak çalışır (BG17).</summary>
    public static ChatButton Say(string label, string? text = null) => new(label, "say", text ?? label);
}

/// <summary>Düğmelerin dört sağlayıcıya göre biçimlenmesi (BG10 için değerlere verilme zamanı eklenir).</summary>
public static class ChatCards
{
    public const string SlackPrefix = "hr360x:";

    private static JsonObject PlainText(string t) => new() { ["type"] = "plain_text", ["text"] = t.Length > 75 ? t[..75] : t, ["emoji"] = true };

    public static JsonObject SlackButtons(IReadOnlyList<ChatButton> buttons, DateTimeOffset now)
    {
        var elements = new JsonArray();
        var i = 0;
        foreach (var b in buttons.Take(25))
        {
            var o = new JsonObject { ["type"] = "button", ["text"] = PlainText(b.Label) };
            if (b.Url is not null) { o["action_id"] = $"hr360_open_{i++}"; o["url"] = b.Url; }
            else
            {
                o["action_id"] = $"{SlackPrefix}{b.Action}:{i++}";
                o["value"] = ButtonStamp.Encode(b.Value, now);
                if (b.Style is "primary" or "danger") o["style"] = b.Style;
            }
            elements.Add(o);
        }
        return new JsonObject { ["type"] = "actions", ["elements"] = elements };
    }

    /// <summary>Slack action_id "hr360x:eylem:sıra" → eylem.</summary>
    public static string? SlackAction(string? actionId) =>
        actionId is not null && actionId.StartsWith(SlackPrefix) ? actionId[SlackPrefix.Length..].Split(':')[0] : null;

    public static JsonArray TeamsActions(IReadOnlyList<ChatButton> buttons, DateTimeOffset now)
    {
        var arr = new JsonArray();
        foreach (var b in buttons.Take(20))
        {
            if (b.Url is not null)
                arr.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = b.Label, ["url"] = b.Url });
            else
            {
                var a = new JsonObject { ["type"] = "Action.Submit", ["title"] = b.Label,
                    ["data"] = new JsonObject { ["hr360"] = "x", ["a"] = b.Action, ["v"] = ButtonStamp.Encode(b.Value, now) } };
                if (b.Style == "primary") a["style"] = "positive";
                if (b.Style == "danger") a["style"] = "destructive";
                arr.Add(a);
            }
        }
        return arr;
    }

    /// <summary>Mattermost ileti ekleri: düğmeler entegrasyon adresine imzalı bağlamla döner; bağlantılar metinde.</summary>
    public static JsonArray MattermostAttachments(ChatApp app, string text, IReadOnlyList<ChatButton> buttons, DateTimeOffset now)
    {
        var secret = SecretBox.Unprotect(app.IncomingTokenEnc) ?? "";
        var actions = new JsonArray();
        var links = new List<string>();
        var i = 0;
        foreach (var b in buttons.Take(20))
        {
            if (b.Url is not null) { links.Add($"[{b.Label}]({b.Url})"); continue; }
            var v = ButtonStamp.Encode(b.Value, now);
            actions.Add(new JsonObject
            {
                ["id"] = $"hr360x{i++}", ["name"] = b.Label, ["type"] = "button",
                ["style"] = b.Style == "danger" ? "danger" : b.Style == "primary" ? "primary" : "default",
                ["integration"] = new JsonObject
                {
                    ["url"] = $"{ChatService.PublicOrigin}/api/governance/chat/mattermost/{app.Id}/actions",
                    ["context"] = new JsonObject { ["a"] = b.Action, ["v"] = v, ["sig"] = IncomingToken.Sign(secret, b.Action, v) },
                },
            });
        }
        var att = new JsonObject { ["text"] = links.Count == 0 ? "" : string.Join(" · ", links) };
        if (actions.Count > 0) att["actions"] = actions;
        return actions.Count == 0 && links.Count == 0 ? new JsonArray() : new JsonArray { att };
    }

    /// <summary>Rocket.Chat: düğme tıklanınca sohbet penceresine "hr360x eylem değer" yazılır ve giden entegrasyonla döner.</summary>
    public static JsonArray RocketAttachments(IReadOnlyList<ChatButton> buttons, DateTimeOffset now)
    {
        var actions = new JsonArray();
        foreach (var b in buttons.Take(20))
        {
            if (b.Url is not null) actions.Add(new JsonObject { ["type"] = "button", ["text"] = b.Label, ["url"] = b.Url, ["is_webview"] = false });
            else actions.Add(new JsonObject { ["type"] = "button", ["text"] = b.Label, ["msg"] = $"hr360x {b.Action} {ButtonStamp.Encode(b.Value, now)}", ["msg_in_chat_window"] = true });
        }
        return actions.Count == 0 ? new JsonArray() : new JsonArray { new JsonObject { ["button_alignment"] = "horizontal", ["actions"] = actions } };
    }

    /// <summary>Rocket.Chat'ten dönen düğme metni: "hr360x eylem değer".</summary>
    public static (string Action, string Value)? RocketAction(string? text)
    {
        var parts = (text ?? "").Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "hr360x" ? (parts[1], parts.Length == 3 ? parts[2] : "") : null;
    }

    /// <summary>Slack/Teams biçimindeki *kalın* → Mattermost/Rocket.Chat **kalın** / *kalın*.</summary>
    public static string MarkdownFor(string platform, string text) => platform == "Mattermost"
        ? System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\*)\*([^*\n]+)\*(?!\*)", "**$1**")
        : text;

    /// <summary>Onay kartı düğmeleri (Mattermost/Rocket.Chat): değer "talep|adım".</summary>
    public static IReadOnlyList<ChatButton> ApprovalButtons(PendingApproval p, bool en) => new[]
    {
        new ChatButton(en ? "Approve" : "Onayla", "decide", $"{p.WorkflowId}|{p.StepId}|a", "primary"),
        new ChatButton(en ? "Reject" : "Reddet", "decide", $"{p.WorkflowId}|{p.StepId}|r", "danger"),
        ChatButton.Link(en ? "Open in HR360" : "HR360'ta aç", ChatService.WorkflowUrl(p.WorkflowId)),
    };

    /// <summary>Mattermost/Rocket.Chat için onay kartı metni.</summary>
    public static string ApprovalText(PendingApproval p, bool en) =>
        $"*{ChatFormat.ApprovalFallback(p, en)}*" + (p.Details is null ? "" : $"\n{p.Details}");
}
