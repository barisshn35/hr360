import { tx, appLocale } from '@/lib/i18n'
const dateFmt = new Intl.DateTimeFormat(appLocale, {
  day: '2-digit',
  month: 'short',
  year: 'numeric',
})

const dateTimeFmt = new Intl.DateTimeFormat(appLocale, {
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
})

const numberFmt = new Intl.NumberFormat(appLocale)

function toDate(value: string | null | undefined): Date | null {
  if (!value) return null
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? null : d
}

export function formatDate(value: string | null | undefined, fallback = '—'): string {
  const d = toDate(value)
  return d ? dateFmt.format(d) : fallback
}

export function formatDateTime(value: string | null | undefined, fallback = '—'): string {
  const d = toDate(value)
  return d ? dateTimeFmt.format(d) : fallback
}

export function formatNumber(value: number | null | undefined, fallback = '—'): string {
  return typeof value === 'number' && Number.isFinite(value) ? numberFmt.format(value) : fallback
}

export function formatPercent(value: number, digits = 0): string {
  return new Intl.NumberFormat(appLocale, {
    style: 'percent',
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  }).format(value)
}

/** "3 gün gecikti" / "5 saat kaldı" gibi göreli ifade. */
export function formatRelativeToNow(value: string | null | undefined): string {
  const d = toDate(value)
  if (!d) return '—'
  const diffMs = d.getTime() - Date.now()
  const rtf = new Intl.RelativeTimeFormat(appLocale, { numeric: 'auto' })
  const abs = Math.abs(diffMs)
  const hour = 3_600_000
  const day = 24 * hour

  if (abs < hour) return rtf.format(Math.round(diffMs / 60_000), 'minute')
  if (abs < day) return rtf.format(Math.round(diffMs / hour), 'hour')
  return rtf.format(Math.round(diffMs / day), 'day')
}

export function initialsOf(firstName?: string, lastName?: string): string {
  const a = firstName?.trim()?.[0] ?? ''
  const b = lastName?.trim()?.[0] ?? ''
  return (a + b).toLocaleUpperCase(appLocale) || '?'
}

export function fullName(p: { firstName?: string; lastName?: string }): string {
  return [p.firstName, p.lastName].filter(Boolean).join(' ').trim() || tx('İsimsiz kayıt')
}

/** Türkçe karakter duyarlı arama normalizasyonu. */
export function normalizeSearch(value: string): string {
  return value
    // Dil seçiminden bağımsız Türkçe küçültme: "en" ile "İ" → "i̇" (birleşik nokta) kalıyordu.
    .toLocaleLowerCase('tr')
    .replace(/ı/g, 'i')
    .replace(/ş/g, 's')
    .replace(/ğ/g, 'g')
    .replace(/ü/g, 'u')
    .replace(/ö/g, 'o')
    .replace(/ç/g, 'c')
    .trim()
}

/** Para birimi biçimi. Kuruş yalnızca tam sayı olmayan tutarlarda gösterilir. */
export function formatMoney(
  value: number | null | undefined,
  currency = 'TRY',
  fallback = '—',
): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback
  return new Intl.NumberFormat(appLocale, {
    style: 'currency',
    currency,
    minimumFractionDigits: Number.isInteger(value) ? 0 : 2,
    maximumFractionDigits: 2,
  }).format(value)
}

/**
 * Kullanıcının yazdığı ondalık sayıyı ayrıştırır: "58.080,27", "58080,27",
 * "58080.27", "58,080.27", "1.000.000" hepsi doğru okunur. Kural: hem '.' hem
 * ',' varsa sonuncusu ondalıktır; yalnızca ',' varsa (tek) ondalıktır; yalnızca
 * '.' varsa ve son noktadan sonra tam 3 hane yoksa ondalıktır. Sayı değilse null.
 */
export function parseDecimal(input: string): number | null {
  let s = input.trim().replace(/[\s ₺]|TL/gi, '')
  if (!s) return null
  const neg = s.startsWith('-')
  if (neg || s.startsWith('+')) s = s.slice(1)
  if (!/^[\d.,]+$/.test(s) || !/\d/.test(s)) return null
  const lastDot = s.lastIndexOf('.')
  const lastComma = s.lastIndexOf(',')
  let dec: '.' | ',' | null = null
  if (lastDot >= 0 && lastComma >= 0) dec = lastDot > lastComma ? '.' : ','
  else if (lastComma >= 0) dec = s.indexOf(',') === lastComma ? ',' : null
  else if (lastDot >= 0) dec = s.indexOf('.') === lastDot && s.length - lastDot - 1 !== 3 ? '.' : null
  let intPart = s
  let frac = ''
  if (dec) {
    const at = s.lastIndexOf(dec)
    intPart = s.slice(0, at)
    frac = s.slice(at + 1)
    if (/[.,]/.test(frac)) return null
  }
  intPart = intPart.replace(/[.,]/g, '')
  if (!intPart && !frac) return null
  const n = Number(`${intPart || '0'}.${frac || '0'}`)
  if (!Number.isFinite(n)) return null
  return neg ? -n : n
}
