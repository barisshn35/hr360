import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { motion, useReducedMotion } from 'motion/react'
import { LoaderCircle, ShieldAlert } from 'lucide-react'
import { governanceApi } from '@/api/governance'
import { mlModelApi } from '@/api/mlModel'
import type { Employee, ExplainResponse, PredictResponse } from '@/api/types'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { ProgressBar } from '@/components/ui/Progress'
import { useToast } from '@/components/ui/Toast'
import { formatPercent } from '@/lib/format'
import { cn } from '@/lib/utils'
import { appLocale, tx } from '@/lib/i18n'
import { reasonText } from '@/features/insights/modelQuality'

const FEATURE_COUNT = 6
/** Model girişi (sıra model kartındaki gibi): kıdem, ücret/bant ortası, son puan,
 * son terfiden bu yana (ay), aylık fazla mesai (saat), yıllık eğitim (saat). */
const TYPICAL = [0, 1, 3, 12, 8, 20]
/** Değer sınırları (ml-inference attrition_ml.FeatureSpec ile aynı; model kartı yüklenince oradaki min/max kullanılır). */
const BOUNDS: Array<[number, number]> = [[0, 45], [0.3, 2], [1, 5], [0, 360], [0, 200], [0, 500]]

/** Kıdem kayıttan hesaplanır; diğer alanlar tipik değerle başlar ve elle düzeltilir. */
function defaultFeatures(employee: Employee): number[] {
  const tenureYears = Math.max(
    0,
    (Date.now() - new Date(employee.hireDate).getTime()) / (365.25 * 24 * 3600 * 1000),
  )
  const values = [...TYPICAL].slice(0, FEATURE_COUNT)
  values[0] = Number(Math.min(tenureYears, BOUNDS[0][1]).toFixed(1))
  return values
}

function toContributions(explain: ExplainResponse, labels: Record<string, string>) {
  const raw = explain.feature_contributions
  const nameAt = (i: number) => {
    const n = explain.feature_names?.[i]
    return n ? labels[n] ?? n : tx('Özellik {0}', [i + 1])
  }
  // ml-inference /explain katkıları giriş sırasına göre düz sayı dizisi olarak döner;
  // ad, giriş alanıyla aynı ("Özellik N") verilir. Nesne biçimi de desteklenir.
  const list = Array.isArray(raw)
    ? raw.map((c, i) =>
        typeof c === 'number'
          ? { feature: nameAt(i), contribution: c }
          : { feature: c.feature, contribution: c.contribution },
      )
    : Object.entries(raw ?? {}).map(([feature, contribution]) => ({ feature, contribution }))
  return list.sort((a, b) => Math.abs(b.contribution) - Math.abs(a.contribution))
}

