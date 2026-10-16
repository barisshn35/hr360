import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { BellRing, FileWarning, Plus, Settings2, Trash2 } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useAction } from '@/features/shared/kit'
import { useDepartments } from '@/features/performance/hooks'
import { documentTypeLabels, expenseApi, type ComplianceState, type DocumentRequirement, type DocumentType } from '@/api/expense'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const KEY = ['expense', 'compliance'] as const
export const complianceStateLabels = (): Record<ComplianceState, string> => ({ Missing: tx('Eksik'), Expired: tx('Süresi doldu'), ExpiringSoon: tx('Süresi doluyor') })
const TONE: Record<ComplianceState, string> = {
  Missing: 'bg-destructive/10 text-destructive', Expired: 'bg-destructive/10 text-destructive', ExpiringSoon: 'bg-amber-400/15 text-amber-600 dark:text-amber-300',
}

function RequirementsModal({ onClose }: { onClose: () => void }) {
  const depts = useDepartments()
  const q = useQuery({ queryKey: [...KEY, 'req'], queryFn: ({ signal }) => expenseApi.complianceRequirements(signal) })
  const [rows, setRows] = useState<DocumentRequirement[] | null>(null)
  const list = rows ?? q.data ?? []
  const set = (i: number, patch: Partial<DocumentRequirement>) => setRows(list.map((r, j) => (j === i ? { ...r, ...patch } : r)))
  const save = useAction(() => expenseApi.saveComplianceRequirements(list), { success: tx('Zorunlu belgeler kaydedildi.'), invalidate: [KEY], onDone: onClose })
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Zorunlu belgeler')}
      note={tx('Her çalışanın özlük dosyasında bulunması gereken belgeler. Departman seçilirse yalnızca o departmana doğrudan atanmış kişilerde aranır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      {q.isPending ? <RowsSkeleton rows={3} /> : (
        <div className="space-y-3">
          {list.map((r, i) => (
            <div key={i} className="grid items-end gap-2 sm:grid-cols-[1fr_1.3fr_120px_auto]">
              <SelectField label={tx('Belge')} value={r.type} onChange={(v) => set(i, { type: v as DocumentType })}
                options={(Object.keys(documentTypeLabels) as DocumentType[]).filter((t) => t !== 'Payslip').map((t) => ({ value: t, label: documentTypeLabels[t] }))} />
              <SelectField label={tx('Kapsam')} value={r.departmentId ?? '__all'} onChange={(v) => set(i, { departmentId: v === '__all' ? null : v })}
                options={[{ value: '__all', label: tx('Tüm şirket') }, ...depts.list.map((d) => ({ value: d.id, label: d.path }))]} />
              <TextField label={tx('Geçerlilik (ay)')} type="number" min={1} max={120} value={r.validityMonths ?? ''} placeholder={tx('süresiz')}
                onChange={(e) => set(i, { validityMonths: e.target.value ? Number(e.target.value) : null })} />
              <Button variant="ghost" size="icon" aria-label={tx('Kaldır')} onClick={() => setRows(list.filter((_, j) => j !== i))}><Trash2 className="size-4" /></Button>
            </div>
          ))}
          <Button variant="outline" size="sm" onClick={() => setRows([...list, { type: 'Contract', departmentId: null, validityMonths: null }])}><Plus className="size-4" />{tx('Belge ekle')}</Button>
          <InfoNote>{tx('Geçerlilik süresi verilen belgede, belgeye ayrıca geçerlilik bitişi girilmemişse kayıt tarihinden hesaplanır. 30 gün içinde dolacaklar "süresi doluyor" olarak işaretlenir.')}</InfoNote>
        </div>
      )}
    </Modal>
  )
}

