using System.Text.Json;
using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>Eksik gün nedeni eşlemesi: izin türü → SGK kodu; ReducesPay = bu izin ücretten düşülür (eksik gün).</summary>
public sealed record MissingDayCode(string LeaveType, string Code, bool ReducesPay);

/// <summary>
/// Şirketin SGK ayarları (madde 58). Kodlar SGK e-Bildirge listelerinden alınmıştır; liste zaman içinde
/// değiştiği için şirket bunları düzenleyebilir (bkz. docs/bordro/README.md — doğrulama notları).
/// </summary>
public sealed record SgkSettings
{
    /// <summary>Belge türü (01 = tüm sigorta kolları; SGDP'li çalışan için 02).</summary>
    public string DefaultDocumentType { get; init; } = "01";
    /// <summary>Kanun no (05510 = teşviksiz; teşvik kanunu kullanılıyorsa ör. 05746, 06111 vb.).</summary>
    public string DefaultLawNo { get; init; } = "05510";
    /// <summary>İşyeri sicil numarası (isteğe bağlı; dosya başlığına yazılır).</summary>
    public string? WorkplaceRegistryNo { get; init; }
    public List<MissingDayCode> MissingDayCodes { get; init; } = DefaultMissingDayCodes();
    /// <summary>İşten çıkış nedeni kodları (ayrılış nedeni → SGK kodu).</summary>
    public Dictionary<string, string> ExitReasonCodes { get; init; } = DefaultExitReasonCodes();

    /// <summary>
    /// Varsayılan eşleme: ücretsiz izin "21" (diğer ücretsiz izin) ve ücretten düşer — bugünkü hesapla aynı;
    /// hastalık/doğum "01" (istirahat) ama varsayılan olarak ücretten düşmez (şirket ücreti ödüyorsa).
    /// </summary>
    public static List<MissingDayCode> DefaultMissingDayCodes() => new()
    {
        new("Unpaid", "21", true),
        new("Sick", "01", false),
        new("Maternity", "01", false),
    };

    public static Dictionary<string, string> DefaultExitReasonCodes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Resignation"] = "03", ["Termination"] = "04", ["ContractEnd"] = "05", ["Retirement"] = "08", ["Other"] = "22",
    };

    /// <summary>Birden fazla eksik gün nedeni olduğunda kullanılan kod.</summary>
    public const string MultipleReasonsCode = "12";

    /// <summary>Eşlemede ücretten düşen izin türleri (hesaplamada eksik gün sayılır).</summary>
    public IReadOnlyList<string> PayReducingLeaveTypes() =>
        MissingDayCodes.Where(m => m.ReducesPay).Select(m => m.LeaveType).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Banka dosyası özel CSV/TXT düzeni (madde 62).</summary>
public sealed record BankCustomLayout
{
    public string Delimiter { get; init; } = ";";
    /// <summary>"." ya da ",".</summary>
    public string DecimalSeparator { get; init; } = ".";
    public bool Header { get; init; } = true;
    public bool TotalsLine { get; init; } = true;
    /// <summary>Alan anahtarları: name, iban, amount, currency, description, date, tckn, employeeNo, seq.</summary>
    public List<string> Columns { get; init; } = new() { "name", "iban", "amount", "currency", "description" };
    /// <summary>utf-8 | utf-8-bom | iso-8859-9 | windows-1254</summary>
    public string Encoding { get; init; } = "utf-8-bom";
    /// <summary>Türkçe karakterleri ASCII'ye çevir (Ç→C, Ğ→G ...).</summary>
    public bool Ascii { get; init; }
    /// <summary>Tarih biçimi (ör. dd.MM.yyyy, yyyyMMdd).</summary>
    public string DateFormat { get; init; } = "dd.MM.yyyy";
}

public sealed record BankSettings
{
    /// <summary>generic | ornek-a | ornek-b | custom</summary>
    public string Template { get; init; } = "generic";
    /// <summary>Açıklama kalıbı: {yil}, {ay} yer tutucuları.</summary>
    public string Description { get; init; } = "{yil}-{ay} maas";
    /// <summary>Şirketin borçlanacak hesap IBAN'ı (örnek şablonların başlık satırı için; isteğe bağlı).</summary>
    public string? DebitIban { get; init; }
    /// <summary>Bankanın verdiği firma/müşteri kodu (isteğe bağlı).</summary>
    public string? CompanyCode { get; init; }
    public BankCustomLayout Custom { get; init; } = new();
}

