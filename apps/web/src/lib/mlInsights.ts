import { formatNumber } from './format'
import { tx } from './i18n'
import type { AnomalyFlag, FlagSeverity } from './expenseAudit'

/**
 * ML dalgası 2 görünüm yardımcıları: bordro/puantaj denetim işaretlerinin yerelleştirilmiş metni,
 * izin kapasitesi tonu, ücret adaleti farkı biçimi, beceri önerisi süzgeci. İşaretler yalnızca
 * insan incelemesi içindir; hiçbir kayıt otomatik değişmez.
 */

const n = (v: unknown, digits = 1) => (typeof v === 'number' && Number.isFinite(v) ? formatNumber(Math.round(v * 10 ** digits) / 10 ** digits) : '—')

const METRIC: Record<string, () => string> = {
  OVERTIME: () => tx('Fazla mesai saati'),
  ADDITIONS: () => tx('Ek ödeme (prim/ikramiye)'),
  DEDUCTIONS: () => tx('Kesinti'),
  GROSS: () => tx('Brüt ücret'),
}

/** Bordro ve puantaj işaret kodu -> yerelleştirilmiş gerekçe. Tanınmayan kodda sunucu gerekçesi. */
export function payrollFlagText(f: AnomalyFlag): string {
  const d = f.details ?? {}
  const own = /^(OVERTIME|ADDITIONS|DEDUCTIONS|GROSS)_SPIKE_OWN$/.exec(f.code)
  if (own) return tx('{0}, çalışanın önceki dönemlerine göre olağan dışı yüksek ({1}; önceki dönemlerin medyanı {2}).', [METRIC[own[1]](), n(d.value), n(d.median)])
  const peer = /^(OVERTIME|ADDITIONS|DEDUCTIONS|GROSS)_OUTLIER_PEER$/.exec(f.code)
  if (peer) return tx('{0}, aynı kademedeki {1} çalışana göre olağan dışı yüksek (kademe medyanı {2}).', [METRIC[peer[1]](), n(d.n, 0), n(d.median)])
  switch (f.code) {
    case 'OVERTIME_ANNUAL_LIMIT':
      return tx('Yıl içindeki toplam fazla mesai {0} saat; yasal üst sınır 270 saat (İş K. m.41).', [n(d.year_to_date)])
    case 'DEDUCTION_RATIO_HIGH':
      return tx('Kesintiler brüt ücretin %{0} kadarı; tutarı ve dayanağını kontrol edin.', [n(typeof d.ratio === 'number' ? d.ratio * 100 : d.ratio, 0)])
    case 'UNUSUAL_PATTERN':
      return tx('Pusula, şirketin geçmiş bordro örüntüsüne göre olağan dışı (mesai/ek ödeme/kesinti/brüt).')
    case 'WEEKLY_HOURS_SPIKE':
      return tx('Haftalık çalışma {0} saat; çalışanın olağan haftalarının medyanı {1} saat.', [n(d.hours), n(d.median)])
    case 'WEEKLY_HOURS_OVER_LIMIT':
      return tx('Haftalık çalışma {0} saat; 45 saati aşan kısım fazla mesai onayı gerektirir (İş K. m.41, m.63).', [n(d.hours)])
    case 'MISSING_PUNCHES':
      return tx('Bu hafta {0} gün giriş ya da çıkış kaydı eksik.', [n(d.count, 0)])
    case 'MISSING_PUNCH_PATTERN':
      return tx('Son 4 haftanın {0} tanesinde eksik giriş/çıkış kaydı var (tekrarlayan örüntü).', [n(d.weeks, 0)])
    default:
      return f.reason
  }
}

const ORDER: FlagSeverity[] = ['low', 'medium', 'high']

/** Çalışan başına en yüksek önem (tablo rozeti için). */
export function worstSeverity(flags: AnomalyFlag[]): FlagSeverity | null {
  let w = -1
  for (const f of flags) w = Math.max(w, ORDER.indexOf(f.severity))
  return w < 0 ? null : ORDER[w]
}

/** İşaretleri önem sırasına göre (yüksek önce) çalışan bazında sıralar. */
export function sortBySeverity<T extends { flags: AnomalyFlag[] }>(rows: T[]): T[] {
  return [...rows].sort((a, b) => ORDER.indexOf(worstSeverity(b.flags) ?? 'low') - ORDER.indexOf(worstSeverity(a.flags) ?? 'low') || b.flags.length - a.flags.length)
}

/** Ekip kapasitesi: beklenen izinli oranına göre ton (>= %20 kritik, >= %10 dikkat). */
export function capacityTone(pct: number): 'danger' | 'warning' | 'neutral' {
  return pct >= 20 ? 'danger' : pct >= 10 ? 'warning' : 'neutral'
}

/** Geri test hatası (MAPE) için sade yorum. */
export function mapeLabel(mape: number | null | undefined): string {
  if (mape == null) return tx('ölçülemedi')
  return mape <= 15 ? tx('iyi') : mape <= 30 ? tx('orta') : tx('zayıf')
}

/** Ücret farkı: "+4,2%" / "−3,1%" (eksi işareti tipografik). */
export function formatGap(pct: number): string {
  const s = formatNumber(Math.abs(pct))
  return pct > 0 ? `+%${s}` : pct < 0 ? `−%${s}` : `%${s}`
}

/** Beceri önerisinden kullanıcının zaten seçtiği / sahip olduğu becerileri çıkarır (büyük-küçük harf duyarsız, Türkçe). */
export function newSuggestions(suggested: string[], existing: string[]): string[] {
  const key = (s: string) => s.trim().toLocaleLowerCase('tr-TR')
  const have = new Set(existing.map(key))
  const out: string[] = []
  for (const s of suggested) {
    const k = key(s)
    if (!k || have.has(k)) continue
    have.add(k)
    out.push(s.trim())
  }
  return out
}

/** Anlamsal arama kaynağının etiketi. */
export function semanticSourceLabel(source: string): string {
  return source === 'kb' ? tx('Bilgi bankası') : source === 'library' ? tx('Doküman kütüphanesi') : source === 'announcement' ? tx('Duyuru') : source
}
