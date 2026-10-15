import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ChevronDown, GitBranch, Plus, ShieldAlert, Trash2, UserRoundCog } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useDirectory } from '@/api/directory'
import { workflowTypeLabels, type WorkflowType } from '@/api/types'
import { workflowApi, type ApproverKind, type ConditionField, type DefinitionInput, type DefinitionStep, type WorkflowDefinition } from '@/api/workflows'
import { PersonSelect, useAction } from '@/features/shared/kit'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/Confirm'
import { DelegationsModal } from './DelegationsModal'

const TYPES: WorkflowType[] = ['LeaveRequest', 'ExpenseClaim', 'Overtime', 'DocumentRequest', 'PositionChange', 'AssetRequest', 'Other']
const APPROVER: Record<ApproverKind, string> = {
  DepartmentHead: tx('Bölüm başı (talep edenin)'),
  ParentDepartmentHead: tx('Üst bölüm başı'),
  Employee: tx('Belirli bir kişi'),
}
const FIELD: Record<ConditionField, string> = { Days: tx('Gün'), Amount: tx('Tutar'), Hours: tx('Saat') }
const HIDEABLE: Array<{ key: string; label: string }> = [
  { key: 'reason', label: tx('Gerekçe / açıklama') },
  { key: 'amount', label: tx('Tutar') },
  { key: 'hours', label: tx('Saat') },
]

const blank = (): DefinitionInput => ({ type: 'LeaveRequest', name: '', isActive: true, steps: [{ approver: 'DepartmentHead' }], hiddenFields: ['reason'] })

function StepCard({ s, i, n, onChange, onRemove, onMove }: {
  s: DefinitionStep; i: number; n: number; onChange: (p: Partial<DefinitionStep>) => void; onRemove: () => void; onMove: (d: -1 | 1) => void
}) {
  const conditional = !!s.conditionField
  return (
    <div className="relative rounded-2xl border border-border bg-card p-4 shadow-sm">
      <div className="mb-3 flex items-center gap-2">
        <span className="grid size-7 place-items-center rounded-full bg-primary/15 text-[12px] font-semibold text-primary">{i + 1}</span>
        <span className="flex-1 text-[13px] font-medium">{conditional ? tx('Koşullu adım') : tx('Her zaman')}</span>
        <Button size="icon" variant="ghost" aria-label={tx('Yukarı')} disabled={i === 0} onClick={() => onMove(-1)}><ArrowUp className="size-4" /></Button>
        <Button size="icon" variant="ghost" aria-label={tx('Aşağı')} disabled={i === n - 1} onClick={() => onMove(1)}><ArrowDown className="size-4" /></Button>
        <Button size="icon" variant="ghost" aria-label={tx('Sil')} disabled={n === 1} onClick={onRemove}><Trash2 className="size-4" /></Button>
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        <SelectField label={tx('Onaycı')} value={s.approver} onChange={(v) => onChange({ approver: v as ApproverKind, employeeId: v === 'Employee' ? s.employeeId : null })}
          options={(Object.keys(APPROVER) as ApproverKind[]).map((k) => ({ value: k, label: APPROVER[k] }))} />
        {s.approver === 'Employee'
          ? <PersonSelect label={tx('Kişi')} value={s.employeeId ?? ''} onChange={(v) => onChange({ employeeId: v })} />
          : <TextField label={tx('Karar süresi (saat, isteğe bağlı)')} inputMode="numeric" value={s.slaHours?.toString() ?? ''} onChange={(e) => onChange({ slaHours: e.target.value ? Number(e.target.value) : null })}
              hint={tx('Aşılırsa üst yöneticiye iletilir')} />}
        {s.approver === 'Employee' && (
          <TextField label={tx('Karar süresi (saat, isteğe bağlı)')} inputMode="numeric" value={s.slaHours?.toString() ?? ''} onChange={(e) => onChange({ slaHours: e.target.value ? Number(e.target.value) : null })} />
        )}
      </div>
      <div className="mt-3 grid gap-3 sm:grid-cols-[1fr_90px_1fr]">
        <SelectField label={tx('Koşul')} value={s.conditionField ?? ''} onChange={(v) => onChange(v ? { conditionField: v as ConditionField, conditionOp: s.conditionOp ?? '>', conditionValue: s.conditionValue ?? 0 } : { conditionField: null, conditionOp: null, conditionValue: null })}
          options={[{ value: '', label: tx('Koşulsuz') }, ...(Object.keys(FIELD) as ConditionField[]).map((k) => ({ value: k, label: FIELD[k] }))]} />
        {conditional && (
          <>
            <SelectField label={tx('İşleç')} value={s.conditionOp ?? '>'} onChange={(v) => onChange({ conditionOp: v as DefinitionStep['conditionOp'] })}
              options={['>', '>=', '<', '<='].map((o) => ({ value: o, label: o }))} />
            <TextField label={tx('Değer')} inputMode="decimal" value={s.conditionValue?.toString() ?? ''} onChange={(e) => onChange({ conditionValue: Number(e.target.value.replace(',', '.')) || 0 })} />
          </>
        )}
      </div>
    </div>
  )
}

