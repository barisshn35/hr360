namespace PerformanceService.Services;

/// <summary>
/// 79: kalibrasyon oturumu kuralları — saf hesap (birim testli).
///  * Hücre taşıma gerekçe ister (10-1000 karakter): karar notu toplantı tutanağıdır.
///  * Taşınan satırın onayı düşer; oturum yalnızca TÜM satırlar İK tarafından onaylanınca sonuçlanır
///    (otomatik karar yok — her nihai hücre bir insan onayından geçer).
///  * Sonuçlanınca yalnızca başlangıç hücresinden farklı olanlar 9-kutu düzeltmesi olarak yazılır.
/// </summary>
public static class CalibrationRules
{
    public const int NoteMin = 10;
    public const int NoteMax = 1000;

    public static string? ValidateMove(int performanceBand, int potentialBand, string? note)
    {
        if (performanceBand is < 1 or > 3 || potentialBand is < 1 or > 3) return "Bantlar 1-3 arasında olmalı";
        var n = note?.Trim() ?? "";
        if (n.Length is < NoteMin or > NoteMax) return "Karar notu 10-1000 karakter olmalı";
        return null;
    }

    public sealed record ItemState(Guid EmployeeId, int OriginalCell, int Cell, bool Confirmed);

    /// <summary>Sonuçlandırma engeli (yoksa null): boş oturum ya da onaysız satır.</summary>
    public static string? FinalizeBlocker(IReadOnlyCollection<ItemState> items)
    {
        if (items.Count == 0) return "Oturumda çalışan yok";
        var pending = items.Count(i => !i.Confirmed);
        return pending > 0 ? $"Sonuçlandırmak için tüm satırların İK onayı gerekir ({pending} onaysız)" : null;
    }

    /// <summary>Sonuçlanınca 9-kutu düzeltmesi yazılacak satırlar (başlangıçtan farklı hücre).</summary>
    public static List<ItemState> Changed(IEnumerable<ItemState> items) => items.Where(i => i.Cell != i.OriginalCell).ToList();
}
