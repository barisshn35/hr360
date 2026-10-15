/**
 * Aksiyon eşikleri ve terfi sürekliliği.
 *
 * Çubuğun üstündeki noktalar açık dönemin gerçek puanları: eşiği
 * oynattıkça kaç kişinin hangi bölgeye geçtiği anında görünür. Puanların
 * kendisi yürürlükteki sürümle hesaplandı; burada yalnızca eşik etkisi
 * önizlenir (katsayı/ağırlık değişiminin etkisi kaydedince oluşur).
 */

import { AnimatePresence, motion } from 'motion/react'
import { TriangleAlert } from 'lucide-react'
import type { ScoringConfigInput, Thresholds } from '@/api/performance'
import { thresholdsOf } from '@/api/performance'
import { cn } from '@/lib/utils'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { NumberStepper } from '../components/NumberStepper'
import { ThresholdTrack, ZONES, thresholdIssues, zoneOf, type TrackDot, type ZoneKey } from '../components/ThresholdTrack'
import { tx } from '@/lib/i18n'

const INPUTS: { key: keyof Thresholds; field: keyof ScoringConfigInput; label: string; hint: string; zone: ZoneKey }[] = [
  { key: 'critical', field: 'criticalThreshold', label: tx('Kritik eşik'), hint: tx('Altı → acil aksiyon'), zone: 'critical' },
  { key: 'improvement', field: 'improvementThreshold', label: tx('Gelişim eşiği'), hint: tx('Altı → gelişim planı'), zone: 'improvement' },
  { key: 'recognition', field: 'recognitionThreshold', label: tx('Takdir eşiği'), hint: tx('Üstü → takdir'), zone: 'recognition' },
  { key: 'promotion', field: 'promotionThreshold', label: tx('Terfi eşiği'), hint: tx('Üstü (üst üste) → terfi adayı'), zone: 'promotion' },
]

