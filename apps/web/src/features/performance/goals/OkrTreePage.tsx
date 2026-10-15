/**
 * Dalga 11 / 80: OKR hizalama ağacı — `/panel/performans/okr`.
 *
 * Şirket amacı → departman amacı → kişisel hedef. İlerleme yukarı doğru ağırlıklı ortalamayla toplanır
 * (backend OkrMath). Ağaç girintili, katlanabilir liste olarak çizilir: organizasyon şeması yerleşimleri
 * (orgLayouts.ts) departman/kişi sayısı modeline bağlı olduğundan burada yeniden kullanılmadı; liste,
 * uzun başlıklarda ve ekran okuyucuda daha okunaklıdır.
 * KVKK: çalışan yalnızca kendi hedeflerini görür; küçük grupların (kendisi dışında 1-4 kişi) ilerlemesi gizlenir.
 */
import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, EyeOff, Link2Off, Network, Pencil, Plus, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { flattenOkr, okrApi, type ObjectiveInput, type OkrKind, type OkrNodeView, type OkrTree } from '@/api/performanceGrowth'
import { errMsg, useAction } from '@/features/shared/kit'
import { cn } from '@/lib/utils'
import { pct, tx } from '@/lib/i18n'
import { formatNumber } from '@/lib/format'
import { useCurrentCycle } from '../hooks'

const kindText: Record<OkrKind, [string, StatusTone]> = {
  Company: [tx('Şirket'), 'info'],
  Department: [tx('Departman'), 'success'],
  Goal: [tx('Kişisel hedef'), 'neutral'],
}

function Progress({ value, hidden, goal }: { value: number | null | undefined; hidden?: boolean; goal?: boolean }) {
  if (hidden) return <span className="flex items-center gap-1 text-[12px] text-muted-foreground" title={tx('Küçük grup: kişisel ilerleme çıkarılamasın diye gizlendi')}><EyeOff className="size-3.5" />{tx('Gizli')}</span>
  // İlerleme girilmemiş hedef %50 sayılmaz ve üst amacın ortalamasına katılmaz (backend OkrMath.GoalProgress).
  if (value === null || value === undefined) {
    const note = goal ? tx('İlerleme girilmedi') : tx('İlerlemesi girilmiş hedef yok')
    return <span className="text-[12px] text-muted-foreground" title={note} aria-label={note}>—</span>
  }
  return (
    <span className="flex w-36 items-center gap-2">
      <span className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted"><span className="block h-full rounded-full bg-primary" style={{ width: `${Math.min(100, Math.max(0, value))}%` }} /></span>
      <span className="tabular w-11 text-right text-[12px]">{pct(formatNumber(value))}</span>
    </span>
  )
}

function ObjectiveDialog({ tree, editing, parent, onClose }: { tree: OkrTree; editing: OkrNodeView | null; parent: OkrNodeView | null; onClose: () => void }) {
  const levels = [
    ...(tree.canEditCompany ? [{ value: 'Company', label: tx('Şirket amacı') }] : []),
    ...(tree.editableDepartments.length ? [{ value: 'Department', label: tx('Departman amacı') }] : []),
  ]
  const [form, setForm] = useState({
    level: (editing?.kind ?? (parent ? 'Department' : levels[0]?.value ?? 'Department')) as 'Company' | 'Department',
    departmentId: editing?.departmentId ?? tree.editableDepartments[0]?.id ?? '',
    parentId: editing?.parentId ?? parent?.id ?? '',
    title: editing?.title ?? '',
    description: editing?.description ?? '',
    weight: String(editing?.weight ?? 100),
  })
  const body = (): ObjectiveInput => ({
    cycleId: tree.cycle.id, level: form.level, departmentId: form.level === 'Department' ? form.departmentId || null : null,
    parentId: form.level === 'Department' ? form.parentId || null : null, title: form.title.trim(), description: form.description.trim() || null, weight: Number(form.weight) || 100,
  })
  const save = useAction(() => (editing ? okrApi.update(editing.id, body()) : okrApi.create(body())), {
    success: editing ? tx('Amaç güncellendi') : tx('Amaç eklendi'), invalidate: [['perf', 'okr']], onDone: onClose,
  })
  const companies = tree.objectives.filter((o) => o.level === 'Company')
  return (
    <Modal open onClose={onClose} title={editing ? tx('Amacı düzenle') : tx('Amaç ekle')}
      footer={<Button onClick={() => save.mutate(undefined)} disabled={save.isPending || form.title.trim().length < 3}>{tx('Kaydet')}</Button>}>
      <div className="space-y-3">
        {!editing && <SelectField label={tx('Seviye')} value={form.level} onChange={(v) => setForm({ ...form, level: v as 'Company' | 'Department' })} options={levels} />}
        {form.level === 'Department' && !editing && (
          <SelectField label={tx('Departman')} value={form.departmentId} onChange={(v) => setForm({ ...form, departmentId: v })}
            options={tree.editableDepartments.map((d) => ({ value: d.id, label: d.name }))} />
        )}
        {form.level === 'Department' && (
          <SelectField label={tx('Bağlı olduğu şirket amacı')} value={form.parentId || '__none__'} onChange={(v) => setForm({ ...form, parentId: v === '__none__' ? '' : v })}
            options={[{ value: '__none__', label: tx('Bağlantısız') }, ...companies.map((c) => ({ value: c.id, label: c.title }))]} />
        )}
        <TextField label={tx('Başlık')} value={form.title} maxLength={200} onChange={(e) => setForm({ ...form, title: e.target.value })} />
        <TextAreaField label={tx('Açıklama (isteğe bağlı)')} rows={2} maxLength={2000} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
        <TextField label={tx('Ağırlık (1-100)')} type="number" min={1} max={100} value={form.weight} onChange={(e) => setForm({ ...form, weight: e.target.value })}
          hint={tx('Üst amacın ilerlemesine katkı oranı.')} />
      </div>
    </Modal>
  )
}

