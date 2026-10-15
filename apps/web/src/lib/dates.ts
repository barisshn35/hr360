/**
 * Yerel takvim günü, "YYYY-MM-DD".
 *
 * NOT: `new Date().toISOString().slice(0, 10)` UTC tarihini verir; Türkiye'de
 * (UTC+3) 00:00–03:00 arası "bugün" yerine DÜNÜ döndürüyordu. Form varsayılanları
 * (zimmet, masraf, ekip tarihleri) ve panodaki günlük grafik bu yüzden bir gün
 * kayıyordu. Kullanıcının takvim gününe dayanan her yerde bu kullanılmalı.
 */
export function localISODate(d: Date = new Date()): string {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}
