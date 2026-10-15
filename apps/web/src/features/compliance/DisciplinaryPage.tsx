import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Gavel, Plus, ScrollText } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { complianceApi, type CaseStatus, type MyCase } from '@/api/compliance'
import { formatDate, formatDateTime } from '@/lib/format'
import { isoDate, PersonSelect, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'
import { useConfirm } from '@/components/ui/Confirm'
import { KvkkNote, Warnings } from './shared'

const statusView: Record<CaseStatus, { label: string; tone: 'neutral' | 'warning' | 'success' | 'danger' | 'info' }> = {
  Open: { label: tx('Açık'), tone: 'info' }, DefenceRequested: { label: tx('Savunma bekleniyor'), tone: 'warning' },
  DefenceReceived: { label: tx('Savunma alındı'), tone: 'info' }, Decided: { label: tx('Karar verildi'), tone: 'success' }, Closed: { label: tx('Kapalı'), tone: 'neutral' },
}

const criminalNote = tx('Adli sicil kaydı, ceza mahkûmiyeti veya güvenlik tedbirlerine ilişkin bilgi YAZMAYIN: özel nitelikli kişisel veridir (KVKK m.6) ve disiplin dosyasında tutulmaz.')

function NewCaseModal({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string, warnings: string[]) => void }) {
  const meta = useQuery({ queryKey: ['disciplinary', 'meta'], queryFn: ({ signal }) => complianceApi.disciplinaryMeta(signal) })
  const [f, setF] = useState({ employeeId: '', incidentDate: isoDate(), category: 'Attendance', description: '' })
  const save = useAction(() => complianceApi.createCase(f), { success: tx('Vaka açıldı'), invalidate: [['disciplinary']], onDone: (r) => onCreated(r.id, r.warnings) })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Yeni disiplin vakası')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={!f.employeeId || f.description.trim().length < 10 || save.isPending}>{tx('Aç')}</Button></>}>
      <div className="space-y-3">
        <KvkkNote>{criminalNote}</KvkkNote>
        <PersonSelect value={f.employeeId} onChange={(v) => setF({ ...f, employeeId: v })} />
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label={tx('Olay tarihi')} type="date" value={f.incidentDate} onChange={(e) => setF({ ...f, incidentDate: e.target.value })} />
          <SelectField label={tx('Kategori')} value={f.category} onChange={(v) => setF({ ...f, category: v })} options={meta.data?.categories ?? []} />
        </div>
        <TextAreaField label={tx('Olayın açıklaması')} rows={5} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} hint={tx('Somut olay: ne, ne zaman, nerede. Kişisel yorum ve özel nitelikli veri içermesin.')} />
        <PiiHint text={f.description} />
      </div>
    </Modal>
  )
}

