/**
 * Değerlendirme formu — `/panel/performans/degerlendirme/:reviewId`.
 *
 * Akış: POST /reviews ile oluşturulan boş taslak burada doldurulur.
 * - Her metrik kendi ölçeğinde girilir (yıldız / 1–10 / yüzde); sınırlar
 *   metriğin `range`'inden.
 * - Taslak sunucuya otomatik kaydedilir (`PUT /reviews/{id}/draft`, son
 *   düzenlemeden ~1,2 sn sonra; sekme gizlenince ve sayfadan çıkınca hemen).
 *   Bu uç zorunluluk aramaz, yalnızca değer aralığını kontrol eder.
 * - Sunucuya ulaşmamış değişiklikler tarayıcıda yedeklenir; kayıt başarılı
 *   olunca yedek silinir. Açılışta yedek bulunursa kullanıcıya sorulur.
 * - Zorunlu metrikler işaretli; eksikse backend 400 + `missing[]` döner ve
 *   o metrikler vurgulanıp ilkine kaydırılır.
 * - Gönderildikten sonra form kilitlenir; kapanmış dönemde gönderim 400 döner.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import {
  ArrowLeft,
  Check,
  CheckCircle2,
  CircleDashed,
  Cloud,
  CloudOff,
  CloudUpload,
  Gauge,
  History,
  LoaderCircle,
  Lock,
  MessageSquarePlus,
  RefreshCw,
  Send,
  TriangleAlert,
} from 'lucide-react'
import { ApiError } from '@/api/client'
import {
  CATEGORIES,
  categoryColor,
  categoryLabels,
  categoryWeightKey,
  formatScore,
  missingMetricIds,
  normalizeToHundred,
  reviewTypeLabels,
  useCycle,
  useMyEmployeeId,
  useReview,
  useSaveReviewDraft,
  useScoringConfig,
  useSubmitReview,
  type Metric,
} from '@/api/performance'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { Modal } from '@/components/ui/Modal'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorState, InfoNote } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { Chip, errorText } from '../components/controls'
import { PersonAvatar } from '../components/people'
import { ScaleInput } from '../components/ScaleInput'
import { usePeople } from '../hooks'
import {
  clearDraft,
  hasUnsendable,
  readDraft,
  sameContent,
  toReviewInput,
  useApplicableMetrics,
  writeDraft,
  type LocalDraft,
} from './reviewSupport'
import { tx, appLocale } from '@/lib/i18n'

type Form = Omit<LocalDraft, 'savedAt'>
type Sync = 'idle' | 'pending' | 'saving' | 'saved' | 'error'

/** Son düzenlemeden sonra sunucuya yazmadan önce beklenen süre. */
const DRAFT_DEBOUNCE_MS = 1200

