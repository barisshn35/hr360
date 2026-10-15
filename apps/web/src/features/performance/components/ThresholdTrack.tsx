/**
 * Aksiyon eşikleri — tek yatay çubukta dört işaretçi.
 *
 *   0 ── kritik ── gelişim ── takdir ── terfi ── 100
 *     Acil    Gelişim   Beklenen   Takdir   Terfi
 *
 * Sürükleyerek işaretçiler birbirini geçemez (sıra hep geçerli kalır). Sayı
 * kutularına yazarken sıra bozulabilir; bozulduğu anda `thresholdIssues`
 * uyarıları üretir ve çakışan işaretçiler kırmızıya döner.
 *
 * `dots` verilirse çalışanların mevcut puanları çubuğun üstüne nokta olarak
 * düşer ve eşik oynadıkça hangi bölgeye geçtikleri canlı görünür.
 * `onChange` verilmezse salt okunur çizilir (ör. öneriler ekranının üstü).
 */

import { useMemo } from 'react'
import { motion } from 'motion/react'
import { Slider as SliderPrimitive } from 'radix-ui'
import type { Thresholds } from '@/api/performance'
import { formatScore } from '@/api/performance'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'

export type ZoneKey = 'critical' | 'improvement' | 'normal' | 'recognition' | 'promotion'

export const ZONES: { key: ZoneKey; label: string; color: string }[] = [
  { key: 'critical', label: tx('Acil aksiyon'), color: 'hsl(var(--destructive))' },
  { key: 'improvement', label: tx('Gelişim planı'), color: 'hsl(var(--warning))' },
  { key: 'normal', label: tx('Beklenen aralık'), color: 'hsl(var(--muted-foreground) / 0.35)' },
  { key: 'recognition', label: tx('Takdir'), color: 'hsl(var(--success))' },
  { key: 'promotion', label: tx('Terfi düzeyi'), color: 'hsl(var(--primary))' },
]

const MARKERS: { key: keyof Thresholds; label: string; short: string; from: string }[] = [
  { key: 'critical', label: tx('Kritik eşik'), short: tx('Kritik'), from: tx('kritik eşikten') },
  { key: 'improvement', label: tx('Gelişim eşiği'), short: tx('Gelişim'), from: tx('gelişim eşiğinden') },
  { key: 'recognition', label: tx('Takdir eşiği'), short: tx('Takdir'), from: tx('takdir eşiğinden') },
  { key: 'promotion', label: tx('Terfi eşiği'), short: tx('Terfi'), from: tx('terfi eşiğinden') },
]

export function zoneOf(score: number, t: Thresholds): ZoneKey {
  if (score < t.critical) return 'critical'
  if (score < t.improvement) return 'improvement'
  if (score >= t.promotion) return 'promotion'
  if (score >= t.recognition) return 'recognition'
  return 'normal'
}

/** Eşik sırası ve aralık kontrolü — ekranda anlık uyarı olarak gösterilir. */
export function thresholdIssues(t: Thresholds): { keys: (keyof Thresholds)[]; message: string }[] {
  const out: { keys: (keyof Thresholds)[]; message: string }[] = []
  for (let i = 1; i < MARKERS.length; i++) {
    const a = MARKERS[i - 1]
    const b = MARKERS[i]
    if (!(t[b.key] > t[a.key])) {
      out.push({
        keys: [a.key, b.key],
        message: tx('{0} ({1}), {2} ({3}) büyük olmalı.', [b.label, t[b.key], a.from, t[a.key]]),
      })
    }
  }
  for (const m of MARKERS) {
    if (t[m.key] < 0 || t[m.key] > 100) out.push({ keys: [m.key], message: tx('{0} 0 ile 100 arasında olmalı.', [m.label]) })
  }
  return out
}

export interface TrackDot {
  id: string
  label: string
  score: number
}

