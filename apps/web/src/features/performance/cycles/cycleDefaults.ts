import type { CyclePeriod, ReviewCycle } from '@/api/performance'
import { tx } from '@/lib/i18n'

const RANGES: Record<CyclePeriod, [string, string]> = {
  Q1: ['01-01', '03-31'],
  Q2: ['04-01', '06-30'],
  Q3: ['07-01', '09-30'],
  Q4: ['10-01', '12-31'],
  H1: ['01-01', '06-30'],
  H2: ['07-01', '12-31'],
  Annual: ['01-01', '12-31'],
}

const TITLES: Record<CyclePeriod, string> = {
  Q1: tx('1. Çeyrek'),
  Q2: tx('2. Çeyrek'),
  Q3: tx('3. Çeyrek'),
  Q4: tx('4. Çeyrek'),
  H1: tx('1. Yarıyıl'),
  H2: tx('2. Yarıyıl'),
  Annual: tx('Yıllık'),
}

/** Dönem türü ve yıldan önerilen ad ve tarihler — kullanıcı değiştirebilir. */
export function defaultsFor(year: number, period: CyclePeriod) {
  const [s, e] = RANGES[period]
  return { name: `${year} ${TITLES[period]}`, startDate: `${year}-${s}`, endDate: `${year}-${e}` }
}

/** Sıradaki mantıklı dönem: en son dönemin ardından gelen çeyrek. */
export function nextSuggestion(cycles: ReviewCycle[]): { year: number; period: CyclePeriod } {
  const quarters = cycles.filter((c) => c.period.startsWith('Q')).sort((a, b) => b.startDate.localeCompare(a.startDate))
  const last = quarters[0]
  if (!last) return { year: new Date().getFullYear(), period: 'Q1' }
  const q = Number(last.period.slice(1))
  return q === 4 ? { year: last.year + 1, period: 'Q1' } : { year: last.year, period: `Q${q + 1}` as CyclePeriod }
}

/** Tarih aralığı çakışan dönemler — uyarı için (engellemez). */
export function overlapping(cycles: ReviewCycle[], start: string, end: string, exceptId?: string) {
  return cycles.filter((c) => c.id !== exceptId && c.startDate <= end && c.endDate >= start)
}

const DAY = 86_400_000

export function timeProgress(c: ReviewCycle, now = new Date()) {
  const s = Date.parse(c.startDate)
  const e = Date.parse(c.endDate) + DAY
  const t = now.getTime()
  const pct = Math.max(0, Math.min(1, (t - s) / (e - s)))
  const daysLeft = Math.max(0, Math.ceil((e - t) / DAY))
  const daysToStart = Math.max(0, Math.ceil((s - t) / DAY))
  return { pct, daysLeft, daysToStart, started: t >= s, ended: t >= e }
}