export function ReviewFormPage() {
  const { reviewId = '' } = useParams()
  const toast = useToast()
  const review = useReview(reviewId)
  const me = useMyEmployeeId()
  const people = usePeople()
  const cycle = useCycle(review.data?.cycleId)
  const scoring = useScoringConfig()
  const applicable = useApplicableMetrics(review.data?.employeeId)
  const submit = useSubmitReview()
  const { mutateAsync: saveDraftAsync } = useSaveReviewDraft()

  const [form, setForm] = useState<Form | null>(null)
  const [restored, setRestored] = useState<string | null>(null)
  const [missing, setMissing] = useState<Set<string>>(new Set())
  const [openComments, setOpenComments] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const firstRender = useRef(true)

  const [sync, setSync] = useState<Sync>('idle')
  const [syncError, setSyncError] = useState<string | null>(null)
  const [savedAt, setSavedAt] = useState<string | null>(null)

  const r = review.data
  const locked = Boolean(r?.isSubmitted)
  const isReviewer = Boolean(r && me.employeeId && r.reviewerEmployeeId === me.employeeId)
  const readOnly = locked || !isReviewer || cycle.data?.status === 'Closed'

  const metrics = applicable.metrics
  const metricIds = useMemo(() => new Set(metrics.map((m) => m.id)), [metrics])

  /* ---- ilk yükleme: sunucudaki taslak, varsa üstüne ulaşmamış yerel değişiklik ---- */
  useEffect(() => {
    if (!r || form) return
    const base: Form = {
      scores: Object.fromEntries(r.scores.map((s) => [s.metricId, { value: s.value, comment: s.comment ?? '' }])),
      strengths: r.strengths ?? '',
      improvements: r.improvements ?? '',
      comments: r.comments ?? '',
    }
    const local = !r.isSubmitted ? readDraft(r.id) : null
    if (local && !sameContent(local, base)) {
      setForm({ scores: { ...base.scores, ...local.scores }, strengths: local.strengths, improvements: local.improvements, comments: local.comments })
      setRestored(local.savedAt)
      setSync('pending')
    } else {
      if (local) clearDraft(r.id)
      setForm(base)
    }
    setOpenComments(new Set(r.scores.filter((s) => s.comment).map((s) => s.metricId)))
  }, [r, form])

  /* ---- sunucuya taslak kaydı ---- */
  // Her düzenleme `rev`i artırır; sunucuya ulaşan son sürüm `savedRev`.
  // Aynı anda tek istek uçar; arada gelen düzenleme bittiğinde yeniden yollanır.
  const rev = useRef(0)
  const savedRev = useRef(0)
  const busy = useRef(false)
  const again = useRef(false)
  const submitting = useRef(false)
  const latest = useRef({ form, metricIds, readOnly })
  useEffect(() => {
    latest.current = { form, metricIds, readOnly }
  })

  const pushDraft = useCallback(function push() {
    const { form: f, metricIds: ids, readOnly: ro } = latest.current
    if (!f || ro || submitting.current) return
    if (busy.current) {
      again.current = true
      return
    }
    busy.current = true
    const sent = rev.current
    setSync('saving')
    saveDraftAsync({ id: reviewId, input: toReviewInput(f, ids, 'draft') })
      .then(() => {
        savedRev.current = Math.max(savedRev.current, sent)
        setSavedAt(new Date().toISOString())
        if (rev.current !== sent) return
        if (!hasUnsendable(f)) clearDraft(reviewId)
        setSync('saved')
        setSyncError(null)
        setRestored(null)
      })
      .catch((e: unknown) => {
        if (rev.current !== sent) return
        setSync('error')
        setSyncError(errorText(e))
      })
      .finally(() => {
        busy.current = false
        if (again.current) {
          again.current = false
          push()
        }
      })
  }, [reviewId, saveDraftAsync])

  // Düzenleme: yedeği hemen yaz, sunucuya kısa bir bekleyişten sonra.
  useEffect(() => {
    if (!form || readOnly) return
    if (firstRender.current) {
      firstRender.current = false
      return
    }
    rev.current += 1
    writeDraft(reviewId, form)
    setSync('pending')
    const t = window.setTimeout(pushDraft, DRAFT_DEBOUNCE_MS)
    return () => window.clearTimeout(t)
  }, [form, readOnly, reviewId, pushDraft])

  // Sekme gizlenince beklemeden kaydet (mobilde uygulama değiştirmek dahil).
  useEffect(() => {
    const onHide = () => {
      if (document.visibilityState === 'hidden' && rev.current !== savedRev.current) pushDraft()
    }
    document.addEventListener('visibilitychange', onHide)
    return () => document.removeEventListener('visibilitychange', onHide)
  }, [pushDraft])

  // Sayfadan çıkarken bekleyen değişikliği gönder; başarısız olursa yedek duruyor.
  useEffect(
    () => () => {
      const { form: f, metricIds: ids, readOnly: ro } = latest.current
      if (!f || ro || submitting.current || rev.current === savedRev.current) return
      saveDraftAsync({ id: reviewId, input: toReviewInput(f, ids, 'draft') })
        .then(() => {
          if (!hasUnsendable(f)) clearDraft(reviewId)
        })
        .catch(() => {})
    },
    [reviewId, saveDraftAsync],
  )

  const byCat = CATEGORIES.map((c) => ({ category: c, items: metrics.filter((m) => m.category === c).sort((a, b) => a.sortOrder - b.sortOrder) })).filter((g) => g.items.length)
  const filled = metrics.filter((m) => form?.scores[m.id]?.value !== null && form?.scores[m.id]?.value !== undefined)
  const required = metrics.filter((m) => m.isRequired)
  const requiredFilled = required.filter((m) => filled.includes(m))

  /* ---- bu forma göre tahmini metrik ortalaması ---- */
  const estimate = useMemo(() => {
    if (!form) return null
    let sum = 0
    let wsum = 0
    for (const c of CATEGORIES) {
      const cw = scoring.data ? (scoring.data[categoryWeightKey[c]] as number) : 1
      if (!cw) continue
      const scored = metrics.filter((m) => m.category === c && form.scores[m.id]?.value !== null && form.scores[m.id]?.value !== undefined)
      const mw = scored.reduce((a, m) => a + m.weight, 0)
      if (!mw) continue
      const catScore = scored.reduce((a, m) => a + normalizeToHundred(m.range, form.scores[m.id].value as number) * m.weight, 0) / mw
      sum += catScore * cw
      wsum += cw
    }
    return wsum ? sum / wsum : null
  }, [form, metrics, scoring.data])

  const setScore = (id: string, value: number | null) => {
    setForm((f) => (f ? { ...f, scores: { ...f.scores, [id]: { value, comment: f.scores[id]?.comment ?? '' } } } : f))
    if (value !== null && missing.has(id)) setMissing((s) => new Set([...s].filter((x) => x !== id)))
  }
  const setComment = (id: string, comment: string) =>
    setForm((f) => (f ? { ...f, scores: { ...f.scores, [id]: { value: f.scores[id]?.value ?? null, comment } } } : f))

  const focusFirst = (ids: Iterable<string>) => {
    const first = metrics.find((m) => [...ids].includes(m.id))
    if (first) document.getElementById(`metric-${first.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }

  const trySubmit = () => {
    const miss = required.filter((m) => !filled.includes(m)).map((m) => m.id)
    if (miss.length) {
      setMissing(new Set(miss))
      toast.stop(tx('{0} zorunlu metrik puanlanmadı.', [miss.length]))
      focusFirst(miss)
      return
    }
    setSubmitError(null)
    setConfirmOpen(true)
  }

  const doSubmit = () => {
    if (!form) return
    // Bekleyen taslak isteği gönderimle yarışmasın.
    submitting.current = true
    submit.mutate(
      { id: reviewId, input: toReviewInput(form, metricIds, 'submit') },
      {
        onSuccess: () => {
          savedRev.current = rev.current
          clearDraft(reviewId)
          setConfirmOpen(false)
          setRestored(null)
          toast.ok(tx('Değerlendirme gönderildi ve kilitlendi.'))
          window.scrollTo({ top: 0, behavior: 'smooth' })
        },
        onError: (e) => {
          submitting.current = false
          const ids = e instanceof ApiError ? missingMetricIds(e.detail) : []
          if (ids.length) {
            setMissing(new Set(ids))
            setConfirmOpen(false)
            toast.stop(errorText(e))
            focusFirst(ids)
          } else setSubmitError(errorText(e))
          // Gönderilemeyen içerik taslak olarak sunucuda kalsın.
          if (rev.current !== savedRev.current) pushDraft()
        },
      },
    )
  }

  /* ------------------------------------ durumlar ------------------------------------ */

  if (review.isError) {
    return (
      <div className="mx-auto w-full max-w-5xl">
        <BackLink />
        <Panel>
          {review.error instanceof ApiError && (review.error.status === 404 || review.error.status === 400) ? (
            <ErrorState title={tx('Değerlendirme bulunamadı')} message={tx('Bağlantı hatalı olabilir ya da kayıt silinmiş.')} />
          ) : (
            <ErrorState title={tx('Değerlendirme açılamadı')} message={errorText(review.error)} onRetry={() => void review.refetch()} />
          )}
        </Panel>
      </div>
    )
  }

  // Dönem durumu bilinmeden form açılmaz; kapanmış dönemde bir an düzenlenebilir görünmesin.
  if (review.isPending || !form || applicable.isPending || cycle.isPending) {
    return (
      <div className="mx-auto w-full max-w-6xl" aria-busy="true">
        <BackLink />
        <Skeleton className="h-24 w-full rounded-2xl" />
        <div className="mt-5 grid gap-5 xl:grid-cols-[minmax(0,1fr)_300px]">
          <div className="flex flex-col gap-4">
            {[0, 1, 2].map((i) => (
              <Skeleton key={i} className="h-48 rounded-xl" />
            ))}
          </div>
          <Skeleton className="h-72 rounded-xl" />
        </div>
      </div>
    )
  }

  const subjectName = r!.type === 'Self' ? tx('Öz değerlendirme') : people.nameOf(r!.employeeId)
  const pct = metrics.length ? filled.length / metrics.length : 0

  return (
    <div className="mx-auto w-full max-w-6xl">
      <BackLink />

      {/* --------------------------------- başlık --------------------------------- */}
      <div className="sticky top-14 z-20 -mx-4 mb-5 border-b border-border bg-background/85 px-4 py-3 backdrop-blur sm:mx-0 sm:rounded-2xl sm:border sm:bg-card/90 sm:px-5 sm:shadow-sm">
        <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
          <div className="flex min-w-0 items-center gap-3">
            <PersonAvatar id={r!.employeeId} name={people.nameOf(r!.employeeId)} size="lg" />
            <div className="min-w-0">
              <p className="flex flex-wrap items-center gap-1.5 text-[16px] font-semibold">
                {subjectName}
                <Chip tone="primary">{reviewTypeLabels[r!.type]}</Chip>
                {locked && (
                  <Chip tone="success">
                    <Lock className="size-3" aria-hidden />
                    {tx('Gönderildi')}
                  </Chip>
                )}
              </p>
              <p className="truncate text-[12px] text-muted-foreground">
                {r!.type === 'Self' ? people.nameOf(r!.employeeId) : tx('Değerlendiren: {0}', [people.nameOf(r!.reviewerEmployeeId)])} · {cycle.data?.name ?? '…'}
              </p>
            </div>
          </div>
          <div className="flex items-center gap-3 sm:gap-4">
            <Completion pct={pct} filled={filled.length} total={metrics.length} />
            <div className="min-w-0 flex-1 text-[12px] md:flex-none">
              <p className={cn('tabular hidden font-medium sm:block', requiredFilled.length < required.length ? 'text-[hsl(var(--warning))]' : 'text-[hsl(var(--success))]')}>{tx('Zorunlu {0}/{1}', [requiredFilled.length, required.length])}
              </p>
              {!readOnly && <SyncStatus sync={sync} savedAt={savedAt} error={syncError} onRetry={pushDraft} />}
            </div>
            {!readOnly && (
              <Button onClick={trySubmit} disabled={submit.isPending}>
                <Send aria-hidden />
                {tx('Gönder')}
              </Button>
            )}
          </div>
        </div>
      </div>

      <div className="mb-4 flex flex-col gap-3">
        <AnimatePresence>
          {restored && !readOnly && (
            <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden">
              <div className="flex flex-col gap-2 rounded-xl border border-primary/25 bg-primary/5 px-4 py-3 text-[13px] sm:flex-row sm:items-center sm:justify-between">
                <span className="flex items-start gap-2">
                  <History className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden />
                  <span>{tx('Sunucuya ulaşmamış değişiklikleriniz bu tarayıcıdan geri yüklendi ({0}).', [formatDateTime(restored)])}<span className="block text-[12px] text-muted-foreground">{tx('Kaydederseniz sunucudaki taslağın yerini alır.')}</span>
                  </span>
                </span>
                <div className="flex shrink-0 items-center gap-1.5 self-end sm:self-auto">
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={sync === 'saving'}
                    onClick={() => {
                      clearDraft(reviewId)
                      setRestored(null)
                      setSync('idle')
                      firstRender.current = true
                      setForm(null)
                    }}
                  >
                    {tx('Yok say')}
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={sync === 'saving'}
                    onClick={() => {
                      rev.current += 1
                      pushDraft()
                    }}
                  >
                    <CloudUpload aria-hidden />
                    {tx('Sunucuya kaydet')}
                  </Button>
                </div>
              </div>
            </motion.div>
          )}
        </AnimatePresence>
        <AnimatePresence>
          {sync === 'error' && !readOnly && (
            <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden">
              <div role="alert" className="flex flex-col gap-2 rounded-xl border border-destructive/30 bg-destructive/5 px-4 py-3 text-[13px] sm:flex-row sm:items-center sm:justify-between">
                <span className="flex items-start gap-2">
                  <CloudOff className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
                  <span>{tx('Taslak sunucuya kaydedilemedi{0}', [syncError ? `: ${syncError}` : '.'])}
                    <span className="block text-[12px] text-muted-foreground">{tx('Değişiklikleriniz kaybolmadı; bu tarayıcıda saklanıyor.')}</span>
                  </span>
                </span>
                <Button size="sm" variant="outline" className="shrink-0 self-end sm:self-auto" onClick={pushDraft}>
                  <RefreshCw aria-hidden />
                  {tx('Tekrar dene')}
                </Button>
              </div>
            </motion.div>
          )}
        </AnimatePresence>
        {locked && (
          <InfoNote>{tx('Bu değerlendirme {0} tarihinde gönderildi ve kilitlendi; değiştirilemez. Puanın nasıl hesaplandığını', [formatDateTime(r!.submittedAt)])}{' '}
            <Link className="font-medium text-primary hover:underline" to={`/panel/performans/puan?calisan=${r!.employeeId}&donem=${r!.cycleId}`}>
              {tx('puan dökümünde')}
            </Link>{' '}{tx('görebilirsiniz.', [])}</InfoNote>
        )}
        {!locked && !isReviewer && <InfoNote>{tx('Bu taslak {0} tarafından doldurulacak; yalnızca görüntülüyorsunuz.', [people.nameOf(r!.reviewerEmployeeId)])}</InfoNote>}
        {!locked && cycle.data?.status === 'Closed' && <InfoNote>{tx('Dönem kapandığı için bu değerlendirme artık gönderilemez.')}</InfoNote>}
        {!applicable.departmentKnown && <InfoNote>{tx('Çalışanın departmanı belirlenemediği için yalnızca genel metrikler gösteriliyor. Eksik zorunlu metrik olursa gönderirken belirtilir.')}</InfoNote>}
      </div>

      <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_300px]">
        <div className="flex min-w-0 flex-col gap-5">
          {metrics.length === 0 && (
            <Panel className="p-6 text-center text-[13px] text-muted-foreground">{tx('Bu çalışana uygulanan etkin metrik yok. Metrikler ekranından tanımlayın.')}</Panel>
          )}

          {byCat.map((g, gi) => (
            <motion.section
              key={g.category}
              id={`cat-${g.category}`}
              initial={{ opacity: 0, y: 14 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.5, ease: EASE, delay: gi * 0.06 }}
              className="scroll-mt-40"
            >
              <Panel>
                <div className="flex items-center gap-2.5 border-b border-border px-5 py-3.5">
                  <span className="size-2.5 rounded-full" style={{ background: categoryColor[g.category] }} />
                  <h2 className="text-[15px] font-semibold">{categoryLabels[g.category]}</h2>
                  <span className="text-[12px] text-muted-foreground">
                    {g.items.filter((m) => filled.includes(m)).length}/{g.items.length}
                  </span>
                </div>
                <ul className="divide-y divide-border">
                  {g.items.map((m) => (
                    <MetricField
                      key={m.id}
                      metric={m}
                      value={form.scores[m.id]?.value ?? null}
                      comment={form.scores[m.id]?.comment ?? ''}
                      commentOpen={openComments.has(m.id) || Boolean(form.scores[m.id]?.comment)}
                      missing={missing.has(m.id)}
                      readOnly={readOnly}
                      onValue={(v) => setScore(m.id, v)}
                      onComment={(c) => setComment(m.id, c)}
                      onOpenComment={() => setOpenComments((s) => new Set([...s, m.id]))}
                    />
                  ))}
                </ul>
              </Panel>
            </motion.section>
          ))}

          <Panel className="p-5">
            <h2 className="mb-4 text-[15px] font-semibold">{tx('Yazılı değerlendirme')}</h2>
            <div className="grid gap-4 md:grid-cols-2">
              <TextAreaField label={tx('Güçlü yönler')} value={form.strengths} onChange={(e) => setForm({ ...form, strengths: e.target.value })} disabled={readOnly} placeholder={tx('Neyi iyi yapıyor? Somut örnek verin.')} />
              <TextAreaField label={tx('Geliştirilmesi gerekenler')} value={form.improvements} onChange={(e) => setForm({ ...form, improvements: e.target.value })} disabled={readOnly} placeholder={tx('Neyi farklı yapabilir? Nasıl destek olunabilir?')} />
            </div>
            <div className="mt-4">
              <TextAreaField label={tx('Genel yorum')} value={form.comments} onChange={(e) => setForm({ ...form, comments: e.target.value })} disabled={readOnly} placeholder={tx('Opsiyonel')} />
            </div>
          </Panel>
        </div>

        {/* ------------------------------ yan sütun ------------------------------ */}
        <aside className="flex flex-col gap-4 xl:sticky xl:top-40">
          <Panel className="p-4">
            <p className="mb-3 text-[12px] font-semibold text-muted-foreground">{tx('İlerleme')}</p>
            <ul className="flex flex-col gap-1">
              {byCat.map((g) => {
                const done = g.items.filter((m) => filled.includes(m)).length
                const hasMissing = g.items.some((m) => missing.has(m.id))
                return (
                  <li key={g.category}>
                    <a href={`#cat-${g.category}`} className="flex items-center gap-2 rounded-md px-2 py-1.5 text-[13px] hover:bg-muted/60">
                      {done === g.items.length ? (
                        <CheckCircle2 className="size-4 text-[hsl(var(--success))]" aria-hidden />
                      ) : hasMissing ? (
                        <TriangleAlert className="size-4 text-destructive" aria-hidden />
                      ) : (
                        <CircleDashed className="size-4 text-muted-foreground" aria-hidden />
                      )}
                      <span className="flex-1">{categoryLabels[g.category]}</span>
                      <span className="tabular text-[11px] text-muted-foreground">
                        {done}/{g.items.length}
                      </span>
                    </a>
                  </li>
                )
              })}
            </ul>
          </Panel>
          <Panel className="p-4">
            <p className="flex items-center gap-1.5 text-[12px] font-semibold text-muted-foreground">
              <Gauge className="size-3.5" aria-hidden />
              {tx('Bu formun metrik ortalaması')}
            </p>
            <p className="mt-1 text-[28px] leading-none font-semibold tracking-tight">
              {estimate === null ? '—' : <AnimatedNumber value={estimate} format={(v) => formatScore(v)} />}
            </p>
            <p className="mt-2 text-[11px] leading-relaxed text-muted-foreground">
              {tx('Yalnızca bu formdaki puanlardan, kategori ağırlıklarıyla hesaplanan tahmindir. Nihai puan diğer değerlendirmeler, değerlendirici katsayıları ve hedeflerle birlikte oluşur.')}
            </p>
          </Panel>
          <Panel className="p-4 text-[12px] leading-relaxed text-muted-foreground">
            <p className="mb-1 font-semibold text-foreground">{tx('Ölçek rehberi')}</p>
            {tx('Yıldız ve 1–10 ölçekte ok tuşlarıyla değer değiştirebilir, Sil tuşuyla temizleyebilirsiniz. Seçili değere tekrar tıklamak da temizler.')}
          </Panel>
        </aside>
      </div>

      <Modal
        open={confirmOpen}
        onClose={() => !submit.isPending && setConfirmOpen(false)}
        title={tx('Değerlendirme gönderilsin mi?')}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirmOpen(false)} disabled={submit.isPending}>
              {tx('Kontrol edeyim')}
            </Button>
            <Button onClick={doSubmit} disabled={submit.isPending}>
              <Send aria-hidden />
              {submit.isPending ? tx('Gönderiliyor…') : tx('Gönder ve kilitle')}
            </Button>
          </>
        }
      >
        <ul className="flex flex-col gap-2 text-[13px]">
          <li className="flex items-center gap-2">
            <Check className="size-4 text-[hsl(var(--success))]" aria-hidden />
            {metrics.length}{' '}{tx('metrikten')}{' '}{filled.length}{' '}{tx('tanesi puanlandı')}
            {metrics.length - filled.length > 0 && <span className="text-muted-foreground">{tx('({0} opsiyonel metrik boş)', [metrics.length - filled.length])}</span>}
          </li>
          <li className="flex items-center gap-2">
            <Check className="size-4 text-[hsl(var(--success))]" aria-hidden />
            {tx('Zorunlu metriklerin tamamı puanlandı')}
          </li>
          <li className="flex items-center gap-2">
            {form.strengths.trim() || form.improvements.trim() ? <Check className="size-4 text-[hsl(var(--success))]" aria-hidden /> : <CircleDashed className="size-4 text-muted-foreground" aria-hidden />}
            {form.strengths.trim() || form.improvements.trim() ? tx('Yazılı değerlendirme eklendi') : tx('Yazılı değerlendirme boş (opsiyonel)')}
          </li>
        </ul>
        <p className="mt-4 flex items-start gap-2 rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2.5 text-[13px]">
          <Lock className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
          {tx('Gönderdikten sonra değerlendirme kilitlenir ve değiştirilemez.')}
        </p>
        {submitError && (
          <p role="alert" className="mt-3 text-[13px] text-destructive">
            {submitError}
          </p>
        )}
      </Modal>
    </div>
  )
}

