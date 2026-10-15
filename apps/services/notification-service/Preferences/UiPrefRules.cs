using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotificationService.Preferences;

/// <summary>
/// Dalga 12: kullanıcı başına arayüz tercihleri (kayıtlı liste görünümleri, ana panel düzeni,
/// "Yenilikler" okundu bilgisi). Saf kurallar — birim testli.
///
/// KVKK: değer kişinin kendi arayüz ayarıdır; boyut ve anahtar sayısı sınırlıdır ki tercihler
/// serbest veri deposuna dönüşmesin.
/// </summary>
public static partial class UiPrefRules
{
    /// <summary>Bir değerin en büyük boyutu (UTF-8 bayt).</summary>
    public const int MaxValueBytes = 16 * 1024;

    /// <summary>Kişi başına en fazla anahtar.</summary>
    public const int MaxKeysPerUser = 60;

    [GeneratedRegex("^[a-z0-9][a-z0-9._:-]{0,99}$")]
    private static partial Regex KeyPattern();

    /// <summary>Anahtar: küçük harf, rakam ve . _ : - (ör. "views:leave-requests", "dashboard").</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>
    /// Değeri doğrular ve sıkıştırılmış JSON metnine çevirir. Geçersizse hata iletisi döner.
    /// Yalnızca nesne ya da dizi kabul edilir (tek başına sayı/metin tercih değildir).
    /// </summary>
    public static (string? Json, string? Error) Normalize(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return (null, "Tercih değeri bir JSON nesnesi ya da dizisi olmalı.");
        var json = JsonSerializer.Serialize(value);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxValueBytes)
            return (null, "Tercih değeri çok büyük (en fazla 16 KB).");
        return (json, null);
    }
}