export function ThresholdTrack({
  value,
  onChange,
  dots = [],
  compareTo,
  className,
}: {
  value: Thresholds
  onChange?: (t: Thresholds) => void
  dots?: TrackDot[]
  /** Kayıtlı (yürürlükteki) eşikler — değiştirildiyse soluk iz olarak görünür. */
  compareTo?: Thresholds
  className?: string
}) {
  const issues = thresholdIssues(value)
  const bad = new Set(issues.flatMap((i) => i.keys))
  const sorted = [value.critical, value.improvement, value.recognition, value.promotion].map((v) => Math.min(100, Math.max(0, v))).sort((a, b) => a - b)

  const bounds = [0, ...sorted, 100]
  const zones = ZONES.map((z, i) => ({ ...z, from: bounds[i], to: bounds[i + 1] }))

  /* Noktaları üst üste binmeyecek şekilde yığ. */
  const placed = useMemo(() => {
    const levels = new Map<number, number>()
    return [...dots]
      .sort((a, b) => a.score - b.score)
      .map((d) => {
        const bucket = Math.round(d.score / 2.2)
        const level = levels.get(bucket) ?? 0
        levels.set(bucket, level + 1)
        return { ...d, level: Math.min(level, 6) }
      })
  }, [dots])

  const dotArea = dots.length ? 16 + (Math.min(6, Math.max(0, ...placed.map((p) => p.level))) + 1) * 9 : 0

  return (
    <div className={cn('select-none', className)}>
      <div className="relative" style={{ paddingTop: dotArea }}>
        {/* Çalışan noktaları */}
        {placed.map((d, i) => {
          const zone = ZONES.find((z) => z.key === zoneOf(d.score, value))!
          return (
            <motion.span
              key={d.id}
              title={`${d.label}: ${formatScore(d.score)}`}
              initial={{ opacity: 0, scale: 0, y: -8 }}
              animate={{ opacity: 1, scale: 1, y: 0, backgroundColor: zone.color }}
              transition={{ delay: 0.3 + i * 0.02, type: 'spring', stiffness: 400, damping: 22, backgroundColor: { duration: 0.25 } }}
              className="absolute size-2 -translate-x-1/2 rounded-full ring-2 ring-card"
              style={{ left: `${d.score}%`, bottom: 18 + d.level * 9 }}
            />
          )
        })}

        {/* Bölgeler */}
        <div className="relative h-3 w-full overflow-hidden rounded-full bg-muted">
          {zones.map((z) => (
            <motion.div
              key={z.key}
              className="absolute inset-y-0"
              initial={false}
              animate={{ left: `${z.from}%`, width: `${Math.max(0, z.to - z.from)}%` }}
              transition={{ type: 'spring', stiffness: 380, damping: 40 }}
              style={{ background: z.color }}
            />
          ))}
          {compareTo &&
            MARKERS.map((m) =>
              compareTo[m.key] !== value[m.key] ? (
                <span
                  key={m.key}
                  aria-hidden
                  className="absolute inset-y-0 w-0.5 -translate-x-1/2 bg-foreground/40"
                  style={{ left: `${compareTo[m.key]}%` }}
                  title={tx('Kayıtlı {0}: {1}', [m.label.toLocaleLowerCase(appLocale), compareTo[m.key]])}
                />
              ) : null,
            )}
        </div>

        {/* Tutamaklar (düzenlenebilirse) */}
        {onChange ? (
          <SliderPrimitive.Root
            value={sorted}
            onValueChange={(v) => onChange({ critical: v[0], improvement: v[1], recognition: v[2], promotion: v[3] })}
            min={0}
            max={100}
            step={1}
            minStepsBetweenThumbs={1}
            aria-label={tx('Aksiyon eşikleri')}
            className="absolute inset-x-0 bottom-[-10px] flex h-8 touch-none items-center"
          >
            <SliderPrimitive.Track className="relative h-3 w-full grow">
              <SliderPrimitive.Range className="absolute h-full" />
            </SliderPrimitive.Track>
            {MARKERS.map((m, i) => (
              <SliderPrimitive.Thumb
                key={m.key}
                aria-label={m.label}
                className={cn(
                  'block h-7 w-3.5 cursor-grab rounded-full border-2 bg-background shadow-md transition-transform hover:scale-110 focus-visible:ring-[4px] focus-visible:ring-ring/40 focus-visible:outline-none active:cursor-grabbing',
                  bad.has(MARKERS[i].key) ? 'border-destructive' : 'border-foreground/70',
                )}
              />
            ))}
          </SliderPrimitive.Root>
        ) : (
          MARKERS.map((m) => (
            <span
              key={m.key}
              aria-hidden
              className="absolute bottom-[-4px] h-5 w-1 -translate-x-1/2 rounded-full bg-foreground/70 ring-2 ring-card"
              style={{ left: `${value[m.key]}%` }}
            />
          ))
        )}
      </div>

      {/* İşaretçi değerleri */}
      <div className="relative mt-3 h-9">
        {MARKERS.map((m) => {
          const v = Math.min(100, Math.max(0, value[m.key]))
          return (
            <motion.div
              key={m.key}
              initial={false}
              animate={{ left: `${v}%` }}
              transition={{ type: 'spring', stiffness: 380, damping: 40 }}
              className="absolute top-0 flex -translate-x-1/2 flex-col items-center"
            >
              <span className={cn('tabular text-[13px] font-semibold', bad.has(m.key) && 'text-destructive')}>{value[m.key]}</span>
              <span className="text-[10px] whitespace-nowrap text-muted-foreground">{m.short}</span>
            </motion.div>
          )
        })}
      </div>

      {/* Bölge adları */}
      <div className="relative mt-1 hidden h-4 sm:block">
        {zones.map((z) =>
          z.to - z.from >= 9 ? (
            <motion.span
              key={z.key}
              initial={false}
              animate={{ left: `${(z.from + z.to) / 2}%` }}
              transition={{ type: 'spring', stiffness: 380, damping: 40 }}
              className="absolute -translate-x-1/2 text-[11px] font-medium whitespace-nowrap"
              style={{ color: z.key === 'normal' ? 'hsl(var(--muted-foreground))' : z.color }}
            >
              {z.label}
            </motion.span>
          ) : null,
        )}
      </div>
    </div>
  )
}