function CaseModal({ id, onClose, initialWarnings }: { id: string; onClose: () => void; initialWarnings?: string[] }) {
  const meta = useQuery({ queryKey: ['disciplinary', 'meta'], queryFn: ({ signal }) => complianceApi.disciplinaryMeta(signal) })
  const q = useQuery({ queryKey: ['disciplinary', 'case', id], queryFn: ({ signal }) => complianceApi.caseDetail(id, signal) })
  const [warnings, setWarnings] = useState<string[]>(initialWarnings ?? [])
  const [deadline, setDeadline] = useState('')
  const [minutes, setMinutes] = useState<{ text: string; witnesses: string } | null>(null)
  const [decision, setDecision] = useState({ value: 'Warning', note: '' })
  const inv = [['disciplinary']]
  const requestDefence = useAction(() => complianceApi.requestDefence(id, deadline || null), { success: (r) => tx('Savunma istendi; son gün {0}', [formatDate(r.deadline)]), invalidate: inv, onDone: (r) => setWarnings(r.warnings) })
  const saveMinutes = useAction(() => complianceApi.saveMinutes(id, minutes!.text, minutes!.witnesses), { success: tx('Tutanak kaydedildi'), invalidate: inv, onDone: (r) => { setWarnings(r.warnings); setMinutes(null) } })
  const decide = useAction(() => complianceApi.decide(id, decision.value, decision.note || undefined), { success: tx('Karar kaydedildi'), invalidate: inv, onDone: (r) => setWarnings(r.warnings) })
  const close = useAction(() => complianceApi.closeCase(id), { success: tx('Vaka kapatıldı'), invalidate: inv })
  const c = q.data
  const hr = !!meta.data?.canManage
  return (
    <Modal open size="xl" onClose={onClose} title={c ? `${c.employeeName ?? '—'} · ${c.categoryLabel}` : tx('Vaka')}
      note={c ? tx('Olay tarihi {0} · açan {1} · bu görüntüleme erişim kaydına yazıldı', [formatDate(c.incidentDate), c.createdBy]) : undefined}>
      {q.isPending || !c ? <RowsSkeleton /> : (
        <div className="space-y-4 text-[13px]">
          <Warnings items={warnings} />
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge tone={statusView[c.status].tone}>{statusView[c.status].label}</StatusBadge>
            {c.defenceOverdue && <StatusBadge tone="danger">{tx('Savunma süresi doldu')}</StatusBadge>}
            {c.decisionLabel && <StatusBadge tone="neutral">{c.decisionLabel}</StatusBadge>}
          </div>
          <section><h3 className="mb-1 font-medium">{tx('Olay')}</h3><p className="whitespace-pre-wrap">{c.description}</p></section>

          <section className="space-y-2 rounded-xl border border-border p-3">
            <h3 className="font-medium">{tx('Savunma')}</h3>
            {c.defenceNotice ? (
              <>
                <p className="text-[12px] text-muted-foreground">{tx('İstendi: {0} · son gün {1}', [formatDateTime(c.defenceRequestedAt), formatDate(c.defenceDeadline)])}</p>
                <details><summary className="cursor-pointer text-[12.5px]">{tx('Savunma istem yazısı')}</summary><pre className="mt-2 whitespace-pre-wrap font-sans text-[12.5px]">{c.defenceNotice}</pre></details>
                {c.defenceText
                  ? <div className="rounded-lg bg-muted/40 p-2"><p className="mb-1 text-[12px] text-muted-foreground">{tx('Çalışanın savunması · {0}', [formatDateTime(c.defenceSubmittedAt)])}</p><p className="whitespace-pre-wrap">{c.defenceText}</p></div>
                  : <p className="text-muted-foreground">{tx('Henüz savunma verilmedi.')}</p>}
              </>
            ) : <p className="text-muted-foreground">{tx('Savunma istenmedi.')}</p>}
            {hr && (c.status === 'Open' || c.status === 'DefenceRequested') && (
              <div className="flex flex-wrap items-end gap-2">
                <div className="w-48"><TextField label={tx('Son gün')} type="date" min={meta.data?.minDefenceDeadline} value={deadline || meta.data?.minDefenceDeadline || ''} onChange={(e) => setDeadline(e.target.value)}
                  hint={tx('En az {0} iş günü', [meta.data?.minDefenceBusinessDays ?? 2])} /></div>
                <Button size="sm" onClick={() => requestDefence.mutate(undefined)} disabled={requestDefence.isPending}>{c.defenceNotice ? tx('Yeniden iste') : tx('Savunma iste')}</Button>
              </div>
            )}
          </section>

          <section className="space-y-2 rounded-xl border border-border p-3">
            <h3 className="font-medium">{tx('Tutanak')}</h3>
            {minutes ? (
              <>
                <KvkkNote>{criminalNote}</KvkkNote>
                <TextAreaField label={tx('Tutanak metni')} rows={5} value={minutes.text} onChange={(e) => setMinutes({ ...minutes, text: e.target.value })} />
                <PiiHint text={minutes.text} />
                <TextField label={tx('Tanıklar (ad soyad, virgülle)')} value={minutes.witnesses} onChange={(e) => setMinutes({ ...minutes, witnesses: e.target.value })} hint={tx('Tanık adları çalışana gösterilmez.')} />
                <Button size="sm" onClick={() => saveMinutes.mutate(undefined)} disabled={saveMinutes.isPending}>{tx('Kaydet')}</Button>
              </>
            ) : (
              <>
                {c.minutesText ? <p className="whitespace-pre-wrap">{c.minutesText}</p> : <p className="text-muted-foreground">{tx('Tutanak yok.')}</p>}
                {c.witnesses && <p className="text-[12px] text-muted-foreground">{tx('Tanıklar: {0}', [c.witnesses])}</p>}
                {hr && c.status !== 'Closed' && <Button size="sm" variant="outline" onClick={() => setMinutes({ text: c.minutesText ?? '', witnesses: c.witnesses ?? '' })}>{tx('Düzenle')}</Button>}
              </>
            )}
          </section>

          <section className="space-y-2 rounded-xl border border-border p-3">
            <h3 className="font-medium">{tx('Karar')}</h3>
            {c.decision ? (
              <p>{c.decisionLabel}{c.decisionNote ? ` — ${c.decisionNote}` : ''} <span className="text-[12px] text-muted-foreground">({c.decidedBy}, {formatDateTime(c.decidedAt)})</span></p>
            ) : hr ? (
              <div className="space-y-2">
                <div className="grid gap-2 md:grid-cols-[14rem_1fr]">
                  <SelectField label={tx('Karar')} value={decision.value} onChange={(v) => setDecision({ ...decision, value: v })} options={meta.data?.decisions ?? []} />
                  <TextField label={tx('Gerekçe (çalışan görür)')} value={decision.note} onChange={(e) => setDecision({ ...decision, note: e.target.value })} />
                </div>
                <p className="text-[12px] text-muted-foreground">{tx('"İşlem yok" dışındaki kararlar için savunmanın alınmış ya da süresinin dolmuş olması gerekir (İş Kanunu m.19).')}</p>
                <Button size="sm" onClick={() => decide.mutate(undefined)} disabled={decide.isPending}><Gavel className="size-4" />{' '}{tx('Karar ver')}</Button>
              </div>
            ) : <p className="text-muted-foreground">{tx('Henüz karar verilmedi.')}</p>}
          </section>
          {hr && c.status !== 'Closed' && <Button variant="outline" onClick={() => close.mutate(undefined)}>{tx('Vakayı kapat')}</Button>}
          {c.status === 'Closed' && <p className="text-[12px] text-muted-foreground">{tx('Kapatıldı: {0}. Saklama süresi dolunca (KVKK › Saklama: Disiplin vakaları) silinir.', [formatDateTime(c.closedAt)])}</p>}
        </div>
      )}
    </Modal>
  )
}

