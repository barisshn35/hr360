using EngagementService.Tenancy;

namespace EngagementService.Models;

/// <summary>Fikir kutusu kaydı. Anonimse yazar kimliği/adı boştur; yazar yalnızca <see cref="AuthorKey"/> ile tanınır.</summary>
public class Idea : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string AuthorKey { get; set; } = "";
    public Guid? AuthorEmployeeId { get; set; }
    public string? AuthorName { get; set; }
    public bool Anonymous { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Category { get; set; } = "Diğer";
    /// <summary>New | UnderReview | Planned | Done | Declined</summary>
    public string Status { get; set; } = "New";
    public string? Response { get; set; }
    public string? RespondedByName { get; set; }
    public DateTime? RespondedAt { get; set; }
    /// <summary>Destekleyenlerin anahtarları (kişi başına bir destek).</summary>
    public List<string> Voters { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class IdeaComment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid IdeaId { get; set; }
    public string AuthorUserId { get; set; } = "";
    public Guid? AuthorEmployeeId { get; set; }
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Haftalık check-in: çalışanın kısa haftalık notu (yöneticisi görür).</summary>
public class WeeklyCheckin : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public DateOnly WeekStart { get; set; }
    public string? Progress { get; set; }
    public string? Blockers { get; set; }
    public string? NextWeek { get; set; }
    public short Mood { get; set; }
    public bool AddedToOneOnOne { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Şirket etkinliği (sosyal, eğitim, gönüllülük…). Kontenjan doluysa katılım bekleme listesine düşer.</summary>
public class CompanyEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Social | Training | Volunteer | Sports | Other</summary>
    public string Kind { get; set; } = "Social";
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Location { get; set; }
    public string? OnlineUrl { get; set; }
    public int? Capacity { get; set; }
    public DateTime? RsvpDeadline { get; set; }
    public decimal? VolunteerHours { get; set; }
    /// <summary>Published | Cancelled</summary>
    public string Status { get; set; } = "Published";
    public string CreatedByUserId { get; set; } = "";
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class EventRsvp : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EventId { get; set; }
    public Guid EmployeeId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Going | Waitlist</summary>
    public string Status { get; set; } = "Going";
    public bool Attended { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