function Editor({ initial, onDone }: { initial: WorkflowDefinition | null; onDone: () => void }) {
  const [f, setF] = useState<DefinitionInput>(initial ? { type: initial.type, name: initial.name, isActive: initial.isActive, steps: initial.steps, hiddenFields: initial.hiddenFields } : blank())
  const [warnings, setWarnings] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  // Canlı doğrulama ve KVKK uyarıları
  useEffect(() => {
    const t = window.setTimeout(() => {
      void workflowApi.validateDefinition(f).then((r) => { setWarnings(r.warnings); setError(r.error) }).catch(() => undefined)
    }, 400)
    return () => window.clearTimeout(t)
  }, [f])
  const save = useAction(() => workflowApi.saveDefinition(initial?.id ?? null, f), { success: tx('Akış kaydedildi'), invalidate: [['wf-definitions']], onDone })
  const setStep = (i: number, p: Partial<DefinitionStep>) => setF({ ...f, steps: f.steps.map((s, j) => (j === i ? { ...s, ...p } : s)) })
  const move = (i: number, d: -1 | 1) => { const a = [...f.steps]; [a[i], a[i + d]] = [a[i + d]!, a[i]!]; setF({ ...f, steps: a }) }
  // Ön izleme
  const [who, setWho] = useState('')
  const [val, setVal] = useState('')
  const preview = useAction(() => workflowApi.previewDefinition({ type: f.type, requesterEmployeeId: who, days: Number(val) || undefined, amount: Number(val) || undefined, hours: Number(val) || undefined }), {})
  return (
    <div className="grid gap-5 xl:grid-cols-[1fr_360px]">
      <Panel>
        <PanelHead title={initial ? tx('Akışı düzenle') : tx('Yeni onay akışı')} note={tx('Adımlar sırayla işler; koşulu sağlanmayan adım atlanır. Talep eden kişi ve art arda aynı onaycı atlanır.')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <SelectField label={tx('Talep türü')} value={f.type} onChange={(v) => setF({ ...f, type: v as WorkflowType })} options={TYPES.map((t) => ({ value: t, label: workflowTypeLabels[t] }))} />
            <TextField label={tx('Ad')} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder={tx('Ör. 3 günü aşan izin: üst yönetici onayı')} />
          </div>
          <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.isActive} onCheckedChange={(v) => setF({ ...f, isActive: v === true })} /> {tx('Etkin (bu türün diğer akışları pasif olur)')}</label>
          <div className="space-y-2">
            {f.steps.map((s, i) => (
              <div key={i}>
                <StepCard s={s} i={i} n={f.steps.length} onChange={(p) => setStep(i, p)} onRemove={() => setF({ ...f, steps: f.steps.filter((_, j) => j !== i) })} onMove={(d) => move(i, d)} />
                {i < f.steps.length - 1 && <div className="flex justify-center py-1 text-muted-foreground" aria-hidden><ChevronDown className="size-5" /></div>}
              </div>
            ))}
          </div>
          <Button variant="outline" disabled={f.steps.length >= 10} onClick={() => setF({ ...f, steps: [...f.steps, { approver: 'ParentDepartmentHead' }] })}><Plus className="size-4" /> {tx('Adım ekle')}</Button>
          <div>
            <p className="mb-2 text-[13px] font-medium">{tx('Onaycılardan gizlenecek alanlar (KVKK)')}</p>
            <div className="flex flex-wrap gap-4">
              {HIDEABLE.map((h) => (
                <label key={h.key} className="flex items-center gap-2 text-[13px]">
                  <Checkbox checked={f.hiddenFields.includes(h.key)} onCheckedChange={(v) => setF({ ...f, hiddenFields: v === true ? [...f.hiddenFields, h.key] : f.hiddenFields.filter((x) => x !== h.key) })} /> {h.label}
                </label>
              ))}
            </div>
          </div>
          {error && <p role="alert" className="text-[13px] text-destructive">{error}</p>}
          {warnings.length > 0 && (
            <div className="space-y-1.5 rounded-xl border border-amber-500/40 bg-amber-500/10 p-3 text-[12.5px]">
              {warnings.map((w, i) => <p key={i} className="flex gap-2"><ShieldAlert className="mt-0.5 size-3.5 shrink-0" />{w}</p>)}
            </div>
          )}
          <div className="flex gap-2">
            <Button disabled={!!error || !f.name.trim() || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
            <Button variant="ghost" onClick={onDone}>{tx('Vazgeç')}</Button>
          </div>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Kim onaylar?')} note={tx('Kaydettikten sonra bir çalışan için zinciri deneyin.')} />
        <PanelBody className="space-y-3">
          <PersonSelect label={tx('Talep eden')} value={who} onChange={setWho} />
          <TextField label={tx('Gün / tutar / saat')} inputMode="decimal" value={val} onChange={(e) => setVal(e.target.value)} />
          <Button variant="outline" disabled={!who || preview.isPending} onClick={() => preview.mutate(undefined)}>{tx('Dene')}</Button>
          {preview.data && (
            preview.data.usesDefinition
              ? <ol className="space-y-1.5 text-[13px]">{preview.data.approvers.map((a, i) => <li key={i}>{i + 1}. {nameOf(a.employeeId)}{a.slaHours ? ` · ${tx('{0} saat', [a.slaHours])}` : ''}</li>)}</ol>
              : <InfoNote>{tx('Etkin akış yok ya da kimse bulunamadı: talep hizmetin varsayılan onaycısına (bölüm başı) gider.')}</InfoNote>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}

/**
 * İK onaycısı: üst onaycısı bulunamayan talepler (ör. departman başının kendi izni) ve süre
 * aşımında iletilecek üst yöneticisi olmayan adımlar bu kişiye gider. Tanımlı değilse bu
 * talepler kimsenin kutusuna düşmez.
 */
function HrApproverPanel() {
  const settings = useQuery({ queryKey: ['wf-settings'], queryFn: ({ signal }) => workflowApi.settings(signal) })
  const candidates = useQuery({ queryKey: ['workflows', 'approver-candidates'], queryFn: ({ signal }) => workflowApi.approverCandidates(signal) })
  const [value, setValue] = useState<string | null>(null)
  const current = value ?? settings.data?.hrApproverEmployeeId ?? ''
  const save = useAction((id: string) => workflowApi.saveSettings({ hrApproverEmployeeId: id || null }), {
    success: tx('İK onaycısı kaydedildi'), invalidate: [['wf-settings']], onDone: () => setValue(null),
  })
  const options = useMemo(() => {
    const list = (candidates.data ?? []).map((c) => ({ value: c.id, label: c.fullName }))
    // Kaydedilen kişi çağıranın kendisiyse aday listesinde yoktur; yine de görünsün.
    const saved = settings.data?.hrApproverEmployeeId
    if (saved && !list.some((o) => o.value === saved)) list.unshift({ value: saved, label: tx('Siz') })
    return [{ value: '', label: tx('Tanımlı değil') }, ...list]
  }, [candidates.data, settings.data])
  return (
    <Panel>
      <PanelHead title={tx('İK onaycısı')} note={tx('Üst onaycısı bulunamayan talepler (ör. bölüm başının kendi izni) ve süre aşımında iletilecek üst yöneticisi olmayan adımlar bu kişiye gider.')} />
      <PanelBody>
        <div className="flex flex-wrap items-end gap-3">
          <div className="min-w-60 flex-1">
            <SelectField label={tx('Kişi')} value={current} onChange={setValue} options={options}
              hint={tx('Yalnızca HR360 giriş hesabı olan, aktif çalışanlar seçilebilir.')} />
          </div>
          <Button disabled={save.isPending || value === null || value === (settings.data?.hrApproverEmployeeId ?? '')} onClick={() => save.mutate(current)}>{tx('Kaydet')}</Button>
        </div>
        {!settings.isPending && !settings.data?.hrApproverEmployeeId && (
          <div className="mt-3"><InfoNote>{tx('İK onaycısı tanımlı değil: bölüm başının kendi talepleri kimsenin onay kutusuna düşmez.')}</InfoNote></div>
        )}
      </PanelBody>
    </Panel>
  )
}

/** /panel/onay-akislari — görsel onay akışı tasarımcısı (İK). */
export function WorkflowDesignerPage() {
  const q = useQuery({ queryKey: ['wf-definitions'], queryFn: ({ signal }) => workflowApi.definitions(signal) })
  const [editing, setEditing] = useState<WorkflowDefinition | 'new' | null>(null)
  const [delegations, setDelegations] = useState(false)
  const del = useAction((id: string) => workflowApi.deleteDefinition(id), { success: tx('Akış silindi'), invalidate: [['wf-definitions']] })
  const confirm = useConfirm()
  const askDelete = async (name: string, id: string) => {
    if (await confirm({ title: tx('“{0}” akışı silinsin mi?', [name]), note: tx('Yeni talepler bu akışa göre yönlendirilmez (uygun başka tanım yoksa bölüm başına gider). Süren onaylar etkilenmez. Geçici olarak durdurmak için akışı pasifleştirebilirsiniz.'), action: tx('Sil') })) del.mutate(id)
  }
  const grouped = useMemo(() => TYPES.map((t) => ({ t, defs: (q.data ?? []).filter((d) => d.type === t) })).filter((g) => g.defs.length), [q.data])
  return (
    <>
      <PageHeader title={tx('Onay akışları')} description={tx('Talep türüne göre çok adımlı onay zinciri: bölüm başı, üst yönetici, belirli kişi; gün/tutar koşulları ve karar süreleri.')}
        actions={<div className="flex gap-2"><Button variant="outline" onClick={() => setDelegations(true)}><UserRoundCog className="size-4" /> {tx('Tüm vekâletler')}</Button><Button onClick={() => setEditing('new')}><Plus className="size-4" /> {tx('Yeni akış')}</Button></div>} />
      {!editing && <div className="mb-4"><HrApproverPanel /></div>}
      {editing ? <Editor initial={editing === 'new' ? null : editing} onDone={() => setEditing(null)} /> : q.isPending ? <RowsSkeleton rows={3} /> : !grouped.length ? (
        <EmptyState icon={GitBranch} title={tx('Henüz akış tanımı yok')} detail={tx('Tanım yoksa talepler bölüm başına gider. Örneğin "3 günü aşan izinlerde üst yönetici de onaylasın" gibi kurallar ekleyebilirsiniz.')} />
      ) : (
        <div className="space-y-4">
          {grouped.map(({ t, defs }) => (
            <Panel key={t}>
              <PanelHead title={workflowTypeLabels[t]} />
              <PanelBody className="p-0">
                <ul className="divide-y divide-border">
                  {defs.map((d) => (
                    <li key={d.id} className={cn('flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]', !d.isActive && 'opacity-60')}>
                      <span className="min-w-0 flex-1 font-medium">{d.name}</span>
                      <span className="text-muted-foreground">{tx('{0} adım', [d.steps.length])}</span>
                      {d.isActive ? <StatusBadge tone="success">{tx('Etkin')}</StatusBadge> : <StatusBadge>{tx('Pasif')}</StatusBadge>}
                      <Button size="sm" variant="ghost" onClick={() => setEditing(d)}>{tx('Düzenle')}</Button>
                      <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => askDelete(d.name, d.id)}><Trash2 className="size-4" /></Button>
                    </li>
                  ))}
                </ul>
              </PanelBody>
            </Panel>
          ))}
        </div>
      )}
      {delegations && <DelegationsModal all onClose={() => setDelegations(false)} />}
    </>
  )
}