export function ThresholdSection({
  draft,
  saved,
  onChange,
  dots,
  cycleName,
}: {
  draft: ScoringConfigInput
  saved: ScoringConfigInput
  onChange: <K extends keyof ScoringConfigInput>(key: K, value: ScoringConfigInput[K]) => void
  dots: TrackDot[]
  cycleName: string | null
}) {
  const t = thresholdsOf(draft)
  const before = thresholdsOf(saved)
  const issues = thresholdIssues(t)
  const bad = new Set(issues.flatMap((i) => i.keys))

  const setAll = (n: Thresholds) => {
    onChange('criticalThreshold', n.critical)
    onChange('improvementThreshold', n.improvement)
    onChange('recognitionThreshold', n.recognition)
    onChange('promotionThreshold', n.promotion)
  }

  const count = (th: Thresholds) => {
    const out: Record<ZoneKey, number> = { critical: 0, improvement: 0, normal: 0, recognition: 0, promotion: 0 }
    for (const d of dots) out[zoneOf(d.score, th)]++
    return out
  }
  const now = count(t)
  const was = count(before)

  return (
    <div className="flex flex-col gap-6">
      <div className="rounded-xl border border-border bg-muted/20 px-4 pt-3 pb-2 sm:px-6">
        {dots.length > 0 && (
          <p className="mb-1 text-[11px] text-muted-foreground">{tx('Noktalar: {0} puanları ({1} çalışan). Üzerine gelince adı görünür.', [cycleName ?? tx('açık dönem'), dots.length])}</p>
        )}
        <ThresholdTrack value={t} onChange={setAll} dots={dots} compareTo={before} />
      </div>

      <AnimatePresence>
        {issues.length > 0 && (
          <motion.ul
            role="alert"
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: 'auto' }}
            exit={{ opacity: 0, height: 0 }}
            className="flex flex-col gap-1 overflow-hidden rounded-lg border border-destructive/30 bg-destructive/5 px-3.5 py-2.5"
          >
            {issues.map((i) => (
              <li key={i.message} className="flex items-start gap-2 text-[12px] text-destructive">
                <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
                {i.message}
              </li>
            ))}
            <li className="pl-5 text-[12px] text-muted-foreground">{tx('Sıra her zaman kritik < gelişim < takdir < terfi olmalı.')}</li>
          </motion.ul>
        )}
      </AnimatePresence>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {INPUTS.map((inp) => {
          const zone = ZONES.find((z) => z.key === inp.zone)!
          return (
            <div
              key={inp.key}
              className={cn('rounded-lg border p-3 transition-colors', bad.has(inp.key) ? 'border-destructive/50 bg-destructive/5' : 'border-border')}
            >
              <p className="flex items-center gap-1.5 text-[12px] font-medium">
                <span className="size-2 rounded-full" style={{ background: zone.color }} />
                {inp.label}
              </p>
              <p className="mt-0.5 text-[11px] text-muted-foreground">{inp.hint}</p>
              <NumberStepper
                className="mt-2"
                value={draft[inp.field] as number}
                onChange={(n) => onChange(inp.field, Math.round(n))}
                min={0}
                max={100}
                ariaLabel={inp.label}
                invalid={bad.has(inp.key)}
              />
            </div>
          )
        })}
      </div>

      {dots.length > 0 && (
        <div>
          <p className="mb-2 text-[12px] font-semibold text-muted-foreground">{tx('Bu eşiklerle {0}', [cycleName ?? tx('açık dönem')])}</p>
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-5">
            {ZONES.map((z) => {
              const delta = now[z.key] - was[z.key]
              return (
                <div key={z.key} className="rounded-lg border border-border px-3 py-2">
                  <p className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
                    <span className="size-2 rounded-full" style={{ background: z.color }} />
                    {z.label}
                  </p>
                  <p className="mt-0.5 flex items-baseline gap-1.5">
                    <AnimatedNumber value={now[z.key]} className="text-[18px] font-semibold" />
                    <span className="text-[11px] text-muted-foreground">{tx('kişi')}</span>
                    <AnimatePresence>
                      {delta !== 0 && (
                        <motion.span
                          initial={{ opacity: 0, y: 4 }}
                          animate={{ opacity: 1, y: 0 }}
                          exit={{ opacity: 0 }}
                          className={cn('tabular text-[11px] font-semibold', delta > 0 ? 'text-primary' : 'text-muted-foreground')}
                        >
                          {delta > 0 ? `+${delta}` : `−${Math.abs(delta)}`}
                        </motion.span>
                      )}
                    </AnimatePresence>
                  </p>
                </div>
              )
            })}
          </div>
          <p className="mt-2 text-[11px] leading-relaxed text-muted-foreground">{tx('"Terfi düzeyi" puanı eşiğin üzerinde olanlardır; terfi adayı sayılmak için eşiğin üst üste {0} dönem aşılması gerekir. Kişi sayıları yürürlükteki sürümle hesaplanmış puanlara göredir.', [draft.promotionConsecutivePeriods])}</p>
        </div>
      )}

      <div className="flex flex-col gap-4 border-t border-border pt-5 sm:flex-row sm:items-center sm:justify-between">
        <div className="max-w-md">
          <p className="text-[13px] font-medium">{tx('Terfi için üst üste dönem sayısı')}</p>
          <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground">
            {tx('Tek bir iyi dönem terfi önerisi için yetmez. Çalışanın puanı terfi eşiğini art arda bu kadar dönem aşmalı.')}
          </p>
        </div>
        <div className="flex items-center gap-4">
          <div className="flex items-end gap-1" aria-hidden>
            {Array.from({ length: draft.promotionConsecutivePeriods }).map((_, i) => (
              <motion.span
                key={i}
                layout
                initial={{ opacity: 0, scaleY: 0 }}
                animate={{ opacity: 1, scaleY: 1 }}
                transition={{ type: 'spring', stiffness: 420, damping: 24 }}
                className="w-4 origin-bottom rounded-sm bg-primary"
                style={{ height: 14 + i * 5 }}
              />
            ))}
          </div>
          <NumberStepper
            value={draft.promotionConsecutivePeriods}
            onChange={(n) => onChange('promotionConsecutivePeriods', Math.round(n))}
            min={1}
            max={8}
            suffix={tx('dön.')}
            ariaLabel={tx('Terfi için üst üste dönem sayısı')}
          />
        </div>
      </div>
    </div>
  )
}
