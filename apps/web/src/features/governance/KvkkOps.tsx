import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, Bell, CheckCircle2, ClipboardCopy, FileText, Link2, Plus, ShieldAlert, ShieldCheck, Trash2, X } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useToast } from '@/components/ui/toast'
import { useDirectory } from '@/api/directory'
import {
  kvkkOpsApi, type AssessmentInput, type BreachInput, type BreachSeverity, type DataBreach, type FieldLevel, type NoticeState,
  type PiaAnswer, type PrivacyAssessment,
} from '@/api/kvkkOps'
import { formatDateTime } from '@/lib/format'
import { PersonSelect, errMsg, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/confirm'
import { PiiHint } from '@/components/PiiHint'

/* ================================================================== K2 aydınlatma metinleri */

function NoticeEditor({ n, onClose }: { n: NoticeState; onClose: () => void }) {
  const [title, setTitle] = useState(n.title)
  const [text, setText] = useState(n.text)
  const [version, setVersion] = useState('')
  const [note, setNote] = useState('')
  const save = useAction(() => kvkkOpsApi.publishNotice({ type: n.type, title, text, version: version || undefined, changeNote: note || undefined }),
    { success: tx('Yeni sürüm yayımlandı'), invalidate: [['privacy']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Yeni sürüm: {0}', [n.title])}
      note={tx('Yayımlanınca çalışanlardan yeni sürümü okumaları/onaylamaları istenir. Eski sürümler silinmez: kimin hangi metni gördüğü kanıt olarak saklanır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || title.trim().length < 3 || text.trim().length < 20}>{tx('Yayımla')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Başlık')} value={title} onChange={(e) => setTitle(e.target.value)} />
        <TextAreaField label={tx('Metin')} rows={10} value={text} onChange={(e) => setText(e.target.value)} />
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label={tx('Sürüm adı (boşsa tarih)')} value={version} onChange={(e) => setVersion(e.target.value)} placeholder={tx('ör. 2026.2')} />
          <TextField label={tx('Değişiklik notu')} value={note} onChange={(e) => setNote(e.target.value)} />
        </div>
        {n.custom && <button type="button" className="text-[12px] text-primary underline" onClick={() => setText(n.builtIn)}>{tx('Yerleşik metni geri yükle')}</button>}
      </div>
    </Modal>
  )
}

export function NoticesPanel() {
  const q = useQuery({ queryKey: ['privacy', 'notices'], queryFn: ({ signal }) => kvkkOpsApi.notices(signal) })
  const [edit, setEdit] = useState<NoticeState | null>(null)
  if (q.isPending) return <RowsSkeleton />
  return (
    <div className="space-y-4">
      <InfoNote>{tx('Aydınlatma metni bir bilgilendirmedir ve "okundu" olarak kaydedilir; açık rıza metinleri ise ayrı ayrı ve özgür iradeyle verilir. İkisi aynı onay kutusunda birleştirilmez (Kurul ilke kararı).')}</InfoNote>
      {q.data?.map((n) => (
        <Panel key={n.type}>
          <PanelHead title={<span className="flex flex-wrap items-center gap-2">{n.title}<StatusBadge tone={n.required ? 'info' : 'neutral'}>{n.required ? tx('Aydınlatma') : tx('Açık rıza')}</StatusBadge><StatusBadge tone="neutral">{tx('sürüm {0}', [n.version])}</StatusBadge>{n.custom && <StatusBadge tone="success">{tx('Şirkete özel')}</StatusBadge>}</span>}
            note={tx('{0} kişi güncel sürümü {1} · {2} kişi eski sürümde', [n.acknowledgedCurrent, n.required ? tx('okudu') : tx('onayladı'), n.onOlderVersion])}
            action={<Button size="sm" variant="outline" onClick={() => setEdit(n)}><FileText className="size-4" />{' '}{tx('Yeni sürüm')}</Button>} />
          <PanelBody>
            <p className="whitespace-pre-wrap text-[13px] text-muted-foreground">{n.text}</p>
            {n.history.length > 0 && (
              <details className="mt-3 text-[12.5px]"><summary className="cursor-pointer text-muted-foreground">{tx('Sürüm geçmişi ({0})', [n.history.length])}</summary>
                <ul className="mt-2 space-y-1">{n.history.map((h) => <li key={h.id}>{h.version} — {formatDateTime(h.publishedAt)} · {h.publishedBy}{h.changeNote ? ` · ${h.changeNote}` : ''}</li>)}</ul>
              </details>
            )}
          </PanelBody>
        </Panel>
      ))}
      {edit && <NoticeEditor n={edit} onClose={() => setEdit(null)} />}
    </div>
  )
}

/* ================================================================== K3 veri ihlali */

const severityLabel = (s: BreachSeverity) => (s === 'High' ? tx('Yüksek') : s === 'Low' ? tx('Düşük') : tx('Orta'))
const toLocalInput = (iso: string | null | undefined) => (iso ? new Date(new Date(iso).getTime() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16) : '')
const fromLocalInput = (v: string) => (v ? new Date(v).toISOString() : undefined)

function PeoplePicker({ value, onChange }: { value: string[]; onChange: (ids: string[]) => void }) {
  const dir = useDirectory()
  const names = useMemo(() => new Map((dir.data ?? []).map((d) => [d.id, d.fullName])), [dir.data])
  return (
    <div className="space-y-2">
      <PersonSelect label={tx('Etkilenen çalışan ekle')} value="" exclude={value} onChange={(id) => id && onChange([...value, id])} />
      <div className="flex flex-wrap gap-1.5">
        {value.map((id) => (
          <span key={id} className="inline-flex items-center gap-1 rounded-full bg-muted px-2.5 py-0.5 text-[12px]">
            {names.get(id) ?? id.slice(0, 8)}
            <button type="button" aria-label={tx('Kaldır')} onClick={() => onChange(value.filter((x) => x !== id))}><X className="size-3" /></button>
          </span>
        ))}
        {value.length > 0 && <button type="button" className="text-[12px] text-muted-foreground underline" onClick={() => onChange([])}>{tx('Temizle')}</button>}
      </div>
    </div>
  )
}

function BreachEditor({ b, onClose }: { b: DataBreach | null; onClose: () => void }) {
  const [f, setF] = useState<BreachInput & { detectedLocal: string; occurredLocal: string }>({
    title: b?.title ?? '', description: b?.description ?? '', dataCategories: b?.dataCategories ?? '', severity: b?.severity ?? 'Medium',
    cause: b?.cause ?? '', measures: b?.measures ?? '', affectedCount: b?.affectedCount ?? null, affectedEmployees: b?.affectedEmployees ?? [],
    detectedLocal: toLocalInput(b?.detectedAt ?? new Date().toISOString()), occurredLocal: toLocalInput(b?.occurredAt),
  })
  const body = (): BreachInput => ({
    title: f.title, description: f.description, dataCategories: f.dataCategories, severity: f.severity, cause: f.cause, measures: f.measures,
    affectedEmployees: f.affectedEmployees, affectedCount: f.affectedCount, detectedAt: fromLocalInput(f.detectedLocal), occurredAt: fromLocalInput(f.occurredLocal) ?? null,
  })
  const save = useAction(() => (b ? kvkkOpsApi.updateBreach(b.id, body()) : kvkkOpsApi.createBreach(body())), { success: tx('İhlal kaydı kaydedildi'), invalidate: [['privacy']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={b ? tx('İhlal kaydını düzenle') : tx('Yeni veri ihlali')}
      note={tx('KVKK m.12/5 ve Kurul kararı: ihlal, öğrenildiği andan itibaren en geç 72 saat içinde Kurul\'a bildirilir; etkilenen kişilere makul en kısa sürede bilgi verilir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !f.title.trim() || !f.description.trim()}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Başlık')} value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder={tx('ör. Bordro dosyası yanlış kişiye gönderildi')} />
        <TextAreaField label={tx('Ne oldu?')} rows={3} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
        <div className="grid gap-3 md:grid-cols-3">
          <TextField label={tx('Tespit zamanı')} type="datetime-local" value={f.detectedLocal} onChange={(e) => setF({ ...f, detectedLocal: e.target.value })} />
          <TextField label={tx('Gerçekleşme zamanı')} type="datetime-local" value={f.occurredLocal} onChange={(e) => setF({ ...f, occurredLocal: e.target.value })} />
          <SelectField label={tx('Önem')} value={f.severity ?? 'Medium'} onChange={(v) => setF({ ...f, severity: v as BreachSeverity })}
            options={(['Low', 'Medium', 'High'] as const).map((s) => ({ value: s, label: severityLabel(s) }))} />
        </div>
        <TextField label={tx('Etkilenen veri kategorileri')} value={f.dataCategories ?? ''} onChange={(e) => setF({ ...f, dataCategories: e.target.value })} placeholder={tx('ör. kimlik, iletişim, ücret')} />
        <PeoplePicker value={f.affectedEmployees ?? []} onChange={(ids) => setF({ ...f, affectedEmployees: ids })} />
        <TextField label={tx('Etkilenen kişi sayısı (çalışan dışı kişiler dahil)')} type="number" min={0} value={f.affectedCount ?? ''} onChange={(e) => setF({ ...f, affectedCount: e.target.value === '' ? null : Number(e.target.value) })} />
        <TextAreaField label={tx('Neden / kaynak')} rows={2} value={f.cause ?? ''} onChange={(e) => setF({ ...f, cause: e.target.value })} />
        <TextAreaField label={tx('Alınan ve alınacak önlemler')} rows={3} value={f.measures ?? ''} onChange={(e) => setF({ ...f, measures: e.target.value })} />
      </div>
    </Modal>
  )
}

function BoardFormModal({ b, onClose }: { b: DataBreach; onClose: () => void }) {
  const q = useQuery({ queryKey: ['privacy', 'breach-form', b.id], queryFn: () => kvkkOpsApi.boardForm(b.id) })
  const [ref, setRef] = useState(b.boardReference ?? '')
  const toast = useToast()
  const report = useAction(() => kvkkOpsApi.reportBreach(b.id, { reference: ref || undefined }), { success: tx('Kurul\'a bildirildi olarak işaretlendi'), invalidate: [['privacy']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Kurul bildirim formu taslağı')}
      note={tx('Taslağı kontrol edip VERBİS üzerinden Kurul\'a gönderin, ardından başvuru numarasını buraya yazın.')}
      footer={<><Button variant="outline" onClick={() => q.data && navigator.clipboard.writeText(q.data.text).then(() => toast.ok(tx('Kopyalandı')), () => toast.stop(tx('Kopyalanamadı')))}><ClipboardCopy className="size-4" />{' '}{tx('Kopyala')}</Button>
        {!b.reportedToBoardAt && <Button onClick={() => report.mutate(undefined)} disabled={report.isPending}>{tx('Bildirildi olarak işaretle')}</Button>}</>}>
      {q.isPending ? <RowsSkeleton /> : <pre className="max-h-[50vh] overflow-auto whitespace-pre-wrap rounded-xl bg-muted/50 p-3 text-[12.5px]">{q.data?.text}</pre>}
      {!b.reportedToBoardAt && <div className="mt-3"><TextField label={tx('Kurul başvuru/referans no')} value={ref} onChange={(e) => setRef(e.target.value)} /></div>}
    </Modal>
  )
}

export function BreachesPanel() {
  const q = useQuery({ queryKey: ['privacy', 'breaches'], queryFn: ({ signal }) => kvkkOpsApi.breaches(signal) })
  const [edit, setEdit] = useState<DataBreach | null | 'new'>(null)
  const [form, setForm] = useState<DataBreach | null>(null)
  const notify = useAction((id: string) => kvkkOpsApi.notifyBreach(id), { success: (r) => tx('{0} çalışana bilgilendirme gönderildi', [r.notified]), invalidate: [['privacy']] })
  const close = useAction((id: string) => kvkkOpsApi.closeBreach(id), { success: tx('İhlal kaydı kapatıldı'), invalidate: [['privacy']] })
  const confirm = useConfirm()
  const askClose = async (b: DataBreach) => {
    const warn = !b.reportedToBoardAt ? ` ${tx('Kurul bildirimi henüz işaretlenmedi.')}` : ''
    if (await confirm({ title: tx('İhlal kaydı kapatılsın mı?'), note: tx('Kapanan kayıt artık düzenlenemez, Kurul formu ve bilgilendirme yapılamaz.') + warn, action: tx('Kapat') })) close.mutate(b.id)
  }
  return (
    <div className="space-y-4">
      <div className="flex justify-end"><Button onClick={() => setEdit('new')}><ShieldAlert className="size-4" />{' '}{tx('İhlal bildir')}</Button></div>
      {q.isPending ? <RowsSkeleton /> : !q.data?.length ? <EmptyState icon={ShieldCheck} title={tx('Kayıtlı ihlal yok')} detail={tx('Bir kişisel veri ihlalini öğrendiğinizde hemen buraya kaydedin; 72 saatlik süre tespit anından başlar.')} /> : (
        q.data.map((b) => (
          <Panel key={b.id}>
            <PanelHead title={<span className="flex flex-wrap items-center gap-2">{b.title}
              <StatusBadge tone={b.severity === 'High' ? 'danger' : b.severity === 'Medium' ? 'warning' : 'neutral'}>{severityLabel(b.severity)}</StatusBadge>
              <StatusBadge tone={b.status === 'Closed' ? 'neutral' : b.status === 'Reported' ? 'success' : b.overdue ? 'danger' : 'warning'}>
                {b.status === 'Closed' ? tx('Kapandı') : b.reportedToBoardAt ? tx('Kurul\'a bildirildi') : b.overdue ? tx('72 saat aşıldı') : tx('{0} saat kaldı', [Math.max(0, Math.floor(b.hoursLeft ?? 0))])}
              </StatusBadge></span>}
              note={tx('Tespit: {0} · son bildirim: {1}', [formatDateTime(b.detectedAt), formatDateTime(b.boardDeadline)])} />
            <PanelBody className="space-y-3">
              <p className="text-[13px]">{b.description}</p>
              <dl className="grid gap-x-6 gap-y-1 text-[12.5px] md:grid-cols-2">
                <div><dt className="inline text-muted-foreground">{tx('Veri kategorileri')}: </dt><dd className="inline">{b.dataCategories ?? '—'}</dd></div>
                <div><dt className="inline text-muted-foreground">{tx('Etkilenen')}: </dt><dd className="inline">{b.affectedCount ?? '—'}</dd></div>
                <div><dt className="inline text-muted-foreground">{tx('Önlemler')}: </dt><dd className="inline">{b.measures ?? '—'}</dd></div>
                <div><dt className="inline text-muted-foreground">{tx('Bilgilendirme')}: </dt><dd className="inline">{b.subjectsNotifiedAt ? formatDateTime(b.subjectsNotifiedAt) : tx('yapılmadı')}</dd></div>
                {b.reportedToBoardAt && <div><dt className="inline text-muted-foreground">{tx('Kurul bildirimi')}: </dt><dd className="inline">{formatDateTime(b.reportedToBoardAt)}{b.boardReference ? ` · ${b.boardReference}` : ''}{b.lateReport ? ` (${tx('geç')})` : ''}</dd></div>}
              </dl>
              {b.status !== 'Closed' && (
                <div className="flex flex-wrap gap-2">
                  <Button size="sm" variant="outline" onClick={() => setEdit(b)}>{tx('Düzenle')}</Button>
                  <Button size="sm" variant="outline" onClick={() => setForm(b)}><FileText className="size-4" />{' '}{tx('Kurul formu')}</Button>
                  <Button size="sm" variant="outline" disabled={!b.affectedEmployees.length || notify.isPending} onClick={() => notify.mutate(b.id)}><Bell className="size-4" />{' '}{tx('Etkilenenleri bilgilendir')}</Button>
                  <Button size="sm" variant="outline" disabled={close.isPending} onClick={() => askClose(b)}>{tx('Kapat')}</Button>
                </div>
              )}
            </PanelBody>
          </Panel>
        ))
      )}
      {edit && <BreachEditor b={edit === 'new' ? null : edit} onClose={() => setEdit(null)} />}
      {form && <BoardFormModal b={form} onClose={() => setForm(null)} />}
    </div>
  )
}

/* ================================================================== K8 başvuru araçları */

export function ExternalRequestModal({ onClose }: { onClose: () => void }) {
  const [f, setF] = useState({ kind: 'Access', personName: '', employeeId: '', channel: 'Email', contact: '', details: '', received: toLocalInput(new Date().toISOString()) })
  const save = useAction(() => kvkkOpsApi.createExternalRequest({
    kind: f.kind, personName: f.personName, employeeId: f.employeeId || null, channel: f.channel, contact: f.contact || undefined,
    details: f.details || undefined, receivedAt: fromLocalInput(f.received),
  }), { success: tx('Başvuru kaydedildi'), invalidate: [['privacy']], onDone: onClose })
  const channelLabel: Record<string, string> = { Email: tx('E-posta'), Kep: tx('KEP'), Mail: tx('Posta'), InPerson: tx('Elden') }
  return (
    <Modal open onClose={onClose} title={tx('Panel dışı başvuru kaydet')} note={tx('30 günlük süre başvurunun size ulaştığı tarihten başlar. Kimlik doğrulanmadan kişisel veri içeren yanıt verilmez.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !f.personName.trim()}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <div className="grid gap-3 md:grid-cols-2">
          <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => setF({ ...f, kind: v })} options={[
            { value: 'Access', label: tx('Bilgi/erişim talebi') }, { value: 'Rectification', label: tx('Düzeltme') }, { value: 'Erasure', label: tx('Silme/yok etme') }, { value: 'Objection', label: tx('İtiraz') }]} />
          <SelectField label={tx('Kanal')} value={f.channel} onChange={(v) => setF({ ...f, channel: v })} options={Object.entries(channelLabel).map(([value, label]) => ({ value, label }))} />
        </div>
        <TextField label={tx('Başvurucunun adı')} value={f.personName} onChange={(e) => setF({ ...f, personName: e.target.value })} />
        <PersonSelect label={tx('Çalışan kaydı (varsa)')} value={f.employeeId} onChange={(v) => setF({ ...f, employeeId: v })} />
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label={tx('Yanıt adresi')} value={f.contact} onChange={(e) => setF({ ...f, contact: e.target.value })} />
          <TextField label={tx('Ulaştığı tarih')} type="datetime-local" value={f.received} onChange={(e) => setF({ ...f, received: e.target.value })} />
        </div>
        <TextAreaField label={tx('Başvuru içeriği')} rows={3} value={f.details} onChange={(e) => setF({ ...f, details: e.target.value })} />
        <PiiHint text={f.details} />
      </div>
    </Modal>
  )
}

export function VerifyIdentityButton({ id }: { id: string }) {
  const meta = useQuery({ queryKey: ['privacy', 'request-meta'], queryFn: ({ signal }) => kvkkOpsApi.requestMeta(signal), staleTime: 600_000 })
  const [open, setOpen] = useState(false)
  const [method, setMethod] = useState('RegisteredEmail')
  const save = useAction(() => kvkkOpsApi.verifyRequest(id, method), { success: tx('Kimlik doğrulandı'), invalidate: [['privacy']], onDone: () => setOpen(false) })
  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)}><ShieldCheck className="size-4" />{' '}{tx('Kimliği doğrula')}</Button>
      {open && (
        <Modal open onClose={() => setOpen(false)} title={tx('Başvurucunun kimliğini doğrula')} note={tx('Kimlik belgesinin kopyası saklanmaz; yalnızca yöntem, doğrulayan ve zaman kaydedilir.')}
          footer={<><Button variant="outline" onClick={() => setOpen(false)}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending}>{tx('Doğrulandı')}</Button></>}>
          <SelectField label={tx('Yöntem')} value={method} onChange={setMethod} options={(meta.data?.verificationMethods ?? []).filter((m) => m.value !== 'Session').map((m) => ({ value: m.value, label: txServer(m.label) }))} />
        </Modal>
      )}
    </>
  )
}

/** Yanıt şablonu seçici: seçilen şablon {ad}/{tarih} doldurularak metne yazılır. */
export function ResponseTemplatePicker({ kind, name, date, onPick }: { kind: string; name: string; date: string; onPick: (text: string, outcome: string) => void }) {
  const meta = useQuery({ queryKey: ['privacy', 'request-meta'], queryFn: ({ signal }) => kvkkOpsApi.requestMeta(signal), staleTime: 600_000 })
  const list = (meta.data?.templates ?? []).filter((t) => t.kind === kind || t.kind === '*')
  if (!list.length) return null
  return (
    <SelectField label={tx('Yanıt şablonu')} value="" onChange={(k) => {
      const t = list.find((x) => x.key === k)
      if (t) onPick(t.text.replaceAll('{ad}', name).replaceAll('{tarih}', date), t.outcome)
    }} options={list.map((t) => ({ value: t.key, label: t.title }))} />
  )
}

/* ================================================================== K9 gizlilik etki değerlendirmesi */

const riskTone = { Low: 'success', Medium: 'warning', High: 'danger' } as const
const riskLabel = (r: PrivacyAssessment['risk']) => (r === 'High' ? tx('Yüksek risk') : r === 'Medium' ? tx('Orta risk') : tx('Düşük risk'))
/** Hiç soru yanıtlanmamışsa sunucunun hesapladığı "Düşük" yanıltıcıdır; "Değerlendirilmedi" gösterilir. */
const isUnanswered = (a: PrivacyAssessment) => !Object.values(a.answers ?? {}).some((x) => !!x?.answer)

function AssessmentEditor({ a, onClose }: { a: PrivacyAssessment | null; onClose: () => void }) {
  const meta = useQuery({ queryKey: ['privacy', 'pia-questions'], queryFn: ({ signal }) => kvkkOpsApi.piaQuestions(signal) })
  const [f, setF] = useState<AssessmentInput>({ subject: a?.subject ?? '', kind: a?.kind ?? 'Integration', providerKey: a?.providerKey ?? null, answers: a?.answers ?? {} })
  const save = useAction(() => (a ? kvkkOpsApi.updateAssessment(a.id, f) : kvkkOpsApi.createAssessment(f)), { success: tx('Değerlendirme kaydedildi'), invalidate: [['privacy']], onDone: onClose })
  const setAnswer = (code: string, answer: PiaAnswer) => setF({ ...f, answers: { ...f.answers, [code]: { answer, note: f.answers[code]?.note ?? null } } })
  return (
    <Modal open size="lg" onClose={onClose} title={a ? tx('Değerlendirmeyi düzenle') : tx('Yeni gizlilik etki değerlendirmesi')}
      note={tx('Yeni bir entegrasyon, özel alan ya da süreç devreye alınmadan önce doldurun. Risk yanıtlardan hesaplanır; değişiklik onayı düşürür.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !f.subject.trim()}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Konu')} value={f.subject} onChange={(e) => setF({ ...f, subject: e.target.value })} placeholder={tx('ör. Slack entegrasyonu')} />
        <div className="grid gap-3 md:grid-cols-2">
          <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => setF({ ...f, kind: v as AssessmentInput['kind'] })}
            options={[{ value: 'Integration', label: tx('Entegrasyon') }, { value: 'CustomField', label: tx('Özel alan') }, { value: 'Process', label: tx('Süreç') }]} />
          {f.kind === 'Integration' && <SelectField label={tx('Hizmet sağlayıcı')} value={f.providerKey ?? ''} onChange={(v) => setF({ ...f, providerKey: v || null })}
            options={[{ value: '', label: '—' }, ...(meta.data?.providers ?? []).map((p) => ({ value: p.key, label: p.name }))]} />}
        </div>
        <ol className="space-y-3">
          {meta.data?.questions.map((qq, i) => (
            <li key={qq.code} className="rounded-xl border border-border p-3">
              <p className="text-[13px]">{i + 1}. {txServer(qq.text)}</p>
              <div className="mt-2 flex gap-2" role="radiogroup">
                {(['yes', 'no', 'na'] as const).map((v) => (
                  <button key={v} type="button" role="radio" aria-checked={f.answers[qq.code]?.answer === v} onClick={() => setAnswer(qq.code, v)}
                    className={`rounded-full border px-3 py-1 text-[12px] ${f.answers[qq.code]?.answer === v ? (v === qq.riskyAnswer ? 'border-destructive bg-destructive/10' : 'border-primary bg-primary/10') : 'border-border'}`}>
                    {v === 'yes' ? tx('Evet') : v === 'no' ? tx('Hayır') : tx('Uygulanmaz')}
                  </button>
                ))}
              </div>
            </li>
          ))}
        </ol>
      </div>
    </Modal>
  )
}

export function AssessmentsPanel() {
  const q = useQuery({ queryKey: ['privacy', 'assessments'], queryFn: ({ signal }) => kvkkOpsApi.assessments(signal) })
  const [edit, setEdit] = useState<PrivacyAssessment | null | 'new'>(null)
  const approve = useAction((id: string) => kvkkOpsApi.approveAssessment(id), { success: tx('Değerlendirme onaylandı'), invalidate: [['privacy']] })
  const del = useAction((id: string) => kvkkOpsApi.deleteAssessment(id), { success: tx('Silindi'), invalidate: [['privacy']] })
  const confirm = useConfirm()
  const askDelete = async (a: PrivacyAssessment) => {
    if (await confirm({ title: tx('“{0}” değerlendirmesi silinsin mi?', [a.subject]), note: tx('Taslak değerlendirme ve yanıtları kalıcı olarak silinir.'), action: tx('Sil') })) del.mutate(a.id)
  }
  return (
    <div className="space-y-4">
      <div className="flex justify-end"><Button onClick={() => setEdit('new')}><Plus className="size-4" />{' '}{tx('Yeni değerlendirme')}</Button></div>
      {q.isPending ? <RowsSkeleton /> : !q.data?.length ? <EmptyState icon={ShieldCheck} title={tx('Değerlendirme yok')} detail={tx('Kullanılan her entegrasyon için onaylı bir değerlendirme önerilir; uyum ekranı eksikleri gösterir.')} /> : (
        <Panel><PanelBody className="p-0"><ul className="divide-y divide-border">
          {q.data.map((a) => (
            <li key={a.id} className="flex flex-wrap items-center gap-3 px-5 py-3">
              <div className="min-w-0 flex-1">
                <p className="text-[13.5px] font-medium">{a.subject}</p>
                <p className="text-[12px] text-muted-foreground">{a.status === 'Approved' ? tx('Onaylayan: {0} · {1}', [a.approvedBy ?? '—', a.approvedAt ? formatDateTime(a.approvedAt) : '']) : tx('Taslak · {0}', [a.createdBy])}</p>
              </div>
              {isUnanswered(a) ? <StatusBadge tone="neutral">{tx('Değerlendirilmedi')}</StatusBadge> : <StatusBadge tone={riskTone[a.risk]}>{riskLabel(a.risk)}</StatusBadge>}
              <StatusBadge tone={a.status === 'Approved' ? 'success' : 'neutral'}>{a.status === 'Approved' ? tx('Onaylı') : tx('Taslak')}</StatusBadge>
              <Button size="sm" variant="outline" onClick={() => setEdit(a)}>{tx('Düzenle')}</Button>
              {a.status !== 'Approved' && <Button size="sm" onClick={() => approve.mutate(a.id)}><CheckCircle2 className="size-4" />{' '}{tx('Onayla')}</Button>}
              {a.status !== 'Approved' && <Button size="sm" variant="ghost" aria-label={tx('Sil')} onClick={() => askDelete(a)}><Trash2 className="size-4" /></Button>}
            </li>
          ))}
        </ul></PanelBody></Panel>
      )}
      {edit && <AssessmentEditor a={edit === 'new' ? null : edit} onClose={() => setEdit(null)} />}
    </div>
  )
}

/* ================================================================== G20 alan yetkileri */

const levelLabel = (l: FieldLevel) => ({ everyone: tx('Tüm çalışanlar'), manager: tx('Bölüm yöneticisi ve İK'), hr: tx('Yalnızca İK'), self: tx('Yalnızca kendisi') }[l])

export function FieldPoliciesPanel() {
  const q = useQuery({ queryKey: ['privacy', 'field-policies'], queryFn: ({ signal }) => kvkkOpsApi.fieldPolicies(signal) })
  const toast = useToast()
  const set = async (field: string, level: FieldLevel) => {
    try { await kvkkOpsApi.setFieldPolicy(field, level); toast.ok(tx('Kaydedildi')); void q.refetch() } catch (e) { toast.stop(errMsg(e)) }
  }
  if (q.isPending) return <RowsSkeleton />
  return (
    <div className="space-y-4">
      <InfoNote>{tx('Profil alanlarının başka çalışanlara hangi düzeyden itibaren görüneceğini belirleyin. Çalışan kendi bilgilerini her zaman görür. T.C. kimlik no ve IBAN yalnızca İK\'ya ya da yalnızca çalışanın kendisine açılabilir; İK\'nın her açışı gerekçeyle erişim kaydına yazılır.')}</InfoNote>
      <Panel><PanelBody className="p-0"><ul className="divide-y divide-border">
        {q.data?.map((f) => (
          <li key={f.field} className="flex flex-wrap items-center gap-3 px-5 py-3">
            <div className="min-w-0 flex-1"><p className="text-[13.5px] font-medium">{txServer(f.label)}</p>
              <p className="text-[12px] text-muted-foreground">{f.updatedAt ? tx('{0} · {1}', [f.updatedBy ?? '', formatDateTime(f.updatedAt)]) : tx('Varsayılan')}</p></div>
            <div className="w-60"><SelectField label={tx('Görebilen')} value={f.level} onChange={(v) => void set(f.field, v as FieldLevel)} options={f.allowed.map((l) => ({ value: l, label: levelLabel(l) }))} /></div>
          </li>
        ))}
      </ul></PanelBody></Panel>
    </div>
  )
}

/* ================================================================== G21 denetim zinciri */

export function AuditIntegrityPanel() {
  const siem = useQuery({ queryKey: ['audit', 'siem'], queryFn: ({ signal }) => kvkkOpsApi.siem(signal) })
  const verify = useAction(() => kvkkOpsApi.verifyAudit(), {})
  const r = verify.data
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Link2 className="size-4" />{' '}{tx('Değiştirilemez denetim kaydı')}</span>}
        note={tx('Her kayıt bir öncekinin özetini (SHA-256) içerir; geçmişte bir satırın değiştirilmesi ya da araya satır silinmesi doğrulamada görünür. Kayıtlar güncellenemez.')}
        action={<Button size="sm" variant="outline" onClick={() => verify.mutate(undefined)} disabled={verify.isPending}>{tx('Zinciri doğrula')}</Button>} />
      <PanelBody className="space-y-2 text-[13px]">
        {r && (
          <p className={`flex items-center gap-2 ${r.ok ? 'text-[hsl(var(--success))]' : 'text-destructive'}`}>
            {r.ok ? <CheckCircle2 className="size-4" /> : <AlertTriangle className="size-4" />}
            {r.ok ? tx('Zincir sağlam: {0} kayıt (#{1}–#{2}).', [r.rows, r.fromSeq ?? 0, r.toSeq ?? 0])
              : tx('Bütünlük sorunu: {0} değiştirilmiş, {1} kopuk bağ, {2} eksik sıra. İlk sorun #{3}.', [r.tampered, r.broken, r.gaps, r.firstProblemSeq ?? '—'])}
          </p>
        )}
        {r?.anchorProblem && (
          <p className="flex items-center gap-2 text-destructive"><AlertTriangle className="size-4" />{tx('Gecelik çapa tutmuyor: {0}', [txServer(r.anchorProblem)])}</p>
        )}
        {r?.head && <p className="break-all text-[11.5px] text-muted-foreground">{tx('Son özet')}: {r.head}</p>}
        {r && (
          <p className="text-muted-foreground">
            {r.nightly
              ? tx('Gecelik otomatik kontrol: {0} · {1}', [formatDateTime(r.nightly.checkedAt), r.nightly.ok ? tx('sorun yok') : txServer(r.nightly.problem ?? '')])
              : tx('Gecelik otomatik kontrol henüz çalışmadı.')}
          </p>
        )}
        <p className="text-muted-foreground">
          {siem.data?.configured
            ? tx('SIEM aktarımı açık ({0}) · {1} kayıt gönderildi{2}', [siem.data.endpoint ?? '', siem.data.state?.sent ?? 0, siem.data.state?.lastError ? ` · ${tx('son hata')}: ${siem.data.state.lastError}` : ''])
            : tx('SIEM aktarımı kapalı. Açmak için .env dosyasında SIEM_SYSLOG_ENDPOINT=udp://sunucu:514 tanımlayın; kullanıcılar takma adla, içerik olmadan gönderilir.')}
        </p>
      </PanelBody>
    </Panel>
  )
}
