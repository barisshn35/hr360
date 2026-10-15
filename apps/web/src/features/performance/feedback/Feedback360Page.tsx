/**
 * Dalga 11 / 81: anonim 360 derece geri bildirim — `/panel/performans/360`.
 *
 * Sekmeler: bana gelen davetler (yanıtla), yönettiğim talepler (yönetici+/İK: aç, izle, kapat, sonuç),
 * sonuçlarım (İK paylaştıysa). Anonimlik: değerlendiren kimliği yanıtla birlikte saklanmaz; sonuçlar
 * yalnızca kapanmış ve en az 5 yanıtlı talepte, yetkinlik başına da en az 5 puanla gösterilir.
 * Yorum alanında kişisel veri uyarısı (PiiHint) tarayıcıda gösterilir.
 */
import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, EyeOff, MessageSquareQuote, Plus, Trash2, Users } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { PiiHint } from '@/components/PiiHint'
import { useAuth } from '@/auth/useAuth'
import { f360Api, type F360Invitation, type F360Relationship } from '@/api/performanceGrowth'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDate, formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'
import { usePeople } from '../hooks'

type TabKey = 'davetler' | 'yonetim' | 'sonuclarim'

const relText: Record<F360Relationship, string> = {
  Manager: tx('Yönetici'),
  Peer: tx('Çalışma arkadaşı'),
  DirectReport: tx('Ekip üyesi'),
  Other: tx('Diğer'),
}
const ANON_NOTE = tx('Yanıtınız anonimdir: kimliğiniz yanıtla birlikte saklanmaz, yalnızca yanıtlayıp yanıtlamadığınız tutulur. Sonuçlar talep kapandıktan sonra ve en az 5 yanıt varsa toplu olarak gösterilir.')