function BackLink() {
  return (
    <Link to="/panel/performans/degerlendirme" className="mb-4 inline-flex items-center gap-1.5 text-[13px] text-muted-foreground hover:text-foreground">
      <ArrowLeft className="size-4" aria-hidden />
      {tx('Değerlendirmeler')}
    </Link>
  )
}

function SyncStatus({ sync, savedAt, error, onRetry }: { sync: Sync; savedAt: string | null; error: string | null; onRetry: () => void }) {
  const time = savedAt ? new Date(savedAt).toLocaleTimeString(appLocale, { hour: '2-digit', minute: '2-digit' }) : null
  const at = time ? ` · ${time}` : ''
  // Dar ekranda kısa, genişte uzun metin.
  const view: Record<Sync, { icon: typeof Cloud; short: string; long: string; tone: string; spin?: boolean }> = {
    idle: { icon: Cloud, short: tx('Otomatik kayıt'), long: tx('Taslak otomatik kaydedilir'), tone: 'text-muted-foreground' },
    pending: { icon: Cloud, short: tx('Değişiklik var'), long: tx('Kaydedilmemiş değişiklik'), tone: 'text-muted-foreground' },
    saving: { icon: LoaderCircle, short: tx('Kaydediliyor…'), long: tx('Taslak kaydediliyor…'), tone: 'text-muted-foreground', spin: true },
    saved: { icon: Check, short: tx('Kaydedildi{0}', [at]), long: tx('Taslak kaydedildi{0}', [at]), tone: 'text-[hsl(var(--success))]' },
    error: { icon: CloudOff, short: tx('Kaydedilemedi'), long: tx('Taslak kaydedilemedi'), tone: 'text-destructive' },
  }
  const v = view[sync]
  const Icon = v.icon
  return (
    <div role="status" aria-live="polite" className="flex min-w-0 items-center gap-1" title={sync === 'error' && error ? error : undefined}>
      {/* Yalnızca giriş animasyonu: metin durumun gerisinde kalmasın. */}
      <motion.span
        key={sync === 'saved' ? `saved-${time}` : sync}
        initial={{ opacity: 0, y: 3 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.2, ease: EASE }}
        className={cn('tabular flex min-w-0 items-center gap-1', v.tone)}
      >
        <Icon className={cn('size-3.5 shrink-0', v.spin && 'animate-spin')} aria-hidden />
        <span className="truncate">
          <span className="sm:hidden">{v.short}</span>
          <span className="hidden sm:inline">{v.long}</span>
        </span>
      </motion.span>
      {sync === 'error' && (
        <button type="button" onClick={onRetry} aria-label={tx('Taslağı yeniden kaydet')} className="grid size-6 shrink-0 place-items-center rounded-md text-destructive hover:bg-destructive/10">
          <RefreshCw className="size-3.5" aria-hidden />
        </button>
      )}
    </div>
  )
}

