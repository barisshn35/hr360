namespace CompensationService.Payroll;

/// <summary>
/// Güvenlik dalgası 2B — görevler ayrılığı (SoD): bordroyu hazırlayan (hesaplayan ya da elle ek
/// ödeme/kesinti giren) kişi aynı dönemi kapatıp onaylayamaz; bir çalışanın IBAN'ını değiştiren kişi
/// de o değişikliğin ilk uygulandığı bordroyu kapatamaz (sahte IBAN'la maaş yönlendirme riski).
///
/// Kural kiracı ayarıyla (governance_security_settings."PayrollSod", varsayılan AÇIK) uygulanır.
/// Tek İK kullanıcısı olan küçük şirketler kuralı şirket yöneticisi onayıyla (gerekçeli, denetim
/// kaydına yazılır) kapatabilir. Saf mantık burada; veritabanı okumaları denetleyicide.
/// </summary>
public static class SegregationOfDuties
{
    public const string SameUserCode = "sod_same_user";
    public const string IbanEditorCode = "sod_iban_editor";

    /// <summary>Kapatma denetiminin girdisi: kim hesapladı, elle kalem girenler, IBAN'ını bu kişinin değiştirdiği çalışan sayısı.</summary>
    public sealed record CloseCheck(
        string CloserUserId,
        string? CalculatedBy,
        IReadOnlyCollection<string> AdjustmentAuthors,
        int IbanEditsByCloser);

    public sealed record Violation(string Code, string Message);

    /// <summary>Kural ihlali yoksa null döner. <paramref name="enforced"/> kiracı ayarıdır.</summary>
    public static Violation? Evaluate(CloseCheck c, bool enforced)
    {
        if (!enforced || string.IsNullOrEmpty(c.CloserUserId)) return null;
        if (string.Equals(c.CalculatedBy, c.CloserUserId, StringComparison.Ordinal)
            || c.AdjustmentAuthors.Contains(c.CloserUserId, StringComparer.Ordinal))
            return new Violation(SameUserCode,
                "Görevler ayrılığı: bu bordro dönemini siz hazırladınız (hesaplama ya da ek ödeme/kesinti). " +
                "Dönemi başka bir bordro yetkilisi kapatmalı. Şirkette tek İK kullanıcısı varsa şirket yöneticisi " +
                "kuralı Veri koruma › Ayarlar ekranından gerekçeyle kapatabilir.");
        if (c.IbanEditsByCloser > 0)
            return new Violation(IbanEditorCode,
                $"Görevler ayrılığı: bu dönemde ilk kez uygulanacak {c.IbanEditsByCloser} çalışanın IBAN bilgisini siz değiştirdiniz. " +
                "Bu bordroyu başka bir bordro yetkilisi kapatmalı.");
        return null;
    }
}
