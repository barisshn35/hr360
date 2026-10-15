/**
 * Vardiya motorunun ortak parçaları: tarih aritmetiği, gün tiplerinin görsel
 * dili (renk + ikon + ad) ve desen şeridi.
 *
 * Tarihler her yerde "YYYY-MM-DD" metni olarak dolaşır ve UTC üzerinden
 * hesaplanır — yaz saati ya da tarayıcı saat dilimi bir günü kaydırmasın.
 */

import type { ReactNode } from 'react'
import { BedDouble, Flag, Moon, PenLine, TreePalm, Sun } from 'lucide-react'
import type {
  RosterDay,
  RosterDayType,
  ShiftDayType,
  ShiftPattern,
  ShiftPatternDay,
} from '@/api/timeshift'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'

/* ------------------------------------ Tarih ------------------------------------ */

const DAY_MS = 86_400_000

const toUtc = (iso: string) => {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return Date.UTC(y, m - 1, d)
}

const fromUtc = (ms: number) => new Date(ms).toISOString().slice(0, 10)

export const addDays = (iso: string, n: number) => fromUtc(toUtc(iso) + n * DAY_MS)

/** `b - a`, gün cinsinden. */
export const diffDays = (a: string, b: string) => Math.round((toUtc(b) - toUtc(a)) / DAY_MS)

/** Yerel takvime göre bugün. */
export function todayIso(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** 0 = Pazar … 6 = Cumartesi */
export const weekdayOf = (iso: string) => new Date(toUtc(iso)).getUTCDay()

export const isWeekend = (iso: string) => {
  const w = weekdayOf(iso)
  return w === 0 || w === 6
}

/** Haftanın pazartesisi. */
export const mondayOf = (iso: string) => addDays(iso, -((weekdayOf(iso) + 6) % 7))

export const monthStart = (iso: string) => `${iso.slice(0, 7)}-01`

export function monthEnd(iso: string): string {
  const [y, m] = iso.split('-').map(Number)
  return fromUtc(Date.UTC(y, m, 0))
}

export function dateRange(from: string, to: string): string[] {
  const n = diffDays(from, to)
  return Array.from({ length: Math.max(0, n + 1) }, (_, i) => addDays(from, i))
}

const WEEKDAY_SHORT = [tx('Paz'), tx('Pzt'), tx('Sal'), tx('Çar'), tx('Per'), tx('Cum'), tx('Cmt')]
export const weekdayShort = (iso: string) => WEEKDAY_SHORT[weekdayOf(iso)]

const dayMonthFmt = new Intl.DateTimeFormat(appLocale, { day: 'numeric', month: 'short', timeZone: 'UTC' })
const longFmt = new Intl.DateTimeFormat(appLocale, {
  day: 'numeric',
  month: 'long',
  weekday: 'long',
  timeZone: 'UTC',
})
const monthFmt = new Intl.DateTimeFormat(appLocale, { month: 'long', year: 'numeric', timeZone: 'UTC' })

export const formatDayMonth = (iso: string) => dayMonthFmt.format(toUtc(iso))
export const formatLongDay = (iso: string) => longFmt.format(toUtc(iso))
export const formatMonth = (iso: string) => monthFmt.format(toUtc(iso))

const mod = (n: number, m: number) => ((n % m) + m) % m

/** Ekibin verilen tarihte desenin kaçıncı gününde olduğu (0 tabanlı). */
export const patternIndexAt = (anchorDate: string, date: string, length: number) =>
  length > 0 ? mod(diffDays(anchorDate, date), length) : 0

/* ------------------------------------ Saat ------------------------------------- */

/** "21:00:00" → "21:00" */
export const hhmm = (t: string | null | undefined) => (t ? t.slice(0, 5) : '')

/** Bitiş başlangıçtan önce (ya da eşit) ise vardiya ertesi güne taşar. */
export const crossesMidnight = (start: string | null, end: string | null) =>
  Boolean(start && end && hhmm(end) <= hhmm(start))

export function timeRange(start: string | null, end: string | null): string {
  if (!start || !end) return ''
  return `${hhmm(start)}–${hhmm(end)}${crossesMidnight(start, end) ? ' (+1)' : ''}`
}

/** Vardiya süresi, dakika cinsinden; gece yarısını geçen vardiyada ertesi güne taşar. */
export function shiftMinutes(start: string | null, end: string | null): number {
  if (!start || !end) return 0
  const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))
  const minutes = toMin(end) - toMin(start)
  return minutes <= 0 ? minutes + 24 * 60 : minutes
}

