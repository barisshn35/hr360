/**
 * İzin gün hesabı (sunucudaki LeaveEntitlement.WorkingDays ile aynı kural; önizleme sapmasın):
 * hafta sonu ve tam gün resmî tatil 0, yarım gün tatil (arife, 28 Ekim) 0,5 gün. Tarihler UTC olarak
 * ayrıştırılır ki yerel saat dilimi günü kaydırmasın.
 */
export function workingDays(start: string, end: string, holidays: ReadonlyMap<string, boolean> = new Map()): number {
  if (!start || !end) return 0
  const a = Date.parse(`${start}T00:00:00Z`)
  const b = Date.parse(`${end}T00:00:00Z`)
  if (Number.isNaN(a) || Number.isNaN(b) || b < a) return 0
  let count = 0
  for (let t = a; t <= b; t += 86_400_000) {
    const d = new Date(t)
    const dow = d.getUTCDay()
    if (dow === 0 || dow === 6) continue
    const half = holidays.get(d.toISOString().slice(0, 10))
    if (half === undefined) count += 1
    else if (half) count += 0.5
  }
  return count
}

/** Tatil listesinden tarih → yarım gün mü haritası (aynı güne iki kayıt varsa tam gün baskın). */
export function holidayMap(list: ReadonlyArray<{ date: string; isHalfDay?: boolean }>): Map<string, boolean> {
  const m = new Map<string, boolean>()
  for (const h of list) {
    const k = h.date.slice(0, 10)
    m.set(k, (m.get(k) ?? true) && Boolean(h.isHalfDay))
  }
  return m
}

export type LeaveUnit = 'full' | 'half' | 'hours'

/**
 * Talebin gün karşılığı: tam gün = iş günü; yarım gün (tek gün) = 0,5 (yarım gün tatilde de en çok 0,5);
 * saatlik (tek gün) = saat / günlük çalışma saati, 2 ondalık (sunucu: HoursToDays).
 */
export function leaveDays(unit: LeaveUnit, start: string, end: string, holidays: ReadonlyMap<string, boolean>, hours: number, dayHours: number): number {
  const wd = workingDays(start, end, holidays)
  if (unit === 'full' || start !== end) return wd
  if (unit === 'half') return Math.min(0.5, wd)
  if (hours <= 0) return 0
  const h = dayHours > 0 && dayHours <= 12 ? dayHours : 7.5
  return Math.round((hours / h) * 100 + Number.EPSILON) / 100
}

/**
 * Saatlik izin alanı hatası: 0,5 saat adımı, en fazla günlük çalışma saati (yarım gün tatilde yarısı).
 * Günlük saatin tamamı (ör. 7,5) da girilebilir: 1 gün sayılır (sunucu: LeaveEntitlement.HoursProblem).
 */
export function hoursProblem(v: number | null, dayHours: number, dayCap = 1): 'invalid' | 'range' | 'step' | null {
  if (v === null || Number.isNaN(v)) return 'invalid'
  if (v <= 0 || v > dayHours * dayCap) return 'range'
  if (!Number.isInteger(v * 2)) return 'step'
  return null
}