/// <summary>Bordro ayarlarının JSON'dan okunması (bozuk/boş JSON = varsayılanlar).</summary>
public sealed record PayrollSettingsModel(SgkSettings Sgk, AccountMap Accounts, Dictionary<string, string> CostCenters, BankSettings Bank)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PayrollSettingsModel Default => new(new SgkSettings(), new AccountMap(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new BankSettings());

    private static T Read<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return fallback;
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    public static PayrollSettingsModel From(PayrollSettings? row)
    {
        if (row is null) return Default;
        var sgk = Read(row.SgkJson, new SgkSettings());
        if (sgk.MissingDayCodes is null || sgk.MissingDayCodes.Count == 0) sgk = sgk with { MissingDayCodes = SgkSettings.DefaultMissingDayCodes() };
        if (sgk.ExitReasonCodes is null || sgk.ExitReasonCodes.Count == 0) sgk = sgk with { ExitReasonCodes = SgkSettings.DefaultExitReasonCodes() };
        var cc = Read(row.CostCentersJson, new Dictionary<string, string>());
        return new(sgk, Read(row.AccountMapJson, new AccountMap()),
            new Dictionary<string, string>(cc, StringComparer.OrdinalIgnoreCase), Read(row.BankTemplateJson, new BankSettings()));
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Ayar denetimi; hata yoksa null.</summary>
    public static string? Validate(SgkSettings? sgk, AccountMap? accounts, Dictionary<string, string>? costCenters, BankSettings? bank)
    {
        static bool Code(string? s, int min, int max) => s is not null && s.Length >= min && s.Length <= max && s.All(char.IsAsciiDigit);
        if (sgk is not null)
        {
            if (!Code(sgk.DefaultDocumentType, 1, 2) || !Code(sgk.DefaultLawNo, 4, 5)) return "Belge türü 1–2, kanun no 4–5 haneli rakam olmalı";
            if (sgk.WorkplaceRegistryNo is { Length: > 40 }) return "İşyeri sicil numarası çok uzun";
            if (sgk.MissingDayCodes is { Count: > 20 } || (sgk.MissingDayCodes ?? new()).Any(m => string.IsNullOrWhiteSpace(m.LeaveType) || !Code(m.Code, 2, 2)))
                return "Eksik gün kodları iki haneli olmalı";
            if ((sgk.ExitReasonCodes ?? new()).Any(kv => !Code(kv.Value, 2, 2))) return "İşten çıkış kodları iki haneli olmalı";
        }
        if (accounts is not null)
        {
            var codes = new[] { accounts.Salary, accounts.EmployerSgk, accounts.NetPayable, accounts.IncomeTax, accounts.StampTax, accounts.Sgk, accounts.Advances };
            if (codes.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 30)) return "Hesap kodları boş olamaz (en fazla 30 karakter)";
        }
        if (costCenters is { Count: > 500 } || (costCenters ?? new()).Any(kv => kv.Key.Length > 120 || kv.Value.Length > 30))
            return "Masraf merkezi eşlemesi geçersiz";
        if (bank is not null)
        {
            if (bank.Template is not ("generic" or "ornek-a" or "ornek-b" or "custom")) return "Geçersiz banka şablonu";
            if (bank.Description is null || bank.Description.Length > 60) return "Açıklama kalıbı en fazla 60 karakter olabilir";
            if (bank.DebitIban is { Length: > 0 } && !Exporters.ValidIban(bank.DebitIban)) return "Borçlu hesap IBAN'ı geçersiz";
            var c = bank.Custom ?? new BankCustomLayout();
            if (c.Delimiter is not (";" or "," or "|" or "\t") || c.DecimalSeparator is not ("." or ",") || c.Delimiter == c.DecimalSeparator)
                return "Ayırıcılar geçersiz (alan: ; , | sekme; ondalık: . ,) ve birbirinden farklı olmalı";
            if (c.Columns is null || c.Columns.Count is < 2 or > 12 || c.Columns.Any(k => !BankFiles.Fields.Contains(k)) || !c.Columns.Contains("iban") || !c.Columns.Contains("amount"))
                return "Sütunlar geçersiz (iban ve amount zorunlu)";
            if (c.Encoding is not ("utf-8" or "utf-8-bom" or "iso-8859-9" or "windows-1254")) return "Geçersiz karakter kodlaması";
            try { _ = DateTime.Now.ToString(c.DateFormat ?? "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture); }
            catch (FormatException) { return "Geçersiz tarih biçimi"; }
            if ((c.DateFormat ?? "").Length > 20) return "Geçersiz tarih biçimi";
        }
        return null;
    }
}
