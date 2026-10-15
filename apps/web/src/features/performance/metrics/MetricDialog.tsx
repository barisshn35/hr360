/**
 * Metrik oluştur / düzenle.
 *
 * Sağda iki canlı önizleme var: metriğin değerlendirme formunda nasıl
 * görüneceği (gerçek ölçek girdisi) ve kategorideki payı (bu ağırlıkla
 * kategorinin yüzde kaçı olacağı). Kullanıcı "2,5 ne demek?" diye
 * sormadan anlasın diye.
 *
 * Ölçek kilidi: kullanılmış bir metriğin ölçeği değiştirilirse backend 400
 * döner. Mesaj gösterilir ve tek tıklık çıkış yolu sunulur: "arşivle ve
 * yeni ölçekle yenisini oluştur".
 */

import { useMemo, useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { Archive, Lock, TriangleAlert } from 'lucide-react'
import {
  CATEGORIES,
  SCALES,
  categoryColor,
  categoryHints,
  categoryLabels,
  formatShareOf,
  scaleHints,
  scaleLabels,
  scaleRange,
  shareOf,
  useCreateMetric,
  useUpdateMetric,
  type Metric,
  type MetricCategory,
  type MetricInput,
  type MetricScale,
} from '@/api/performance'
import { ApiError } from '@/api/client'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { cn } from '@/lib/utils'
import { Switch, errorText } from '../components/controls'
import { ScaleInput, ScaleBadge } from '../components/ScaleInput'
import { ShareBar } from '../components/WeightShare'
import { basisFor, type Scope } from './share'
import { tx, appLocale } from '@/lib/i18n'

export type MetricDialogMode = { kind: 'create'; preset?: Partial<MetricInput> } | { kind: 'edit'; metric: Metric }

/** Backend'in `code` türetmesinin önizlemesi — gönderilmez, yalnızca gösterilir. */
export function codeFromName(name: string): string {
  return name
    .toLocaleLowerCase(appLocale)
    .replace(/ç/g, 'c').replace(/ğ/g, 'g').replace(/ı/g, 'i')
    .replace(/ö/g, 'o').replace(/ş/g, 's').replace(/ü/g, 'u')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

interface Draft {
  name: string
  description: string
  category: MetricCategory
  scale: MetricScale
  weight: string
  departmentId: string
  isRequired: boolean
  sortOrder: string
}

function initial(mode: MetricDialogMode): Draft {
  if (mode.kind === 'edit') {
    const m = mode.metric
    return {
      name: m.name,
      description: m.description ?? '',
      category: m.category,
      scale: m.scale,
      weight: String(m.weight).replace('.', ','),
      departmentId: m.departmentId ?? '',
      isRequired: m.isRequired,
      sortOrder: String(m.sortOrder),
    }
  }
  const p = mode.preset ?? {}
  return {
    name: p.name ?? '',
    description: p.description ?? '',
    category: p.category ?? 'Technical',
    scale: p.scale ?? 'OneToFive',
    weight: p.weight !== undefined ? String(p.weight).replace('.', ',') : '1',
    departmentId: p.departmentId ?? '',
    isRequired: p.isRequired ?? false,
    sortOrder: p.sortOrder !== undefined ? String(p.sortOrder) : '',
  }
}

const parseNum = (s: string) => {
  const n = Number(s.replace(',', '.').trim())
  return s.trim() === '' || !Number.isFinite(n) ? null : n
}

export function MetricDialog({
  mode,
  onClose,
  active,
  departments,
  scope,
  onArchiveAndRecreate,
}: {
  mode: MetricDialogMode
  onClose: () => void
  active: Metric[]
  departments: { id: string; name: string; path?: string }[]
  scope: Scope
  /** Ölçek kilidinde: bu metriği arşivle, aynı bilgilerle yeni ölçekte oluştur. */
  onArchiveAndRecreate: (metric: Metric, draft: MetricInput) => void
}) {
  const toast = useToast()
  const create = useCreateMetric()
  const update = useUpdateMetric()
  const [draft, setDraft] = useState<Draft>(() => initial(mode))
  const [touched, setTouched] = useState(false)
  const [preview, setPreview] = useState<number | null>(null)
  const [serverError, setServerError] = useState<{ message: string; scaleLocked: boolean } | null>(null)

  const editing = mode.kind === 'edit' ? mode.metric : null
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setDraft((d) => ({ ...d, [key]: value }))
    setServerError(null)
  }

  const weight = parseNum(draft.weight)
  const errors = {
    name: !draft.name.trim() ? tx('Metriğe bir ad verin.') : draft.name.trim().length > 80 ? tx('En fazla 80 karakter.') : undefined,
    weight:
      weight === null ? tx('Ağırlık girin (ör. 1 ya da 2,5).') : weight <= 0 ? tx('Ağırlık sıfırdan büyük olmalı.') : weight > 100 ? tx('En fazla 100.') : undefined,
    sortOrder: draft.sortOrder.trim() && parseNum(draft.sortOrder) === null ? tx('Sayı girin.') : undefined,
  }
  const invalid = Object.values(errors).some(Boolean)

  /* ---- pay önizlemesi ---- */
  const shareView = useMemo(() => {
    const probe = { category: draft.category, departmentId: draft.departmentId || null }
    const previewScope: Scope = draft.departmentId ? draft.departmentId : scope === 'all' ? 'global' : scope
    const others = basisFor(probe, active, previewScope).filter((m) => m.id !== editing?.id)
    const w = weight && weight > 0 ? weight : 0
    const total = others.reduce((a, m) => a + m.weight, 0) + w
    return {
      items: [
        ...others.map((m) => ({ id: m.id, label: m.name, weight: m.weight })),
        { id: '__this__', label: draft.name.trim() || tx('Bu metrik'), weight: w, color: categoryColor[draft.category] },
      ],
      share: shareOf(w, total),
      count: others.length,
      deptName: draft.departmentId ? departments.find((d) => d.id === draft.departmentId)?.name : null,
    }
  }, [active, draft.category, draft.departmentId, draft.name, weight, editing?.id, scope, departments])

  const scaleChanged = editing !== null && editing.scale !== draft.scale
  const pending = create.isPending || update.isPending

  const toInput = (): MetricInput => ({
    name: draft.name.trim(),
    description: draft.description.trim() || null,
    category: draft.category,
    scale: draft.scale,
    weight: weight ?? 1,
    departmentId: draft.departmentId || null,
    isRequired: draft.isRequired,
    ...(parseNum(draft.sortOrder) !== null ? { sortOrder: parseNum(draft.sortOrder)! } : {}),
  })

  const submit = () => {
    setTouched(true)
    if (invalid) return
    const input = toInput()
    const onError = (e: unknown) =>
      setServerError({
        message: errorText(e),
        scaleLocked: scaleChanged && e instanceof ApiError && e.status === 400,
      })
    if (editing) {
      update.mutate(
        { id: editing.id, input },
        {
          onSuccess: () => {
            toast.ok(tx('«{0}» güncellendi.', [input.name]))
            onClose()
          },
          onError,
        },
      )
    } else {
      create.mutate(input, {
        onSuccess: () => {
          toast.ok(tx('«{0}» eklendi.', [input.name]))
          onClose()
        },
        onError,
      })
    }
  }

  const code = editing ? editing.code : codeFromName(draft.name)

  return (
    <Modal
      open
      onClose={onClose}
      size="xl"
      title={editing ? tx('Metriği düzenle') : tx('Yeni metrik')}
      note={tx('Çalışanlar dönem boyunca bu metriklerle değerlendirilir. Ağırlık oransaldır; payı kategorideki diğer metriklere göre hesaplanır.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={pending}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={pending || (touched && invalid)}>
            {pending ? tx('Kaydediliyor…') : editing ? tx('Değişiklikleri kaydet') : tx('Metriği ekle')}
          </Button>
        </>
      }
    >
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_260px]">
        <div className="flex flex-col gap-5">
          <AnimatePresence>
            {serverError && (
              <motion.div
                role="alert"
                initial={{ opacity: 0, height: 0 }}
                animate={{ opacity: 1, height: 'auto' }}
                exit={{ opacity: 0, height: 0 }}
                className="overflow-hidden"
              >
                <div className="rounded-lg border border-destructive/30 bg-destructive/5 p-3.5">
                  <p className="flex items-start gap-2 text-[13px] font-medium text-destructive">
                    <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
                    {serverError.message}
                  </p>
                  {serverError.scaleLocked && editing && (
                    <div className="mt-3 rounded-md border border-border bg-background p-3">
                      <p className="text-[13px] font-medium">{tx('Önerilen yol: arşivleyip yenisini oluşturun')}</p>
                      <p className="mt-1 text-[12px] leading-relaxed text-muted-foreground">{tx('«{0}» arşivlenir; geçmiş değerlendirmelerdeki puanları olduğu gibi kalır. Aynı ad ve ayarlarla,', [editing.name])}{' '}
                        <strong className="font-medium text-foreground">{scaleLabels[draft.scale]}</strong>{' '}{tx('ölçekli yeni bir metrik oluşturulur.')}
                      </p>
                      <Button
                        size="sm"
                        variant="outline"
                        className="mt-3"
                        onClick={() => onArchiveAndRecreate(editing, toInput())}
                      >
                        <Archive aria-hidden />
                        {tx('Arşivle ve yeni ölçekle oluştur')}
                      </Button>
                    </div>
                  )}
                </div>
              </motion.div>
            )}
          </AnimatePresence>

          <div>
            <TextField
              label={tx('Ad')}
              value={draft.name}
              onChange={(e) => set('name', e.target.value)}
              error={touched ? errors.name : undefined}
              placeholder={tx('ör. Kod kalitesi')}
              maxLength={80}
              autoFocus={!editing}
              required
            />
            <p className="mt-1.5 flex items-center gap-1.5 text-[12px] text-muted-foreground">
              {editing && <Lock className="size-3" aria-hidden />}
              {tx('Kod:')}
              <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px] text-foreground">{code || '—'}</code>
              {editing ? tx('değiştirilemez') : tx('addan otomatik türetilir, sonradan değişmez')}
            </p>
          </div>

          <TextAreaField
            label={tx('Açıklama')}
            value={draft.description}
            onChange={(e) => set('description', e.target.value)}
            placeholder={tx('Değerlendiren kişi neye bakmalı? Kısa ve somut yazın.')}
            hint={tx('Değerlendirme formunda metriğin altında görünür.')}
          />

          {/* Kategori */}
          <fieldset>
            <legend className="mb-2 text-[13px] font-medium">{tx('Kategori')}</legend>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
              {CATEGORIES.map((c) => {
                const on = draft.category === c
                return (
                  <button
                    key={c}
                    type="button"
                    role="radio"
                    aria-checked={on}
                    onClick={() => set('category', c)}
                    className={cn(
                      'relative rounded-lg border p-2.5 text-left transition-colors outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                      on ? 'border-transparent' : 'border-border hover:border-primary/30',
                    )}
                  >
                    {on && (
                      <motion.span
                        layoutId="metric-cat"
                        className="absolute inset-0 rounded-lg border-2"
                        style={{ borderColor: categoryColor[c], background: `color-mix(in oklab, ${categoryColor[c]} 8%, transparent)` }}
                        transition={{ type: 'spring', stiffness: 500, damping: 36 }}
                      />
                    )}
                    <span className="relative flex items-center gap-1.5 text-[13px] font-medium">
                      <span className="size-2 rounded-full" style={{ background: categoryColor[c] }} />
                      {categoryLabels[c]}
                    </span>
                    <span className="relative mt-0.5 block text-[11px] leading-snug text-muted-foreground">{categoryHints[c]}</span>
                  </button>
                )
              })}
            </div>
          </fieldset>

          {/* Ölçek */}
          <fieldset>
            <legend className="mb-2 text-[13px] font-medium">{tx('Ölçek')}</legend>
            <div className="grid gap-2 sm:grid-cols-3">
              {SCALES.map((s) => {
                const on = draft.scale === s
                return (
                  <button
                    key={s}
                    type="button"
                    role="radio"
                    aria-checked={on}
                    onClick={() => {
                      set('scale', s)
                      setPreview(null)
                    }}
                    className={cn(
                      'relative rounded-lg border p-2.5 text-left transition-colors outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                      on ? 'border-transparent' : 'border-border hover:border-primary/30',
                    )}
                  >
                    {on && (
                      <motion.span
                        layoutId="metric-scale"
                        className="absolute inset-0 rounded-lg border-2 border-primary bg-primary/5"
                        transition={{ type: 'spring', stiffness: 500, damping: 36 }}
                      />
                    )}
                    <span className="relative flex items-center justify-between gap-2">
                      <span className="text-[13px] font-medium">{scaleLabels[s]}</span>
                      <ScaleBadge scale={s} />
                    </span>
                    <span className="relative mt-1 block text-[11px] leading-snug text-muted-foreground">{scaleHints[s]}</span>
                  </button>
                )
              })}
            </div>
            <AnimatePresence>
              {scaleChanged && (
                <motion.p
                  initial={{ opacity: 0, height: 0 }}
                  animate={{ opacity: 1, height: 'auto' }}
                  exit={{ opacity: 0, height: 0 }}
                  className="mt-2 flex items-start gap-1.5 overflow-hidden text-[12px] leading-relaxed text-[hsl(var(--warning))]"
                >
                  <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
                  {tx('Metrik daha önce değerlendirmelerde kullanıldıysa ölçeği değiştirilemez; kaydederken sistem kontrol eder.')}
                </motion.p>
              )}
            </AnimatePresence>
          </fieldset>

          <div className="grid gap-4 sm:grid-cols-2">
            <TextField
              label={tx('Ağırlık')}
              inputMode="decimal"
              value={draft.weight}
              onChange={(e) => set('weight', e.target.value)}
              error={touched ? errors.weight : undefined}
              hint={tx('Oransal: 2, ağırlığı 1 olan metriğin iki katı sayılır.')}
              className="tabular"
            />
            <SelectField
              label={tx('Departman')}
              value={draft.departmentId || '__all__'}
              onChange={(v) => set('departmentId', v === '__all__' ? '' : v)}
              options={[{ value: '__all__', label: tx('Tüm departmanlar') }, ...departments.map((d) => ({ value: d.id, label: d.path ?? d.name }))]}
              hint={tx('Seçerseniz yalnızca o departmanın çalışanları bu metrikle değerlendirilir.')}
            />
          </div>

          <div className="grid items-start gap-4 sm:grid-cols-2">
            <Switch
              checked={draft.isRequired}
              onChange={(v) => set('isRequired', v)}
              label={tx('Zorunlu metrik')}
              hint={tx('Puanlanmadan değerlendirme gönderilemez.')}
            />
            <TextField
              label={tx('Sıra')}
              inputMode="numeric"
              value={draft.sortOrder}
              onChange={(e) => set('sortOrder', e.target.value)}
              error={touched ? errors.sortOrder : undefined}
              hint={tx('Boş bırakırsanız kategorinin sonuna eklenir.')}
              className="tabular"
            />
          </div>
        </div>

        {/* ---------------------------- canlı önizleme ---------------------------- */}
        <aside className="flex flex-col gap-4 lg:border-l lg:border-border lg:pl-6">
          <div>
            <p className="text-[12px] font-semibold text-muted-foreground">{tx('Değerlendirmede böyle görünür')}</p>
            <div className="mt-2 rounded-lg border border-border bg-muted/30 p-3">
              <p className="text-[13px] font-medium">
                {draft.name.trim() || tx('Metrik adı')}
                {draft.isRequired && <span className="ml-1 text-destructive" aria-label="zorunlu">*</span>}
              </p>
              {draft.description.trim() && (
                <p className="mt-0.5 line-clamp-2 text-[11px] text-muted-foreground">{draft.description.trim()}</p>
              )}
              <div className="mt-2">
                <ScaleInput scale={draft.scale} range={scaleRange[draft.scale]} value={preview} onChange={setPreview} size="sm" />
              </div>
            </div>
          </div>

          <div>
            <p className="text-[12px] font-semibold text-muted-foreground">{tx('Kategorideki payı')}</p>
            <div className="mt-2 rounded-lg border border-border p-3">
              <p className="text-[13px] leading-snug">
                {tx('Bu ağırlıkla')}{' '}
                {shareView.deptName ? <>{tx('{0} çalışanlarında', [shareView.deptName])}{' '}</> : null}
                <span className="font-medium">{categoryLabels[draft.category]}</span>{' '}{tx('kategorisinin', [])}{' '}
                <motion.span
                  key={Math.round(shareView.share)}
                  initial={{ opacity: 0.3, y: 3 }}
                  animate={{ opacity: 1, y: 0 }}
                  className="tabular inline-block font-semibold"
                  style={{ color: categoryColor[draft.category] }}
                >
                  {formatShareOf(shareView.share)}
                </motion.span>{' '}{tx('olur.', [])}</p>
              <ShareBar className="mt-3" items={shareView.items} color={categoryColor[draft.category]} highlightId="__this__" height={10} />
              <p className="mt-2 text-[11px] text-muted-foreground">
                {shareView.count === 0
                  ? tx('Kategoride başka metrik yok; tek başına %100 sayılır.')
                  : tx('Kategoride {0} metrik daha var.', [shareView.count])}
              </p>
            </div>
          </div>
        </aside>
      </div>
    </Modal>
  )
}
