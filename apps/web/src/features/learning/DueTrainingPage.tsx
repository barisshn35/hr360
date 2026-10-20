/**
 * Dalga 11 (madde 84) zorunlu eğitim ve İSG eğitimi takibi: çalışanın son tarihli eğitimleri, süresi dolan
 * sertifikaları ve İSG eğitimleri; yönetici (kendi departmanı) ve İK için gecikme panosu ve son tarihli atama.
 * Hatırlatmalar sunucuda 30/7/0 gün kala çalışana ve yöneticisine uygulama içi bildirim olarak gider.
 */
import { useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { AlarmClock, CalendarClock, ClipboardCheck, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/tabs'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useAuth } from '@/auth/useAuth'
import { useMyEmployeeId } from '@/api/queries'
import { learningApi } from '@/api/learning'
import { learningW11Api, dueKindLabels, dueBadge, type DueItem } from '@/api/learningW11'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDate } from '@/lib/format'
import { tx } from '@/lib/i18n'

type TabKey = 'benim' | 'ekip'

function DueRow({ i, showPerson, action }: { i: DueItem; showPerson?: boolean; action?: ReactNode }) {
  const b = dueBadge(i.daysLeft)
  return (
    <li className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-[13.5px]">
      <span className="min-w-0 flex-1">
        <span className="font-medium">{i.title}</span>
        <span className="block text-[12px] text-muted-foreground">
          {[showPerson ? i.employee : null, showPerson ? i.department : null, dueKindLabels[i.kind],
            i.kind === 'Training' ? tx('son tarih {0}', [formatDate(i.dueOn)]) : tx('geçerlilik bitişi {0}', [formatDate(i.dueOn)])]
            .filter(Boolean).join(' · ')}
        </span>
      </span>
      {i.mandatory && <StatusBadge tone="warning">{tx('Zorunlu')}</StatusBadge>}
      <StatusBadge tone={b.tone}>{b.label}</StatusBadge>
      {action}
    </li>
  )
}

/* ---------------------------------------------------------------- benim */

function Mine() {
  const q = useQuery({ queryKey: ['learning', 'due', 'me'], queryFn: ({ signal }) => learningW11Api.myDue(signal) })
  if (q.isPending) return <RowsSkeleton rows={3} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  if (!q.data.linked) return <EmptyState title={tx('Çalışan kaydı yok')} detail={tx('Bu hesap bir çalışan kaydına bağlı değil.')} />
  const items = q.data.items
  return (
    <Panel>
      <PanelHead title={tx('Son tarihi yaklaşan ve geçen kalemlerim')} note={tx('Önümüzdeki 60 gün ve gecikenler')} />
      {items.length === 0 ? (
        <EmptyState icon={ClipboardCheck} title={tx('Bekleyen zorunlu eğitiminiz yok')} detail={tx('Son tarihli eğitim, süresi dolan sertifika ya da İSG eğitimi bulunmuyor.')} />
      ) : (
        <ul className="divide-y divide-border">
          {items.map((i) => (
            <DueRow key={`${i.kind}-${i.sourceId}`} i={i}
              action={i.kind === 'Training' && i.courseId
                ? <Button asChild size="sm" variant="outline"><Link to={`/panel/egitim/${i.courseId}`}>{tx('Eğitime git')}</Link></Button>
                : undefined} />
          ))}
        </ul>
      )}
    </Panel>
  )
}

/* ---------------------------------------------------------------- ekip / İK */

function AssignModal({ departments, onClose }: { departments: Array<{ id: string; name: string }>; onClose: () => void }) {
  const courses = useQuery({ queryKey: ['learning', 'courses', 'all'], queryFn: ({ signal }) => learningApi.listCourses({}, signal) })
  const [courseId, setCourseId] = useState('')
  const [mode, setMode] = useState<'departman' | 'kisi'>('departman')
  const [departmentId, setDepartmentId] = useState(departments[0]?.id ?? '')
  const [employeeId, setEmployeeId] = useState('')
  const [dueOn, setDueOn] = useState('')
  const assignable = useQuery({ queryKey: ['learning', 'due', 'assignable'], enabled: mode === 'kisi', queryFn: ({ signal }) => learningW11Api.assignable(signal) })
  const save = useAction(() => learningW11Api.assign({
    courseId,
    departmentId: mode === 'departman' ? departmentId : undefined,
    employeeIds: mode === 'kisi' ? [employeeId] : undefined,
    dueOn: dueOn || null,
  }), {
    success: (r) => tx('{0} yeni atama, {1} güncelleme, {2} değişmedi', [r.created, r.updated, r.skipped]),
    invalidate: [['learning']],
    onDone: onClose,
  })
  const ready = courseId && (mode === 'departman' ? departmentId : employeeId)
  return (
    <Modal open onClose={onClose} title={tx('Eğitim ata')} note={tx('Tamamlanmış kayıtlar değişmez; mevcut kayıtların son tarihi güncellenir. Atanan çalışana bildirim gider.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ready || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Ata')}</Button></>}>
      <div className="space-y-3">
        <SelectField label={tx('Eğitim')} value={courseId} onChange={setCourseId}
          options={(courses.data ?? []).map((c) => ({ value: c.id, label: c.isMandatory ? tx('{0} (zorunlu)', [c.title]) : c.title }))} />
        <SelectField label={tx('Kime')} value={mode} onChange={(v) => setMode(v as 'departman' | 'kisi')}
          options={[{ value: 'departman', label: tx('Departmandaki herkese') }, { value: 'kisi', label: tx('Bir çalışana') }]} />
        {mode === 'departman' ? (
          <SelectField label={tx('Departman')} value={departmentId} onChange={setDepartmentId} options={departments.map((d) => ({ value: d.id, label: d.name }))} />
        ) : (
          <SelectField id="due-assign-employee" label={tx('Çalışan')} value={employeeId} onChange={setEmployeeId}
            options={(assignable.data ?? []).map((p) => ({ value: p.id, label: p.department ? `${p.fullName} · ${p.department}` : p.fullName }))}
            hint={assignable.isPending ? tx('Yükleniyor') : assignable.isError ? errMsg(assignable.error) : undefined} />
        )}
        <TextField label={tx('Son tarih')} type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)}
          hint={tx('Boş bırakılırsa son tarih olmadan atanır ve hatırlatma gitmez.')} />
      </div>
    </Modal>
  )
}