export function AttritionRiskPanel({ employee }: { employee: Employee }) {
  const toast = useToast()
  const reduced = useReducedMotion()
  const [features, setFeatures] = useState<number[]>(() => defaultFeatures(employee))
  const [result, setResult] = useState<PredictResponse | null>(null)
  const [explain, setExplain] = useState<ExplainResponse | null>(null)

  const mutation = useMutation({
    // KVKK m.11/1-g: istek governance'tan geçer; çalışanın itirazı varsa skor üretilmez ve
    // her hesaplama çalışanın erişim kaydına yazılır.
    mutationFn: () => governanceApi.attritionRisk(employee.id, features),
    onSuccess: ({ prediction, explanation }) => {
      setResult(prediction)
      setExplain(explanation)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Risk analizi yapılamadı.')),
  })

  // Alan adları ve sürüm model kartından (İK rolleri; bu panel de yalnızca İK'ya açık).
  const card = useQuery({ queryKey: ['ml-model', 'card'], queryFn: ({ signal }) => mlModelApi.card(signal), retry: false })
  const labels = useMemo(
    () => Object.fromEntries((card.data?.features ?? []).map((f) => [f.name, tx(f.label)])),
    [card.data],
  )

  const objection = useQuery({
    queryKey: ['privacy', 'objection-status', employee.id, 'AttritionRisk'],
    queryFn: ({ signal }) => governanceApi.objectionStatus(employee.id, 'AttritionRisk', signal),
  })
  const blocked = objection.data?.blocked === true

  const probability = useMemo(() => {
    if (!result?.probability?.length) return null
    // İkili sınıflandırmada pozitif sınıf (ayrılma) olasılığı son elemandır.
    return result.probability[result.probability.length - 1]
  }, [result])

  const boundsAt = (i: number): [number, number] | undefined => {
    const f = card.data?.features[i]
    return f && typeof f.min === 'number' && typeof f.max === 'number' ? [f.min, f.max] : BOUNDS[i]
  }
  const fieldErrors = features.map((v, i) => {
    if (!Number.isFinite(v)) return tx('Değer girin.')
    const b = boundsAt(i)
    return b && (v < b[0] || v > b[1])
      ? tx('{0} ile {1} arasında olmalı.', [String(b[0]).replace('.', ','), String(b[1]).replace('.', ',')])
      : undefined
  })
  const hasFieldError = fieldErrors.some(Boolean)

  const contributions = useMemo(() => (explain ? toContributions(explain, labels) : []), [explain, labels])
  const maxAbs = contributions[0] ? Math.abs(contributions[0].contribution) : 1

  const tone: StatusTone =
    probability === null
      ? 'neutral'
      : probability >= 0.66
        ? 'danger'
        : probability >= 0.33
          ? 'warning'
          : 'success'
  const label =
    probability === null ? '' : probability >= 0.66 ? tx('Yüksek') : probability >= 0.33 ? tx('Orta') : tx('Düşük')
  // Şirketin İK'ca seçtiği inceleme eşiği (model kartı > Kalibrasyon). Eşik üstü bir karar değil,
  // "insan incelemesi önerilir" işaretidir.
  const threshold = typeof result?.threshold === 'number' ? result.threshold : null
  const reasons = explain?.reasons ?? []

  return (
    <Panel>
      <PanelHead
        title={tx('Devir riski')}
        note={card.data?.version ? tx('hr360-attrition-risk, MLflow sürüm {0}', [card.data.version]) : 'hr360-attrition-risk'}
        action={<StatusBadge tone="neutral">{tx('Model')}</StatusBadge>}
      />
      <PanelBody className="space-y-4">
        <p className="border-l-2 border-border pl-3 text-[12px] leading-relaxed text-muted-foreground">
          {tx('Kıdem kayıttan dolduruldu; diğer alanlar tipik değerle başlar, çalışanın bilgileriyle düzeltin.')}{' '}
          <Link to="/panel/model-karti" className="underline underline-offset-2 hover:text-foreground">
            {tx('Model kartı')}
          </Link>
        </p>
        <p className="rounded-xl bg-muted/50 p-3 text-[12px] leading-relaxed text-muted-foreground">
          {tx('KVKK m.11: Bu skor otomatik bir analizdir; tek başına karar için kullanılamaz. Çalışan itiraz edebilir ve her hesaplama çalışanın erişim kaydında görünür.')}
        </p>
        {blocked && (
          <div role="status" className="flex items-start gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-3 text-[12.5px]">
            <ShieldAlert className="mt-0.5 size-4 shrink-0" />
            <span>{objection.data?.status === 'Upheld'
              ? tx('Çalışanın bu analize itirazı kabul edildi; skor üretilmez.')
              : tx('Çalışan bu analize itiraz etti; itiraz sonuçlanana kadar skor üretilmez.')}</span>
          </div>
        )}

        <fieldset className="grid grid-cols-3 gap-2">
          <legend className="sr-only">{tx('Model giriş özellikleri')}</legend>
          {features.map((value, i) => (
            <label key={i} className="flex flex-col gap-1">
              <span className="truncate text-[11px] text-muted-foreground" title={card.data?.features[i]?.description}>
                {card.data?.features[i] ? `${tx(card.data.features[i].label)} (${tx(card.data.features[i].unit)})` : tx('Özellik {0}', [i + 1])}
              </span>
              <Input
                type="number"
                step="any"
                min={boundsAt(i)?.[0]}
                max={boundsAt(i)?.[1]}
                value={Number.isFinite(value) ? value : ''}
                aria-invalid={fieldErrors[i] ? true : undefined}
                aria-describedby={fieldErrors[i] ? `attrition-f${i}-error` : undefined}
                onChange={(e) => {
                  const next = [...features]
                  next[i] = e.target.value === '' ? Number.NaN : Number(e.target.value)
                  setFeatures(next)
                }}
                className="tabular h-9 px-2 text-[13px]"
              />
              {fieldErrors[i] && (
                <span id={`attrition-f${i}-error`} role="alert" className="text-[11px] leading-snug text-destructive">
                  {fieldErrors[i]}
                </span>
              )}
            </label>
          ))}
        </fieldset>

        <Button
          className="w-full cursor-pointer"
          disabled={mutation.isPending || blocked || objection.isPending || hasFieldError}
          onClick={() => mutation.mutate()}
        >
          {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
          {tx('Riski hesapla')}
        </Button>

        {result && (
          <motion.div
            initial={reduced ? false : { opacity: 0, y: 6 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.26, ease: 'easeOut' }}
            className="space-y-4 border-t border-border pt-4"
          >
            <div className="flex items-end justify-between gap-3">
              <div>
                <p className="text-[12px] text-muted-foreground">{tx('Ayrılma olasılığı')}</p>
                <p className="tabular mt-1 text-[30px] leading-none font-bold">
                  {probability === null ? '—' : formatPercent(probability, 1)}
                </p>
              </div>
              <div className="flex flex-col items-end gap-1">
                {label && <StatusBadge tone={tone}>{tx('{0} risk', [label])}</StatusBadge>}
                {typeof result.flagged === 'boolean' && (
                  <StatusBadge tone={result.flagged ? 'warning' : 'neutral'}>
                    {result.flagged ? tx('İnsan incelemesi önerilir') : tx('İnceleme eşiğinin altında')}
                  </StatusBadge>
                )}
              </div>
            </div>
            {threshold !== null && (
              <p className="text-[11.5px] text-muted-foreground">
                {tx('Şirketinizin inceleme eşiği {0}. Eşik bir karar değildir; skor yalnızca görüşmeyi önceliklendirmeye yardım eder.', [
                  new Intl.NumberFormat(appLocale, { maximumFractionDigits: 3 }).format(threshold),
                ])}
              </p>
            )}

            {reasons.length > 0 && (
              <div>
                <p className="mb-1.5 text-[12px] font-medium">{tx('Neden?')}</p>
                <ul className="list-disc space-y-1 pl-5 text-[12.5px]">
                  {reasons.map((r) => <li key={r.feature}>{reasonText(r)}</li>)}
                </ul>
                <p className="mt-1 text-[11px] text-muted-foreground">{tx('(+) riski artırır, (−) azaltır. Model ilişki gösterir, neden-sonuç değil.')}</p>
              </div>
            )}

            {probability !== null && (
              <ProgressBar
                value={probability * 100}
                tone={tone}
                label={tx('Ayrılma olasılığı')}
                thick
              />
            )}

            {contributions.length > 0 && (
              <div>
                <p className="mb-2 text-[12px] text-muted-foreground">
                  {tx('Hangi özellik ne kadar etkiledi')}
                </p>
                <ul className="space-y-2">
                  {contributions.slice(0, 6).map((c) => {
                    const width = Math.round((Math.abs(c.contribution) / maxAbs) * 100)
                    const raises = c.contribution >= 0
                    return (
                      <li key={c.feature} className="flex items-center gap-2.5 text-[12px]">
                        <span className="w-28 shrink-0 truncate text-muted-foreground" title={c.feature}>
                          {c.feature}
                        </span>
                        <span className="h-1 flex-1 overflow-hidden rounded-full bg-muted">
                          <span
                            className={cn(
                              'block h-full rounded-full',
                              raises ? 'bg-destructive' : 'bg-[hsl(var(--success))]',
                            )}
                            style={{ width: `${width}%` }}
                          />
                        </span>
                        <span
                          className={cn(
                            'tabular w-14 shrink-0 text-right font-semibold',
                            raises ? 'text-destructive' : 'text-[hsl(var(--success))]',
                          )}
                        >
                          {raises ? '+' : ''}
                          {c.contribution.toFixed(3)}
                        </span>
                      </li>
                    )
                  })}
                </ul>
                <p className="mt-2 text-[11px] text-muted-foreground">
                  {tx('Kırmızı riski artırır, yeşil azaltır.')}
                </p>
              </div>
            )}
          </motion.div>
        )}
      </PanelBody>
    </Panel>
  )
}
