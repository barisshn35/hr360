namespace NotificationService.Email;

/// <summary>
/// Platformun varsayilan SMTP yapilandirmasi (install.sh kurulumda sorar; bos
/// birakilirsa paketteki Mailpit). Kiracinin kendi SMTP'si varsa o kullanilir. Ortam degiskenlerinden okunur, hicbiri
/// koda gomulmez (bkz. Program.cs).
/// </summary>
public class EmailOptions
{
    public required string SmtpHost { get; init; }
    public required int SmtpPort { get; init; }
    public required string SmtpUser { get; init; }
    public required string SmtpPassword { get; init; }
    public required string FromAddress { get; init; }
    public string FromName { get; init; } = "HR360";

    public static EmailOptions FromEnvironment()
    {
        string Req(string key) => Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException($"{key} tanimli olmali");

        return new EmailOptions
        {
            SmtpHost = Req("SMTP_HOST"),
            SmtpPort = int.Parse(Environment.GetEnvironmentVariable("SMTP_PORT") ?? "587"),
            SmtpUser = Req("SMTP_USER"),
            SmtpPassword = Req("SMTP_PASSWORD"),
            FromAddress = Environment.GetEnvironmentVariable("SMTP_FROM_ADDRESS") ?? "noreply@hr360.local",
            FromName = Environment.GetEnvironmentVariable("SMTP_FROM_NAME") ?? "HR360",
        };
    }
}
