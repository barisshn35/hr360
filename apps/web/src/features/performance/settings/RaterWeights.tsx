/**
 * Değerlendirici katsayıları ve geçerlilik kuralları.
 *
 * Katsayı tek başına anlamsız bir sayı; bu yüzden her satır öz
 * değerlendirmeye oranla okunur ("4× öz değerlendirme") ve altta canlı
 * bir örnek hesap var: "Yönetici 80, öz 100 verdi → 84".
 */

import { AnimatePresence, motion } from 'motion/react'
import { Calculator, RotateCcw } from 'lucide-react'
import {
  DEFAULT_REVIEW_WEIGHTS,
  REVIEW_TYPES,
  formatScore,
  formatWeight,
  reviewTypeHints,
  reviewTypeLabels,
  reviewWeightKey,
  type ReviewType,
  type ScoringConfigInput,
} from '@/api/performance'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { Chip, Switch } from '../components/controls'
import { NumberStepper } from '../components/NumberStepper'
import { tx, appLocale } from '@/lib/i18n'

const EXAMPLE: { type: ReviewType; score: number }[] = [
  { type: 'Manager', score: 80 },
  { type: 'Peer', score: 70 },
  { type: 'Self', score: 100 },
]

function ratioText(a: number, b: number): string {
  if (b === 0) return a === 0 ? tx('eşit') : '—'
  const r = a / b
  if (Math.abs(r - 1) < 0.01) return tx('eşit ağırlıkta')
  return `${formatWeight(Math.round(r * 100) / 100)} kat`
}