function Completion({ pct, filled, total }: { pct: number; filled: number; total: number }) {
  const r = 18
  const c = 2 * Math.PI * r
  return (
    <div className="flex items-center gap-2">
      <div className="relative size-11">
        <svg viewBox="0 0 44 44" className="size-11 -rotate-90">
          <circle cx="22" cy="22" r={r} fill="none" stroke="hsl(var(--muted))" strokeWidth="4" />
          <motion.circle cx="22" cy="22" r={r} fill="none" stroke={pct >= 1 ? 'hsl(var(--success))' : 'hsl(var(--primary))'} strokeWidth="4" strokeLinecap="round" strokeDasharray={c} animate={{ strokeDashoffset: c * (1 - pct) }} transition={{ type: 'spring', stiffness: 120, damping: 20 }} initial={false} />
        </svg>
        <span className="tabular absolute inset-0 flex items-center justify-center text-[10px] font-semibold">%{Math.round(pct * 100)}</span>
      </div>
      <span className="tabular text-[12px] text-muted-foreground">
        {filled}/{total}
        <span className="block">{tx('puanlandı')}</span>
      </span>
    </div>
  )
}

function MetricField({
  metric: m,
  value,
  comment,
  commentOpen,
  missing,
  readOnly,
  onValue,
  onComment,
  onOpenComment,
}: {
  metric: Metric
  value: number | null
  comment: string
  commentOpen: boolean
  missing: boolean
  readOnly: boolean
  onValue: (v: number | null) => void
  onComment: (c: string) => void
  onOpenComment: () => void
}) {
  const labelId = `metric-label-${m.id}`
  return (
    <li id={`metric-${m.id}`} className={cn('relative scroll-mt-40 px-5 py-4 transition-colors', missing && 'bg-destructive/5')}>
      <AnimatePresence>{missing && <motion.span aria-hidden initial={{ scaleY: 0 }} animate={{ scaleY: 1 }} exit={{ scaleY: 0 }} className="absolute inset-y-0 left-0 w-1 origin-top bg-destructive" />}</AnimatePresence>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between lg:gap-6">
        <div className="min-w-0 lg:max-w-sm">
          <p id={labelId} className="flex flex-wrap items-center gap-1.5 text-[14px] font-medium">
            {m.name}
            {m.isRequired && <Chip tone="danger">{tx('Zorunlu')}</Chip>}
          </p>
          {m.description && <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground">{m.description}</p>}
          <AnimatePresence>
            {missing && (
              <motion.p initial={{ opacity: 0, y: -4 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }} className="mt-1 text-[12px] font-medium text-destructive">
                {tx('Zorunlu metrik — göndermeden önce puanlayın.')}
              </motion.p>
            )}
          </AnimatePresence>
        </div>
        <div className="shrink-0">
          <ScaleInput scale={m.scale} range={m.range} value={value} onChange={onValue} disabled={readOnly} invalid={missing} labelledBy={labelId} showNormalized />
        </div>
      </div>
      <AnimatePresence initial={false}>
        {commentOpen ? (
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden">
            <textarea
              value={comment}
              onChange={(e) => onComment(e.target.value)}
              disabled={readOnly}
              placeholder={tx('Bu metrik için yorum (opsiyonel)')}
              aria-label={tx('{0} yorumu', [m.name])}
              className="mt-3 min-h-16 w-full rounded-md border border-input bg-background px-3 py-2 text-[13px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-70"
            />
          </motion.div>
        ) : (
          !readOnly && (
            <button type="button" onClick={onOpenComment} className="mt-2 inline-flex items-center gap-1 text-[12px] font-medium text-muted-foreground hover:text-primary">
              <MessageSquarePlus className="size-3.5" aria-hidden />
              {tx('Yorum ekle')}
            </button>
          )
        )}
      </AnimatePresence>
    </li>
  )
}