function Team() {
  const [dept, setDept] = useState('')
  const [within, setWithin] = useState('30')
  const [assign, setAssign] = useState(false)
  const q = useQuery({
    queryKey: ['learning', 'due', 'overdue', dept, within],
    queryFn: ({ signal }) => learningW11Api.overdue(dept || undefined, Number(within), signal),
  })
  if (q.isPending) return <RowsSkeleton rows={5} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const d = q.data
  const overdue = d.items.filter((i) => i.state === 'Overdue')
  const soon = d.items.filter((i) => i.state !== 'Overdue')
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-72">
          <SelectField label={tx('Departman')} value={dept || '__all__'} onChange={(v) => setDept(v === '__all__' ? '' : v)}
            options={[{ value: '__all__', label: tx('Tümü') }, ...d.departments.map((x) => ({ value: x.id, label: x.name }))]} />
        </div>
        <div className="w-48">
          <SelectField label={tx('Yaklaşan')} value={within} onChange={setWithin}
            options={[{ value: '7', label: tx('7 gün içinde') }, { value: '30', label: tx('30 gün içinde') }, { value: '90', label: tx('90 gün içinde') }]} />
        </div>
        <span className="flex-1" />
        <Button onClick={() => setAssign(true)}><Plus className="size-4" />{' '}{tx('Eğitim ata')}</Button>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Panel><PanelBody className="space-y-1"><p className="text-[12px] text-muted-foreground">{tx('Gecikmiş')}</p><p className="tabular text-2xl font-semibold text-destructive">{d.summary.overdue}</p></PanelBody></Panel>
        <Panel><PanelBody className="space-y-1"><p className="text-[12px] text-muted-foreground">{tx('Yaklaşan')}</p><p className="tabular text-2xl font-semibold">{d.summary.dueSoon}</p></PanelBody></Panel>
        {d.summary.byKind.map((k) => (
          <Panel key={k.kind}><PanelBody className="space-y-1"><p className="text-[12px] text-muted-foreground">{dueKindLabels[k.kind]}</p>
            <p className="text-[13px]">{tx('{0} gecikmiş · {1} yaklaşan', [k.overdue, k.dueSoon])}</p></PanelBody></Panel>
        ))}
      </div>
      {d.summary.byDepartment.length > 1 && (
        <Panel>
          <PanelHead title={tx('Departmanlara göre')} />
          <ul className="divide-y divide-border">
            {d.summary.byDepartment.map((x) => (
              <li key={x.department} className="flex items-center gap-3 px-5 py-2 text-[13.5px]">
                <span className="flex-1">{x.department}</span>
                <span className="tabular text-destructive">{tx('{0} gecikmiş', [x.overdue])}</span>
                <span className="tabular text-muted-foreground">{tx('{0} yaklaşan', [x.dueSoon])}</span>
              </li>
            ))}
          </ul>
        </Panel>
      )}
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><AlarmClock className="size-4 text-destructive" />{' '}{tx('Gecikenler')}</span>} />
        {overdue.length === 0 ? <PanelBody><p className="text-[13px] text-muted-foreground">{tx('Gecikmiş kalem yok.')}</p></PanelBody>
          : <ul className="divide-y divide-border">{overdue.map((i) => <DueRow key={`${i.kind}-${i.sourceId}-${i.employeeId}`} i={i} showPerson />)}</ul>}
      </Panel>
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><CalendarClock className="size-4 text-primary" />{' '}{tx('Yaklaşanlar')}</span>} />
        {soon.length === 0 ? <PanelBody><p className="text-[13px] text-muted-foreground">{tx('Seçilen aralıkta yaklaşan kalem yok.')}</p></PanelBody>
          : <ul className="divide-y divide-border">{soon.map((i) => <DueRow key={`${i.kind}-${i.sourceId}-${i.employeeId}`} i={i} showPerson />)}</ul>}
      </Panel>
      {assign && <AssignModal departments={d.departments} onClose={() => setAssign(false)} />}
    </div>
  )
}