function RespondDialog({ inv, onClose }: { inv: F360Invitation; onClose: () => void }) {
  const [ratings, setRatings] = useState<Record<string, number>>({})
  const [comment, setComment] = useState('')
  const save = useAction(() => f360Api.respond(inv.id, { ratings, comment: comment.trim() || undefined }), {
    success: tx('Yanıtınız anonim olarak kaydedildi'), invalidate: [['perf', 'f360']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} size="lg" title={inv.title} note={tx('Değerlendirilen: {0}', [inv.subjectName ?? '—'])}
      footer={<Button onClick={() => save.mutate(undefined)} disabled={save.isPending || Object.keys(ratings).length === 0}>{tx('Gönder')}</Button>}>
      <div className="space-y-4">
        <InfoNote>{ANON_NOTE}</InfoNote>
        {inv.competencies.map((c) => (
          <fieldset key={c.key} className="space-y-1.5">
            <legend className="text-[13px] font-medium">{c.label}</legend>
            <div className="flex flex-wrap gap-1.5">
              {[1, 2, 3, 4, 5].map((v) => (
                <button key={v} type="button" aria-pressed={ratings[c.key] === v}
                  onClick={() => setRatings({ ...ratings, [c.key]: v })}
                  className={cn('tabular h-8 w-9 cursor-pointer rounded-lg border text-[13px]', ratings[c.key] === v ? 'border-primary bg-primary text-primary-foreground' : 'border-border hover:border-primary/60')}>
                  {v}
                </button>
              ))}
              <button type="button" aria-pressed={ratings[c.key] === undefined}
                onClick={() => { const n = { ...ratings }; delete n[c.key]; setRatings(n) }}
                className={cn('h-8 cursor-pointer rounded-lg border px-2.5 text-[12.5px]', ratings[c.key] === undefined ? 'border-primary/60 bg-muted' : 'border-border')}>
                {tx('Fikrim yok')}
              </button>
            </div>
          </fieldset>
        ))}
        <p className="text-[12px] text-muted-foreground">{tx('1 = gelişime çok açık, 5 = çok güçlü.')}</p>
        <div>
          <TextAreaField label={tx('Yorum (isteğe bağlı)')} rows={3} maxLength={2000} value={comment} onChange={(e) => setComment(e.target.value)}
            hint={tx('Davranışa ve işe odaklanın. Kimliğinizi ele verecek ayrıntı ve özel nitelikli veri yazmayın.')} />
          <PiiHint text={comment} className="mt-2" />
        </div>
      </div>
    </Modal>
  )
}

function Invitations() {
  const q = useQuery({ queryKey: ['perf', 'f360', 'mine'], queryFn: ({ signal }) => f360Api.mine(signal) })
  const [pick, setPick] = useState<F360Invitation | null>(null)
  if (q.isPending) return <RowsSkeleton rows={3} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  if (q.data.length === 0) return <EmptyState icon={MessageSquareQuote} title={tx('Bekleyen 360 davetiniz yok')} detail={tx('Biri için 360 değerlendirmesine davet edildiğinizde burada görünür.')} />
  return (
    <Panel>
      <PanelHead title={tx('Bana gelen davetler')} note={ANON_NOTE} />
      <PanelBody>
        <ul className="divide-y divide-border">
          {q.data.map((i) => (
            <li key={i.id} className="flex flex-wrap items-center gap-3 py-2.5">
              <span className="min-w-0 flex-1">
                <span className="block text-[13.5px] font-medium">{i.title}</span>
                <span className="text-[12px] text-muted-foreground">{i.subjectName} · {relText[i.relationship]}{i.dueDate ? ` · ${tx('son tarih {0}', [formatDate(i.dueDate)])}` : ''}</span>
              </span>
              {i.submitted
                ? <StatusBadge tone="success">{tx('Yanıtlandı')}</StatusBadge>
                : <Button size="sm" onClick={() => setPick(i)}>{tx('Yanıtla')}</Button>}
            </li>
          ))}
        </ul>
      </PanelBody>
      {pick && <RespondDialog inv={pick} onClose={() => setPick(null)} />}
    </Panel>
  )
}

function ResultsDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const q = useQuery({ queryKey: ['perf', 'f360', 'results', id], queryFn: ({ signal }) => f360Api.results(id, signal) })
  const num = new Intl.NumberFormat(appLocale, { maximumFractionDigits: 2 })
  return (
    <Modal open onClose={onClose} size="lg" title={q.data?.title ?? tx('360 sonuçları')} note={q.data?.subjectName ?? undefined}>
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : q.data.hidden ? (
        <EmptyState icon={EyeOff} title={tx('Sonuçlar gizli')}
          detail={q.data.closed ? tx('Anonimlik için en az {0} yanıt gerekir; {1} yanıt var.', [q.data.threshold, q.data.responses]) : tx('Sonuçlar talep kapandıktan sonra gösterilir ({0} yanıt).', [q.data.responses])} />
      ) : (
        <div className="space-y-4">
          <InfoNote>{q.data.notice}</InfoNote>
          <p className="text-[12.5px] text-muted-foreground">{tx('{0} yanıt', [q.data.responses])}</p>
          <ul className="space-y-2.5">
            {q.data.competencies.map((c) => (
              <li key={c.key} className="space-y-1">
                <div className="flex items-baseline justify-between text-[13px]"><span className="font-medium">{c.label}</span>
                  <span className="tabular">{c.hidden ? tx('Gizli ({0} puan)', [c.ratings]) : `${num.format(c.average ?? 0)} / 5`}</span></div>
                {!c.hidden && c.distribution && (
                  <div className="flex h-2 overflow-hidden rounded-full bg-muted" aria-label={tx('Dağılım: {0}', [c.distribution.join(', ')])}>
                    {c.distribution.map((n, i) => n > 0 && (
                      <span key={i} title={`${i + 1}: ${n}`} className="h-full" style={{ width: `${(n / c.ratings) * 100}%`, background: `hsl(var(--primary) / ${0.25 + i * 0.18})` }} />
                    ))}
                  </div>
                )}
              </li>
            ))}
          </ul>
          {q.data.comments.length > 0 && (
            <section>
              <h3 className="mb-1.5 text-[13px] font-semibold">{tx('Yorumlar (yanıt sırası gösterilmez)')}</h3>
              <ul className="space-y-1.5 text-[13px]">{q.data.comments.map((c, i) => <li key={i} className="rounded-lg border border-border p-2">{c}</li>)}</ul>
            </section>
          )}
        </div>
      )}
    </Modal>
  )
}

