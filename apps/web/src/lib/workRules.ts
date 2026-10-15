import { tx, appLocale } from '@/lib/i18n'

/**
 * Madde 66: vardiya deseni düzenleyicisinde canlı kural uyarıları (sunucudaki WorkRules ile aynı ölçütler).
 * Desen günlerinde mola bilgisi olmadığından süreler BRÜT hesaplanır (sunucu atamada molayı düşer).
 * Desen döngüseldir: son günden sonra ilk gün gelir. Haftalık süre, herhangi 7 günlük dilimin toplamıdır.
 */
export interface RuleLimits {
  minRestHours: number
  weeklyMaxHours: number
  dailyMaxHours: number
  nightMaxHours: number
  maxConsecutiveDays: number
}

export const DEFAULT_LIMITS: RuleLimits = { minRestHours: 11, weeklyMaxHours: 45, dailyMaxHours: 11, nightMaxHours: 7.5, maxConsecutiveDays: 6 }

export interface PatternDayLike { type: string; startTime: string | null; endTime: string | null }
export interface PatternWarning { code: 'rest' | 'weekly' | 'daily' | 'night' | 'consecutive'; day: number | null; message: string }

const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))
const h = (n: number) => n.toLocaleString(appLocale, { maximumFractionDigits: 1 })

/** Vardiya aralığı (dakika, gün başından): gece yarısını geçen ertesi güne taşar. */
export function span(startTime: string, endTime: string): { start: number; end: number } {
  const s = toMin(startTime)
  let e = toMin(endTime)
  if (e <= s) e += 1440
  return { start: s, end: e }
}

/** 20:00–06:00 dilimine düşen dakika. */
export function nightMinutes(start: number, end: number): number {
  let total = 0
  for (const base of [-1440, 0, 1440]) {
    const a = Math.max(start, base + 1200)
    const b = Math.min(end, base + 1440 + 360)
    if (b > a) total += b - a
  }
  return total
}

export function limitsFrom(s?: Partial<RuleLimits> | null): RuleLimits {
  return {
    minRestHours: s?.minRestHours ?? DEFAULT_LIMITS.minRestHours,
    weeklyMaxHours: s?.weeklyMaxHours ?? DEFAULT_LIMITS.weeklyMaxHours,
    dailyMaxHours: s?.dailyMaxHours ?? DEFAULT_LIMITS.dailyMaxHours,
    nightMaxHours: s?.nightMaxHours ?? DEFAULT_LIMITS.nightMaxHours,
    maxConsecutiveDays: s?.maxConsecutiveDays ?? DEFAULT_LIMITS.maxConsecutiveDays,
  }
}

export function patternWarnings(days: ReadonlyArray<PatternDayLike>, limits: RuleLimits = DEFAULT_LIMITS): PatternWarning[] {
  const n = days.length
  if (n === 0) return []
  const work = days.map((d) => (d.type !== 'Off' && d.startTime && d.endTime && d.startTime.slice(0, 5) !== d.endTime.slice(0, 5) ? span(d.startTime, d.endTime) : null))
  const out: PatternWarning[] = []

  work.forEach((w, i) => {
    if (!w) return
    const len = w.end - w.start
    if (len > limits.dailyMaxHours * 60)
      out.push({ code: 'daily', day: i, message: tx('{0}. gün {1} saat (günlük en çok {2} saat)', [i + 1, h(len / 60), h(limits.dailyMaxHours)]) })
    if (nightMinutes(w.start, w.end) * 2 >= len && len > limits.nightMaxHours * 60)
      out.push({ code: 'night', day: i, message: tx('{0}. gün gece çalışması {1} saat (en çok {2} saat)', [i + 1, h(len / 60), h(limits.nightMaxHours)]) })
    // Dinlenme: döngüde bu günden sonraki ilk çalışma gününe kadar.
    for (let k = 1; k <= n; k++) {
      const j = (i + k) % n
      const next = work[j]
      if (!next) continue
      const gap = k * 1440 + next.start - w.end
      if (gap < limits.minRestHours * 60)
        out.push({ code: 'rest', day: j, message: tx('{0}. gün ile {1}. gün arası {2} saat dinlenme (en az {3} saat)', [i + 1, j + 1, h(Math.max(0, gap) / 60), h(limits.minRestHours)]) })
      break
    }
  })

  // Ardışık çalışma günü (döngüsel).
  const working = work.map(Boolean)
  if (working.every(Boolean)) {
    out.push({ code: 'consecutive', day: null, message: tx('Desende dinlenme günü yok (en çok {0} gün üst üste)', [limits.maxConsecutiveDays]) })
  } else {
    let best = 0
    let run = 0
    for (let k = 0; k < 2 * n; k++) {
      run = working[k % n] ? run + 1 : 0
      best = Math.max(best, run)
    }
    if (best > limits.maxConsecutiveDays)
      out.push({ code: 'consecutive', day: null, message: tx('{0} gün üst üste çalışma (en çok {1} gün)', [best, limits.maxConsecutiveDays]) })
  }

  // Haftalık: herhangi 7 günlük dilim.
  let maxWeek = 0
  for (let s0 = 0; s0 < n; s0++) {
    let m = 0
    for (let k = 0; k < 7; k++) {
      const w = work[(s0 + k) % n]
      if (w) m += w.end - w.start
    }
    maxWeek = Math.max(maxWeek, m)
  }
  if (maxWeek > limits.weeklyMaxHours * 60)
    out.push({ code: 'weekly', day: null, message: tx('7 günlük dilimde {0} saate çıkıyor (haftalık en çok {1} saat, mola dahil)', [h(maxWeek / 60), h(limits.weeklyMaxHours)]) })
  return out
}