/** /panel/disiplin — İK (yönetim) ve departman başı (salt okunur). */
export function DisciplinaryPage() {
  const meta = useQuery({ queryKey: ['disciplinary', 'meta'], queryFn: ({ signal }) => complianceApi.disciplinaryMeta(signal) })
  const [status, setStatus] = useState('all')
  const q = useQuery({ queryKey: ['disciplinary', 'list', status], queryFn: ({ signal }) => complianceApi.cases(status === 'all' ? undefined : status, signal) })
  const [open, setOpen] = useState<{ id: string; warnings?: string[] } | null>(null)
  const [creating, setCreating] = useState(false)
  const hr = !!meta.data?.canManage
  return (
    <>
      <PageHeader title={tx('Disiplin süreçleri')} description={tx('Savunma istemi, tutanak ve karar — yalnızca İK ve çalışanın departman yöneticisi görür.')}
        actions={hr ? <Button onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Yeni vaka')}</Button> : undefined} />
      <div className="space-y-5">
        {!hr && <InfoNote>{tx('Departmanınızdaki çalışanların vakalarını salt okunur görürsünüz; her görüntüleme kaydedilir.')}</InfoNote>}
        <Panel>
          <PanelHead title={tx('Vakalar')} action={<div className="w-48"><SelectField label={tx('Durum')} value={status} onChange={setStatus}
            options={[{ value: 'all', label: tx('Tümü') }, ...(Object.keys(statusView) as CaseStatus[]).map((k) => ({ value: k, label: statusView[k].label }))]} /></div>} />
          <PanelBody className="p-0">
            {q.isPending ? <div className="p-5"><RowsSkeleton /></div> : !q.data?.length ? <EmptyState icon={ScrollText} title={tx('Vaka yok')} /> : (
              <ul className="divide-y divide-border">
                {q.data.map((c) => (
                  <li key={c.id}>
                    <button type="button" className="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left text-[13px] hover:bg-muted/40" onClick={() => setOpen({ id: c.id })}>
                      <span className="min-w-0 flex-1"><span className="font-medium">{c.employeeName ?? '—'}</span>
                        <span className="block text-[12px] text-muted-foreground">{c.categoryLabel} · {formatDate(c.incidentDate)}{c.department ? ` · ${c.department}` : ''}{c.defenceDeadline ? ` · ${tx('savunma son gün {0}', [formatDate(c.defenceDeadline)])}` : ''}</span></span>
                      {c.defenceOverdue && <StatusBadge tone="danger">{tx('Süre doldu')}</StatusBadge>}
                      <StatusBadge tone={statusView[c.status].tone}>{statusView[c.status].label}</StatusBadge>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </PanelBody>
        </Panel>
      </div>
      {creating && <NewCaseModal onClose={() => setCreating(false)} onCreated={(id, warnings) => { setCreating(false); setOpen({ id, warnings }) }} />}
      {open && <CaseModal id={open.id} initialWarnings={open.warnings} onClose={() => setOpen(null)} />}
    </>
  )
}

function DefenceForm({ c }: { c: MyCase }) {
  const [text, setText] = useState('')
  const [warnings, setWarnings] = useState<string[]>([])
  const submit = useAction(() => complianceApi.submitDefence(c.id, text), { success: tx('Savunmanız iletildi'), invalidate: [['disciplinary']], onDone: (r) => setWarnings(r.warnings) })
  const confirm = useConfirm()
  return (
    <div className="space-y-2">
      <Warnings items={warnings} />
      <KvkkNote>{tx('Savunmanızda sağlık bilgisi, adli sicil kaydı gibi özel nitelikli verilerinizi paylaşmanız gerekmez.')}</KvkkNote>
      <TextAreaField label={tx('Yazılı savunmanız')} rows={6} value={text} onChange={(e) => setText(e.target.value)} />
      <PiiHint text={text} />
      <Button onClick={async () => { if (await confirm({ title: tx('Savunma gönderilsin mi?'), note: tx('Savunmanız gönderildikten sonra değiştirilemez.'), action: tx('Gönder'), destructive: false })) submit.mutate(undefined) }} disabled={text.trim().length < 10 || submit.isPending}>{tx('Savunmayı gönder')}</Button>
    </div>
  )
}

/** /panel/disiplin/savunmalarim — çalışanın kendi savunma talepleri ve kararlar. */
export function MyDefencesPage() {
  const q = useQuery({ queryKey: ['disciplinary', 'mine'], queryFn: ({ signal }) => complianceApi.myCases(signal) })
  return (
    <>
      <PageHeader title={tx('Savunmalarım')} description={tx('Sizden istenen yazılı savunmalar ve sonuçları.')} />
      {q.isPending ? <RowsSkeleton /> : !q.data?.length ? (
        <EmptyState icon={ScrollText} title={tx('Savunma talebi yok')} detail={tx('Hakkınızda savunma istendiğinde burada görünür ve bildirim alırsınız.')} />
      ) : (
        <div className="space-y-5">
          {q.data.map((c) => (
            <Panel key={c.id}>
              <PanelHead title={`${c.categoryLabel} · ${formatDate(c.incidentDate)}`} note={c.defenceDeadline ? tx('Savunma için son gün: {0}', [formatDate(c.defenceDeadline)]) : undefined}
                action={<StatusBadge tone={statusView[c.status].tone}>{statusView[c.status].label}</StatusBadge>} />
              <PanelBody className="space-y-4 text-[13px]">
                {c.defenceNotice && <pre className="whitespace-pre-wrap rounded-xl border border-border bg-muted/30 p-3 font-sans text-[13px]">{c.defenceNotice}</pre>}
                {c.defenceText ? (
                  <div><p className="mb-1 text-[12px] text-muted-foreground">{tx('Savunmanız · {0}', [formatDateTime(c.defenceSubmittedAt)])}</p><p className="whitespace-pre-wrap">{c.defenceText}</p></div>
                ) : c.canSubmitDefence ? <DefenceForm c={c} /> : c.status === 'DefenceRequested' && <InfoNote>{tx('Savunma süresi doldu. Ek açıklama için İK ile görüşebilirsiniz.')}</InfoNote>}
                {c.decision && (
                  <div className="rounded-xl border border-border p-3">
                    <p className="font-medium">{tx('Karar: {0}', [c.decisionLabel ?? c.decision])}</p>
                    {c.decisionNote && <p className="mt-1 whitespace-pre-wrap">{c.decisionNote}</p>}
                    <p className="mt-1 text-[12px] text-muted-foreground">{formatDateTime(c.decidedAt)}</p>
                  </div>
                )}
              </PanelBody>
            </Panel>
          ))}
        </div>
      )}
    </>
  )
}
