namespace EngagementService.Infrastructure.Badges;

/// <summary>
/// Rozet koleksiyonu ve kilometre taşları (yenilik haritası #29) — saf kurallar. Rozetler mevcut
/// verilerden hesaplanır (kıdem, takdir, ayın çalışanı, fikir, gönüllülük, etkinlik, kahve, kart,
/// sertifika); adları ve açıklamaları arayüz dilinde istemcide tutulur, sunucu yalnızca kimlik ve ilerleme döner.
/// </summary>
public static class BadgeCatalog
{
    public sealed record Badge(string Id, string Metric, decimal Target, string Group);

    /// <summary>Kişinin rozet ölçütleri (ad → değer). Eksik ölçüt 0 sayılır.</summary>
    public sealed class Metrics : Dictionary<string, decimal> { }

    public const string Tenure = "tenureYears", KudosReceived = "kudosReceived", KudosGiven = "kudosGiven", EomWins = "eomWins",
        Ideas = "ideas", IdeasDone = "ideasDone", VolunteerHours = "volunteerHours", EventsAttended = "eventsAttended",
        CoffeeMet = "coffeeMet", CardsSigned = "cardsSigned", Certifications = "certifications";

    public static readonly Badge[] All =
    [
        new("tenure-1", Tenure, 1, "milestone"), new("tenure-3", Tenure, 3, "milestone"), new("tenure-5", Tenure, 5, "milestone"), new("tenure-10", Tenure, 10, "milestone"),
        new("kudos-received-1", KudosReceived, 1, "recognition"), new("kudos-received-10", KudosReceived, 10, "recognition"), new("kudos-received-50", KudosReceived, 50, "recognition"),
        new("kudos-given-10", KudosGiven, 10, "recognition"), new("kudos-given-50", KudosGiven, 50, "recognition"),
        new("eom-1", EomWins, 1, "recognition"), new("eom-3", EomWins, 3, "recognition"),
        new("idea-1", Ideas, 1, "community"), new("idea-done-1", IdeasDone, 1, "community"),
        new("volunteer-10", VolunteerHours, 10, "community"), new("volunteer-50", VolunteerHours, 50, "community"),
        new("events-5", EventsAttended, 5, "community"), new("coffee-3", CoffeeMet, 3, "community"), new("cards-10", CardsSigned, 10, "community"),
        new("cert-1", Certifications, 1, "growth"), new("cert-5", Certifications, 5, "growth"),
    ];

    public sealed record Result(string Id, string Group, string Metric, decimal Value, decimal Target, bool Earned);

    public static List<Result> Evaluate(Metrics m) =>
        All.Select(b => { var v = m.GetValueOrDefault(b.Metric); return new Result(b.Id, b.Group, b.Metric, Math.Min(v, b.Target), b.Target, v >= b.Target); }).ToList();

    /// <summary>Tam yıl kıdem (Türkiye tarihiyle; yıl dönümü günü dahil).</summary>
    public static int TenureYears(DateOnly hire, DateOnly today)
    {
        var y = today.Year - hire.Year;
        if (today < hire.AddYears(y)) y--;
        return Math.Max(0, y);
    }
}
