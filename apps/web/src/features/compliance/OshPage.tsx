import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, BellRing, GraduationCap, HardHat, Lock, Plus, Stethoscope, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { complianceApi, type ExamResult, type IncidentInput, type OshExam, type OshIncident } from '@/api/compliance'
import { formatDate } from '@/lib/format'
import { errMsg, isoDate, PersonSelect, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/Confirm'
import { KvkkNote, PeopleChecklist } from './shared'

const resultView: Record<ExamResult, { label: string; tone: 'success' | 'danger' | 'warning' }> = {
  Fit: { label: tx('Uygun'), tone: 'success' }, Unfit: { label: tx('Uygun değil'), tone: 'danger' }, Conditional: { label: tx('Şartlı uygun'), tone: 'warning' },
}
const examTypes = [
  { value: 'Periodic', label: tx('Periyodik') }, { value: 'PreEmployment', label: tx('İşe giriş') },
  { value: 'ReturnToWork', label: tx('İşe dönüş') }, { value: 'JobChange', label: tx('İş değişikliği') },
]
const dueView = { Overdue: { label: tx('Süresi geçti'), tone: 'danger' as const }, DueSoon: { label: tx('Yaklaşıyor'), tone: 'warning' as const } }

function IncidentModal({ initial, onClose }: { initial?: OshIncident; onClose: () => void }) {
  const [f, setF] = useState<IncidentInput>(initial ? {
    kind: initial.kind, occurredOn: initial.occurredOn, occurredTime: initial.occurredTime, location: initial.location, description: initial.description,
    injuredEmployeeId: initial.injuredEmployeeId, lostDays: initial.lostDays, rootCause: initial.rootCause, correctiveActions: initial.correctiveActions,
    sgkNotifiedOn: initial.sgkNotifiedOn, sgkReference: initial.sgkReference, status: initial.status,
  } : { kind: 'Accident', occurredOn: isoDate(), description: '', location: '', lostDays: 0, status: 'Open' })
  const save = useAction(() => (initial ? complianceApi.updateIncident(initial.id, f) : complianceApi.createIncident(f)),
    { success: tx('Olay kaydedildi'), invalidate: [['osh']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={initial ? tx('Olayı düzenle') : tx('Yeni iş kazası / ramak kala')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || f.description.trim().length < 5}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <div className="grid gap-3 md:grid-cols-3">
          <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => setF({ ...f, kind: v as IncidentInput['kind'] })}
            options={[{ value: 'Accident', label: tx('İş kazası') }, { value: 'NearMiss', label: tx('Ramak kala') }]} />
          <TextField label={tx('Tarih')} type="date" value={f.occurredOn} onChange={(e) => setF({ ...f, occurredOn: e.target.value })} />
          <TextField label={tx('Saat')} type="time" value={f.occurredTime ?? ''} onChange={(e) => setF({ ...f, occurredTime: e.target.value })} />
        </div>
        <TextField label={tx('Yer')} value={f.location ?? ''} onChange={(e) => setF({ ...f, location: e.target.value })} />
        <TextAreaField label={tx('Ne oldu?')} rows={3} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
        <KvkkNote>{tx('Yaralanmanın tıbbi ayrıntılarını (tanı, tedavi) buraya yazmayın. Sağlık bilgisi yalnızca işyeri hekiminin şifreli muayene notunda tutulur.')}</KvkkNote>
        <div className="grid gap-3 md:grid-cols-2">
          <PersonSelect value={f.injuredEmployeeId ?? ''} onChange={(v) => setF({ ...f, injuredEmployeeId: v || null })} label={tx('Etkilenen çalışan (isteğe bağlı)')} />
          <TextField label={tx('Kayıp iş günü')} type="number" min={0} value={f.lostDays ?? 0} onChange={(e) => setF({ ...f, lostDays: Number(e.target.value) })} />
        </div>
        <TextAreaField label={tx('Kök neden')} rows={2} value={f.rootCause ?? ''} onChange={(e) => setF({ ...f, rootCause: e.target.value })} />
        <TextAreaField label={tx('Düzeltici / önleyici faaliyetler')} rows={2} value={f.correctiveActions ?? ''} onChange={(e) => setF({ ...f, correctiveActions: e.target.value })} />
        {f.kind === 'Accident' && (
          <div className="grid gap-3 md:grid-cols-2">
            <TextField label={tx('SGK bildirim tarihi')} type="date" value={f.sgkNotifiedOn ?? ''} onChange={(e) => setF({ ...f, sgkNotifiedOn: e.target.value || null })} hint={tx('Kazadan sonraki 3 iş günü içinde (5510 s. K. m.13).')} />
            <TextField label={tx('SGK bildirim no')} value={f.sgkReference ?? ''} onChange={(e) => setF({ ...f, sgkReference: e.target.value })} />
          </div>
        )}
        <SelectField label={tx('Durum')} value={f.status ?? 'Open'} onChange={(v) => setF({ ...f, status: v as 'Open' | 'Closed' })}
          options={[{ value: 'Open', label: tx('Açık') }, { value: 'Closed', label: tx('Kapalı') }]} />
      </div>
    </Modal>
  )
}

function IncidentsTab() {
  const q = useQuery({ queryKey: ['osh', 'incidents'], queryFn: ({ signal }) => complianceApi.incidents(signal) })
  const [edit, setEdit] = useState<OshIncident | 'new' | null>(null)
  const remove = useAction((id: string) => complianceApi.deleteIncident(id), { success: tx('Silindi'), invalidate: [['osh']] })
  const confirm = useConfirm()
  const overdue = (q.data ?? []).filter((i) => i.sgkOverdue).length
  return (
    <Panel>
      <PanelHead title={tx('İş kazası ve ramak kala kayıtları')} note={overdue ? tx('{0} kazanın SGK bildirimi gecikmiş!', [overdue]) : tx('İş kazası SGK’ya 3 iş günü içinde bildirilir.')}
        action={<Button onClick={() => setEdit('new')}><Plus className="size-4" />{' '}{tx('Yeni kayıt')}</Button>} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton /></div> : !q.data?.length ? <EmptyState icon={HardHat} title={tx('Kayıt yok')} /> : (
          <ul className="divide-y divide-border">
            {q.data.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setEdit(i)}>
                  <span className="font-medium">{i.kind === 'Accident' ? tx('İş kazası') : tx('Ramak kala')} · {formatDate(i.occurredOn)}{i.location ? ` · ${i.location}` : ''}</span>
                  <span className="block truncate text-[12px] text-muted-foreground">{i.description}{i.injuredName ? ` · ${i.injuredName}` : ''}{i.lostDays ? ` · ${tx('{0} gün kayıp', [i.lostDays])}` : ''}</span>
                </button>
                {i.kind === 'Accident' && (i.sgkNotifiedOn
                  ? <StatusBadge tone={i.sgkLate ? 'warning' : 'success'}>{i.sgkLate ? tx('SGK’ya geç bildirildi') : tx('SGK’ya bildirildi')}</StatusBadge>
                  : i.sgkOverdue
                    ? <StatusBadge tone="danger">{tx('SGK bildirimi gecikti ({0})', [formatDate(i.sgkDeadline)])}</StatusBadge>
                    : <StatusBadge tone="warning">{tx('SGK son gün: {0}', [formatDate(i.sgkDeadline)])}</StatusBadge>)}
                <StatusBadge tone={i.status === 'Open' ? 'info' : 'neutral'}>{i.status === 'Open' ? tx('Açık') : tx('Kapalı')}</StatusBadge>
                <Button size="sm" variant="ghost" aria-label={tx('Sil')} onClick={async () => { if (await confirm({ title: tx('İSG olay kaydı silinsin mi?'), note: tx('Kayıt kalıcı olarak silinir.'), action: tx('Sil') })) remove.mutate(i.id) }}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {edit && <IncidentModal initial={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} />}
    </Panel>
  )
}

function ExamModal({ physician, onClose }: { physician: boolean; onClose: () => void }) {
  const [f, setF] = useState({ employeeId: '', examType: 'Periodic', examDate: isoDate(), nextDueDate: '', hazardClass: 'Hazardous', result: 'Fit' as ExamResult, notes: '' })
  const save = useAction(() => complianceApi.createExam({ ...f, nextDueDate: f.nextDueDate || null, notes: physician && f.notes ? f.notes : null }),
    { success: (r) => tx('Muayene kaydedildi; sonraki muayene {0}', [formatDate(r.nextDueDate)]), invalidate: [['osh']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Muayene kaydı')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={!f.employeeId || save.isPending}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <PersonSelect value={f.employeeId} onChange={(v) => setF({ ...f, employeeId: v })} />
        <div className="grid gap-3 md:grid-cols-3">
          <SelectField label={tx('Tür')} value={f.examType} onChange={(v) => setF({ ...f, examType: v })} options={examTypes} />
          <TextField label={tx('Muayene tarihi')} type="date" value={f.examDate} onChange={(e) => setF({ ...f, examDate: e.target.value })} />
          <SelectField label={tx('Sonuç')} value={f.result} onChange={(v) => setF({ ...f, result: v as ExamResult })}
            options={(Object.keys(resultView) as ExamResult[]).map((k) => ({ value: k, label: resultView[k].label }))} />
        </div>
        <div className="grid gap-3 md:grid-cols-2">
          <SelectField label={tx('Tehlike sınıfı')} value={f.hazardClass} onChange={(v) => setF({ ...f, hazardClass: v })}
            options={[{ value: 'VeryHazardous', label: tx('Çok tehlikeli (1 yıl)') }, { value: 'Hazardous', label: tx('Tehlikeli (3 yıl)') }, { value: 'LessHazardous', label: tx('Az tehlikeli (5 yıl)') }]} />
          <TextField label={tx('Sonraki muayene (boşsa sınıfa göre)')} type="date" value={f.nextDueDate} onChange={(e) => setF({ ...f, nextDueDate: e.target.value })} />
        </div>
        {physician ? (
          <TextAreaField label={tx('Sağlık notu (yalnızca işyeri hekimi görür; şifreli)')} rows={4} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} />
        ) : (
          <KvkkNote>{tx('Sağlık notlarını yalnızca işyeri hekimi girebilir ve görebilir. İK ve yöneticiler yalnızca sonucu ve tarihleri görür.')}</KvkkNote>
        )}
      </div>
    </Modal>
  )
}

function NotesModal({ exam, onClose }: { exam: OshExam; onClose: () => void }) {
  const q = useQuery({ queryKey: ['osh', 'notes', exam.id], queryFn: () => complianceApi.examNotes(exam.id), retry: false, gcTime: 0, staleTime: 0 })
  const [draft, setDraft] = useState<string | null>(null)
  const save = useAction(() => complianceApi.setExamNotes(exam.id, draft ?? ''), { success: tx('Not kaydedildi'), invalidate: [['osh']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Sağlık notu — {0}', [exam.employeeName ?? ''])} note={tx('Bu görüntüleme erişim kaydına yazıldı.')}
      footer={<Button onClick={() => save.mutate(undefined)} disabled={draft === null || save.isPending}>{tx('Kaydet')}</Button>}>
      {q.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p> : q.isPending ? <RowsSkeleton rows={2} /> : <TextAreaField label={tx('Not')} rows={6} value={draft ?? q.data?.notes ?? ''} onChange={(e) => setDraft(e.target.value)} />}
    </Modal>
  )
}

function ExamsTab({ canManage, physician }: { canManage: boolean; physician: boolean }) {
  const [latest, setLatest] = useState(true)
  const q = useQuery({ queryKey: ['osh', 'exams', latest], queryFn: ({ signal }) => complianceApi.exams(latest, signal) })
  const [creating, setCreating] = useState(false)
  const [notes, setNotes] = useState<OshExam | null>(null)
  const remove = useAction((id: string) => complianceApi.deleteExam(id), { success: tx('Silindi'), invalidate: [['osh']] })
  const confirm = useConfirm()
  return (
    <Panel>
      <PanelHead title={tx('Sağlık muayeneleri')} note={physician ? tx('İşyeri hekimi olarak sağlık notlarını görebilirsiniz; her açılış kaydedilir.') : tx('Yalnızca sonuç ve tarihler gösterilir.')}
        action={<div className="flex items-center gap-2">
          <Button size="sm" variant="outline" onClick={() => setLatest(!latest)}>{latest ? tx('Tüm geçmiş') : tx('Yalnızca son muayene')}</Button>
          {canManage && <Button size="sm" onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Muayene')}</Button>}
        </div>} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton /></div> : !q.data?.length ? <EmptyState icon={Stethoscope} title={tx('Muayene kaydı yok')} /> : (
          <ul className="divide-y divide-border">
            {q.data.map((e) => (
              <li key={e.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <span className="min-w-0 flex-1"><span className="font-medium">{e.employeeName ?? '—'}</span>
                  <span className="block text-[12px] text-muted-foreground">{examTypes.find((t) => t.value === e.examType)?.label} · {formatDate(e.examDate)} · {tx('sonraki: {0}', [formatDate(e.nextDueDate)])}</span></span>
                {(e.dueState === 'Overdue' || e.dueState === 'DueSoon') && <StatusBadge tone={dueView[e.dueState].tone}>{dueView[e.dueState].label}</StatusBadge>}
                <StatusBadge tone={resultView[e.result].tone}>{resultView[e.result].label}</StatusBadge>
                {physician && <Button size="sm" variant="ghost" onClick={() => setNotes(e)}><Lock className="size-4" />{' '}{e.hasNotes ? tx('Not') : tx('Not ekle')}</Button>}
                {canManage && <Button size="sm" variant="ghost" aria-label={tx('Sil')} onClick={async () => { if (await confirm({ title: tx('Muayene kaydı silinsin mi?'), note: tx('Kayıt kalıcı olarak silinir.'), action: tx('Sil') })) remove.mutate(e.id) }}><Trash2 className="size-4" /></Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {creating && <ExamModal physician={physician} onClose={() => setCreating(false)} />}
      {notes && <NotesModal exam={notes} onClose={() => setNotes(null)} />}
    </Panel>
  )
}

function TrainingModal({ onClose }: { onClose: () => void }) {
  const [f, setF] = useState({ topic: '', trainingDate: isoDate(), durationHours: '8', validityMonths: '12', trainer: '', participantIds: [] as string[] })
  const save = useAction(() => complianceApi.createTraining({
    topic: f.topic, trainingDate: f.trainingDate, durationHours: Number(f.durationHours), validityMonths: f.validityMonths ? Number(f.validityMonths) : null,
    trainer: f.trainer || null, participantIds: f.participantIds,
  }), { success: tx('Eğitim kaydedildi'), invalidate: [['osh']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('İSG eğitimi')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || f.topic.trim().length < 3 || !f.participantIds.length}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <TextField label={tx('Konu')} value={f.topic} onChange={(e) => setF({ ...f, topic: e.target.value })} placeholder={tx('ör. Temel İSG eğitimi, Yangın tatbikatı')} />
        <div className="grid gap-3 md:grid-cols-3">
          <TextField label={tx('Tarih')} type="date" value={f.trainingDate} onChange={(e) => setF({ ...f, trainingDate: e.target.value })} />
          <TextField label={tx('Süre (saat)')} type="number" min={0.5} step={0.5} value={f.durationHours} onChange={(e) => setF({ ...f, durationHours: e.target.value })} />
          <TextField label={tx('Geçerlilik (ay)')} type="number" min={1} value={f.validityMonths} onChange={(e) => setF({ ...f, validityMonths: e.target.value })} />
        </div>
        <TextField label={tx('Eğitmen')} value={f.trainer} onChange={(e) => setF({ ...f, trainer: e.target.value })} />
        <PeopleChecklist label={tx('Katılımcılar')} value={f.participantIds} onChange={(ids) => setF({ ...f, participantIds: ids })} />
      </div>
    </Modal>
  )
}

function TrainingsTab({ canManage }: { canManage: boolean }) {
  const q = useQuery({ queryKey: ['osh', 'trainings'], queryFn: ({ signal }) => complianceApi.trainings(signal) })
  const [creating, setCreating] = useState(false)
  const remove = useAction((id: string) => complianceApi.deleteTraining(id), { success: tx('Silindi'), invalidate: [['osh']] })
  const confirm = useConfirm()
  const today = isoDate()
  return (
    <Panel>
      <PanelHead title={canManage ? tx('İSG eğitimleri') : tx('İSG eğitimlerim')} action={canManage ? <Button size="sm" onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Eğitim')}</Button> : undefined} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton /></div> : !q.data?.length ? <EmptyState icon={GraduationCap} title={tx('Eğitim kaydı yok')} /> : (
          <ul className="divide-y divide-border">
            {q.data.map((t) => (
              <li key={t.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <span className="min-w-0 flex-1"><span className="font-medium">{t.topic}</span>
                  <span className="block text-[12px] text-muted-foreground">{formatDate(t.trainingDate)} · {tx('{0} saat', [t.durationHours])}{canManage ? ` · ${tx('{0} katılımcı', [t.participantCount])}` : ''}{t.trainer ? ` · ${t.trainer}` : ''}</span></span>
                {t.expiresOn && <StatusBadge tone={t.expiresOn < today ? 'danger' : 'neutral'}>{tx('Geçerlilik: {0}', [formatDate(t.expiresOn)])}</StatusBadge>}
                {canManage && <Button size="sm" variant="ghost" aria-label={tx('Sil')} onClick={async () => { if (await confirm({ title: tx('Eğitim kaydı silinsin mi?'), note: tx('Kayıt kalıcı olarak silinir.'), action: tx('Sil') })) remove.mutate(t.id) }}><Trash2 className="size-4" /></Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {creating && <TrainingModal onClose={() => setCreating(false)} />}
    </Panel>
  )
}

function RemindersTab() {
  const q = useQuery({ queryKey: ['osh', 'reminders'], queryFn: ({ signal }) => complianceApi.oshReminders(signal) })
  if (q.isPending) return <RowsSkeleton />
  const d = q.data
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      <Panel>
        <PanelHead title={tx('Periyodik muayene')} note={tx('Süresi geçmiş ya da 30 gün içinde dolacak')} />
        <PanelBody className="p-0">
          {!d?.exams.length ? <EmptyState icon={Stethoscope} title={tx('Yaklaşan muayene yok')} /> : (
            <ul className="divide-y divide-border">{d.exams.map((e) => (
              <li key={e.employeeId} className="flex items-center gap-3 px-5 py-2.5 text-[13px]"><span className="flex-1">{e.employeeName}</span>
                <span className="text-muted-foreground">{formatDate(e.nextDueDate)}</span><StatusBadge tone={dueView[e.state].tone}>{dueView[e.state].label}</StatusBadge></li>
            ))}</ul>
          )}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Eğitim yenileme')} note={tx('Süresi geçmiş ya da 60 gün içinde dolacak')} />
        <PanelBody className="p-0">
          {!d?.trainings.length ? <EmptyState icon={GraduationCap} title={tx('Yenilenecek eğitim yok')} /> : (
            <ul className="divide-y divide-border">{d.trainings.map((t, i) => (
              <li key={i} className="flex items-center gap-3 px-5 py-2.5 text-[13px]"><span className="flex-1">{t.employeeName}<span className="block text-[12px] text-muted-foreground">{t.topic}</span></span>
                <span className="text-muted-foreground">{formatDate(t.expiresOn)}</span><StatusBadge tone={dueView[t.state].tone}>{dueView[t.state].label}</StatusBadge></li>
            ))}</ul>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}

type OshTab = 'incidents' | 'exams' | 'trainings' | 'reminders'

/** /panel/isg — Y6 iş sağlığı ve güvenliği. Çalışan kendi muayene sonucu ve eğitimlerini görür. */
export function OshPage() {
  const me = useQuery({ queryKey: ['osh', 'me'], queryFn: ({ signal }) => complianceApi.oshMe(signal) })
  const canManage = !!me.data?.canManage
  const physician = !!me.data?.isPhysician
  const [tab, setTab] = useTabParam<OshTab>('sekme', 'exams')
  const tabs = canManage
    ? [{ key: 'incidents' as const, label: tx('Olaylar') }, { key: 'exams' as const, label: tx('Muayeneler') }, { key: 'trainings' as const, label: tx('Eğitimler') }, { key: 'reminders' as const, label: tx('Hatırlatmalar') }]
    : [{ key: 'exams' as const, label: me.data?.isManager ? tx('Muayeneler') : tx('Muayenelerim') }, { key: 'trainings' as const, label: tx('Eğitimlerim') }]
  const current = tabs.some((t) => t.key === tab) ? tab : 'exams'
  return (
    <>
      <PageHeader title={tx('İş sağlığı ve güvenliği')} description={tx('İş kazaları, periyodik muayeneler ve İSG eğitimleri.')} />
      {me.isPending ? <RowsSkeleton /> : (
        <div className="space-y-5">
          <Tabs label={tx('İSG sekmeleri')} value={current} onChange={setTab} tabs={tabs} />
          {current === 'incidents' && canManage && <IncidentsTab />}
          {current === 'exams' && <ExamsTab canManage={canManage} physician={physician} />}
          {current === 'trainings' && <TrainingsTab canManage={canManage} />}
          {current === 'reminders' && canManage && <RemindersTab />}
          {!canManage && <InfoNote><span className="inline-flex items-center gap-1.5"><BellRing className="size-4" />{tx('Muayene ayrıntılarınızı (sağlık notları) yalnızca işyeri hekimi görür.')}</span></InfoNote>}
          {canManage && !physician && current === 'exams' && (
            <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground"><AlertTriangle className="size-3.5" />{tx('Sağlık notları özel nitelikli veridir; yalnızca "osh-physician" rolündeki işyeri hekimi erişebilir.')}</p>
          )}
        </div>
      )}
    </>
  )
}
