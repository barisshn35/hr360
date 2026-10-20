import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Briefcase, Pencil, Plus, Trash2, UserX } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextField } from '@/components/ui/field'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useConfirm } from '@/components/ui/confirm'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { apiFetch } from '@/api/client'
import { useAuth } from '@/auth/useAuth'
import { useDepartments } from '@/features/performance/hooks'
import { useAction } from '@/features/shared/kit'
import { formatMoney, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { useModules } from '@/lib/modules'

interface PositionRow {
  id: string; departmentId: string; department: string; title: string; headcount: number; filled: number; open: number; over: number
  grade: string | null; notes: string | null; budgetMonthly: number | null; currency: string | null; holders: { employeeId: string; name: string }[]
}
interface PositionsView {
  positions: PositionRow[]
  outside: { employeeId: string; name: string; departmentId: string; department: string; positionTitle: string | null }[]
  summary: { approved: number; filled: number; open: number; over: number; outside: number; budgetMonthly: { currency: string; amount: number }[] | null }
}
type Input = { departmentId: string; title: string; headcount: number; grade?: string; budgetMonthly?: number | null; currency?: string; notes?: string }

const B = '/api/organization/positions'
const api = {
  list: (departmentId?: string, signal?: AbortSignal) => apiFetch<PositionsView>(`${B}${departmentId ? `?departmentId=${departmentId}` : ''}`, { signal }),
  create: (b: Input) => apiFetch<unknown>(B, { method: 'POST', body: b }),
  update: (id: string, b: Input) => apiFetch<unknown>(`${B}/${id}`, { method: 'PUT', body: b }),
  remove: (id: string) => apiFetch<void>(`${B}/${id}`, { method: 'DELETE' }),
}

function PositionModal({ row, initial, onClose, showBudget }: { row?: PositionRow; initial?: Partial<Input>; onClose: () => void; showBudget: boolean }) {
  const depts = useDepartments()
  const [f, setF] = useState({
    departmentId: row?.departmentId ?? initial?.departmentId ?? '', title: row?.title ?? initial?.title ?? '', headcount: String(row?.headcount ?? initial?.headcount ?? 1),
    grade: row?.grade ?? '', budget: row?.budgetMonthly != null ? String(row.budgetMonthly) : '', notes: row?.notes ?? '',
  })
  const body = (): Input => ({ departmentId: f.departmentId, title: f.title.trim(), headcount: Number(f.headcount), grade: f.grade.trim() || undefined,
    budgetMonthly: showBudget ? (f.budget ? Number(f.budget) : null) : row?.budgetMonthly ?? null, notes: f.notes.trim() || undefined })
  const save = useAction(() => (row ? api.update(row.id, body()) : api.create(body())), { success: tx('Pozisyon kaydedildi.'), invalidate: [['positions']], onDone: onClose })
  const ok = f.departmentId && f.title.trim().length >= 2 && Number(f.headcount) >= 0
  return (
    <Modal open onClose={onClose} title={row ? tx('Pozisyonu düzenle') : tx('Yeni pozisyon')}
      note={tx('Dolu kadro, bu departmanda aynı unvanla atanmış çalışanlardan hesaplanır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ok || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <SelectField label={tx('Departman')} value={f.departmentId} onChange={(v) => setF({ ...f, departmentId: v })} options={depts.list.map((d) => ({ value: d.id, label: d.path }))} />
        <div className="grid gap-3 sm:grid-cols-[1fr_120px]">
          <TextField label={tx('Unvan')} value={f.title} maxLength={120} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder={tx('ör. Yazılım Mühendisi')} />
          <TextField label={tx('Onaylı kadro')} type="number" min={0} value={f.headcount} onChange={(e) => setF({ ...f, headcount: e.target.value })} />
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={tx('Kademe (isteğe bağlı)')} value={f.grade} maxLength={40} onChange={(e) => setF({ ...f, grade: e.target.value })} />
          {showBudget && <TextField label={tx('Aylık bütçe, brüt (isteğe bağlı)')} type="number" min={0} value={f.budget} onChange={(e) => setF({ ...f, budget: e.target.value })} />}
        </div>
        <TextField label={tx('Not (isteğe bağlı)')} value={f.notes} maxLength={500} onChange={(e) => setF({ ...f, notes: e.target.value })} />
      </div>
    </Modal>
  )
}

function Bar({ filled, headcount }: { filled: number; headcount: number }) {
  const pct = headcount ? Math.min(100, (filled / headcount) * 100) : filled ? 100 : 0
  return (
    <div className="flex items-center gap-2">
      <div className="h-1.5 w-24 overflow-hidden rounded-full bg-muted"><div className={cn('h-full rounded-full', filled > headcount ? 'bg-amber-400' : 'bg-primary')} style={{ width: `${pct}%` }} /></div>
      <span className="text-[12.5px] tabular-nums">{filled}/{headcount}</span>
    </div>
  )
}

/** Norm kadro ve pozisyonlar (yenilik haritası #12). */
export function PositionsPage() {
  const { roles } = useAuth()
  const isHr = roles.some((r) => r === 'hr-admin' || r === 'tenant-admin')
  const recruitment = useModules().isEnabled('recruitment')
  const confirm = useConfirm()
  const depts = useDepartments()
  const [dept, setDept] = useState('')
  const [edit, setEdit] = useState<{ row?: PositionRow; initial?: Partial<Input> } | null>(null)
  const q = useQuery({ queryKey: ['positions', dept], queryFn: ({ signal }) => api.list(dept || undefined, signal) })
  const remove = useAction((id: string) => api.remove(id), { success: tx('Pozisyon kaldırıldı.'), invalidate: [['positions']] })
  const groups = useMemo(() => {
    const m = new Map<string, PositionRow[]>()
    for (const p of q.data?.positions ?? []) m.set(p.department, [...(m.get(p.department) ?? []), p])
    return [...m.entries()]
  }, [q.data])
  const s = q.data?.summary

  return (
    <div className="space-y-5">
      <PageHeader title={tx('Norm kadro')} eyebrow={[tx('Kişiler')]} description={tx('Departmanların onaylı pozisyonları, dolu ve boş kadro, bütçe. Boş kadrodan tek tıkla ilan açılır.')}
        actions={isHr && <Button onClick={() => setEdit({})}><Plus className="size-4" />{tx('Yeni pozisyon')}</Button>} />
      <div className="w-72"><SelectField label={tx('Departman')} value={dept || '__all'} onChange={(v) => setDept(v === '__all' ? '' : v)}
        options={[{ value: '__all', label: tx('Tümü') }, ...depts.list.map((d) => ({ value: d.id, label: d.path }))]} /></div>
      {s && (
        <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
          {[[tx('Onaylı kadro'), s.approved], [tx('Dolu'), s.filled], [tx('Boş'), s.open], [tx('Kadro fazlası'), s.over], [tx('Kadro dışı çalışan'), s.outside]].map(([l, v]) => (
            <Panel key={String(l)} className="gap-1 p-4"><span className="text-[12px] text-muted-foreground">{l}</span><span className="text-[24px] font-semibold tabular-nums">{formatNumber(Number(v))}</span></Panel>
          ))}
        </div>
      )}
      {s?.budgetMonthly && s.budgetMonthly.length > 0 && <InfoNote>{tx('Toplam aylık kadro bütçesi: {0}', [s.budgetMonthly.map((b) => formatMoney(b.amount, b.currency)).join(' · ')])}</InfoNote>}
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : !groups.length ? (
        <EmptyState icon={Briefcase} title={tx('Henüz pozisyon tanımlanmadı')} detail={tx('Her departman için onaylı unvanları ve kadro sayısını girin; dolu ve boş kadro kendiliğinden hesaplanır.')}
          action={isHr && <Button onClick={() => setEdit({})}>{tx('Yeni pozisyon')}</Button>} />
      ) : groups.map(([name, rows]) => (
        <Panel key={name}>
          <PanelHead title={name} note={tx('{0} pozisyon · {1}/{2} dolu', [rows.length, rows.reduce((a, r) => a + r.filled, 0), rows.reduce((a, r) => a + r.headcount, 0)])} />
          <PanelBody className="overflow-x-auto p-0">
            <table className="w-full min-w-[720px] text-[13px]">
              <thead className="text-left text-[11.5px] text-muted-foreground">
                <tr><th className="px-5 py-2 font-normal">{tx('Unvan')}</th><th className="px-3 py-2 font-normal">{tx('Dolu / onaylı')}</th><th className="px-3 py-2 font-normal">{tx('Kişiler')}</th>
                  {isHr && <th className="px-3 py-2 text-right font-normal">{tx('Aylık bütçe')}</th>}<th className="px-5 py-2" /></tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id} className="border-t border-border">
                    <td className="px-5 py-2.5"><span className="block font-medium">{r.title}</span>{r.grade && <span className="text-[11.5px] text-muted-foreground">{r.grade}</span>}</td>
                    <td className="px-3 py-2.5"><Bar filled={r.filled} headcount={r.headcount} />{r.over > 0 && <span className="text-[11.5px] text-amber-600 dark:text-amber-300">{tx('{0} fazla', [r.over])}</span>}</td>
                    <td className="px-3 py-2.5"><span className="flex -space-x-1.5">{r.holders.slice(0, 6).map((h) => <span key={h.employeeId} title={h.name}><EmployeeAvatar id={h.employeeId} name={h.name} size={24} className="ring-2 ring-card" /></span>)}</span></td>
                    {isHr && <td className="px-3 py-2.5 text-right tabular-nums">{r.budgetMonthly != null ? formatMoney(r.budgetMonthly, r.currency ?? 'TRY') : '—'}</td>}
                    <td className="px-5 py-2.5">
                      <div className="flex justify-end gap-1">
                        {r.open > 0 && recruitment && <Button size="sm" variant="outline" asChild>
                          <Link to={`/panel/ise-alim?unvan=${encodeURIComponent(r.title)}&departman=${r.departmentId}&kadro=${r.open}`}>{tx('{0} boş: ilan aç', [r.open])}</Link>
                        </Button>}
                        {isHr && <Button size="icon" variant="ghost" aria-label={tx('Düzenle')} onClick={() => setEdit({ row: r })}><Pencil className="size-4" /></Button>}
                        {isHr && <Button size="icon" variant="ghost" aria-label={tx('Kaldır')} onClick={async () => { if (await confirm({ title: tx('"{0}" pozisyonu kaldırılsın mı?', [r.title]), action: tx('Kaldır') })) remove.mutate(r.id) }}><Trash2 className="size-4" /></Button>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </PanelBody>
        </Panel>
      ))}
      {(q.data?.outside.length ?? 0) > 0 && (
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><UserX className="size-4 text-amber-500" />{tx('Kadro dışı çalışanlar')}</span>}
            note={tx('Unvanı departmanındaki hiçbir pozisyonla eşleşmeyenler: ya pozisyon eksik ya da unvan farklı yazılmış.')} />
          <PanelBody className="flex flex-wrap gap-2">
            {q.data!.outside.map((o) => (
              <span key={o.employeeId} className="flex items-center gap-2 rounded-full border border-border py-1 pr-3 pl-1 text-[12.5px]">
                <EmployeeAvatar id={o.employeeId} name={o.name} size={24} />
                <span>{o.name} <span className="text-muted-foreground">· {o.positionTitle ?? tx('unvansız')} · {o.department}</span></span>
                {isHr && o.positionTitle && <button type="button" className="cursor-pointer text-primary hover:underline" onClick={() => setEdit({ initial: { departmentId: o.departmentId, title: o.positionTitle!, headcount: 1 } })}>{tx('pozisyon oluştur')}</button>}
              </span>
            ))}
          </PanelBody>
        </Panel>
      )}
      {edit && <PositionModal row={edit.row} initial={edit.initial} showBudget={isHr} onClose={() => setEdit(null)} />}
    </div>
  )
}
