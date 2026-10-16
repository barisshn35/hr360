using EngagementService.Infrastructure.Eom;
using EngagementService.Tenancy;

namespace EngagementService.Models;

/// <summary>
/// "Ayın çalışanı" kiracı ayarı (tek satır; yoksa varsayılanlar ve kapalı). EF ile yazılır: değişiklik
/// denetim kaydına girer. Dönemler, oylar ve birimler oy gizliliği için SQL ile yönetilir (bkz. EomStore).
/// </summary>
public class EomSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public bool Enabled { get; set; }
    public string Cadence { get; set; } = Eom.Monthly;
    public string Scope { get; set; } = Eom.TopLevel;
    public string Process { get; set; } = Eom.Direct;
    public string Visibility { get; set; } = Eom.WinnerOnly;
    public string RevealTo { get; set; } = Eom.RevealHrAndHead;
    public int StartDay { get; set; } = 1;
    public int OpenHour { get; set; } = 9;
    public int NominationDays { get; set; } = 2;
    public int FinalistCount { get; set; } = 5;
    public int VotingDays { get; set; } = 3;
    public int Stage2Days { get; set; } = 1;
    public int ReminderDaysBefore { get; set; } = 2;
    public int CooldownPeriods { get; set; } = 3;
    public decimal WeightEmployee { get; set; } = 1;
    public decimal WeightManager { get; set; } = 2;
    public decimal WeightHr { get; set; } = 2;
    public bool AllowComment { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByName { get; set; }

    public EomConfig ToConfig() => new(Cadence, Scope, Process, Visibility, RevealTo, StartDay, OpenHour, NominationDays,
        FinalistCount, VotingDays, Stage2Days, ReminderDaysBefore, CooldownPeriods, WeightEmployee, WeightManager, WeightHr, AllowComment);

    public void Apply(EomConfig c)
    {
        (Cadence, Scope, Process, Visibility, RevealTo) = (c.Cadence, c.Scope, c.Process, c.Visibility, c.RevealTo);
        (StartDay, OpenHour, NominationDays, FinalistCount, VotingDays, Stage2Days) = (c.StartDay, c.OpenHour, c.NominationDays, c.FinalistCount, c.VotingDays, c.Stage2Days);
        (ReminderDaysBefore, CooldownPeriods, AllowComment) = (c.ReminderDaysBefore, c.CooldownPeriods, c.AllowComment);
        (WeightEmployee, WeightManager, WeightHr) = (c.WeightEmployee, c.WeightManager, c.WeightHr);
    }
}
