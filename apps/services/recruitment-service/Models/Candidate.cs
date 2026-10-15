using RecruitmentService.Tenancy;

namespace RecruitmentService.Models;

public class Candidate : ITenantOwned
{
    public string TenantSlug { get; set; } = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public string? Phone { get; set; }
    /// <summary>Ozgecmis dosyasinin MinIO storage anahtari.</summary>
    public string? ResumeStorageKey { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Application> Applications { get; set; } = new();

    // G13: tekrar aday tespiti icin karsilastirma anahtarlari (bkz. Services/DuplicateDetector).
    [System.Text.Json.Serialization.JsonIgnore] public string? NormalizedEmail { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? NormalizedPhone { get; set; }
    public List<string> Skills { get; set; } = new();
    /// <summary>Ozgecmis metni (dosya saklanmaz; aday yapistirir ya da IK CV'den ayristirir).</summary>
    public string? ResumeText { get; set; }
    /// <summary>KVKK m.5/1: aday havuzunda saklama icin ACIK RIZA (aydinlatmadan ayri, istege bagli).</summary>
    public bool TalentPoolConsent { get; set; }
    public DateTimeOffset? TalentPoolConsentAt { get; set; }
    /// <summary>Saklama suresi dolunca kimlik/iletisim alanlari geri dondurulemez bicimde silinir.</summary>
    public DateTimeOffset? AnonymizedAt { get; set; }

    public void Normalize()
    {
        NormalizedEmail = Services.DuplicateDetector.NormalizeEmail(Email);
        NormalizedPhone = Services.DuplicateDetector.NormalizePhone(Phone);
    }
}