function DetailDialog({ id, onClose, onResults }: { id: string; onClose: () => void; onResults: () => void }) {
  const q = useQuery({ queryKey: ['perf', 'f360', 'detail', id], queryFn: ({ signal }) => f360Api.get(id, signal) })
  const confirm = useConfirm()
  const inv = [['perf', 'f360']]
  const close = useAction(() => f360Api.close(id), { success: tx('Talep kapatıldı'), invalidate: inv })
  const release = useAction((v: boolean) => f360Api.release(id, v), { success: tx('Paylaşım ayarı güncellendi'), invalidate: inv })
  const remove = useAction(() => f360Api.remove(id), { success: tx('Talep silindi'), invalidate: inv, onDone: onClose })
  return (
    <Modal open onClose={onClose} size="lg" title={q.data?.title ?? tx('360 talebi')} note={q.data?.subjectName ?? undefined}>
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : (
        <div className="space-y-4">
          <p className="text-[12.5px] text-muted-foreground">{tx('Katılım listesi yalnızca hatırlatma içindir; yanıt içerikleriyle ilişkilendirilmez.')}</p>
          <ul className="divide-y divide-border text-[13px]">
            {q.data.participants.map((p) => (
              <li key={p.reviewerEmployeeId} className="flex items-center gap-2 py-1.5">
                <span className="flex-1">{p.name ?? '—'} <span className="text-[12px] text-muted-foreground">· {relText[p.relationship]}</span></span>
                {p.submitted ? <StatusBadge tone="success">{tx('Yanıtladı')}</StatusBadge> : <StatusBadge>{tx('Bekleniyor')}</StatusBadge>}
              </li>
            ))}
          </ul>
          <div className="flex flex-wrap items-center gap-2">
            {q.data.status === 'Open' && (
              <Button size="sm" onClick={async () => {
                if (await confirm({ title: tx('Talep kapatılsın mı?'), note: tx('Kapandıktan sonra yanıt alınmaz; en az 5 yanıt varsa sonuçlar görünür.'), action: tx('Kapat') })) close.mutate(undefined)
              }} disabled={close.isPending}>{tx('Talebi kapat')}</Button>
            )}
            {q.data.status === 'Closed' && <Button size="sm" variant="outline" onClick={onResults}>{tx('Sonuçları gör')}</Button>}
            {q.data.status === 'Closed' && q.data.canRelease && (
              <label className="flex cursor-pointer items-center gap-2 text-[13px]">
                <Checkbox checked={q.data.releasedToSubject} onCheckedChange={(v) => release.mutate(v === true)} />
                {tx('Sonuçları kişiyle paylaş (varsayılan: gizli)')}
              </label>
            )}
            <Button size="sm" variant="ghost" onClick={async () => {
              if (await confirm({ title: tx('Talep silinsin mi?'), note: tx('Tüm anonim yanıtlar da kalıcı olarak silinir.'), action: tx('Sil') })) remove.mutate(undefined)
            }}><Trash2 className="size-4" />{tx('Sil')}</Button>
          </div>
        </div>
      )}
    </Modal>
  )
}

function CreateDialog({ onClose }: { onClose: () => void }) {
  const people = usePeople()
  const [subject, setSubject] = useState('')
  const [title, setTitle] = useState('')
  const [due, setDue] = useState('')
  const [search, setSearch] = useState('')
  const [reviewers, setReviewers] = useState<Record<string, F360Relationship>>({})
  const count = Object.keys(reviewers).length
  const candidates = useMemo(() => {
    const needle = search.trim().toLocaleLowerCase(appLocale)
    return people.list.filter((p) => p.id !== subject && (!needle || p.name.toLocaleLowerCase(appLocale).includes(needle))).slice(0, 60)
  }, [people.list, search, subject])
  const save = useAction(() => f360Api.create({
    subjectEmployeeId: subject, title: title.trim(), dueDate: due || null,
    reviewers: Object.entries(reviewers).map(([employeeId, relationship]) => ({ employeeId, relationship })),
  }), { success: tx('360 talebi açıldı'), invalidate: [['perf', 'f360']], onDone: onClose })
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Yeni 360 talebi')} note={tx('Anonimlik için en az 5 değerlendiren seçin.')}
      footer={<Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !subject || title.trim().length < 3 || count < 5}>{tx('Talep aç ({0} kişi)', [count])}</Button>}>
      <div className="space-y-3">
        <SelectField label={tx('Değerlendirilen kişi')} value={subject} onChange={(v) => { setSubject(v); const n = { ...reviewers }; delete n[v]; setReviewers(n) }}
          options={people.list.map((p) => ({ value: p.id, label: p.name }))} />
        <TextField label={tx('Başlık')} value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} placeholder={tx('Ör. 2026 yıl sonu 360 değerlendirmesi')} />
        <TextField label={tx('Son tarih (isteğe bağlı)')} type="date" value={due} onChange={(e) => setDue(e.target.value)} />
        <p className="text-[12.5px] text-muted-foreground">{tx('Yetkinlikler: iletişim, iş birliği, sorumluluk alma, problem çözme, gelişime açıklık.')}</p>
        <TextField label={tx('Değerlendiren ara')} value={search} onChange={(e) => setSearch(e.target.value)} />
        <ul className="max-h-64 divide-y divide-border overflow-y-auto rounded-xl border border-border">
          {candidates.map((p) => {
            const on = p.id in reviewers
            return (
              <li key={p.id} className="flex items-center gap-2 px-3 py-1.5 text-[13px]">
                <label className="flex flex-1 cursor-pointer items-center gap-2">
                  <Checkbox checked={on} onCheckedChange={(v) => { const n = { ...reviewers }; if (v === true) n[p.id] = 'Peer'; else delete n[p.id]; setReviewers(n) }} />
                  {p.name}
                </label>
                {on && (
                  <select aria-label={tx('İlişki')} value={reviewers[p.id]} onChange={(e) => setReviewers({ ...reviewers, [p.id]: e.target.value as F360Relationship })}
                    className="rounded-md border border-border bg-card px-2 py-1 text-[12.5px]">
                    {(Object.keys(relText) as F360Relationship[]).map((r) => <option key={r} value={r}>{relText[r]}</option>)}
                  </select>
                )}
              </li>
            )
          })}
        </ul>
      </div>
    </Modal>
  )
}

