/**
 * İşe alım+ (Dalga 5c): kanban panosu (G13), mülakat planlama ve puan kartı (Y17),
 * teklif mektubu ve onayı (Y18), "Mülakatlarım" sayfası.
 */
import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, CalendarClock, ClipboardCheck, Copy, FileSignature, GripVertical, Plus, Printer, Star, Trash2, Video } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useDirectory } from '@/api/directory'
import {
  applicationStatusLabels,
  interviewTypeLabels,
  offerStatusLabels,
  recommendationLabels,
  recruitmentApi,
  type ApplicationStatus,
  type CriterionScore,
  type InterviewType,
  type NoteWarning,
  type Offer,
  type OfferInput,
  type OfferStatus,
  type PipelineCard,
  type Recommendation,
  type ScheduledInterview,
  type ScorecardCriterion,
} from '@/api/recruitment'
import { formatDate, formatDateTime, formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'
import { errMsg, isoDate, PersonSelect, useAction } from '@/features/shared/kit'
import { printDocuments } from '@/features/governance/DocTemplatesPage'
import { tx } from '@/lib/i18n'
import { ConsistencyView } from './RecruitmentW11'
import { OfferSignatureModal, SigningLinkModal } from './OfferSignatureHr'

const PRIVACY_HINT = tx('Özel nitelikli veri yazmayın: sağlık, hamilelik, din, siyasi görüş, sendika, etnik köken, engellilik, medeni hal, çocuk, yaş gibi bilgiler değerlendirmeye konu olamaz (KVKK m.6). Yalnızca işle ilgili yetkinlikleri not edin.')

export const offerTone: Record<OfferStatus, StatusTone> = {
  PendingApproval: 'warning', Approved: 'info', Rejected: 'danger', Sent: 'info',
  Accepted: 'success', Declined: 'neutral', Withdrawn: 'neutral', Expired: 'neutral',
}

function escapeHtml(s: string) {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)
}

/** Teklif mektubunu yazdır / PDF olarak kaydet. */
export function printLetter(title: string, text: string) {
  printDocuments(title, [{ employeeId: '', name: title, html: `<div style="white-space:pre-wrap">${escapeHtml(text)}</div>` }])
}

/** Özel nitelikli veri uyarıları (engellemez). */
export function NoteWarnings({ warnings }: { warnings: NoteWarning[] }) {
  if (!warnings.length) return null
  return (
    <div role="alert" className="space-y-1 rounded-xl border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-[12.5px]">
      <p className="flex items-center gap-1.5 font-medium"><AlertTriangle className="size-4 text-amber-600" />{tx('Özel nitelikli veri yazmayın')}</p>
      <ul className="list-disc pl-5 text-muted-foreground">
        {warnings.map((w) => <li key={w.category}>{w.message}</li>)}
      </ul>
    </div>
  )
}

/** Yazarken (600 ms gecikmeli) sunucu tarafı not denetimi. */
function useNoteWarnings(text: string) {
  const [warnings, setWarnings] = useState<NoteWarning[]>([])
  useEffect(() => {
    if (text.trim().length < 3) { setWarnings([]); return }
    const ctrl = new AbortController()
    const t = setTimeout(() => {
      recruitmentApi.notesCheck(text, ctrl.signal).then((r) => setWarnings(r.warnings)).catch(() => undefined)
    }, 600)
    return () => { clearTimeout(t); ctrl.abort() }
  }, [text])
  return warnings
}

/* ================================================================ kanban (G13) */

const STAGE_TONE: Partial<Record<ApplicationStatus, string>> = {
  Hired: 'border-emerald-500/40', Rejected: 'border-destructive/30', Withdrawn: 'border-border',
}

