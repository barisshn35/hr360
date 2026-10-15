import { tx } from '@/lib/i18n'
import type { BandPosition, ExitReason, Integrity, RetroCandidate } from '@/api/payrollTr'

/**
 * Bordro dalgası 8 yardımcıları (saf; Vitest: payrollTr.test.ts). Sunucu kuralının aynısı arayüzde
 * erken uyarı için kullanılır; asıl denetim sunucudadır.
 */

/** IBAN'ın mod-97 kalanı (ISO 13616: ilk dört karakter sona, harfler 10–35). */
export function ibanMod97(iban: string): number {
  const s = iban.slice(4) + iban.slice(0, 4)
  let mod = 0
  for (const c of s) {
    if (c >= '0' && c <= '9') mod = (mod * 10 + (c.charCodeAt(0) - 48)) % 97
    else mod = (mod * 100 + (c.charCodeAt(0) - 55)) % 97
  }
  return mod
}

/** Maaş ödemesi için TR IBAN'ı: 26 karakter, mod-97 = 1 (boşluklar yok sayılır). */
export function validTrIban(raw: string | null | undefined): boolean {
  const iban = (raw ?? '').replace(/\s+/g, '').toUpperCase()
  return /^TR\d{24}$/.test(iban) && ibanMod97(iban) === 1
}

/** SGK meslek kodu: 0000.00 (ör. 2512.01). */
export const validOccupationCode = (s: string) => /^\d{4}\.\d{2}$/.test(s.trim())

export const positionLabel: Record<BandPosition, string> = {
  below: tx('Bandın altında'), within: tx('Bant içinde'), above: tx('Bandın üstünde'), none: tx('Bant yok'),
}
export const positionTone: Record<BandPosition, 'warning' | 'success' | 'info' | 'neutral'> = {
  below: 'warning', within: 'success', above: 'info', none: 'neutral',
}

export const exitReasonLabel: Record<ExitReason, string> = {
  Resignation: tx('İstifa'), Termination: tx('İşveren feshi'), Retirement: tx('Emeklilik'),
  ContractEnd: tx('Belirli süreli sözleşmenin sona ermesi'), Other: tx('Diğer'),
}

export const integrityLabel: Record<Integrity, string> = {
  ok: tx('Değişmedi'), changed: tx('Yayımdan sonra değişti'), missing: tx('Pusula yok'), not_published: tx('Yayımlanmadı'),
}

/** Özetin okunur kısaltması: ilk 16 karakter, dörtlü gruplar. */
export const shortHash = (h: string | null | undefined) => (h ? h.slice(0, 16).replace(/(.{4})(?=.)/g, '$1 ') : '—')

/** "Bölüm = KOD" satırlarını eşlemeye çevirir (boş/eşittirsiz satırlar atlanır). */
export function parseCostCenters(text: string): Record<string, string> {
  const out: Record<string, string> = {}
  for (const line of text.split(/\r?\n/)) {
    const i = line.indexOf('=')
    if (i <= 0) continue
    const k = line.slice(0, i).trim()
    const v = line.slice(i + 1).trim()
    if (k && v) out[k] = v
  }
  return out
}

export const formatCostCenters = (m: Record<string, string>) =>
  Object.entries(m).sort(([a], [b]) => a.localeCompare(b, 'tr')).map(([k, v]) => `${k} = ${v}`).join('\n')

/** Seçilen ve uygulanabilir fark satırlarının toplamı. */
export function retroTotal(rows: RetroCandidate[], selected: Set<string>) {
  return rows.filter((r) => r.applicable && selected.has(retroKey(r))).reduce((a, r) => a + r.diffGross, 0)
}

export const retroKey = (r: Pick<RetroCandidate, 'employeeId' | 'sourcePeriodId'>) => `${r.employeeId}:${r.sourcePeriodId}`

/** Oran girişini (%14 ya da 0,14) kesire çevirir; geçersizse null. */
export function parseRate(s: string): number | null {
  const t = s.trim().replace('%', '').replace(',', '.')
  if (!t) return null
  const n = Number(t)
  if (!Number.isFinite(n) || n < 0) return null
  return s.includes('%') || n > 1 ? n / 100 : n
}