export const hoursLabel = (minutes: number) =>
  `${(minutes / 60).toLocaleString(appLocale, { maximumFractionDigits: 1 })} sa`

/** Vardiya süresi, saat cinsinden ("12 sa", "7,5 sa"). */
export function durationLabel(start: string | null, end: string | null): string {
  if (!start || !end) return ''
  return hoursLabel(shiftMinutes(start, end))
}

/* ------------------------------- Gün tiplerinin dili ------------------------------- */

interface DayStyle {
  label: string
  /** Sayılı özetlerde küçük harfli ad ("3 gece"). */
  lower: string
  icon: React.ElementType
  /** Blok zemini + yazı rengi. */
  block: string
  /** Lejant ve sayaç noktası. */
  dot: string
}

export const DAY_STYLE: Record<RosterDayType, DayStyle> = {
  Day: {
    label: tx('Gündüz'),
    lower: tx('gündüz'),
    icon: Sun,
    block: 'bg-[hsl(var(--chart-4))]/18 text-[hsl(var(--chart-4))] ring-[hsl(var(--chart-4))]/35',
    dot: 'bg-[hsl(var(--chart-4))]',
  },
  Night: {
    label: tx('Gece'),
    lower: 'gece',
    icon: Moon,
    block: 'bg-[hsl(var(--chart-1))]/18 text-[hsl(var(--chart-1))] ring-[hsl(var(--chart-1))]/35',
    dot: 'bg-[hsl(var(--chart-1))]',
  },
  Off: {
    label: tx('Tatil'),
    lower: 'tatil',
    icon: BedDouble,
    block: 'bg-muted text-muted-foreground/70 ring-border',
    dot: 'bg-muted-foreground/40',
  },
  Leave: {
    label: tx('İzinli'),
    lower: 'izinli',
    icon: TreePalm,
    // Çizgili zemin: istisna olduğu renkten başka bir işaretle de anlaşılsın.
    block:
      'bg-[hsl(var(--success))]/15 text-[hsl(var(--success))] ring-[hsl(var(--success))]/45 bg-[repeating-linear-gradient(135deg,transparent_0_4px,hsl(var(--success)/0.14)_4px_8px)]',
    dot: 'bg-[hsl(var(--success))]',
  },
  Holiday: {
    label: tx('Resmî tatil'),
    lower: tx('resmî tatil'),
    icon: Flag,
    block:
      'bg-[hsl(var(--chart-5))]/14 text-[hsl(var(--chart-5))] ring-[hsl(var(--chart-5))]/45 bg-[repeating-linear-gradient(135deg,transparent_0_4px,hsl(var(--chart-5)/0.12)_4px_8px)]',
    dot: 'bg-[hsl(var(--chart-5))]',
  },
  Manual: {
    label: tx('Elle değişiklik'),
    lower: tx('elle değişiklik'),
    icon: PenLine,
    block:
      'bg-[hsl(var(--chart-2))]/14 text-[hsl(var(--chart-2))] ring-[hsl(var(--chart-2))]/45 bg-[repeating-linear-gradient(135deg,transparent_0_4px,hsl(var(--chart-2)/0.12)_4px_8px)]',
    dot: 'bg-[hsl(var(--chart-2))]',
  },
}

export const PATTERN_TYPES: ShiftDayType[] = ['Day', 'Night', 'Off']
export const OVERRIDE_TYPES: RosterDayType[] = ['Leave', 'Holiday', 'Manual']

/** Bilinmeyen bir tip gelirse (backend genişlerse) takvim çökmesin. */
export const styleOf = (type: string | null): DayStyle =>
  type === null ? DAY_STYLE.Off : DAY_STYLE[type as RosterDayType] ?? DAY_STYLE.Manual

