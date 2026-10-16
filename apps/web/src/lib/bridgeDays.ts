/**
 * Köprü günü önerileri (yenilik haritası #3): resmi tatilleri hafta sonuyla birleştirip az izinle uzun
 * tatil yapılabilecek aralıkları bulur. Saf fonksiyon; tarihler "yyyy-aa-gg" (yerel takvim günü).
 *
 * Kurallar: aralık en az bir resmi tatil içerir; maliyet aralıktaki iş günleridir (yarım gün tatil 0,5);
 * aralık "en geniş"tir (iki ucundaki gün iş günüdür, bedavaya uzatılamaz); maliyet 0'dan büyük ve
 * <paramref name="maxLeave"/>'den küçük ya da eşittir. Aynı tatil için en iyi oran (gün / izin) seçilir.
 */
export interface BridgeHoliday { date: string; name?: string; isHalfDay?: boolean }
export interface BridgeSuggestion {
  start: string
  end: string
  totalDays: number
  leaveDays: number
  /** İzin alınması gereken iş günleri (yarım tatil günleri dahil). */
  leaveDates: string[]
  holidays: string[]
}

const DAY = 86_400_000
const toKey = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const parse = (s: string) => { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d) }

export function bridgeSuggestions(holidays: BridgeHoliday[], today: string, opts: { maxLeave?: number; horizonDays?: number; limit?: number } = {}): BridgeSuggestion[] {
  const maxLeave = opts.maxLeave ?? 4
  const horizon = opts.horizonDays ?? 365
  const byDate = new Map(holidays.map((h) => [h.date, h]))
  const start0 = parse(today).getTime() + DAY // yarından itibaren
  const end0 = start0 + horizon * DAY
  const at = (t: number) => new Date(t)
  const cost = (t: number) => {
    const d = at(t)
    const wd = d.getDay()
    if (wd === 0 || wd === 6) return 0
    const h = byDate.get(toKey(d))
    if (!h) return 1
    return h.isHalfDay ? 0.5 : 0
  }
  const free = (t: number) => cost(t) === 0

  const best = new Map<string, BridgeSuggestion & { ratio: number }>()
  for (const h of holidays) {
    const ht = parse(h.date).getTime()
    if (ht < start0 || ht > end0) continue
    for (let s = ht - 9 * DAY; s <= ht; s += DAY) {
      if (s < start0 || free(s - DAY)) continue // sol uç bedavaya uzatılabiliyorsa en geniş değil
      for (let e = ht; e <= ht + 9 * DAY; e += DAY) {
        if (free(e + DAY)) continue
        let c = 0
        const leaveDates: string[] = []
        const hs: string[] = []
        for (let t = s; t <= e; t += DAY) {
          const k = cost(t)
          c += k
          if (k > 0) leaveDates.push(toKey(at(t)))
          // Yalnızca hafta içine denk gelen tatil kazanç sağlar (Pazar'a denk gelen tatil öneri sebebi değil).
          if (byDate.has(toKey(at(t))) && at(t).getDay() % 6 !== 0) hs.push(toKey(at(t)))
        }
        if (c <= 0 || c > maxLeave || hs.length === 0) continue
        const total = Math.round((e - s) / DAY) + 1
        if (total < c + 2) continue // hafta sonu kazancı yok
        const ratio = total / c
        const cand = { start: toKey(at(s)), end: toKey(at(e)), totalDays: total, leaveDays: c, leaveDates, holidays: hs, ratio }
        const key = hs[0]
        const prev = best.get(key)
        if (!prev || ratio > prev.ratio + 1e-9 || (Math.abs(ratio - prev.ratio) < 1e-9 && total > prev.totalDays)) best.set(key, cand)
      }
    }
  }
  // Çakışan öneriler (arife ve bayram, bayramın günleri) tek öneri olur: en iyi oran kalır.
  const picked: (BridgeSuggestion & { ratio: number })[] = []
  for (const x of [...best.values()].sort((a, b) => b.ratio - a.ratio || b.totalDays - a.totalDays))
    if (!picked.some((p) => x.start <= p.end && p.start <= x.end)) picked.push(x)
  return picked
    .sort((a, b) => a.start.localeCompare(b.start))
    .slice(0, opts.limit ?? 8)
    .map(({ ratio: _r, ...x }) => x)
}
