namespace NotificationService.Email;

/// <summary>
/// Bildirimin duz metin Subject/Body'sini, HR360 marka kimligiyle
/// (zumrut yesili, logo, sade bir govde) saran responsive bir HTML e-posta
/// govdesine cevirir. Tum e-posta istemcilerinde (Outlook dahil) tutarli
/// gorunmesi icin inline stil ve table-tabanli duzen kullanilir - modern
/// CSS (flex/grid) cogu masaustu Outlook surumunde calismaz.
/// </summary>
public static class EmailTemplateRenderer
{
    private const string Emerald = "#10b77f";
    private const string EmeraldDark = "#0b8f63";
    private const string HeaderBg = "#eefaf5";
    private const string TextDark = "#1f2937";
    private const string TextMuted = "#6b7280";
    private const string Border = "#e5e7eb";
    private const string BgOuter = "#f4f6f5";

    public static string Render(
        string subject, string bodyPlainText, string? actionUrl = null, string? actionLabel = null,
        string? logoUrl = null, string? companyName = null, string lang = "tr")
    {
        var en = lang == "en";
        var bodyHtml = System.Net.WebUtility.HtmlEncode(bodyPlainText)
            .Replace("\n", "<br/>");

        // Kiracinin kendi logosu varsa (tenant-service uzerinden yuklenmis)
        // basliktaki varsayilan HR360 isaretinin/metninin YERINE gosterilir -
        // e-posta da uygulamanin geri kalani gibi beyaz etiketlenmis olur.
        var brandName = string.IsNullOrWhiteSpace(companyName) ? "HR360" : companyName;
        var headerMark = string.IsNullOrWhiteSpace(logoUrl)
            ? $$"""
                <table role="presentation" cellpadding="0" cellspacing="0" align="center">
                  <tr>
                    <td style="vertical-align:middle;padding-right:12px;">
                      <table role="presentation" cellpadding="0" cellspacing="0" align="center">
                        <tr>
                          <td align="center" style="padding-bottom:4px;">
                            <div style="width:24px;height:8px;background-color:{{EmeraldDark}};border-radius:4px;"></div>
                          </td>
                        </tr>
                        <tr>
                          <td>
                            <table role="presentation" cellpadding="0" cellspacing="0">
                              <tr>
                                <td style="padding-right:4px;">
                                  <div style="width:16px;height:8px;background-color:{{EmeraldDark}};border-radius:4px;"></div>
                                </td>
                                <td>
                                  <div style="width:16px;height:8px;background-color:{{EmeraldDark}};border-radius:4px;"></div>
                                </td>
                              </tr>
                            </table>
                          </td>
                        </tr>
                      </table>
                    </td>
                    <td style="vertical-align:middle;">
                      <span style="color:{{TextDark}};font-size:20px;font-weight:600;letter-spacing:-0.02em;">HR360</span>
                    </td>
                  </tr>
                </table>
                """
            : $"""
                <img src="{System.Net.WebUtility.HtmlEncode(logoUrl)}"
                     alt="{System.Net.WebUtility.HtmlEncode(brandName)}"
                     style="max-height:36px;max-width:220px;display:inline-block;vertical-align:middle;"/>
                """;

        var actionButton = actionUrl is null ? "" : $"""
            <tr>
              <td align="center" style="padding: 8px 0 28px 0;">
                <a href="{actionUrl}" style="background-color:{Emerald};color:#ffffff;text-decoration:none;
                   font-family:Segoe UI,Arial,sans-serif;font-size:15px;font-weight:600;
                   padding:12px 28px;border-radius:8px;display:inline-block;">
                  {System.Net.WebUtility.HtmlEncode(actionLabel ?? (en ? "View" : "Görüntüle"))}
                </a>
              </td>
            </tr>
            """;

        return $"""
        <!DOCTYPE html>
        <html lang="{(en ? "en" : "tr")}">
        <head>
          <meta charset="utf-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
          <title>{System.Net.WebUtility.HtmlEncode(subject)}</title>
        </head>
        <body style="margin:0;padding:0;background-color:{BgOuter};font-family:Segoe UI,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:{BgOuter};padding:32px 16px;">
            <tr>
              <td align="center">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background-color:#ffffff;border-radius:12px;overflow:hidden;border:1px solid {Border};">

                  <tr>
                    <td align="center" style="background-color:{HeaderBg};padding:28px 32px;border-bottom:1px solid {Border};">
                      {headerMark}
                    </td>
                  </tr>

                  <tr>
                    <td style="padding:32px 32px 8px 32px;">
                      <h1 style="margin:0 0 16px 0;font-size:20px;font-weight:700;color:{TextDark};">
                        {System.Net.WebUtility.HtmlEncode(subject)}
                      </h1>
                      <p style="margin:0 0 24px 0;font-size:15px;line-height:1.6;color:{TextDark};">
                        {bodyHtml}
                      </p>
                    </td>
                  </tr>

                  {actionButton}

                  <tr>
                    <td style="padding:0 32px;">
                      <div style="border-top:1px solid {Border};"></div>
                    </td>
                  </tr>

                  <tr>
                    <td style="padding:20px 32px 28px 32px;">
                      <p style="margin:0;font-size:12px;line-height:1.6;color:{TextMuted};">
                        {(en
                            ? $"This email was sent automatically by {System.Net.WebUtility.HtmlEncode(brandName)}. If no action is required, you don't need to do anything."
                            : $"Bu e-posta {System.Net.WebUtility.HtmlEncode(brandName)} tarafından otomatik olarak gönderilmiştir. Bir işlem yapmanız gerekmiyorsa herhangi bir şey yapmanıza gerek yoktur.")}
                      </p>
                    </td>
                  </tr>

                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }
}
