/**
 * G14: rol (unvan/departman) bazlı görev şablonları, ilk gün karşılama iletisi ayarları ve
 * plan sayfasındaki yol arkadaşı (buddy) / buluşma yeri / karşılama önizlemesi.
 */
import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Mail, Pencil, Plus, Trash2, UserRoundCheck, X } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { useConfirm } from '@/components/ui/Confirm'
import { localISODate } from '@/lib/dates'
import { isHr } from '@/auth/roles'
import { useCompanies } from '@/api/queries'
import { useDirectory } from '@/api/directory'
import { taskCategoryLabels, type OnboardingPlan, type PlanStatus, type TaskCategory } from '@/api/onboarding'
import { opsApi, ownerRoleLabels, type OwnerRole, type TaskTemplate, type TemplateInput, type TemplateItem } from '@/api/opsPlus'
import { formatDateTime } from '@/lib/format'
import { PersonSelect, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const NONE = '__none'

function useDepartments() {
  const companies = useCompanies()
  return useMemo(() => (companies.data ?? []).flatMap((c) => c.departments ?? []), [companies.data])
}

/**
 * Gösterilecek plan durumu: sunucu planı oluşturulunca "Sürüyor" sayar; başlangıç tarihi henüz
 * gelmemiş ve hiçbir görevine dokunulmamış plan kullanıcıya "Başlamadı" olarak gösterilir.
 * Kayıttaki durum değişmez (yalnızca gösterim).
 */
export function displayPlanStatus(plan: Pick<OnboardingPlan, 'status' | 'startDate' | 'tasks'>): PlanStatus {
  if (plan.status !== 'InProgress') return plan.status
  const touched = (plan.tasks ?? []).some((t) => t.status !== 'Pending')
  return !touched && plan.startDate.slice(0, 10) > localISODate() ? 'NotStarted' : plan.status
}

export function useIsOnboardingHr() {
  const { roles } = useAuth()
  return isHr(roles, 'ext-onboarding-manage')
}

/* ------------------------------------------------------------------ şablon düzenleyici */

function TemplateModal({ template, onClose }: { template: TaskTemplate | null; onClose: () => void }) {
  const departments = useDepartments()
  const [f, setF] = useState<TemplateInput>(() => template
    ? { name: template.name, positionTitle: template.positionTitle, departmentId: template.departmentId, isActive: template.isActive,
        items: template.items.map((i) => ({ title: i.title, category: i.category, ownerRole: i.ownerRole, offsetDays: i.offsetDays })) }
    : { name: '', positionTitle: '', departmentId: null, isActive: true, items: [{ title: '', category: 'IT', ownerRole: 'IT', offsetDays: 0 }] })
  const save = useAction(() => {
    const body = { ...f, positionTitle: f.positionTitle?.trim() || null, items: f.items.filter((i) => i.title.trim()) }
    return template ? opsApi.updateTemplate(template.id, body) : opsApi.createTemplate(body)
  }, { success: tx('Şablon kaydedildi'), invalidate: [['onboarding', 'templates']], onDone: onClose })
  const setItem = (i: number, patch: Partial<TemplateItem>) => setF({ ...f, items: f.items.map((x, j) => (j === i ? { ...x, ...patch } : x)) })
  return (
    <Modal open size="xl" onClose={onClose} title={template ? tx('Şablonu düzenle') : tx('Yeni görev şablonu')}
      note={tx('Plan açılırken yeni çalışanın unvanı ve departmanı eşleşen tüm etkin şablonlar uygulanır. Boş bırakılan alan her değere uyar.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !f.name.trim() || !f.items.some((i) => i.title.trim())}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <div className="grid gap-3 md:grid-cols-3">
          <TextField label={tx('Şablon adı')} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
          <TextField label={tx('Unvan')} hint={tx('Örn. Yazılım Mühendisi')} value={f.positionTitle ?? ''} onChange={(e) => setF({ ...f, positionTitle: e.target.value })} />
          <SelectField label={tx('Departman')} value={f.departmentId ?? NONE} onChange={(v) => setF({ ...f, departmentId: v === NONE ? null : v })}
            options={[{ value: NONE, label: tx('Tüm departmanlar') }, ...departments.map((d) => ({ value: d.id, label: d.name }))]} />
        </div>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.isActive} onCheckedChange={(v) => setF({ ...f, isActive: v === true })} />{' '}{tx('Etkin')}</label>
        <div className="space-y-2">
          <p className="text-[13px] font-medium">{tx('Görevler')}</p>
          {f.items.map((it, i) => (
            <div key={i} className="grid items-end gap-2 rounded-xl border border-border p-2.5 md:grid-cols-[1fr_150px_150px_90px_auto]">
              <TextField label={tx('Görev')} value={it.title} onChange={(e) => setItem(i, { title: e.target.value })} />
              <SelectField label={tx('Kategori')} value={it.category} onChange={(v) => setItem(i, { category: v as TaskCategory })}
                options={(Object.keys(taskCategoryLabels) as TaskCategory[]).map((c) => ({ value: c, label: taskCategoryLabels[c] }))} />
              <SelectField label={tx('Sahibi')} value={it.ownerRole} onChange={(v) => setItem(i, { ownerRole: v as OwnerRole })}
                options={(Object.keys(ownerRoleLabels) as OwnerRole[]).map((r) => ({ value: r, label: ownerRoleLabels[r] }))} />
              <TextField label={tx('Gün')} type="number" min={-60} max={365} value={it.offsetDays} onChange={(e) => setItem(i, { offsetDays: Number(e.target.value) })} />
              <Button variant="ghost" size="icon" aria-label={tx('Görevi kaldır')} onClick={() => setF({ ...f, items: f.items.filter((_, j) => j !== i) })}><X className="size-4" /></Button>
            </div>
          ))}
          <Button size="sm" variant="outline" onClick={() => setF({ ...f, items: [...f.items, { title: '', category: 'HR', ownerRole: 'HR', offsetDays: 0 }] })}><Plus className="size-4" />{' '}{tx('Görev ekle')}</Button>
          <p className="text-[12px] text-muted-foreground">{tx('“Gün” başlangıç tarihine göredir (−3: başlamadan 3 gün önce). Yönetici görevleri bölüm başına, yol arkadaşı görevleri plandaki buddy’ye, yeni çalışan görevleri kişinin kendisine atanır; İK ve BT görevleri ekip kuyruğunda kalır.')}</p>
        </div>
      </div>
    </Modal>
  )
}

export function TemplatesPanel() {
  const q = useQuery({ queryKey: ['onboarding', 'templates'], queryFn: ({ signal }) => opsApi.templates(signal) })
  const departments = useDepartments()
  const hr = useIsOnboardingHr()
  const [edit, setEdit] = useState<TaskTemplate | null | 'new'>(null)
  const del = useAction((id: string) => opsApi.deleteTemplate(id), { success: tx('Şablon silindi'), invalidate: [['onboarding', 'templates']] })
  const confirm = useConfirm()
  const askDelete = async (t: TaskTemplate) => {
    if (await confirm({ title: tx('“{0}” şablonu silinsin mi?', [t.name]), note: tx('Şablon yeni planlara artık eklenmez; önceden oluşturulmuş planlardaki görevler değişmez.'), action: tx('Sil') })) del.mutate(t.id)
  }
  const deptName = (id: string | null) => (id ? departments.find((d) => d.id === id)?.name ?? '—' : tx('Tüm departmanlar'))
  return (
    <Panel>
      <PanelHead title={tx('Rol görev şablonları')} note={tx('Unvana ve departmana göre otomatik eklenen görevler.')}
        action={hr && <Button size="sm" variant="outline" onClick={() => setEdit('new')}><Plus className="size-4" />{' '}{tx('Yeni şablon')}</Button>} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : !q.data?.length ? (
          <EmptyState title={tx('Şablon yok')} detail={tx('Örn. “Yazılım Mühendisi” için depo erişimi, geliştirme ortamı ve kod inceleme eşliği.')} />
        ) : (
          <ul className="divide-y divide-border">
            {q.data.map((t) => (
              <li key={t.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <span className="min-w-0 flex-1 font-medium">{t.name}
                  <span className="block text-[12px] font-normal text-muted-foreground">
                    {t.positionTitle ?? tx('Tüm unvanlar')} · {deptName(t.departmentId)} · {tx('{0} görev', [t.items.length])}
                  </span>
                </span>
                {!t.isActive && <StatusBadge tone="neutral">{tx('Pasif')}</StatusBadge>}
                {hr && <Button size="icon" variant="ghost" aria-label={tx('Düzenle')} onClick={() => setEdit(t)}><Pencil className="size-4" /></Button>}
                {hr && <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => askDelete(t)}><Trash2 className="size-4" /></Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {edit && <TemplateModal template={edit === 'new' ? null : edit} onClose={() => setEdit(null)} />}
    </Panel>
  )
}

/* ------------------------------------------------------------------ karşılama ayarları */

export function WelcomeSettingsPanel() {
  const q = useQuery({ queryKey: ['onboarding', 'settings'], queryFn: ({ signal }) => opsApi.settings(signal) })
  const hr = useIsOnboardingHr()
  const [draft, setDraft] = useState<{ subject: string; body: string; hr: string; days: string } | null>(null)
  const d = draft ?? (q.data ? { subject: q.data.welcomeSubject ?? '', body: q.data.welcomeBody ?? '', hr: q.data.hrContactEmployeeId ?? '', days: String(q.data.reminderDaysBefore) } : null)
  const save = useAction(() => opsApi.saveSettings({ welcomeSubject: d!.subject || null, welcomeBody: d!.body || null, hrContactEmployeeId: d!.hr || null, reminderDaysBefore: Number(d!.days) || 0 }),
    { success: tx('Ayarlar kaydedildi'), invalidate: [['onboarding', 'settings']], onDone: () => setDraft(null) })
  if (q.isPending || !d) return <RowsSkeleton rows={2} />
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Mail className="size-4 text-primary" />{' '}{tx('İlk gün karşılama iletisi')}</span>}
        note={tx('Başlangıç günü yeni çalışana uygulama içi bildirim ve e-posta olarak bir kez gönderilir.')} />
      <PanelBody className="space-y-3">
        <TextField label={tx('Konu')} placeholder={q.data!.defaultSubject} value={d.subject} disabled={!hr} onChange={(e) => setDraft({ ...d, subject: e.target.value })} />
        <TextAreaField label={tx('İleti')} rows={5} placeholder={q.data!.defaultBody} value={d.body} disabled={!hr} onChange={(e) => setDraft({ ...d, body: e.target.value })}
          hint={tx('Yer tutucular: {0}. Boş bırakılırsa varsayılan metin kullanılır.', [q.data!.placeholders.join(', ')])} />
        <InfoNote>{tx('KVKK: karşılama iletisine ücret, kimlik numarası, adres gibi kişisel veriler yazmayın; yalnızca ad, başlangıç tarihi, yönetici, yol arkadaşı ve buluşma yeri kullanılır.')}</InfoNote>
        <div className="grid gap-3 md:grid-cols-2">
          <PersonSelect label={tx('Zimmet hatırlatmaları için İK sorumlusu')} value={d.hr} onChange={(v) => setDraft({ ...d, hr: v })}
            hint={tx('Boşsa zimmet sahibinin bölüm başı bilgilendirilir.')} />
          <TextField label={tx('İade hatırlatması (gün önce)')} type="number" min={0} max={60} value={d.days} disabled={!hr} onChange={(e) => setDraft({ ...d, days: e.target.value })} />
        </div>
        {hr && <Button onClick={() => save.mutate(undefined)} disabled={save.isPending || draft === null}>{tx('Kaydet')}</Button>}
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ plan sayfası: buddy */

export function BuddyPanel({ plan, canEdit }: { plan: OnboardingPlan; canEdit: boolean }) {
  const dir = useDirectory()
  const nameOf = (id?: string | null) => (id ? dir.data?.find((x) => x.id === id)?.fullName ?? '—' : '—')
  const [buddy, setBuddy] = useState(plan.buddyEmployeeId ?? '')
  const [location, setLocation] = useState(plan.location ?? '')
  const preview = useQuery({ queryKey: ['onboarding', 'welcome', plan.id, plan.buddyEmployeeId, plan.location], queryFn: ({ signal }) => opsApi.welcomePreview(plan.id, signal), enabled: canEdit })
  const saveBuddy = useAction(() => opsApi.setBuddy(plan.id, buddy || null), { success: tx('Yol arkadaşı güncellendi ve bilgilendirildi'), invalidate: [['onboarding']] })
  const saveLoc = useAction(() => opsApi.setLocation(plan.id, location.trim() || null), { success: tx('Buluşma yeri kaydedildi'), invalidate: [['onboarding']] })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><UserRoundCheck className="size-4 text-primary" />{' '}{tx('Yol arkadaşı ve ilk gün')}</span>}
        note={plan.appliedTemplates ? tx('Uygulanan şablonlar: {0}', [plan.appliedTemplates]) : undefined} />
      <PanelBody className="space-y-4">
        {canEdit ? (
          <div className="grid gap-3 md:grid-cols-2">
            <div className="flex items-end gap-2">
              <div className="flex-1"><PersonSelect label={tx('Yol arkadaşı (buddy)')} value={buddy} exclude={[plan.employeeId]} onChange={setBuddy} /></div>
              <Button variant="outline" onClick={() => saveBuddy.mutate(undefined)} disabled={saveBuddy.isPending || buddy === (plan.buddyEmployeeId ?? '')}>{tx('Ata')}</Button>
            </div>
            <div className="flex items-end gap-2">
              <div className="flex-1"><TextField label={tx('İlk gün buluşma yeri')} maxLength={200} value={location} onChange={(e) => setLocation(e.target.value)} /></div>
              <Button variant="outline" onClick={() => saveLoc.mutate(undefined)} disabled={saveLoc.isPending || location === (plan.location ?? '')}>{tx('Kaydet')}</Button>
            </div>
          </div>
        ) : (
          <p className="text-[13px]">{tx('Yol arkadaşı:')}{' '}<b>{nameOf(plan.buddyEmployeeId)}</b>{plan.location ? ` · ${tx('Buluşma: {0}', [plan.location])}` : ''}</p>
        )}
        {canEdit && preview.data && (
          <div className="rounded-xl border border-border bg-muted/30 p-3 text-[13px]">
            <p className="mb-1 flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
              {plan.welcomeSentAt ? <StatusBadge tone="success">{tx('Gönderildi {0}', [formatDateTime(plan.welcomeSentAt)])}</StatusBadge> : <StatusBadge tone="info">{tx('Başlangıç günü gönderilecek')}</StatusBadge>}
              {tx('Uygulama içi bildirim + e-posta')}
            </p>
            <p className="font-medium">{preview.data.subject}</p>
            <p className="mt-1 whitespace-pre-line text-muted-foreground">{preview.data.body}</p>
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}
