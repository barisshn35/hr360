import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { Bar, BarChart, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { GripVertical, Plus, Save, Trash2, UserMinus, UserPlus, Workflow } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextField } from '@/components/ui/field'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { engagementApi, type OrgMove, type OrgScenario } from '@/api/engagement'
import { formatMoney, parseDecimal } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Metric, TenantGate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/confirm'

const NONE = '__none__'

function Editor({ scenario }: { scenario: OrgScenario }) {
  const base = useQuery({ queryKey: ['org-baseline'], queryFn: ({ signal }) => engagementApi.orgBaseline(signal) })
  const [moves, setMoves] = useState<OrgMove[]>(scenario.moves)
  useEffect(() => setMoves(scenario.moves), [scenario.id, scenario.moves])
  const dirty = JSON.stringify(moves) !== JSON.stringify(scenario.moves)
  const save = useAction(() => engagementApi.updateScenario(scenario.id, { name: scenario.name, moves }), { success: tx('Senaryo kaydedildi'), invalidate: [['org-scenarios']] })
  const impact = useQuery({ queryKey: ['org-scenarios', scenario.id, 'impact', scenario.updatedAt], queryFn: ({ signal }) => engagementApi.scenarioImpact(scenario.id, signal) })
  const [drag, setDrag] = useState<string | null>(null)
  const [hire, setHire] = useState<string | null>(null)
  const [hireName, setHireName] = useState(tx('Yeni pozisyon'))
  const [hireSalary, setHireSalary] = useState('60000')
  const salary = hireSalary.trim() ? parseDecimal(hireSalary) : null
  const salaryError = hireSalary.trim() && (salary === null || salary < 0) ? tx('Maaş sıfır ya da pozitif bir tutar olmalı.') : undefined

  const columns = useMemo(() => {
    if (!base.data) return []
    const deptIds = [...base.data.departments.map((d) => d.id), NONE]
    const placed = new Map<string, { id: string; name: string; position: string | null; moved: boolean; kind: 'person' | 'hire'; idx?: number }[]>()
    deptIds.forEach((d) => placed.set(d, []))
    for (const p of base.data.people) {
      const m = moves.find((x) => x.employeeId === p.employeeId && x.kind !== 'Hire')
      if (m?.kind === 'Exit') continue
      const target = m?.kind === 'Move' ? m.toDepartmentId ?? NONE : p.departmentId ?? NONE
      placed.get(target)?.push({ id: p.employeeId, name: p.name, position: m?.newPosition ?? p.position, moved: !!m, kind: 'person' })
    }
    moves.forEach((m, idx) => m.kind === 'Hire' && placed.get(m.toDepartmentId ?? NONE)?.push({ id: `hire-${idx}`, name: m.name, position: m.plannedSalary ? formatMoney(m.plannedSalary) : null, moved: true, kind: 'hire', idx }))
    return deptIds.map((id) => ({ id, name: id === NONE ? tx('Atanmamış') : base.data!.departments.find((d) => d.id === id)!.name, people: placed.get(id)! }))
  }, [base.data, moves])

  const exits = base.data?.people.filter((p) => moves.some((m) => m.employeeId === p.employeeId && m.kind === 'Exit')) ?? []

  const moveTo = (personId: string, deptId: string) => {
    if (personId.startsWith('hire-')) {
      const idx = Number(personId.slice(5))
      setMoves((ms) => ms.map((m, i) => (i === idx ? { ...m, toDepartmentId: deptId === NONE ? null : deptId } : m)))
      return
    }
    const p = base.data!.people.find((x) => x.employeeId === personId)!
    const original = p.departmentId ?? NONE
    setMoves((ms) => {
      const rest = ms.filter((m) => !(m.employeeId === personId && m.kind !== 'Hire'))
      return deptId === original ? rest : [...rest, { employeeId: personId, name: p.name, fromDepartmentId: p.departmentId, toDepartmentId: deptId === NONE ? null : deptId, kind: 'Move' }]
    })
  }
  const exit = (personId: string) => {
    if (personId.startsWith('hire-')) return setMoves((ms) => ms.filter((_, i) => i !== Number(personId.slice(5))))
    const p = base.data!.people.find((x) => x.employeeId === personId)!
    setMoves((ms) => [...ms.filter((m) => !(m.employeeId === personId && m.kind !== 'Hire')), { employeeId: personId, name: p.name, fromDepartmentId: p.departmentId, kind: 'Exit' }])
  }

  if (base.isPending) return <RowsSkeleton />
  if (base.isError) return <ErrorState message={(base.error as Error).message} />
  const t = impact.data?.totals
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center gap-2">
        <InfoNote>{tx('Kişileri sürükleyip başka departmana bırakın. Senaryo gerçek organizasyonu DEĞİŞTİRMEZ; yalnızca etki hesaplanır.')}</InfoNote>
        <div className="ml-auto flex gap-2">
          <Button variant="outline" onClick={() => setMoves(scenario.moves)} disabled={!dirty}>{tx('Geri al')}</Button>
          <Button onClick={() => save.mutate(undefined)} disabled={!dirty || save.isPending}><Save className="size-4" />{' '}{tx('Kaydet ve hesapla')}</Button>
        </div>
      </div>
      <div className="flex gap-4 overflow-x-auto pb-2">
        {columns.map((col) => (
          <div key={col.id} onDragOver={(e) => e.preventDefault()} onDrop={() => { if (drag) moveTo(drag, col.id); setDrag(null) }}
            className={cn('w-64 shrink-0 rounded-2xl border bg-card/40 p-3 transition', drag ? 'border-dashed border-primary/50' : 'border-border')}>
            <div className="mb-2 flex items-center justify-between"><p className="text-[13.5px] font-semibold">{col.name}</p><span className="tabular rounded-full bg-muted px-2 text-[12px]">{col.people.length}</span></div>
            <ul className="min-h-24 space-y-1.5">
              <AnimatePresence>
                {col.people.map((p) => (
                  <motion.li key={p.id} layout layoutId={p.id} initial={{ opacity: 0, scale: 0.9 }} animate={{ opacity: 1, scale: 1 }} exit={{ opacity: 0, scale: 0.9 }}
                    draggable onDragStart={() => setDrag(p.id)} onDragEnd={() => setDrag(null)}
                    className={cn('group flex cursor-grab items-center gap-2 rounded-xl border px-2.5 py-2 text-[12.5px] active:cursor-grabbing', p.kind === 'hire' ? 'border-dashed border-emerald-500/50 bg-emerald-500/10' : p.moved ? 'border-primary/50 bg-primary/10' : 'border-border bg-background/60')}>
                    <GripVertical className="size-3.5 text-muted-foreground" />
                    <div className="min-w-0 flex-1"><p className="truncate font-medium">{p.name}</p><p className="truncate text-[11px] text-muted-foreground">{p.kind === 'hire' ? tx('yeni kadro · {0}', [p.position ?? '']) : p.position ?? '—'}</p></div>
                    <button onClick={() => exit(p.id)} aria-label={tx('Çıkar')} className="cursor-pointer text-muted-foreground opacity-0 group-hover:opacity-100 hover:text-destructive"><UserMinus className="size-3.5" /></button>
                  </motion.li>
                ))}
              </AnimatePresence>
            </ul>
            {col.id !== NONE && <button onClick={() => setHire(col.id)} className="mt-2 flex w-full cursor-pointer items-center justify-center gap-1 rounded-xl border border-dashed border-border py-1.5 text-[12px] text-muted-foreground hover:border-emerald-500/50 hover:text-foreground"><UserPlus className="size-3.5" />{' '}{tx('Yeni kadro')}</button>}
          </div>
        ))}
        {exits.length > 0 && (
          <div className="w-56 shrink-0 rounded-2xl border border-destructive/30 bg-destructive/5 p-3">
            <p className="mb-2 text-[13.5px] font-semibold text-destructive">{tx('Ayrılanlar')}</p>
            {exits.map((p) => <button key={p.employeeId} onClick={() => setMoves((ms) => ms.filter((m) => !(m.employeeId === p.employeeId && m.kind === 'Exit')))} className="mb-1 block w-full cursor-pointer truncate rounded-lg bg-background/60 px-2 py-1.5 text-left text-[12.5px] line-through hover:no-underline">{p.name}</button>)}
          </div>
        )}
      </div>
      <Panel>
        <PanelHead title={tx('Etki')} note={dirty ? tx('Kaydedilmemiş değişiklikler var — hesap son kayda göredir.') : tx('Kayıtlı senaryoya göre.')} />
        <PanelBody>
          {impact.isPending ? <RowsSkeleton rows={2} /> : impact.data && (
            <div className="grid gap-5 lg:grid-cols-[1fr_1.4fr]">
              <div className="grid grid-cols-2 gap-3">
                <Metric label={tx('Kadro (önce → sonra)')} value={`${t!.headcountBefore} → ${t!.headcountAfter}`} />
                <Metric label={tx('Taşınan kişi')} value={t!.movedPeople} />
                {impact.data.costVisible ? (
                  <>
                    <Metric label={tx('Aylık maaş farkı')} value={formatMoney(t!.monthlyCostDelta ?? 0)} tone={(t!.monthlyCostDelta ?? 0) > 0 ? 'warn' : 'good'} />
                    <Metric label={tx('Yeni kadro maliyeti')} value={formatMoney(t!.hireCost ?? 0)} />
                  </>
                ) : <p className="col-span-2 text-[12.5px] text-muted-foreground">{tx('Maliyet etkisi yalnızca ücret görme yetkisi olanlara gösterilir.')}</p>}
              </div>
              <div className="h-56">
                <ResponsiveContainer>
                  <BarChart data={impact.data.departments}>
                    <XAxis dataKey="department" fontSize={11} tickLine={false} axisLine={false} />
                    <YAxis allowDecimals={false} fontSize={11} width={24} tickLine={false} axisLine={false} />
                    <Tooltip contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12 }} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                    <Bar dataKey="before" name={tx('Önce')} fill="hsl(var(--muted-foreground))" radius={[6, 6, 0, 0]} />
                    <Bar dataKey="after" name={tx('Sonra')} fill="hsl(var(--primary))" radius={[6, 6, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>
          )}
        </PanelBody>
      </Panel>
      {hire && (
        <Modal open onClose={() => setHire(null)} title={tx('Yeni kadro ekle')} footer={<><Button variant="outline" onClick={() => setHire(null)}>{tx('Vazgeç')}</Button><Button disabled={!!salaryError || !hireName.trim()} onClick={() => { setMoves((ms) => [...ms, { employeeId: '00000000-0000-0000-0000-000000000000', name: hireName.trim(), toDepartmentId: hire, kind: 'Hire', plannedSalary: salary || null }]); setHire(null) }}>{tx('Ekle')}</Button></>}>
          <div className="grid gap-4 sm:grid-cols-2"><TextField label={tx('Pozisyon adı')} value={hireName} onChange={(e) => setHireName(e.target.value)} /><TextField label={tx('Planlanan brüt maaş')} inputMode="decimal" value={hireSalary} onChange={(e) => setHireSalary(e.target.value)} error={salaryError} /></div>
        </Modal>
      )}
    </div>
  )
}