export const isOverride = (type: string | null) =>
  type === 'Leave' || type === 'Holiday' || type === 'Manual'

/** Takvim hücresinin açıklaması (title / ekran okuyucu). Üyeliğin henüz
 * başlamadığı/bittiği günlerde `type` null gelir — boş hücre. */
export function describeRosterDay(day: RosterDay): string {
  if (day.type === null) return tx('Üyelik dışı')
  const s = styleOf(day.type)
  if (day.startTime) return `${s.label} ${timeRange(day.startTime, day.endTime!)}`
  if (day.note) return `${s.label} · ${day.note}`
  return s.label
}

/** "3 gece → 3 tatil → 3 gündüz" — art arda aynı tipler tek grup sayılır. */
export function describeDays(days: Array<{ type: ShiftDayType }>): string {
  const runs: Array<{ type: ShiftDayType; count: number }> = []
  for (const d of days) {
    const last = runs[runs.length - 1]
    if (last && last.type === d.type) last.count++
    else runs.push({ type: d.type, count: 1 })
  }
  return runs.map((r) => `${r.count} ${DAY_STYLE[r.type].lower}`).join(' → ')
}

export const sortedDays = (p: Pick<ShiftPattern, 'days'>): ShiftPatternDay[] =>
  [...(p.days ?? [])].sort((a, b) => a.dayIndex - b.dayIndex)

/* ----------------------------------- Bileşenler ---------------------------------- */

/** Tek bir gün bloğu — desen şeridinde ve takvim hücresinde aynı dil. */
export function DayBlock({
  type,
  size = 'md',
  title,
  className,
  children,
}: {
  type: string | null
  size?: 'xs' | 'sm' | 'md'
  title?: string
  className?: string
  children?: ReactNode
}) {
  const s = styleOf(type)
  const Icon = s.icon
  return (
    <span
      title={title}
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-[5px] ring-1 ring-inset',
        size === 'xs' ? 'size-3.5' : size === 'sm' ? 'size-6' : 'size-8',
        s.block,
        className,
      )}
    >
      {children ?? (size !== 'xs' && <Icon aria-hidden className={size === 'sm' ? 'size-3.5' : 'size-4'} strokeWidth={1.75} />)}
    </span>
  )
}

/**
 * Desenin şerit görünümü. `startIndex` verilirse şerit o günden başlar
 * (ekibin "bugün" hangi günde olduğunu göstermek için).
 */
export function PatternStrip({
  days,
  size = 'sm',
  startIndex = 0,
  highlightIndex,
  className,
}: {
  days: ShiftPatternDay[]
  size?: 'xs' | 'sm' | 'md'
  startIndex?: number
  highlightIndex?: number
  className?: string
}) {
  const ordered = [...days].sort((a, b) => a.dayIndex - b.dayIndex)
  const rotated = ordered.map((_, i) => ordered[(i + startIndex) % ordered.length])
  return (
    <div
      role="img"
      aria-label={describeDays(ordered)}
      className={cn('flex flex-wrap items-center', size === 'xs' ? 'gap-0.5' : 'gap-1', className)}
    >
      {rotated.map((d, i) => (
        <DayBlock
          key={i}
          type={d.type}
          size={size}
          title={tx('{0}. gün · {1}{2}', [d.dayIndex + 1, DAY_STYLE[d.type].label, d.startTime ? ` ${timeRange(d.startTime, d.endTime)}` : ''])}
          className={cn(highlightIndex === d.dayIndex && 'ring-2 ring-foreground/70')}
        />
      ))}
    </div>
  )
}

/** Renk lejantı. */
export function Legend({ types, className }: { types: RosterDayType[]; className?: string }) {
  return (
    <ul className={cn('flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[12px] text-muted-foreground', className)}>
      {types.map((t) => (
        <li key={t} className="flex items-center gap-1.5">
          <DayBlock type={t} size="xs" />
          {DAY_STYLE[t].label}
        </li>
      ))}
    </ul>
  )
}