export function PipelineBoard({
  postingId,
  canManage,
  canOffer,
  onSchedule,
  onOffer,
}: {
  postingId: string
  canManage: boolean
  canOffer: boolean
  onSchedule: (card: PipelineCard) => void
  onOffer: (card: PipelineCard) => void
}) {
  const q = useQuery({ queryKey: ['recruitment', 'pipeline', postingId], queryFn: ({ signal }) => recruitmentApi.pipeline(postingId, signal) })
  const [dragId, setDragId] = useState<string | null>(null)
  const [over, setOver] = useState<ApplicationStatus | null>(null)
  const move = useAction(({ id, status }: { id: string; status: ApplicationStatus }) => recruitmentApi.moveApplication(id, status), {
    success: (r) => tx('Aday "{0}" aşamasına taşındı', [applicationStatusLabels[r.status]]),
    invalidate: [['recruitment']],
  })

  if (q.isPending) return <RowsSkeleton rows={3} />
  if (q.isError) return <p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p>
  const { stages, cards } = q.data

  const drop = (status: ApplicationStatus) => {
    const card = cards.find((c) => c.id === dragId)
    setDragId(null)
    setOver(null)
    if (!card || card.status === status) return
    move.mutate({ id: card.id, status })
  }

  return (
    <div className="-mx-1 flex gap-3 overflow-x-auto px-1 pb-2" aria-label={tx('Başvuru panosu')}>
      {stages.map((s) => {
        const list = cards.filter((c) => c.status === s.key)
        return (
          <section
            key={s.key}
            aria-label={applicationStatusLabels[s.key]}
            onDragOver={(e) => { if (canManage && dragId) { e.preventDefault(); setOver(s.key) } }}
            onDragLeave={() => setOver((o) => (o === s.key ? null : o))}
            onDrop={(e) => { e.preventDefault(); drop(s.key) }}
            className={cn(
              'flex w-60 shrink-0 flex-col rounded-2xl border bg-card/40 p-2 transition-colors',
              STAGE_TONE[s.key] ?? 'border-border',
              over === s.key && 'border-primary bg-primary/5',
              s.terminal && 'opacity-90',
            )}
          >
            <header className="mb-2 flex items-center justify-between px-1.5 pt-0.5">
              <h3 className="text-[12.5px] font-semibold">{applicationStatusLabels[s.key]}</h3>
              <span className="tabular rounded-full bg-muted px-2 text-[11.5px] text-muted-foreground">{list.length}</span>
            </header>
            <ul className="flex min-h-16 flex-col gap-2">
              {list.map((c) => {
                const locked = s.terminal
                return (
                  <li
                    key={c.id}
                    draggable={canManage && !locked}
                    onDragStart={(e) => { e.dataTransfer.setData('text/plain', c.id); e.dataTransfer.effectAllowed = 'move'; setDragId(c.id) }}
                    onDragEnd={() => { setDragId(null); setOver(null) }}
                    className={cn(
                      'rounded-xl border border-border bg-background p-2.5 text-[12.5px] shadow-sm',
                      canManage && !locked && 'cursor-grab active:cursor-grabbing',
                      dragId === c.id && 'opacity-50',
                    )}
                  >
                    <div className="flex items-start gap-1.5">
                      {canManage && !locked && <GripVertical aria-hidden="true" className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />}
                      <div className="min-w-0 flex-1">
                        <p className="truncate font-medium">{c.candidateName ?? tx('Aday')}</p>
                        <p className="text-[11.5px] text-muted-foreground">
                          {formatDate(c.appliedAt)}{c.channel === 'Career' ? ` · ${tx('Kariyer sayfası')}` : ''}
                        </p>
                      </div>
                      {c.averageScore != null && (
                        <span title={tx('Ortalama puan kartı puanı')} className="tabular inline-flex items-center gap-0.5 rounded-full bg-amber-500/10 px-1.5 text-[11.5px] text-amber-700 dark:text-amber-400">
                          <Star className="size-3" />{c.averageScore.toFixed(2)}
                        </span>
                      )}
                      {c.averageHidden && (
                        <span title={tx('Kör değerlendirme: tüm görüşmeciler puan kartını gönderince ortalama görünür')} className="inline-flex items-center gap-0.5 rounded-full bg-muted px-1.5 text-[11.5px] text-muted-foreground">
                          <Star className="size-3" />{tx('Gizli')}
                        </span>
                      )}
                    </div>
                    {c.duplicateReason && (
                      <p className="mt-1.5 flex items-start gap-1 text-[11.5px] text-amber-700 dark:text-amber-400"><AlertTriangle className="mt-0.5 size-3 shrink-0" />{c.duplicateReason}</p>
                    )}
                    {c.nextInterviewAt && <p className="mt-1 text-[11.5px] text-muted-foreground"><CalendarClock className="mr-1 inline size-3" />{formatDateTime(c.nextInterviewAt)}</p>}
                    {c.offerStatus && <div className="mt-1.5"><StatusBadge tone={offerTone[c.offerStatus]}>{tx('Teklif: {0}', [offerStatusLabels[c.offerStatus]])}</StatusBadge></div>}
                    {canManage && !locked && (
                      <div className="mt-2 flex flex-wrap items-center gap-1">
                        <select
                          aria-label={tx('{0} adayını taşı', [c.candidateName ?? ''])}
                          value=""
                          onChange={(e) => e.target.value && move.mutate({ id: c.id, status: e.target.value as ApplicationStatus })}
                          className="h-7 rounded-lg border border-input bg-background px-1.5 text-[11.5px]"
                        >
                          <option value="">{tx('Taşı…')}</option>
                          {stages.filter((x) => x.key !== c.status).map((x) => <option key={x.key} value={x.key}>{applicationStatusLabels[x.key]}</option>)}
                        </select>
                        <Button size="sm" variant="ghost" className="h-7 px-2 text-[11.5px]" onClick={() => onSchedule(c)}>{tx('Mülakat')}</Button>
                        {canOffer && <Button size="sm" variant="ghost" className="h-7 px-2 text-[11.5px]" onClick={() => onOffer(c)}>{tx('Teklif')}</Button>}
                      </div>
                    )}
                  </li>
                )
              })}
            </ul>
          </section>
        )
      })}
    </div>
  )
}

/* ================================================================ planlama (Y17) */