export function OrgScenariosPage() {
  const list = useQuery({ queryKey: ['org-scenarios'], queryFn: ({ signal }) => engagementApi.orgScenarios(signal) })
  const [sel, setSel] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const create = useAction(() => engagementApi.createScenario({ name }), { success: tx('Senaryo oluşturuldu'), invalidate: [['org-scenarios']], onDone: (s) => { setSel(s.id); setCreating(false); setName('') } })
  const qc = useQueryClient()
  const confirm = useConfirm()
  // Silinen senaryonun etki sorgusu yeniden çekilmesin (404): önce listeden çıkarılır, sorguları atılır,
  // sonra yalnızca liste tazelenir.
  const del = useAction(async (id: string) => {
    await engagementApi.deleteScenario(id)
    qc.setQueryData<OrgScenario[]>(['org-scenarios'], (old) => old?.filter((s) => s.id !== id))
    await qc.cancelQueries({ queryKey: ['org-scenarios', id] })
    qc.removeQueries({ queryKey: ['org-scenarios', id] })
  }, { success: tx('Silindi'), onDone: () => { setSel(null); void qc.invalidateQueries({ queryKey: ['org-scenarios'], exact: true }) } })
  const askDelete = async (s: OrgScenario) => {
    if (await confirm({ title: tx('“{0}” senaryosu silinsin mi?', [s.name]), note: tx('Senaryo ve {0} hamlesi kalıcı olarak silinir. Gerçek organizasyon etkilenmez.', [s.moves.length]), action: tx('Sil') })) del.mutate(s.id)
  }
  const current = list.data?.find((s) => s.id === sel) ?? list.data?.[0]
  return (
    <TenantGate>
      <PageHeader title={tx('Org senaryoları')} description={tx('“Ekibi ikiye bölsek?”, “Bu departmanı birleştirsek?” — taşıma, ayrılış ve yeni kadro hamlelerinin kadro ve maliyet etkisini önceden görün.')} actions={<Button onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Yeni senaryo')}</Button>} />
      {list.isPending ? <RowsSkeleton /> : list.isError ? <ErrorState message={(list.error as Error).message} /> : (list.data ?? []).length === 0 ? (
        <EmptyState icon={Workflow} title={tx('Henüz senaryo yok')} detail={tx('Bir senaryo oluşturup kişileri departmanlar arasında sürükleyin.')} action={<Button onClick={() => setCreating(true)}>{tx('Senaryo oluştur')}</Button>} />
      ) : (
        <>
          <div className="mb-5 flex flex-wrap items-end gap-3">
            <div className="w-72"><SelectField label={tx('Senaryo')} value={current?.id ?? ''} onChange={setSel} options={list.data!.map((s) => ({ value: s.id, label: tx('{0} ({1} hamle)', [s.name, s.moves.length]) }))} /></div>
            {current && <Button variant="ghost" onClick={() => askDelete(current)} disabled={del.isPending}><Trash2 className="size-4" />{' '}{tx('Sil')}</Button>}
          </div>
          {current && <Editor scenario={current} />}
        </>
      )}
      {creating && (
        <Modal open onClose={() => setCreating(false)} title={tx('Yeni senaryo')} footer={<><Button variant="outline" onClick={() => setCreating(false)}>{tx('Vazgeç')}</Button><Button disabled={!name.trim()} onClick={() => create.mutate(undefined)}>{tx('Oluştur')}</Button></>}>
          <TextField label={tx('Senaryo adı')} value={name} onChange={(e) => setName(e.target.value)} placeholder={tx('Örn. 2027 yeniden yapılanma')} />
        </Modal>
      )}
    </TenantGate>
  )
}
