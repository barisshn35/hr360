/**
 * Geri bildirim yazma paneli.
 *
 * - Geri bildirim anonim DEĞİL: formun en üstünde, kimin adına yazıldığıyla
 *   birlikte açıkça söylenir.
 * - "Yapıcı eleştiri" seçildiği anda gerekçe alanı zorunlu olur (backend
 *   de gerekçesiz 400 döner).
 * - Görünürlük kapatılırsa net uyarı: "Bu not çalışana gösterilmez,
 *   yalnızca yöneticiler görür." (Yalnızca yöneticilere açık.)
 * - Sağda alıcının göreceği kart canlı önizlenir.
 */

import { useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { EyeOff, Send, TriangleAlert, UserRoundCheck, X } from 'lucide-react'
import {
  REASONS,
  SENTIMENTS,
  reasonLabels,
  sentimentLabels,
  useCreateFeedback,
  useMetrics,
  type FeedbackReason,
  type FeedbackSentiment,
} from '@/api/performance'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Button } from '@/components/ui/button'
import { SelectField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { Switch, errorText } from '../components/controls'
import { PersonAvatar, PersonPicker } from '../components/people'
import { usePeople } from '../hooks'
import { FeedbackCard, REASON_ICON, SENTIMENT_COLOR } from './FeedbackCard'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'

const SENTIMENT_HINT: Record<FeedbackSentiment, string> = {
  Positive: tx('Takdir, teşekkür, iyi giden bir şey'),
  Neutral: tx('Bilgi, gözlem, yönlendirme'),
  Constructive: tx('Geliştirilecek bir alan — gerekçe zorunlu'),
}

export function FeedbackComposer({
  open,
  onClose,
  me,
  manager,
  presetTo,
}: {
  open: boolean
  onClose: () => void
  me: string
  manager: boolean
  presetTo?: string | null
}) {
  const toast = useToast()
  const people = usePeople()
  const metrics = useMetrics()
  const create = useCreateFeedback()

  const [to, setTo] = useState<string | null>(presetTo ?? null)
  const [picking, setPicking] = useState(!presetTo)
  const [reason, setReason] = useState<FeedbackReason>('Recognition')
  const [sentiment, setSentiment] = useState<FeedbackSentiment>('Positive')
  const [reasonDetail, setReasonDetail] = useState('')
  const [body, setBody] = useState('')
  const [metricId, setMetricId] = useState('__none__')
  const [visible, setVisible] = useState(true)
  const [touched, setTouched] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const needsDetail = sentiment === 'Constructive'
  const errs = {
    to: !to ? 'Geri bildirimin kime yazıldığını seçin.' : undefined,
    detail: needsDetail && !reasonDetail.trim() ? tx('Yapıcı eleştiride gerekçe zorunlu: somut bir olay ya da gözlem yazın.') : undefined,
    body: body.trim().length < 10 ? tx('En az 10 karakter yazın.') : undefined,
  }
  const invalid = Object.values(errs).some(Boolean)
  const metric = metrics.data?.find((m) => m.id === metricId)

  const reset = () => {
    setTo(presetTo ?? null)
    setPicking(!presetTo)
    setReason('Recognition')
    setSentiment('Positive')
    setReasonDetail('')
    setBody('')
    setMetricId('__none__')
    setVisible(true)
    setTouched(false)
    setError(null)
  }

  const submit = () => {
    setTouched(true)
    if (invalid || !to) return
    create.mutate(
      {
        toEmployeeId: to,
        reason,
        sentiment,
        body: body.trim(),
        visibleToEmployee: manager ? visible : true,
        ...(reasonDetail.trim() ? { reasonDetail: reasonDetail.trim() } : {}),
        ...(metricId !== '__none__' ? { metricId } : {}),
      },
      {
        onSuccess: () => {
          toast.ok(tx('Geri bildiriminiz {0} kişisine gönderildi.', [people.nameOf(to)]))
          reset()
          onClose()
        },
        onError: (e) => setError(errorText(e)),
      },
    )
  }

  const previewFeedback = {
    id: 'preview',
    from: { employeeId: me, name: people.nameOf(me) },
    to: { employeeId: to ?? 'x', name: to ? people.nameOf(to) : 'Alıcı' },
    reason,
    reasonDetail: reasonDetail.trim() || null,
    sentiment,
    body: body.trim(),
    metricId: metric?.id ?? null,
    metricName: metric?.name ?? null,
    visibleToEmployee: manager ? visible : true,
    isRead: false,
    readAt: null,
    createdAt: new Date().toISOString(),
  }

  return (
    <Sheet open={open} onOpenChange={(v) => !v && onClose()}>
      <SheetContent className="w-full gap-0 overflow-y-auto sm:max-w-4xl">
        <SheetHeader className="border-b border-border p-5">
          <SheetTitle className="text-[18px]">{tx('Geri bildirim yaz')}</SheetTitle>
          <SheetDescription className="text-[13px]">{tx('Somut, zamanında ve davranışa odaklı geri bildirim en çok işe yarayanıdır.')}</SheetDescription>
        </SheetHeader>

        <div className="grid gap-6 p-5 lg:grid-cols-[minmax(0,1fr)_320px]">
          <div className="flex flex-col gap-5">
            <div className="flex items-center gap-3 rounded-xl border border-primary/25 bg-primary/5 px-3.5 py-3">
              <PersonAvatar id={me} name={people.nameOf(me)} />
              <p className="text-[13px] leading-relaxed">
                <span className="font-semibold">{people.nameOf(me)}</span>{' '}{tx('olarak yazıyorsunuz.', [])}{' '}
                <span className="text-foreground/80">{tx('Geri bildirim anonim değildir; alıcı kimin yazdığını görür.')}</span>
              </p>
            </div>

            <AnimatePresence>
              {error && (
                <motion.p role="alert" initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="flex items-start gap-2 overflow-hidden rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-[13px] text-destructive">
                  <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
                  {error}
                </motion.p>
              )}
            </AnimatePresence>

            {/* Kime */}
            <div>
              <p className="mb-2 text-[13px] font-medium">
                {tx('Kime')}{' '}<span className="font-normal text-muted-foreground">{tx('zorunlu')}</span>
              </p>
              {to && !picking ? (
                <div className="flex items-center gap-3 rounded-lg border border-border p-2.5">
                  <PersonAvatar id={to} name={people.nameOf(to)} />
                  <span className="flex-1 text-[13px] font-medium">{people.nameOf(to)}</span>
                  <Button size="sm" variant="ghost" onClick={() => setPicking(true)}>
                    {tx('Değiştir')}
                  </Button>
                </div>
              ) : (
                <PersonPicker
                  people={people.list}
                  value={to}
                  onChange={(id) => {
                    setTo(id)
                    setPicking(false)
                  }}
                  exclude={[me]}
                  detailOf={people.titleOf}
                  height={200}
                />
              )}
              {touched && errs.to && <p className="mt-1 text-[12px] text-destructive">{errs.to}</p>}
            </div>

            {/* Neden */}
            <fieldset>
              <legend className="mb-2 text-[13px] font-medium">{tx('Neden')}</legend>
              <div className="grid grid-cols-2 gap-1.5 sm:grid-cols-4">
                {REASONS.map((r) => {
                  const Icon = REASON_ICON[r]
                  const on = reason === r
                  return (
                    <button
                      key={r}
                      type="button"
                      role="radio"
                      aria-checked={on}
                      onClick={() => setReason(r)}
                      className={cn('relative flex items-center gap-2 rounded-lg border px-2.5 py-2 text-left text-[12px] font-medium transition-colors', on ? 'border-transparent' : 'border-border hover:border-primary/30')}
                    >
                      {on && <motion.span layoutId="fb-reason" className="absolute inset-0 rounded-lg border-2 border-primary bg-primary/5" transition={{ type: 'spring', stiffness: 500, damping: 36 }} />}
                      <Icon className={cn('relative size-4 shrink-0', on ? 'text-primary' : 'text-muted-foreground')} aria-hidden />
                      <span className="relative">{reasonLabels[r]}</span>
                    </button>
                  )
                })}
              </div>
            </fieldset>

            {/* Ton */}
            <fieldset>
              <legend className="mb-2 text-[13px] font-medium">{tx('Ton')}</legend>
              <div className="grid gap-2 sm:grid-cols-3">
                {SENTIMENTS.map((s) => {
                  const on = sentiment === s
                  return (
                    <button
                      key={s}
                      type="button"
                      role="radio"
                      aria-checked={on}
                      onClick={() => setSentiment(s)}
                      className={cn('relative rounded-lg border p-2.5 text-left transition-colors', on ? 'border-transparent' : 'border-border hover:border-primary/30')}
                    >
                      {on && (
                        <motion.span
                          layoutId="fb-sentiment"
                          className="absolute inset-0 rounded-lg border-2"
                          style={{ borderColor: SENTIMENT_COLOR[s], background: `color-mix(in oklab, ${SENTIMENT_COLOR[s]} 8%, transparent)` }}
                          transition={{ type: 'spring', stiffness: 500, damping: 36 }}
                        />
                      )}
                      <span className="relative flex items-center gap-1.5 text-[13px] font-medium">
                        <span className="size-2 rounded-full" style={{ background: SENTIMENT_COLOR[s] }} />
                        {sentimentLabels[s]}
                      </span>
                      <span className="relative mt-0.5 block text-[11px] text-muted-foreground">{SENTIMENT_HINT[s]}</span>
                    </button>
                  )
                })}
              </div>
            </fieldset>

            {/* Gerekçe */}
            <div>
              <label htmlFor="fb-detail" className="mb-1.5 flex items-center gap-2 text-[13px] font-medium">
                {tx('Gerekçe')}
                <AnimatePresence mode="wait" initial={false}>
                  <motion.span
                    key={needsDetail ? 'req' : 'opt'}
                    initial={{ opacity: 0, y: 4 }}
                    animate={{ opacity: 1, y: 0 }}
                    exit={{ opacity: 0, y: -4 }}
                    className={cn('rounded px-1.5 py-0.5 text-[11px] font-medium', needsDetail ? 'bg-[hsl(var(--warning))]/15 text-[hsl(var(--warning))]' : 'font-normal text-muted-foreground')}
                  >
                    {needsDetail ? tx('zorunlu — yapıcı eleştiri') : 'opsiyonel'}
                  </motion.span>
                </AnimatePresence>
              </label>
              <textarea
                id="fb-detail"
                value={reasonDetail}
                onChange={(e) => setReasonDetail(e.target.value)}
                placeholder={tx('Somut olay ya da gözlem: ne oldu, ne zaman? (ör. Sprint 34 ve 35’te tahminler %40 saptı.)')}
                aria-invalid={touched && Boolean(errs.detail)}
                className={cn(
                  'min-h-16 w-full rounded-md border bg-background px-3 py-2 text-[13px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                  needsDetail ? 'border-[hsl(var(--warning))]/50' : 'border-input',
                  touched && errs.detail && 'border-destructive',
                )}
              />
              {touched && errs.detail && <p className="mt-1 text-[12px] text-destructive">{errs.detail}</p>}
              <PiiHint text={reasonDetail} className="mt-1.5" />
            </div>

            {/* Metin */}
            <div>
              <label htmlFor="fb-body" className="mb-1.5 flex items-center justify-between text-[13px] font-medium">
                <span>
                  {tx('Metin')}{' '}<span className="font-normal text-muted-foreground">{tx('zorunlu')}</span>
                </span>
                <span className={cn('tabular text-[11px] font-normal', body.trim().length < 10 ? 'text-muted-foreground' : 'text-[hsl(var(--success))]')}>{tx('{0} karakter', [body.trim().length])}</span>
              </label>
              <textarea
                id="fb-body"
                value={body}
                onChange={(e) => setBody(e.target.value)}
                placeholder={tx('Ne gördünüz, etkisi ne oldu, bundan sonrası için öneriniz ne?')}
                className={cn('min-h-28 w-full rounded-md border border-input bg-background px-3 py-2 text-[13px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50', touched && errs.body && 'border-destructive')}
              />
              {touched && errs.body && <p className="mt-1 text-[12px] text-destructive">{errs.body}</p>}
              <PiiHint text={body} className="mt-1.5" />
            </div>

            <SelectField
              label={tx('İlgili metrik')}
              value={metricId}
              onChange={setMetricId}
              options={[{ value: '__none__', label: tx('Metrik yok') }, ...(metrics.data ?? []).filter((m) => m.isActive).map((m) => ({ value: m.id, label: m.name }))]}
              hint={tx('Opsiyonel. Seçerseniz geri bildirim o metriğe bağlanır.')}
            />

            {manager && (
              <div className="flex flex-col gap-3">
                <Switch
                  checked={visible}
                  onChange={setVisible}
                  label={tx('Çalışan görebilsin')}
                  hint={visible ? tx('Alıcı bu geri bildirimi kendi ekranında görür.') : undefined}
                />
                <AnimatePresence>
                  {!visible && (
                    <motion.p
                      role="status"
                      initial={{ opacity: 0, height: 0 }}
                      animate={{ opacity: 1, height: 'auto' }}
                      exit={{ opacity: 0, height: 0 }}
                      transition={{ duration: 0.3, ease: EASE }}
                      className="flex items-start gap-2 overflow-hidden rounded-lg border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 px-3 py-2.5 text-[13px] font-medium"
                    >
                      <EyeOff className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
                      {tx('Bu not çalışana gösterilmez, yalnızca yöneticiler görür.')}
                    </motion.p>
                  )}
                </AnimatePresence>
              </div>
            )}
          </div>

          {/* Önizleme */}
          <aside className="lg:sticky lg:top-4 lg:self-start">
            <p className="mb-2 flex items-center gap-1.5 text-[12px] font-semibold text-muted-foreground">
              <UserRoundCheck className="size-3.5" aria-hidden />
              {manager && !visible ? tx('Yöneticiler böyle görecek') : tx('Alıcı böyle görecek')}
            </p>
            <div className="relative">
              <FeedbackCard feedback={previewFeedback} perspective={manager && !visible ? 'manager' : 'received'} nameOf={people.nameOf} preview />
              {manager && !visible && (
                <p className="mt-2 flex items-center gap-1.5 text-[11px] text-muted-foreground">
                  <X className="size-3" aria-hidden />
                  {tx('{0} bu kartı hiç görmez.', [to ? people.nameOf(to) : tx('Çalışan')])}</p>
              )}
            </div>
            <div className="mt-5 flex gap-2">
              <Button variant="outline" onClick={onClose} disabled={create.isPending} className="flex-1">
                {tx('Vazgeç')}
              </Button>
              <Button onClick={submit} disabled={create.isPending || (touched && invalid)} className="flex-1">
                <Send aria-hidden />
                {create.isPending ? tx('Gönderiliyor…') : tx('Gönder')}
              </Button>
            </div>
          </aside>
        </div>
      </SheetContent>
    </Sheet>
  )
}