export function RaterWeights({
  draft,
  onChange,
}: {
  draft: ScoringConfigInput
  onChange: <K extends keyof ScoringConfigInput>(key: K, value: ScoringConfigInput[K]) => void
}) {
  const w = (t: ReviewType) => draft[reviewWeightKey[t]] as number
  const max = Math.max(...REVIEW_TYPES.map(w), 0.0001)
  const self = w('Self')
  const isDefault = REVIEW_TYPES.every((t) => w(t) === DEFAULT_REVIEW_WEIGHTS[t])

  const manager = w('Manager')
  const times = (a: number, b: number) => formatWeight(Math.round((a / b) * 100) / 100)
  const headline =
    self === 0 && manager === 0
      ? tx('Yönetici ve öz değerlendirme puana katılmıyor (ikisinin de katsayısı 0).')
      : self === 0
        ? tx('Öz değerlendirme puana hiç katılmaz (katsayı 0).')
        : manager === 0
          ? tx('Yönetici değerlendirmesi puana hiç katılmaz (katsayı 0).')
          : Math.abs(manager - self) < 0.001
            ? tx('Yönetici değerlendirmesi ile öz değerlendirme eşit ağırlıkta sayılır.')
            : manager > self
              ? tx('Yönetici değerlendirmesi öz değerlendirmeden {0} kat ağır sayılır.', [times(manager, self)])
              : tx('Öz değerlendirme yönetici değerlendirmesinden {0} kat ağır sayılır.', [times(self, manager)])

  const exSum = EXAMPLE.reduce((a, e) => a + w(e.type), 0)
  const exResult = exSum > 0 ? EXAMPLE.reduce((a, e) => a + e.score * w(e.type), 0) / exSum : null

  return (
    <div className="flex flex-col gap-6">
      <div>
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <AnimatePresence mode="wait">
            <motion.p
              key={headline}
              initial={{ opacity: 0, y: 4 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -4 }}
              className="text-[14px] font-medium"
            >
              {headline}
            </motion.p>
          </AnimatePresence>
          {!isDefault && (
            <Button
              size="xs"
              variant="ghost"
              onClick={() => REVIEW_TYPES.forEach((t) => onChange(reviewWeightKey[t], DEFAULT_REVIEW_WEIGHTS[t]))}
            >
              <RotateCcw aria-hidden />
              {tx('Varsayılana dön (0,5 / 2 / 1,5 / 1 / 1)')}
            </Button>
          )}
        </div>

        <ul className="flex flex-col gap-2.5">
          {REVIEW_TYPES.map((t) => {
            const v = w(t)
            return (
              <li key={t} className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-1.5 sm:grid-cols-[180px_minmax(0,1fr)_auto]">
                <div className="min-w-0">
                  <p className="text-[13px] font-medium">{reviewTypeLabels[t]}</p>
                  <p className="truncate text-[11px] text-muted-foreground">{reviewTypeHints[t]}</p>
                </div>
                <div className="order-last col-span-2 flex items-center gap-3 sm:order-none sm:col-span-1">
                  <div className="relative h-6 flex-1 overflow-hidden rounded-md bg-muted">
                    <motion.div
                      className={cn('absolute inset-y-0 left-0 rounded-md', t === 'Self' ? 'bg-muted-foreground/40' : 'bg-primary/80')}
                      initial={false}
                      animate={{ width: `${(v / max) * 100}%` }}
                      transition={{ type: 'spring', stiffness: 300, damping: 34 }}
                    />
                    <span className="absolute inset-y-0 left-2 flex items-center text-[11px] font-medium text-foreground">
                      {t === 'Self'
                        ? 'referans'
                        : self > 0
                          ? Math.abs(v - self) < 0.001
                            ? tx('öz değerlendirmeyle eşit')
                            : tx('öz değerlendirmenin {0}ı', [ratioText(v, self)])
                          : ''}
                    </span>
                  </div>
                  {v === 0 && <Chip tone="warning">{tx('Puana katılmaz')}</Chip>}
                </div>
                <NumberStepper value={v} onChange={(n) => onChange(reviewWeightKey[t], n)} min={0} max={10} step={0.5} decimals={2} ariaLabel={tx('{0} katsayısı', [reviewTypeLabels[t]])} />
              </li>
            )
          })}
        </ul>

        <div className="mt-4 rounded-lg border border-dashed border-border bg-muted/30 p-3.5">
          <p className="flex items-center gap-1.5 text-[12px] font-semibold text-muted-foreground">
            <Calculator className="size-3.5" aria-hidden />
            {tx('Örnek hesap — bir metrikte')}
          </p>
          <p className="mt-1.5 text-[13px] leading-relaxed">
            {EXAMPLE.map((e, i) => (
              <span key={e.type}>
                {i > 0 && ', '}
                {reviewTypeLabels[e.type].toLocaleLowerCase(appLocale)} <span className="tabular font-semibold">{e.score}</span>
              </span>
            ))}{' '}
            {tx('verdi →')}
          </p>
          <p className="tabular mt-1 overflow-x-auto text-[13px] whitespace-nowrap text-muted-foreground">
            ({EXAMPLE.map((e) => `${e.score}×${formatWeight(w(e.type))}`).join(' + ')}) ÷ {formatWeight(exSum)} ={' '}
            <motion.span key={exResult ?? -1} initial={{ opacity: 0.2 }} animate={{ opacity: 1 }} className="font-semibold text-foreground">
              {formatScore(exResult)}
            </motion.span>
          </p>
        </div>
      </div>

      <div className="grid gap-5 border-t border-border pt-5 md:grid-cols-2">
        <div>
          <div className="flex items-center justify-between gap-4">
            <div>
              <p className="text-[13px] font-medium">{tx('Geçerli puan için en az değerlendirme')}</p>
              <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground">
                {tx('Bundan az değerlendirmeye dayanan puan')}{' '}<strong className="font-medium text-foreground">{tx('geçici')}</strong>{' '}{tx('sayılır ve her yerde işaretli görünür.')}
              </p>
            </div>
            <NumberStepper
              value={draft.minReviewsForValidScore}
              onChange={(n) => onChange('minReviewsForValidScore', Math.round(n))}
              min={1}
              max={10}
              ariaLabel={tx('Geçerli puan için en az değerlendirme')}
            />
          </div>
          <div className="mt-3 flex gap-1" aria-hidden>
            {Array.from({ length: 10 }).map((_, i) => (
              <motion.span
                key={i}
                initial={false}
                animate={{ opacity: i < draft.minReviewsForValidScore ? 1 : 0.25, scaleY: i < draft.minReviewsForValidScore ? 1 : 0.6 }}
                className="h-3 flex-1 origin-bottom rounded-sm bg-primary"
              />
            ))}
          </div>
        </div>
        <Switch
          checked={draft.allowSelfOnlyScore}
          onChange={(v) => onChange('allowSelfOnlyScore', v)}
          label={tx('Yalnızca öz değerlendirmeyle geçerli puan oluşsun')}
          hint={
            draft.allowSelfOnlyScore
              ? tx('Açık: yalnızca öz değerlendirmesi olan çalışanın puanı da geçerli sayılır.')
              : tx('Kapalı: yalnızca öz değerlendirmesi olan çalışanın puanı geçici kalır; yönetici ya da ekip arkadaşı değerlendirmesi beklenir.')
          }
        />
      </div>
    </div>
  )
}
