import { Fragment, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { GitBranch, Pencil, Plus, Sparkles, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { engagementApi, levelLabels, readinessLabels, type Level, type Readiness, type SuccessionInput, type SuccessionPlan } from '@/api/engagement'
import { cn } from '@/lib/utils'
import { Initials, PersonSelect, TenantGate, useAction } from '@/features/shared/kit'
import { useDirectory } from '@/api/directory'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/Confirm'

const LEVELS: Level[] = ['High', 'Medium', 'Low']
const levelTone = { High: 'danger', Medium: 'warning', Low: 'success' } as const
const readyCls: Record<Readiness, string> = { ReadyNow: 'bg-emerald-500/15 text-emerald-400', OneToTwoYears: 'bg-amber-400/15 text-amber-400', ThreePlusYears: 'bg-zinc-500/15 text-zinc-400' }

function PlanModal({ plan, onClose }: { plan?: SuccessionPlan; onClose: () => void }) {
  const dir = useDirectory()
  const [f, setF] = useState<SuccessionInput>({
    positionTitle: plan?.positionTitle ?? '', departmentName: plan?.departmentName ?? '', incumbentEmployeeId: plan?.incumbentEmployeeId ?? '',
    criticality: plan?.criticality ?? 'High', vacancyRisk: plan?.vacancyRisk ?? 'Medium', notes: plan?.notes ?? '',
    candidates: plan?.candidates.map((c) => ({ employeeId: c.employeeId, name: c.name, readiness: c.readiness, notes: c.notes })) ?? [],
  })
  const [add, setAdd] = useState('')
  const sug = useQuery({ queryKey: ['succession', 'suggest', f.incumbentEmployeeId], queryFn: ({ signal }) => engagementApi.suggestSuccessors(f.incumbentEmployeeId || undefined, signal) })
  const incumbent = f.incumbentEmployeeId || ''
  // Seçilip "+" basılmamış aday kaydedilirken sessizce atılmaz; havuza eklenir.
  const pendingAdd = add && add !== incumbent && !f.candidates.some((c) => c.employeeId === add) ? add : ''
  const incumbentIsCandidate = !!incumbent && f.candidates.some((c) => c.employeeId === incumbent)
  const save = useAction(() => {
    const candidates = pendingAdd
      ? [...f.candidates, { employeeId: pendingAdd, name: dir.data?.find((d) => d.id === pendingAdd)?.fullName ?? '', readiness: 'OneToTwoYears' as Readiness }]
      : f.candidates
    const body = { ...f, candidates, incumbentEmployeeId: f.incumbentEmployeeId || null }
    return plan ? engagementApi.updateSuccession(plan.id, body) : engagementApi.createSuccession(body)
  }, { success: tx('Ardıl planı kaydedildi'), invalidate: [['succession']], onDone: onClose })
  // Sunucu aynı departmanı öne alır ama listeyi diğer departmanlarla tamamlar; görevdeki kişi seçiliyken
  // yalnızca gerçekten aynı departmandakiler "aynı departman" önerisi olarak gösterilir.
  const suggestions = (sug.data ?? [])
    .filter((s) => (incumbent ? s.sameDepartment : true) && s.employeeId !== incumbent && !f.candidates.some((c) => c.employeeId === s.employeeId))
    .slice(0, 6)
  const addCandidate = (id: string, name: string) => {
    // Görevdeki kişi kendi ardılı olamaz.
    if (!id || id === incumbent || f.candidates.some((c) => c.employeeId === id)) return
    setF({ ...f, candidates: [...f.candidates, { employeeId: id, name, readiness: 'OneToTwoYears' }] })
  }
  return (
    <Modal open onClose={onClose} size="xl" title={plan ? tx('Ardıl planını düzenle') : tx('Yeni kritik pozisyon')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!f.positionTitle.trim() || incumbentIsCandidate || save.isPending} onClick={() => save.mutate(undefined)}>{pendingAdd ? tx('Adayı ekle ve kaydet') : tx('Kaydet')}</Button></>}>
      <div className="grid gap-6 lg:grid-cols-2">
        <div className="space-y-4">
          <TextField label={tx('Pozisyon')} value={f.positionTitle} onChange={(e) => setF({ ...f, positionTitle: e.target.value })} />
          <PersonSelect label={tx('Görevdeki kişi')} value={f.incumbentEmployeeId ?? ''} onChange={(v) => setF({ ...f, incumbentEmployeeId: v })}
            hint={incumbentIsCandidate ? tx('Görevdeki kişi aday havuzunda; kendi ardılı olamaz. Havuzdan çıkarın ya da başka kişi seçin.') : undefined} />
          <div className="grid grid-cols-2 gap-3">
            <SelectField label={tx('Kritiklik')} value={f.criticality} onChange={(v) => setF({ ...f, criticality: v as Level })} options={LEVELS.map((l) => ({ value: l, label: levelLabels[l] }))} />
            <SelectField label={tx('Ayrılma riski')} value={f.vacancyRisk} onChange={(v) => setF({ ...f, vacancyRisk: v as Level })} options={LEVELS.map((l) => ({ value: l, label: levelLabels[l] }))} />
          </div>
          <TextAreaField label={tx('Notlar')} rows={3} value={f.notes ?? ''} onChange={(e) => setF({ ...f, notes: e.target.value })} />
        </div>
        <div className="space-y-4">
          <p className="text-[13.5px] font-medium">{tx('Aday havuzu')}</p>
          <ul className="space-y-2">
            {f.candidates.map((c, i) => (
              <li key={c.employeeId} className="flex items-center gap-2 rounded-xl border border-border p-2">
                <Initials name={c.name} size={28} />
                <span className="flex-1 truncate text-[13px]">{c.name}</span>
                <div className="w-36"><SelectField label="" value={c.readiness} onChange={(v) => setF({ ...f, candidates: f.candidates.map((x, j) => (j === i ? { ...x, readiness: v as Readiness } : x)) })} options={(Object.keys(readinessLabels) as Readiness[]).map((r) => ({ value: r, label: readinessLabels[r] }))} /></div>
                <Button size="icon" variant="ghost" aria-label={tx('Çıkar')} onClick={() => setF({ ...f, candidates: f.candidates.filter((_, j) => j !== i) })}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
          <div className="flex items-end gap-2"><div className="flex-1"><PersonSelect label={tx('Aday ekle')} value={add} onChange={setAdd} exclude={[...(incumbent ? [incumbent] : []), ...f.candidates.map((c) => c.employeeId)]}
            hint={pendingAdd ? tx('Kaydettiğinizde bu kişi de havuza eklenir.') : undefined} /></div>
            <Button variant="outline" aria-label={tx('Aday ekle')} disabled={!pendingAdd} onClick={() => { addCandidate(add, dir.data?.find((d) => d.id === add)?.fullName ?? ''); setAdd('') }}><Plus className="size-4" /></Button></div>
          {suggestions.length > 0 && (
            <div>
              <p className="mb-1.5 flex items-center gap-1.5 text-[12.5px] text-muted-foreground"><Sparkles className="size-3.5 text-primary" />{' '}{incumbent ? tx('Öneriler (aynı departman + son performans puanı)') : tx('Öneriler (son performans puanı)')}</p>
              <div className="flex flex-wrap gap-1.5">
                {suggestions.map((s) => (
                  <button key={s.employeeId} onClick={() => addCandidate(s.employeeId, s.name)} className="cursor-pointer rounded-full border border-dashed border-border px-2.5 py-1 text-[12px] hover:border-primary/50">
                    + {s.name} <span className="text-muted-foreground">{tx('{0} · {1} yıl', [s.score != null ? `· ${Math.round(s.score)}` : '', s.tenureYears])}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </Modal>
  )
}

export function SuccessionPage() {
  const q = useQuery({ queryKey: ['succession'], queryFn: ({ signal }) => engagementApi.succession(signal) })
  const [edit, setEdit] = useState<SuccessionPlan | null | undefined>(undefined)
  const del = useAction((id: string) => engagementApi.deleteSuccession(id), { success: tx('Silindi'), invalidate: [['succession']] })
  const confirm = useConfirm()
  const askDelete = async (p: SuccessionPlan) => {
    if (await confirm({ title: tx('“{0}” ardıl planı silinsin mi?', [p.positionTitle]), note: tx('Pozisyon ve {0} kişilik aday havuzu kalıcı olarak silinir.', [p.candidates.length]), action: tx('Sil') })) del.mutate(p.id)
  }
  const plans = q.data ?? []
  return (
    <TenantGate>
      <PageHeader title={tx('Ardıl planlama')} description={tx('Kritik rollerde görevdeki kişi yarın ayrılsa kim hazır? Riskli pozisyonları ve yedek havuzunu görün.')} actions={<Button onClick={() => setEdit(null)}><Plus className="size-4" />{' '}{tx('Kritik pozisyon')}</Button>} />
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState message={(q.error as Error).message} /> : plans.length === 0 ? (
        <EmptyState icon={GitBranch} title={tx('Henüz ardıl planı yok')} detail={tx('Önce şirketin en kritik 3–5 rolünü ekleyin.')} action={<Button onClick={() => setEdit(null)}>{tx('Kritik pozisyon ekle')}</Button>} />
      ) : (
        <div className="grid gap-6 xl:grid-cols-[380px_1fr]">
          <Panel>
            <PanelHead title={tx('Risk matrisi')} note={tx('Satır: kritiklik · Sütun: ayrılma riski')} />
            <PanelBody>
              <div className="grid grid-cols-[60px_repeat(3,1fr)] gap-1.5 text-[11.5px]">
                <span />
                {['Low', 'Medium', 'High'].map((r) => <span key={r} className="text-center text-muted-foreground">{levelLabels[r as Level]}</span>)}
                {LEVELS.map((c) => (
                  <Fragment key={c}>
                    <span className="self-center text-muted-foreground">{levelLabels[c]}</span>
                    {(['Low', 'Medium', 'High'] as Level[]).map((r) => {
                      const items = plans.filter((p) => p.criticality === c && p.vacancyRisk === r)
                      const heat = (LEVELS.length - LEVELS.indexOf(c)) + (['Low', 'Medium', 'High'].indexOf(r) + 1)
                      return (
                        <div key={`${c}-${r}`} className={cn('min-h-16 rounded-xl border p-1.5', heat >= 5 ? 'border-destructive/40 bg-destructive/10' : heat >= 4 ? 'border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/10' : 'border-border bg-muted/30')}>
                          {items.map((p) => <button key={p.id} onClick={() => setEdit(p)} className="mb-1 block w-full cursor-pointer truncate rounded-md bg-background/60 px-1.5 py-0.5 text-left text-[11px] hover:bg-background">{p.positionTitle}</button>)}
                        </div>
                      )
                    })}
                  </Fragment>
                ))}
              </div>
            </PanelBody>
          </Panel>
          <div className="grid gap-4 md:grid-cols-2">
            {plans.map((p, i) => (
              <motion.div key={p.id} initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="surface rounded-2xl border border-border p-5">
                <div className="flex items-start justify-between gap-2">
                  <div><h3 className="text-[15.5px] font-semibold">{p.positionTitle}</h3><p className="text-[12.5px] text-muted-foreground">{p.departmentName ?? '—'} · {p.incumbentName ?? tx('boş')}</p></div>
                  <div className="flex gap-1"><Button size="icon" variant="ghost" aria-label={tx('Düzenle')} onClick={() => setEdit(p)}><Pencil className="size-4" /></Button><Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => askDelete(p)}><Trash2 className="size-4" /></Button></div>
                </div>
                <div className="mt-2 flex flex-wrap gap-1.5">
                  <StatusBadge tone={levelTone[p.criticality]}>{tx('Kritiklik: {0}', [levelLabels[p.criticality]])}</StatusBadge>
                  <StatusBadge tone={levelTone[p.vacancyRisk]}>{tx('Risk: {0}', [levelLabels[p.vacancyRisk]])}</StatusBadge>
                  <StatusBadge tone={p.benchStrength === 'Strong' ? 'success' : p.benchStrength === 'Developing' ? 'warning' : 'danger'}>{p.benchStrength === 'Strong' ? tx('Yedek hazır') : p.benchStrength === 'Developing' ? tx('Yedek gelişiyor') : tx('Yedek yok')}</StatusBadge>
                </div>
                <ul className="mt-4 space-y-1.5">
                  {p.candidates.length === 0 && <li className="text-[12.5px] text-muted-foreground">{tx('Aday eklenmemiş.')}</li>}
                  {p.candidates.map((c) => (
                    <li key={c.employeeId} className="flex items-center gap-2 text-[13px]"><Initials name={c.name} size={26} /><span className="flex-1 truncate">{c.name}</span>{c.score != null && <span className="tabular text-[12px] text-muted-foreground">{Math.round(c.score)}</span>}<span className={cn('rounded-full px-2 py-0.5 text-[11px]', readyCls[c.readiness])}>{readinessLabels[c.readiness]}</span></li>
                  ))}
                </ul>
              </motion.div>
            ))}
          </div>
        </div>
      )}
      {edit !== undefined && <PlanModal plan={edit ?? undefined} onClose={() => setEdit(undefined)} />}
    </TenantGate>
  )
}