export function DueTrainingPage() {
  const { can } = useAuth()
  const isManager = can('employee:viewAll')
  const { notLinked } = useMyEmployeeId()
  const [tab, setTab] = useTabParam<TabKey>('gorunum', notLinked && isManager ? 'ekip' : 'benim')
  const tabs: Array<TabDef<TabKey>> = [
    ...(!notLinked ? [{ key: 'benim' as TabKey, label: tx('Benim') }] : []),
    ...(isManager ? [{ key: 'ekip' as TabKey, label: tx('Ekip ve gecikmeler') }] : []),
  ]
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Zorunlu eğitim takibi')} description={tx('Son tarihli eğitimler, süresi dolan sertifikalar ve İSG eğitimleri; 30, 7 gün kala ve son günde hatırlatma.')} />
      <InfoNote>{tx('Hatırlatmalar çalışana ve zorunlu kalemlerde yöneticisine uygulama içi bildirim olarak gider. İSG eğitimleri İSG modülündeki kayıtlardan okunur.')}</InfoNote>
      <Tabs tabs={tabs} value={tab} onChange={setTab} label={tx('Eğitim takibi görünümü')} />
      {tab === 'benim' && !notLinked && <Mine />}
      {tab === 'ekip' && isManager && <Team />}
      {notLinked && !isManager && <EmptyState title={tx('Çalışan kaydı yok')} detail={tx('Bu hesap bir çalışan kaydına bağlı değil.')} />}
    </div>
  )
}