export function ScheduleInterviewModal({ target, onClose }: { target: { applicationId: string; candidateName: string } | null; onClose: () => void }) {
  const toast = useToast()
  const dir = useDirectory()
  const [f, setF] = useState({ type: 'Technical' as InterviewType, at: '', duration: '60', location: '', url: '', notify: true })
  const [people, setPeople] = useState<string[]>([])
  const [pick, setPick] = useState('')
  const [error, setError] = useState<string>()
  const [done, setDone] = useState<ScheduledInterview | null>(null)
  const names = useMemo(() => new Map((dir.data ?? []).map((d) => [d.id, d.fullName])), [dir.data])
  const save = useAction(() => recruitmentApi.scheduleInterviewPlus(target!.applicationId, {
    type: f.type, scheduledAt: new Date(f.at).toISOString(), interviewerEmployeeIds: people, durationMinutes: Number(f.duration),
    location: f.location.trim() || undefined, meetingUrl: f.url.trim() || undefined, notifyCandidate: f.notify,
  }), { success: tx('Mülakat planlandı'), invalidate: [['recruitment']], onDone: (r) => setDone(r) })

  useEffect(() => {
    if (target) { setDone(null); setError(undefined); setPeople([]); setF((x) => ({ ...x, at: '', location: '', url: '' })) }
  }, [target])
  if (!target) return null

  const submit = () => {
    if (!f.at) return setError(tx('Tarih ve saat zorunlu.'))
    if (people.length === 0) return setError(tx('En az bir görüşmeci seçin.'))
    setError(undefined)
    save.mutate(undefined, { onError: (e) => setError(errMsg(e)) })
  }

  if (done) {
    return (
      <Modal open onClose={onClose} title={tx('Mülakat planlandı')} note={done.candidateNotified ? tx('Adaya e-posta daveti gönderildi; görüşmecilere bildirim gitti.') : tx('Görüşmecilere bildirim gitti. Daveti adaya kendiniz iletebilirsiniz.')}
        footer={<Button onClick={onClose}>{tx('Kapat')}</Button>}>
        <div className="space-y-2">
          <TextAreaField label={tx('Aday davet metni')} rows={10} readOnly value={done.invitationText} />
          <Button variant="outline" size="sm" onClick={() => { void navigator.clipboard?.writeText(done.invitationText); toast.ok(tx('Kopyalandı')) }}>
            <Copy className="size-4" /> {tx('Metni kopyala')}
          </Button>
        </div>
      </Modal>
    )
  }

  return (
    <Modal open size="lg" onClose={onClose} title={tx('Mülakat planla')} note={target.candidateName}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={save.isPending} onClick={submit}>{tx('Planla')}</Button></>}>
      <div className="space-y-3">
        <div className="grid gap-3 md:grid-cols-3">
          <SelectField label={tx('Mülakat türü')} value={f.type} onChange={(v) => setF({ ...f, type: v as InterviewType })}
            options={(Object.keys(interviewTypeLabels) as InterviewType[]).map((t) => ({ value: t, label: interviewTypeLabels[t] }))} />
          <TextField label={tx('Tarih ve saat')} type="datetime-local" required value={f.at} onChange={(e) => setF({ ...f, at: e.target.value })} />
          <SelectField label={tx('Süre')} value={f.duration} onChange={(v) => setF({ ...f, duration: v })}
            options={['30', '45', '60', '90', '120'].map((m) => ({ value: m, label: tx('{0} dk', [m]) }))} />
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label={tx('Yer (isteğe bağlı)')} value={f.location} maxLength={300} onChange={(e) => setF({ ...f, location: e.target.value })} placeholder={tx('Ör. Merkez ofis, 3. kat')} />
          <TextField label={tx('Çevrim içi bağlantı (isteğe bağlı)')} value={f.url} maxLength={500} onChange={(e) => setF({ ...f, url: e.target.value })} placeholder="https://" />
        </div>
        <div className="space-y-2">
          <div className="grid gap-2 md:grid-cols-[1fr_auto]">
            <PersonSelect label={tx('Görüşmeci ekle')} value={pick} onChange={setPick} exclude={people} />
            <Button type="button" variant="outline" className="self-end" disabled={!pick || people.length >= 6} onClick={() => { setPeople([...people, pick]); setPick('') }}>
              <Plus className="size-4" /> {tx('Ekle')}
            </Button>
          </div>
          {people.length > 0 && (
            <ul className="flex flex-wrap gap-1.5">
              {people.map((p) => (
                <li key={p} className="inline-flex items-center gap-1 rounded-full bg-primary/10 px-2.5 py-0.5 text-[12.5px] text-primary">
                  {names.get(p) ?? p.slice(0, 8)}
                  <button type="button" aria-label={tx('{0} kaldır', [names.get(p) ?? ''])} onClick={() => setPeople(people.filter((x) => x !== p))}><Trash2 className="size-3" /></button>
                </li>
              ))}
            </ul>
          )}
        </div>
        <label className="flex items-center gap-2 text-[13px]">
          <Checkbox checked={f.notify} onCheckedChange={(v) => setF({ ...f, notify: v === true })} />
          {tx('Adaya e-posta daveti gönder')}
        </label>
        <InfoNote>{tx('Görüşmecinin aynı saatte başka bir mülakatı varsa planlama engellenir. Görüşmeci bildiriminde aday adı yer almaz.')}</InfoNote>
        {error && <p role="alert" className="text-[13px] text-destructive">{error}</p>}
      </div>
    </Modal>
  )
}