function Managed() {
  const q = useQuery({ queryKey: ['perf', 'f360', 'managed'], queryFn: ({ signal }) => f360Api.managed(signal) })
  const [creating, setCreating] = useState(false)
  const [detail, setDetail] = useState<string | null>(null)
  const [results, setResults] = useState<string | null>(null)
  return (
    <Panel>
      <PanelHead title={tx('Yönettiğim 360 talepleri')} note={tx('İK herkes için, yönetici departmanındaki çalışanlar için talep açar.')}
        action={<Button size="sm" onClick={() => setCreating(true)}><Plus className="size-4" />{tx('Yeni talep')}</Button>} />
      <PanelBody>
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : q.data.length === 0 ? (
          <EmptyState icon={Users} title={tx('Henüz 360 talebi yok')} detail={tx('Bir çalışan için en az 5 değerlendiren seçerek talep açın.')} />
        ) : (
          <ul className="divide-y divide-border">
            {q.data.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-3 py-2.5">
                <button type="button" className="min-w-0 flex-1 cursor-pointer text-left" onClick={() => setDetail(r.id)}>
                  <span className="block text-[13.5px] font-medium">{r.title}</span>
                  <span className="text-[12px] text-muted-foreground">{r.subjectName} · {tx('{0} / {1} yanıt', [r.submitted, r.invited])} · {formatDateTime(r.createdAt)}</span>
                </button>
                {r.releasedToSubject && <StatusBadge tone="info">{tx('Kişiyle paylaşıldı')}</StatusBadge>}
                <StatusBadge tone={r.status === 'Open' ? 'warning' : 'success'}>{r.status === 'Open' ? tx('Açık') : tx('Kapalı')}</StatusBadge>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {creating && <CreateDialog onClose={() => setCreating(false)} />}
      {detail && <DetailDialog id={detail} onClose={() => setDetail(null)} onResults={() => { setResults(detail); setDetail(null) }} />}
      {results && <ResultsDialog id={results} onClose={() => setResults(null)} />}
    </Panel>
  )
}

function MyResults() {
  const q = useQuery({ queryKey: ['perf', 'f360', 'my-results'], queryFn: ({ signal }) => f360Api.myResults(signal) })
  const [open, setOpen] = useState<string | null>(null)
  if (q.isPending) return <RowsSkeleton rows={2} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  if (q.data.length === 0) return <EmptyState icon={CheckCircle2} title={tx('Sizinle paylaşılmış 360 sonucu yok')} detail={tx('İK, kapanmış bir 360 değerlendirmesinin toplu sonuçlarını sizinle paylaştığında burada görünür.')} />
  return (
    <Panel>
      <PanelHead title={tx('360 sonuçlarım')} />
      <PanelBody>
        <ul className="divide-y divide-border">
          {q.data.map((r) => (
            <li key={r.id} className="flex items-center gap-3 py-2">
              <span className="flex-1 text-[13.5px]">{r.title} <span className="text-[12px] text-muted-foreground">· {formatDate(r.closedAt)}</span></span>
              <Button size="sm" variant="outline" onClick={() => setOpen(r.id)}>{tx('Görüntüle')}</Button>
            </li>
          ))}
        </ul>
      </PanelBody>
      {open && <ResultsDialog id={open} onClose={() => setOpen(null)} />}
    </Panel>
  )
}

export function Feedback360Page() {
  const { can } = useAuth()
  const manager = can('performance:manage')
  const [tab, setTab] = useTabParam<TabKey>('sekme', 'davetler')
  const tabs = [
    { key: 'davetler' as const, label: tx('Davetlerim') },
    ...(manager ? [{ key: 'yonetim' as const, label: tx('Talepler') }] : []),
    { key: 'sonuclarim' as const, label: tx('Sonuçlarım') },
  ]
  const active: TabKey = tab === 'yonetim' && !manager ? 'davetler' : tab
  return (
    <div className="space-y-5">
      <PageHeader title={tx('360 derece geri bildirim')} description={tx('Anonim, yetkinlik bazlı çok kaynaklı geri bildirim; sonuçlar gelişim içindir.')} />
      <Tabs tabs={tabs} value={active} onChange={setTab} label={tx('360 geri bildirim sekmeleri')} />
      {active === 'davetler' ? <Invitations /> : active === 'yonetim' ? <Managed /> : <MyResults />}
    </div>
  )
}
