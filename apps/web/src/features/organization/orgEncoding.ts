/**
 * Organizasyon şeması renklendirmeleri: departman (varsayılan), kıdem bandı ve
 * "bugün izinde". Ücret/yan hak gibi hassas alanlar hiçbir koşulda kodlanmaz.
 *
 * KVKK: departman düzeyindeki özetler (ortanca kıdem, izindekilerin oranı) 5'ten az
 * kişili gruplarda gösterilmez — düğüm "yetersiz grup" rengini alır. Kişi kartında
 * ise yalnızca görüntüleyenin zaten görebildiği bilgi kullanılır (tam çalışan listesi
 * yönetici ve üstüne açık; izin kaydı sunucuda ekibe/İK'ya göre süzülür). İzin türü
 * (hastalık vb. sağlık verisi olabilir) hiç gösterilmez, yalnızca "izinde".
 *
 * Konum: çalışan/departman kayıtlarında konum alanı yok; konuma göre renklendirme
 * veri modeli genişleyince eklenecek.
 */

import type { ChartDept } from './orgChartModel'
import { tx } from '@/lib/i18n'

/** Bu sayıdan küçük gruplar için özet gösterilmez. */
export const MIN_GROUP = 5

const DEPT_COLORS = ['--chart-2', '--chart-3', '--chart-5', '--chart-4', '--chart-1']
export const deptColor = (i: number) => (i < 0 ? 'hsl(var(--primary))' : `hsl(var(${DEPT_COLORS[i % DEPT_COLORS.length]}))`)

/** Özeti gösterilemeyen (küçük) grup ve verisi olmayan düğüm rengi. */
export const NEUTRAL = 'hsl(var(--muted-foreground) / 0.35)'

/* ------------------------------------------------------------------ kıdem */

export type TenureBand = 'lt1' | 'y1to3' | 'y3to5' | 'y5to10' | 'gte10'
export const TENURE_BANDS: TenureBand[] = ['lt1', 'y1to3', 'y3to5', 'y5to10', 'gte10']

/** Kıdem bantları tek renk tonunun koyulaşan basamakları (sıralı ölçek). */
const TENURE_COLOR: Record<TenureBand, string> = {
  lt1: 'hsl(199 89% 78%)',
  y1to3: 'hsl(199 85% 64%)',
  y3to5: 'hsl(199 89% 50%)',
  y5to10: 'hsl(205 85% 38%)',
  gte10: 'hsl(212 80% 27%)',
}

export const tenureLabel = (b: TenureBand): string =>
  ({
    lt1: tx('1 yıldan az'),
    y1to3: tx('1–3 yıl'),
    y3to5: tx('3–5 yıl'),
    y5to10: tx('5–10 yıl'),
    gte10: tx('10 yıl ve üzeri'),
  })[b]

export const tenureColor = (b: TenureBand) => TENURE_COLOR[b]

/** Gün cinsinden kıdem → yıl (yaklaşık; 365.25 gün). */
export function tenureYears(hireDate: string | null | undefined, today: string): number | null {
  if (!hireDate) return null
  const h = Date.parse(hireDate.slice(0, 10))
  const t = Date.parse(today.slice(0, 10))
  if (!Number.isFinite(h) || !Number.isFinite(t) || h > t) return null
  return (t - h) / (365.25 * 86_400_000)
}

export function tenureBand(hireDate: string | null | undefined, today: string): TenureBand | null {
  const y = tenureYears(hireDate, today)
  return y === null ? null : bandOfYears(y)
}

export function bandOfYears(y: number): TenureBand {
  if (y < 1) return 'lt1'
  if (y < 3) return 'y1to3'
  if (y < 5) return 'y3to5'
  if (y < 10) return 'y5to10'
  return 'gte10'
}

/** Departmanın (yalnızca doğrudan üyeleri) ortanca kıdem bandı; 5'ten az kişide null. */
export function deptTenureBand(node: ChartDept, today: string): TenureBand | null {
  const years = node.members
    .map((m) => tenureYears(m.hireDate, today))
    .filter((y): y is number => y !== null)
    .sort((a, b) => a - b)
  if (years.length < MIN_GROUP) return null
  const mid = years.length / 2
  const median = years.length % 2 ? years[Math.floor(mid)] : (years[mid - 1] + years[mid]) / 2
  return bandOfYears(median)
}

/* ------------------------------------------------------------------ bugün izinde */

export type LeaveBucket = 'none' | 'low' | 'mid' | 'high'
export const LEAVE_BUCKETS: LeaveBucket[] = ['none', 'low', 'mid', 'high']

const LEAVE_COLOR: Record<LeaveBucket, string> = {
  none: 'hsl(152 45% 62%)',
  low: 'hsl(45 93% 62%)',
  mid: 'hsl(32 95% 52%)',
  high: 'hsl(12 80% 48%)',
}
export const leaveColor = (b: LeaveBucket) => LEAVE_COLOR[b]
/** Kişi kartındaki "izinde" işareti. */
export const ON_LEAVE_COLOR = LEAVE_COLOR.mid

export const leaveLabel = (b: LeaveBucket): string =>
  ({
    none: tx('Bugün izinde kimse yok'),
    low: tx('%10\'a kadar izinde'),
    mid: tx('%10–25 izinde'),
    high: tx('%25\'ten fazla izinde'),
  })[b]

export function leaveBucket(share: number): LeaveBucket {
  if (share <= 0) return 'none'
  if (share <= 0.1) return 'low'
  if (share <= 0.25) return 'mid'
  return 'high'
}

/** Departmanın (doğrudan üyeleri) bugün izinde olanların oranı; 5'ten az kişide null. */
export function deptLeaveShare(node: ChartDept, onLeave: ReadonlySet<string>): number | null {
  const n = node.members.length
  if (n < MIN_GROUP) return null
  return node.members.filter((m) => onLeave.has(m.id)).length / n
}

/* ------------------------------------------------------------------ ortak */

export interface LegendItem {
  key: string
  label: string
  color: string
}

/** Departman düğümünün dolgu/şerit rengi (seçili kodlamaya göre). */
export function nodeColor(
  encoding: 'department' | 'tenure' | 'leave',
  node: ChartDept | undefined,
  colorIndex: number,
  ctx: { today: string; onLeave: ReadonlySet<string> | null },
): string {
  if (encoding === 'department' || !node) return deptColor(colorIndex)
  if (encoding === 'tenure') {
    const b = deptTenureBand(node, ctx.today)
    return b ? tenureColor(b) : NEUTRAL
  }
  if (!ctx.onLeave) return NEUTRAL
  const s = deptLeaveShare(node, ctx.onLeave)
  return s === null ? NEUTRAL : leaveColor(leaveBucket(s))
}

export const smallGroupLegend = (): LegendItem => ({
  key: 'small',
  label: tx('5 kişiden az: özet gösterilmez'),
  color: NEUTRAL,
})