/* ================================================================ puan kartı (Y17) */

export function ScorecardModal({ interviewId, title, onClose }: { interviewId: string | null; title?: string; onClose: () => void }) {
  const toast = useToast()
  const form = useQuery({ queryKey: ['recruitment', 'scorecard-form', interviewId], queryFn: ({ signal }) => recruitmentApi.scorecardForm(interviewId!, signal), enabled: !!interviewId })
  const [scores, setScores] = useState<Record<string, number>>({})
  const [evidence, setEvidence] = useState<Record<string, string>>({})
  const [rec, setRec] = useState<Recommendation | ''>('')
  const [notes, setNotes] = useState('')
  const warnings = useNoteWarnings(notes)
  useEffect(() => {
    const m = form.data?.mine
    setScores(Object.fromEntries((m?.scores ?? []).map((s) => [s.key, s.score])))
    setEvidence(Object.fromEntries((m?.scores ?? []).filter((s) => s.evidence).map((s) => [s.key, s.evidence!])))
    setRec(m?.recommendation ?? '')
    setNotes(m?.notes ?? '')
  }, [form.data])
  const criteria = form.data?.criteria ?? []
  const preview = useMemo(() => {
    const scored = criteria.filter((c) => scores[c.key])
    const w = scored.reduce((a, c) => a + c.weight, 0)
    return w ? scored.reduce((a, c) => a + c.weight * scores[c.key]!, 0) / w : null
  }, [criteria, scores])
  const save = useAction(() => recruitmentApi.submitScorecard(interviewId!, {
    scores: Object.entries(scores).map(([key, score]): CriterionScore => ({ key, score, evidence: evidence[key]?.trim() || undefined })),
    recommendation: rec || undefined, notes: notes.trim() || undefined,
  }), {
    invalidate: [['recruitment']],
    onDone: (r) => {
      if (r.warnings.length) toast.stop(tx('Kaydedildi; notta özel nitelikli veri olabilir — lütfen düzenleyin.'))
      else toast.ok(tx('Puan kartı kaydedildi ({0})', [r.overallScore.toFixed(2)]))
      if (!r.warnings.length) onClose()
    },
  })
  if (!interviewId) return null
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Mülakat puan kartı')} note={title}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button>
        <Button disabled={!form.data?.canSubmit || save.isPending || preview == null} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      {form.isPending ? <RowsSkeleton rows={4} /> : form.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(form.error)}</p> : (
        <div className="space-y-4">
          <InfoNote>{PRIVACY_HINT}</InfoNote>
          <table className="w-full text-[13px]">
            <thead><tr className="text-left text-[12px] text-muted-foreground"><th className="py-1">{tx('Ölçüt')}</th><th>{tx('Ağırlık')}</th><th>{tx('Puan (1-5)')}</th></tr></thead>
            <tbody>
              {criteria.map((c) => (
                <tr key={c.key} className="border-t border-border">
                  <td className="py-2 pr-2">{c.label}</td>
                  <td className="tabular">×{c.weight}</td>
                  <td>
                    <div role="radiogroup" aria-label={c.label} className="flex gap-1">
                      {[1, 2, 3, 4, 5].map((n) => (
                        <button key={n} type="button" role="radio" aria-checked={scores[c.key] === n} disabled={!form.data.canSubmit}
                          onClick={() => setScores({ ...scores, [c.key]: n })}
                          className={cn('size-7 rounded-lg border text-[12.5px] tabular', scores[c.key] === n ? 'border-primary bg-primary text-primary-foreground' : 'border-border hover:border-primary/50')}>
                          {n}
                        </button>
                      ))}
                    </div>
                    {/* Dalga 11: puanı destekleyen kanıt / gözlem (isteğe bağlı) */}
                    <input aria-label={tx('{0} için kanıt', [c.label])} placeholder={tx('Kanıt / gözlem (isteğe bağlı)')} maxLength={1000}
                      disabled={!form.data.canSubmit} value={evidence[c.key] ?? ''} onChange={(e) => setEvidence({ ...evidence, [c.key]: e.target.value })}
                      className="mt-1.5 h-8 w-full rounded-lg border border-input bg-background px-2 text-[12px]" />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="text-[13px]">{tx('Ağırlıklı puan')}: <strong className="tabular">{preview == null ? '—' : preview.toFixed(2)}</strong> / 5</p>
          <SelectField label={tx('Öneri (isteğe bağlı)')} value={rec} onChange={(v) => setRec(v as Recommendation)}
            options={(Object.keys(recommendationLabels) as Recommendation[]).map((r) => ({ value: r, label: recommendationLabels[r] }))} placeholder={tx('Seçin')} />
          <TextAreaField label={tx('Notlar')} rows={4} maxLength={4000} value={notes} onChange={(e) => setNotes(e.target.value)} hint={tx('İşle ilgili gözlemler; özel nitelikli veri yazmayın.')} />
          <NoteWarnings warnings={warnings} />
          {form.data.locked ? <p className="text-[12.5px] text-muted-foreground">{tx('Tüm görüşmeciler puan kartını gönderdi; değerlendirmeler artık değiştirilemez.')}</p>
            : !form.data.canSubmit && <p className="text-[12.5px] text-muted-foreground">{tx('Puan kartını yalnızca mülakatın görüşmecileri doldurabilir.')}</p>}
          {(form.data.panelSize ?? 0) > 1 && !form.data.locked && (
            <p className="text-[12px] text-muted-foreground">{tx('Kör değerlendirme: diğer görüşmecilerin puanları, panelin tamamı ({0} kişi) gönderene kadar gizlidir. Gönderen: {1}.', [form.data.panelSize, form.data.submittedCount ?? 0])}</p>
          )}
        </div>
      )}
    </Modal>
  )
}

export function ScorecardsModal({ interviewId, onClose }: { interviewId: string | null; onClose: () => void }) {
  const q = useQuery({ queryKey: ['recruitment', 'scorecards', interviewId], queryFn: ({ signal }) => recruitmentApi.scorecards(interviewId!, signal), enabled: !!interviewId })
  if (!interviewId) return null
  const label = new Map((q.data?.criteria ?? []).map((c) => [c.key, c.label]))
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Puan kartları')} note={q.data?.average != null ? tx('Ortalama {0} / 5', [q.data.average.toFixed(2)]) : undefined}
      footer={<Button onClick={onClose}>{tx('Kapat')}</Button>}>
      {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p>
        : q.data.scorecards.length === 0 ? <EmptyState icon={ClipboardCheck} title={tx('Henüz puan kartı yok')} detail={tx('Görüşmeciler mülakattan sonra doldurur.')} /> : (
          <ul className="space-y-3">
            {q.data.blind && <li><InfoNote>{tx('Kör değerlendirme: siz de bu mülakatın görüşmecisi olduğunuz için diğer puan kartları, tüm görüşmeciler gönderene kadar gizli ({0}/{1} gönderildi).', [q.data.submittedCount ?? 0, q.data.panelSize ?? 0])}</InfoNote></li>}
            {q.data.consistency && <li><ConsistencyView c={q.data.consistency} /></li>}
            {q.data.scorecards.map((s) => (
              <li key={s.id} className="rounded-xl border border-border p-3 text-[13px]">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium">{s.interviewer ?? tx('Görüşmeci')}</span>
                  <span className="tabular">{s.overallScore?.toFixed(2) ?? '—'} / 5{s.recommendation ? ` · ${recommendationLabels[s.recommendation]}` : ''}</span>
                </div>
                <p className="mt-1 text-[12px] text-muted-foreground">{s.scores.map((x) => `${label.get(x.key) ?? x.key}: ${x.score}`).join(' · ')}</p>
                {s.scores.some((x) => x.evidence) && (
                  <ul className="mt-1 space-y-0.5 text-[12px]">
                    {s.scores.filter((x) => x.evidence).map((x) => <li key={x.key}><span className="text-muted-foreground">{label.get(x.key) ?? x.key}:</span> {x.evidence}</li>)}
                  </ul>
                )}
                {s.notes && <p className="mt-1.5 whitespace-pre-wrap border-l-2 border-border pl-2.5">{s.notes}</p>}
                <div className="mt-1.5"><NoteWarnings warnings={s.warnings} /></div>
              </li>
            ))}
            {q.data.pending.length > 0 && <li className="text-[12.5px] text-muted-foreground">{tx('{0} görüşmeci henüz doldurmadı.', [q.data.pending.length])}</li>}
          </ul>
        )}
    </Modal>
  )
}