/* ------------------------------ Yasal sınır uyarıları ------------------------------ */

type LaborDay = { type: ShiftDayType; startTime: string | null; endTime: string | null }

const toMinutes = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))

/** Vardiyanın 20:00–06:00 gece aralığına düşen dakikası (İş Kanunu m.69). */
function nightMinutes(start: string, end: string): number {
  const s = toMinutes(start)
  const e = s + shiftMinutes(start, end)
  // Önceki, aynı ve ertesi günün gece pencereleri (dakika, gün başına göre).
  const windows: Array<[number, number]> = [[-240, 360], [1200, 1800], [2640, 3240]]
  return windows.reduce((sum, [a, b]) => sum + Math.max(0, Math.min(e, b) - Math.max(s, a)), 0)
}

/**
 * Desen için İş Kanunu sınır uyarıları — engellemez, yalnızca bilgilendirir
 * (denkleştirme, mola ve toplu sözleşme gibi istisnaları planlayıcı bilir).
 * Mola desende tutulmadığından süreler molasız brüt hesaplanır.
 *  - günlük çalışma 11 saati aşamaz (m.63, Çalışma Süresi Yönetmeliği)
 *  - haftalık ortalama 45 saat (m.63; denkleştirmeyle ortalama)
 *  - gece çalışması 7,5 saati aşamaz (m.69)
 *  - iki vardiya arasında en az 11 saat kesintisiz dinlenme (Yönetmelik m.8)
 */
export function laborWarnings(days: LaborDay[]): string[] {
  const out: string[] = []
  const work = days.map((d) => (d.type !== 'Off' && d.startTime && d.endTime ? d : null))
  if (!work.some(Boolean)) return out

  const longest = Math.max(...work.map((d) => (d ? shiftMinutes(d.startTime, d.endTime) : 0)))
  if (longest > 11 * 60)
    out.push(tx('Günlük çalışma 11 saati aşıyor (en uzun vardiya {0}).', [hoursLabel(longest)]))

  const total = work.reduce((sum, d) => sum + (d ? shiftMinutes(d.startTime, d.endTime) : 0), 0)
  const weekly = (total / days.length) * 7
  if (weekly > 45 * 60)
    out.push(tx('Haftalık ortalama 45 saati aşıyor ({0}).', [hoursLabel(weekly)]))

  const night = Math.max(...work.map((d) => (d ? nightMinutes(d.startTime!, d.endTime!) : 0)))
  if (night > 7.5 * 60)
    out.push(tx('Gece çalışması (20:00–06:00) 7,5 saati aşıyor ({0}).', [hoursLabel(night)]))

  // Döngüsel: son günden sonra ilk gün gelir.
  let minRest = Infinity
  for (let i = 0; i < work.length; i++) {
    const a = work[i]
    const b = work[(i + 1) % work.length]
    if (!a || !b) continue
    const endA = toMinutes(a.startTime!) + shiftMinutes(a.startTime, a.endTime)
    const rest = 24 * 60 + toMinutes(b.startTime!) - endA
    minRest = Math.min(minRest, rest)
  }
  if (minRest < 11 * 60)
    out.push(
      minRest <= 0
        ? tx('Art arda iki vardiya çakışıyor ya da arada dinlenme yok (en az 11 saat olmalı).')
        : tx('Vardiyalar arası dinlenme 11 saatin altında (en kısa {0}).', [hoursLabel(minRest)]),
    )
  return out
}

/** Yasal sınır uyarılarını gösteren kutu; uyarı yoksa hiçbir şey çizmez. */
export function LaborWarnings({ days, className }: { days: LaborDay[]; className?: string }) {
  const list = laborWarnings(days)
  if (list.length === 0) return null
  return (
    <div
      role="note"
      className={cn(
        'rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2 text-[12px]',
        className,
      )}
    >
      <p className="font-medium">{tx('Yasal sınır uyarısı (kaydı engellemez)')}</p>
      <ul className="mt-1 list-disc space-y-0.5 pl-4">
        {list.map((w) => (
          <li key={w}>{w}</li>
        ))}
      </ul>
    </div>
  )
}