function TreeView({ tree }: { tree: OkrTree }) {
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [dialog, setDialog] = useState<{ editing: OkrNodeView | null; parent: OkrNodeView | null } | null>(null)
  const confirm = useConfirm()
  const remove = useAction((id: string) => okrApi.remove(id), { success: tx('Amaç silindi'), invalidate: [['perf', 'okr']] })
  const unlink = useAction((goalId: string) => okrApi.align(goalId, null), { success: tx('Bağlantı kaldırıldı'), invalidate: [['perf', 'okr']] })
  const align = useAction(({ goalId, parent }: { goalId: string; parent: string }) => okrApi.align(goalId, parent), { success: tx('Hedef amaca bağlandı'), invalidate: [['perf', 'okr']] })
  const rows = useMemo(() => flattenOkr(tree.roots, collapsed), [tree.roots, collapsed])
  const toggle = (id: string) => setCollapsed((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const canAdd = tree.canEditCompany || tree.editableDepartments.length > 0
  const objectiveOptions = tree.objectives.map((o) => ({ value: o.id, label: `${o.level === 'Company' ? tx('Şirket') : tx('Departman')} · ${o.title}` }))

  return (
    <div className="space-y-4">
      <Panel>
        <PanelHead title={tree.cycle.name} note={tx('Şirket → departman → kişisel hedef. İlerleme alt düğümlerin ağırlıklı ortalamasıdır.')}
          action={canAdd ? <Button size="sm" onClick={() => setDialog({ editing: null, parent: null })}><Plus className="size-4" />{tx('Amaç ekle')}</Button> : undefined} />
        <PanelBody>
          {rows.length === 0 ? (
            <EmptyState icon={Network} title={tx('Bu dönem için amaç tanımlanmamış')} detail={canAdd ? tx('Önce şirket amaçlarını, ardından departman amaçlarını ekleyin; kişisel hedefleri bunlara bağlayın.') : tx('Amaçları İK ve departman başları tanımlar.')} />
          ) : (
            <ul role="tree" aria-label={tx('OKR hizalama ağacı')} className="divide-y divide-border">
              {rows.map(({ node, depth }) => (
                <li key={node.id} role="treeitem" aria-level={depth + 1} aria-expanded={node.children.length ? !collapsed.has(node.id) : undefined}
                  className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2" style={{ paddingLeft: depth * 22 }}>
                  {node.children.length > 0 ? (
                    <button type="button" className="cursor-pointer text-muted-foreground" onClick={() => toggle(node.id)} aria-label={collapsed.has(node.id) ? tx('Aç') : tx('Kapat')}>
                      {collapsed.has(node.id) ? <ChevronRight className="size-4" /> : <ChevronDown className="size-4" />}
                    </button>
                  ) : <span className="w-4" />}
                  <StatusBadge tone={kindText[node.kind][1]}>{kindText[node.kind][0]}</StatusBadge>
                  <span className={cn('min-w-0 flex-1 text-[13.5px]', node.kind !== 'Goal' && 'font-medium')}>
                    {node.title}
                    {node.departmentName && <span className="ml-2 text-[12px] text-muted-foreground">{node.departmentName}</span>}
                    {node.employeeName && <span className="ml-2 text-[12px] text-muted-foreground">{node.employeeName}</span>}
                    {(node.hiddenGoals ?? 0) > 0 && <span className="ml-2 text-[12px] text-muted-foreground">{tx('+{0} kişisel hedef', [node.hiddenGoals ?? 0])}</span>}
                  </span>
                  <span className="tabular text-[12px] text-muted-foreground">{tx('ağırlık {0}', [node.weight])}</span>
                  <Progress value={node.progress} hidden={node.progressHidden} goal={node.kind === 'Goal'} />
                  {node.kind !== 'Goal' && node.canEdit && tree.cycle.status !== 'Closed' && (
                    <span className="flex gap-1">
                      {node.kind === 'Company' && tree.editableDepartments.length > 0 && (
                        <Button size="icon" variant="ghost" aria-label={tx('Alt amaç ekle')} onClick={() => setDialog({ editing: null, parent: node })}><Plus className="size-4" /></Button>
                      )}
                      <Button size="icon" variant="ghost" aria-label={tx('Düzenle')} onClick={() => setDialog({ editing: node, parent: null })}><Pencil className="size-4" /></Button>
                      <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={async () => {
                        if (await confirm({ title: tx('Amaç silinsin mi?'), note: tx('Bağlı alt amaçlar ve hedefler silinmez, bağlantısız kalır.'), action: tx('Sil') })) remove.mutate(node.id)
                      }}><Trash2 className="size-4" /></Button>
                    </span>
                  )}
                  {node.kind === 'Goal' && tree.canAlign && (
                    <Button size="icon" variant="ghost" aria-label={tx('Bağlantıyı kaldır')} onClick={() => unlink.mutate(node.id)}><Link2Off className="size-4" /></Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      {tree.unaligned.length > 0 && (
        <Panel>
          <PanelHead title={tx('Bağlantısız hedefler')} note={tree.canAlign ? tx('Kişisel hedefi bir şirket ya da departman amacına bağlayın.') : tx('Bir amaca bağlanmamış hedefleriniz.')} />
          <PanelBody>
            <ul className="divide-y divide-border">
              {tree.unaligned.map((g) => (
                <li key={g.id} className="flex flex-wrap items-center gap-3 py-2">
                  <span className="min-w-0 flex-1 text-[13px]">{g.title}{g.employeeName && <span className="ml-2 text-[12px] text-muted-foreground">{g.employeeName}</span>}</span>
                  <Progress value={g.progress} goal />
                  {tree.canAlign && objectiveOptions.length > 0 && (
                    <div className="w-72">
                      <SelectField label={tx('Bağla')} value="" onChange={(v) => align.mutate({ goalId: g.id, parent: v })} options={objectiveOptions} placeholder={tx('Amaç seçin')} />
                    </div>
                  )}
                </li>
              ))}
            </ul>
          </PanelBody>
        </Panel>
      )}
      {dialog && <ObjectiveDialog tree={tree} editing={dialog.editing} parent={dialog.parent} onClose={() => setDialog(null)} />}
    </div>
  )
}

export function OkrTreePage() {
  const { cycles, current, isPending } = useCurrentCycle()
  const list = useMemo(() => cycles.filter((c) => c.status !== 'Planned'), [cycles])
  const [cycleId, setCycleId] = useState('')
  useEffect(() => { if (!cycleId && current) setCycleId(current.id) }, [current, cycleId])
  const q = useQuery({ queryKey: ['perf', 'okr', cycleId], queryFn: ({ signal }) => okrApi.tree(cycleId, signal), enabled: !!cycleId })
  return (
    <div className="space-y-5">
      <PageHeader title={tx('OKR hizalama')} description={tx('Şirket, departman ve kişisel hedeflerin birbirine bağlandığı ağaç; ilerleme yukarı doğru toplanır.')} />
      <InfoNote>{tx('Kişisel hedefleri yalnızca yöneticiler ve İK görür. Çalışan görünümünde başkalarının hedefleri yalnızca sayı olarak yer alır ve 5 kişiden küçük grupların ilerlemesi gizlenir.')}</InfoNote>
      <div className="w-80">
        <SelectField label={tx('Dönem')} value={cycleId} onChange={setCycleId} options={list.map((c) => ({ value: c.id, label: c.name }))} hint={isPending ? tx('Yükleniyor') : undefined} />
      </div>
      {!cycleId ? (
        isPending ? <RowsSkeleton rows={3} /> : <EmptyState icon={Network} title={tx('Açık ya da kapanmış dönem yok')} detail={tx('OKR ağacı bir değerlendirme dönemine bağlıdır.')} />
      ) : q.isPending ? <RowsSkeleton rows={6} /> : q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : <TreeView tree={q.data} />}
    </div>
  )
}