export function ScorecardTemplatePanel({ postingId, canEdit }: { postingId: string; canEdit: boolean }) {
  const q = useQuery({ queryKey: ['recruitment', 'scorecard-template', postingId], queryFn: ({ signal }) => recruitmentApi.scorecardTemplate(postingId, signal) })
  const [rows, setRows] = useState<ScorecardCriterion[]>([])
  useEffect(() => { if (q.data) setRows(q.data.criteria) }, [q.data])
  const save = useAction(() => recruitmentApi.saveScorecardTemplate(postingId, rows.map((r, i) => ({ ...r, key: r.key || `c${i + 1}` }))), {
    success: tx('Puan kartı şablonu kaydedildi'), invalidate: [['recruitment', 'scorecard-template', postingId]],
  })
  return (
    <Panel>
      <PanelHead title={tx('Mülakat puan kartı')} note={q.data?.isDefault ? tx('Varsayılan şablon — ilana göre düzenleyin') : tx('Ölçütler 1-5 puanlanır; ağırlık 1-5')} />
      <PanelBody className="space-y-2">
        {q.isPending ? <RowsSkeleton rows={3} /> : rows.map((r, i) => (
          <div key={i} className="grid grid-cols-[1fr_90px_auto] items-end gap-2">
            <TextField label={tx('Ölçüt {0}', [i + 1])} value={r.label} disabled={!canEdit} maxLength={120}
              onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, label: e.target.value } : x)))} />
            <SelectField label={tx('Ağırlık')} value={String(r.weight)} onChange={(v) => setRows(rows.map((x, j) => (j === i ? { ...x, weight: Number(v) } : x)))}
              options={[1, 2, 3, 4, 5].map((n) => ({ value: String(n), label: `×${n}` }))} />
            {canEdit && <Button variant="ghost" size="sm" aria-label={tx('Ölçütü sil')} onClick={() => setRows(rows.filter((_, j) => j !== i))}><Trash2 className="size-4" /></Button>}
          </div>
        ))}
        {canEdit && (
          <div className="flex gap-2 pt-1">
            <Button variant="outline" size="sm" disabled={rows.length >= 15} onClick={() => setRows([...rows, { key: `c${Date.now().toString(36)}`, label: '', weight: 1 }])}><Plus className="size-4" /> {tx('Ölçüt ekle')}</Button>
            <Button size="sm" disabled={save.isPending || rows.some((r) => !r.label.trim())} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ================================================================ teklif (Y18) */

export function OfferModal({ target, onClose }: { target: { applicationId: string; candidateName: string; postingTitle: string } | null; onClose: () => void }) {
  const toast = useToast()
  const plus = (d: number) => isoDate(new Date(Date.now() + d * 864e5))
  const [f, setF] = useState({ position: '', salary: '', currency: 'TRY', start: plus(30), expires: plus(7), benefits: '' })
  const [letter, setLetter] = useState<string | null>(null)
  useEffect(() => { if (target) { setF((x) => ({ ...x, position: target.postingTitle })); setLetter(null) } }, [target])
  const input = (): OfferInput => ({
    applicationId: target!.applicationId, positionTitle: f.position.trim(), grossSalary: Number(f.salary), currency: f.currency,
    startDate: f.start, expiresAt: f.expires, benefits: f.benefits.trim() || undefined,
  })
  const preview = useAction(() => recruitmentApi.previewOffer(input()), { onDone: (r) => setLetter(r.letterText) })
  const create = useAction(() => recruitmentApi.createOffer(input()), {
    invalidate: [['recruitment']], onDone: (r) => { toast.ok(r.approvalNote); onClose() },
  })
  if (!target) return null
  const valid = f.position.trim().length >= 2 && Number(f.salary) > 0 && f.start && f.expires
  return (
    <Modal open size="xl" onClose={onClose} title={tx('İş teklifi hazırla')} note={target.candidateName}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
        <Button variant="outline" disabled={!valid || preview.isPending} onClick={() => preview.mutate(undefined)}>{tx('Mektubu önizle')}</Button>
        <Button disabled={!valid || create.isPending} onClick={() => create.mutate(undefined)}><FileSignature className="size-4" /> {tx('Onaya gönder')}</Button></>}>
      <div className="grid gap-4 lg:grid-cols-[1fr_1.1fr]">
        <div className="space-y-3">
          <TextField label={tx('Pozisyon')} value={f.position} maxLength={200} onChange={(e) => setF({ ...f, position: e.target.value })} />
          <div className="grid grid-cols-[1fr_100px] gap-2">
            <TextField label={tx('Aylık brüt ücret')} type="number" min={1} value={f.salary} onChange={(e) => setF({ ...f, salary: e.target.value })} />
            <SelectField label={tx('Para birimi')} value={f.currency} onChange={(v) => setF({ ...f, currency: v })} options={['TRY', 'USD', 'EUR', 'GBP'].map((c) => ({ value: c, label: c }))} />
          </div>
          <div className="grid grid-cols-2 gap-2">
            <TextField label={tx('İşe başlama')} type="date" value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} />
            <TextField label={tx('Teklif geçerliliği')} type="date" value={f.expires} onChange={(e) => setF({ ...f, expires: e.target.value })} />
          </div>
          <TextAreaField label={tx('Yan haklar')} rows={3} maxLength={2000} value={f.benefits} onChange={(e) => setF({ ...f, benefits: e.target.value })} />
          <InfoNote>{tx('Teklif, ilanın departman başının onayına gider (yoksa kararı İK verir). Ücret yalnızca İK ve onaycıya görünür; bildirimlerde yer almaz.')}</InfoNote>
        </div>
        <div className="min-h-64 rounded-xl border border-border bg-card/50 p-4">
          {letter ? <pre className="font-sans text-[12.5px] leading-relaxed whitespace-pre-wrap">{letter}</pre>
            : <p className="text-[12.5px] text-muted-foreground">{tx('Mektup önizlemesi için alanları doldurup "Mektubu önizle"ye basın. Metin şirket şablonundan üretilir.')}</p>}
        </div>
      </div>
    </Modal>
  )
}

function OfferTemplateModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const q = useQuery({ queryKey: ['recruitment', 'offer-template'], queryFn: ({ signal }) => recruitmentApi.offerTemplate(signal), enabled: open })
  const [body, setBody] = useState('')
  useEffect(() => { if (q.data) setBody(q.data.body) }, [q.data])
  const save = useAction(() => recruitmentApi.saveOfferTemplate(body), { success: tx('Şablon kaydedildi'), invalidate: [['recruitment', 'offer-template']], onDone: onClose })
  if (!open) return null
  return (
    <Modal open size="xl" onClose={onClose} title={tx('Teklif mektubu şablonu')} note={tx('Yer tutucular: {0}', [(q.data?.placeholders ?? []).map((p) => `{${p}}`).join(' ')])}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={save.isPending || !body.trim()} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      {q.isPending ? <RowsSkeleton rows={4} /> : <TextAreaField label={tx('Şablon')} rows={18} value={body} onChange={(e) => setBody(e.target.value)} className="font-mono text-[12.5px]" />}
    </Modal>
  )
}

export function OffersPanel({ postingId, isHr, names }: { postingId: string; isHr: boolean; names: Map<string, string> }) {
  const q = useQuery({ queryKey: ['recruitment', 'offers', postingId], queryFn: ({ signal }) => recruitmentApi.offers({ jobPostingId: postingId }, signal) })
  const [tpl, setTpl] = useState(false)
  // Dalga 11: imza bağlantısı (bir kez gösterilir) ve kanıt penceresi.
  const [linkPath, setLinkPath] = useState<string | null>(null)
  const [evidenceFor, setEvidenceFor] = useState<string | null>(null)
  const inv = { invalidate: [['recruitment']] }
  const decide = useAction(({ id, approve }: { id: string; approve: boolean }) => recruitmentApi.decideOffer(id, approve), { ...inv, success: tx('Karar kaydedildi') })
  const send = useAction((id: string) => recruitmentApi.sendOffer(id), { ...inv, success: tx('Teklif adaya gönderildi'), onDone: (o) => setLinkPath(o.signingPath ?? null) })
  const renewLink = useAction((id: string) => recruitmentApi.renewSigningLink(id), { ...inv, success: tx('İmza bağlantısı yenilendi'), onDone: (o) => setLinkPath(o.signingPath ?? null) })
  const revokeLink = useAction((id: string) => recruitmentApi.revokeSigningLink(id), { ...inv, success: tx('İmza bağlantısı iptal edildi') })
  const respond = useAction(({ id, accept }: { id: string; accept: boolean }) => recruitmentApi.respondOffer(id, accept), {
    ...inv, success: (r) => (r.offer.status === 'Accepted' ? tx('Kabul kaydedildi; başvuru "İşe alındı" oldu') : tx('Yanıt kaydedildi')),
  })
  const withdraw = useAction((id: string) => recruitmentApi.withdrawOffer(id), { ...inv, success: tx('Teklif geri çekildi') })
  const row = (o: Offer) => (
    <li key={o.id} className="flex flex-wrap items-center gap-3 px-4 py-3 text-[13px]">
      <div className="min-w-0 flex-1">
        <p className="font-medium">{names.get(o.applicationId) ?? tx('Aday')} · {o.positionTitle}</p>
        <p className="text-[12px] text-muted-foreground">
          {o.grossSalary != null ? `${formatMoney(o.grossSalary, o.currency)} ${tx('brüt')} · ` : ''}{tx('Başlangıç {0} · Geçerlilik {1}', [formatDate(o.startDate), formatDate(o.expiresAt)])}
          {o.hrDecides ? ` · ${tx('kararı İK verir')}` : ''}{o.decisionNote ? ` · ${o.decisionNote}` : ''}
        </p>
      </div>
      <StatusBadge tone={offerTone[o.status]}>{offerStatusLabels[o.status]}</StatusBadge>
      {o.signed && <Button size="sm" variant="ghost" onClick={() => setEvidenceFor(o.id)}><FileSignature className="size-4" /> {tx('E-imzalı · kanıt')}</Button>}
      {isHr && (
        <div className="flex flex-wrap gap-1.5">
          {o.hrDecides && <><Button size="sm" onClick={() => decide.mutate({ id: o.id, approve: true })}>{tx('Onayla')}</Button>
            <Button size="sm" variant="outline" onClick={() => decide.mutate({ id: o.id, approve: false })}>{tx('Reddet')}</Button></>}
          {o.status === 'Approved' && <Button size="sm" onClick={() => send.mutate(o.id)}>{tx('Adaya gönder')}</Button>}
          {o.status === 'Sent' && !o.signed && <>
            <Button size="sm" variant="outline" onClick={() => renewLink.mutate(o.id)}>{o.signingLinkActive ? tx('İmza bağlantısını yenile') : tx('İmza bağlantısı oluştur')}</Button>
            {o.signingLinkActive && <Button size="sm" variant="ghost" onClick={() => revokeLink.mutate(o.id)}>{tx('Bağlantıyı iptal et')}</Button>}
          </>}
          {o.status === 'Sent' && <><Button size="sm" onClick={() => respond.mutate({ id: o.id, accept: true })}>{tx('Kabul etti')}</Button>
            <Button size="sm" variant="outline" onClick={() => respond.mutate({ id: o.id, accept: false })}>{tx('Reddetti')}</Button></>}
          {(o.status === 'PendingApproval' || o.status === 'Approved' || o.status === 'Sent') && <Button size="sm" variant="ghost" onClick={() => withdraw.mutate(o.id)}>{tx('Geri çek')}</Button>}
          {o.letterText && <Button size="sm" variant="ghost" onClick={() => printLetter(tx('Teklif mektubu'), o.letterText!)}><Printer className="size-4" /> {tx('Yazdır')}</Button>}
        </div>
      )}
    </li>
  )
  return (
    <Panel>
      <PanelHead title={tx('Teklifler')} note={tx('Yalnızca onaylı teklif adaya gönderilir; kabulde başvuru "İşe alındı" olur ve işe giriş sagası başlar.')}
        action={isHr ? <Button size="sm" variant="outline" onClick={() => setTpl(true)}><FileSignature className="size-4" /> {tx('Şablon')}</Button> : undefined} />
      {q.isPending ? <div className="p-4"><RowsSkeleton rows={2} /></div> : !q.data?.length
        ? <EmptyState icon={FileSignature} title={tx('Teklif yok')} detail={tx('Panodaki bir adayın kartından "Teklif" ile hazırlayın.')} />
        : <ul className="divide-y divide-border">{q.data.map(row)}</ul>}
      <OfferTemplateModal open={tpl} onClose={() => setTpl(false)} />
      <SigningLinkModal path={linkPath} onClose={() => setLinkPath(null)} />
      <OfferSignatureModal offerId={evidenceFor} onClose={() => setEvidenceFor(null)} />
    </Panel>
  )
}