/** Eksik belge takibi (yenilik haritası #11): İK için özet, liste ve toplu hatırlatma. */
export function DocumentCompliancePanel() {
  const depts = useDepartments()
  const [dept, setDept] = useState('')
  const [edit, setEdit] = useState(false)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const q = useQuery({ queryKey: [...KEY, dept], queryFn: ({ signal }) => expenseApi.complianceOverview(dept || undefined, signal) })
  const remind = useAction((ids: string[]) => expenseApi.remindCompliance(ids), {
    success: (r) => tx('{0} çalışana hatırlatma gönderildi.', [r.sent]), onDone: () => setPicked(new Set()),
  })
  const d = q.data
  const labels = complianceStateLabels()
  const toggle = (id: string) => setPicked((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><FileWarning className="size-4 text-primary" />{tx('Eksik belge takibi')}</span>}
        note={d && d.requirementCount > 0 ? tx('{0}/{1} çalışanın dosyası tam', [d.summary.compliant, d.summary.employees]) : undefined}
        action={<Button size="sm" variant="outline" onClick={() => setEdit(true)}><Settings2 className="size-4" />{tx('Zorunlu belgeler')}</Button>} />
      <PanelBody className="space-y-4">
        {q.isPending ? <RowsSkeleton rows={3} /> : !d ? null : d.requirementCount === 0 ? (
          <p className="text-[13px] text-muted-foreground">{tx('Henüz zorunlu belge tanımlanmadı. "Zorunlu belgeler" ile sözleşme, kimlik, sağlık raporu gibi belgeleri tanımlayın; eksikler burada listelenir.')}</p>
        ) : (
          <>
            <div className="flex flex-wrap items-end gap-3">
              <div className="w-64"><SelectField label={tx('Departman')} value={dept || '__all'} onChange={(v) => setDept(v === '__all' ? '' : v)}
                options={[{ value: '__all', label: tx('Tüm şirket') }, ...depts.list.map((x) => ({ value: x.id, label: x.path }))]} /></div>
              {(['missing', 'expired', 'expiringSoon'] as const).map((k) => (
                <span key={k} className="rounded-xl border border-border px-3 py-1.5 text-[12.5px]">
                  {k === 'missing' ? tx('Eksik') : k === 'expired' ? tx('Süresi doldu') : tx('Süresi doluyor')} <b className="tabular-nums">{d.summary[k]}</b>
                </span>
              ))}
              <Button size="sm" className="ml-auto" disabled={!picked.size || remind.isPending} onClick={() => remind.mutate([...picked])}>
                <BellRing className="size-4" />{picked.size ? tx('{0} kişiye hatırlat', [picked.size]) : tx('Hatırlat')}
              </Button>
            </div>
            {d.rows.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Tüm dosyalar tam.')}</p> : (
              <div className="max-h-96 overflow-auto rounded-xl border border-border">
                <table className="w-full text-[13px]">
                  <thead className="sticky top-0 bg-muted text-left text-[11.5px] text-muted-foreground">
                    <tr>
                      <th className="w-10 px-3 py-2"><Checkbox aria-label={tx('Tümünü seç')} checked={picked.size === d.rows.length}
                        onCheckedChange={(v) => setPicked(v === true ? new Set(d.rows.map((r) => r.employeeId)) : new Set())} /></th>
                      <th className="px-3 py-2 font-normal">{tx('Çalışan')}</th>
                      <th className="px-3 py-2 font-normal">{tx('Belgeler')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {d.rows.map((r) => (
                      <tr key={r.employeeId} className="border-t border-border">
                        <td className="px-3 py-2"><Checkbox aria-label={r.name} checked={picked.has(r.employeeId)} onCheckedChange={() => toggle(r.employeeId)} /></td>
                        <td className="px-3 py-2"><span className="flex items-center gap-2"><EmployeeAvatar id={r.employeeId} name={r.name} size={24} />
                          <span><span className="block font-medium">{r.name}</span><span className="block text-[11.5px] text-muted-foreground">{r.department ?? '—'}</span></span></span></td>
                        <td className="px-3 py-2"><span className="flex flex-wrap gap-1.5">
                          {r.issues.map((i) => (
                            <span key={i.type} title={i.validUntil ? formatDate(i.validUntil) : undefined} className={cn('rounded-full px-2 py-0.5 text-[11.5px]', TONE[i.state])}>
                              {documentTypeLabels[i.type]} · {labels[i.state]}
                            </span>
                          ))}
                        </span></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </PanelBody>
      {edit && <RequirementsModal onClose={() => setEdit(false)} />}
    </Panel>
  )
}