/* ================================================================ Mülakatlarım */

export function MyInterviewsPage() {
  const q = useQuery({ queryKey: ['recruitment', 'my-interviews'], queryFn: ({ signal }) => recruitmentApi.myInterviews(signal) })
  const [open, setOpen] = useState<{ id: string; title: string } | null>(null)
  const now = Date.now()
  return (
    <>
      <PageHeader title={tx('Mülakatlarım')} description={tx('Görüşmeci olarak atandığınız mülakatlar ve puan kartları.')} />
      <Panel>
        <PanelBody className="p-0">
          {q.isPending ? <div className="p-4"><RowsSkeleton rows={3} /></div> : q.isError ? <p role="alert" className="p-4 text-[13px] text-destructive">{errMsg(q.error)}</p>
            : !q.data.length ? <EmptyState icon={CalendarClock} title={tx('Atanmış mülakatınız yok')} detail={tx('İK sizi görüşmeci olarak eklediğinde burada görünür.')} /> : (
              <ul className="divide-y divide-border">
                {q.data.map((i) => (
                  <li key={i.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                    <div className="min-w-0 flex-1">
                      <p className="font-medium">{i.candidateName ?? tx('Aday')} · {i.posting}</p>
                      <p className="text-[12px] text-muted-foreground">
                        {interviewTypeLabels[i.type]} · {formatDateTime(i.scheduledAt)} · {tx('{0} dk', [i.durationMinutes])}{i.location ? ` · ${i.location}` : ''}
                      </p>
                    </div>
                    {i.result !== 'Cancelled' && i.meetingUrl && new Date(i.scheduledAt).getTime() + i.durationMinutes * 60e3 > now && (
                      <Button size="sm" variant="outline" asChild><a href={i.meetingUrl} target="_blank" rel="noreferrer noopener"><Video className="size-4" /> {tx('Katıl')}</a></Button>
                    )}
                    {i.result === 'Cancelled' ? (
                      <StatusBadge tone="neutral">{tx('İptal edildi')}</StatusBadge>
                    ) : (
                      <>
                        {i.myScorecard
                          ? <StatusBadge tone="success">{tx('Puan kartı: {0}', [i.myScorecard.overallScore?.toFixed(2) ?? '—'])}</StatusBadge>
                          : <StatusBadge tone="warning">{tx('Puan kartı bekliyor')}</StatusBadge>}
                        <Button size="sm" onClick={() => setOpen({ id: i.id, title: `${i.candidateName ?? ''} · ${i.posting ?? ''}` })}>
                          <ClipboardCheck className="size-4" /> {i.myScorecard ? tx('Düzenle') : tx('Doldur')}
                        </Button>
                      </>
                    )}
                  </li>
                ))}
              </ul>
            )}
        </PanelBody>
      </Panel>
      <ScorecardModal interviewId={open?.id ?? null} title={open?.title} onClose={() => setOpen(null)} />
    </>
  )
}
