/**
 * G12 9-kutu: performans (nihai puan) × potansiyel (yönetici değerlendirmesi, 1-3). İK gerekçeyle
 * kalibrasyon düzeltmesi yapar (denetim kaydına yazılır). KVKK: tablo yalnızca tartışma aracıdır;
 * otomatik karar üretmez. Potansiyel İK yayımlamadıkça çalışana gösterilmez.
 */
import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Grid3x3, ShieldCheck } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useCycles } from '@/api/performance'
import { performanceExtrasApi, potentialLabels, type NineBoxEmployee, type NineBoxGrid } from '@/api/performanceExtras'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const perfLabels: Record<number, string> = { 1: tx('Beklentinin altında'), 2: tx('Beklentiyi karşılıyor'), 3: tx('Beklentinin üstünde') }
const bandOptions = [1, 2, 3].map((b) => ({ value: String(b), label: `${b} · ${potentialLabels[b]}` }))

function cellTone(cell: number) {
  const perf = ((cell - 1) % 3) + 1
  const pot = Math.floor((cell - 1) / 3) + 1
  const s = perf + pot
  return s >= 5 ? 'bg-[hsl(var(--success))]/10 border-[hsl(var(--success))]/30' : s >= 4 ? 'bg-primary/5 border-primary/20' : 'bg-muted/50 border-border'
}

function EmployeeDialog({ cycleId, e, canCalibrate, onClose }: { cycleId: string; e: NineBoxEmployee; canCalibrate: boolean; onClose: () => void }) {
  const [pot, setPot] = useState(String(e.potential ?? 2))
  const [note, setNote] = useState(e.potentialNote ?? '')
  const [ov, setOv] = useState({ perf: String(e.override?.performanceBand ?? e.performanceBand ?? 2), pot: String(e.override?.potentialBand ?? e.potential ?? 2), reason: '' })
  const inv = [['perf', 'nine-box', cycleId]]
  const savePot = useAction(() => performanceExtrasApi.setPotential({ cycleId, employeeId: e.employeeId, rating: Number(pot), note: note || undefined }),
    { success: tx('Potansiyel kaydedildi'), invalidate: inv, onDone: onClose })
  const publish = useAction((published: boolean) => performanceExtrasApi.publishPotential({ cycleId, employeeId: e.employeeId, published }),
    { success: tx('Paylaşım ayarı güncellendi'), invalidate: inv })
  const calibrate = useAction(() => performanceExtrasApi.override({ cycleId, employeeId: e.employeeId, performanceBand: Number(ov.perf), potentialBand: Number(ov.pot), reason: ov.reason.trim() }),
    { success: (r) => tx('Hücre düzeltildi: {0}', [r.label]), invalidate: inv, onDone: onClose })
  return (
    <Modal open onClose={onClose} size="lg" title={e.name} note={[e.positionTitle, e.department].filter(Boolean).join(' · ')}>
      <div className="space-y-5">
        <dl className="grid grid-cols-2 gap-3 text-[13px]">
          <div><dt className="text-muted-foreground">{tx('Nihai puan')}</dt><dd className="tabular font-semibold">{e.score ?? '—'}{e.isProvisional && <StatusBadge className="ml-2" tone="warning">{tx('Geçici')}</StatusBadge>}</dd></div>
          <div><dt className="text-muted-foreground">{tx('Performans bandı')}</dt><dd>{e.performanceBand ? perfLabels[e.performanceBand] : '—'}</dd></div>
          <div><dt className="text-muted-foreground">{tx('Potansiyel')}</dt><dd>{e.potential ? potentialLabels[e.potential] : '—'}{e.potentialRatedBy && <span className="block text-[12px] text-muted-foreground">{e.potentialRatedBy}</span>}</dd></div>
          <div><dt className="text-muted-foreground">{tx('Hesaplanan / geçerli hücre')}</dt><dd>{e.computedCell ?? '—'} / {e.cell ?? '—'}</dd></div>
        </dl>
        {e.override && (
          <InfoNote>{tx('Kalibrasyon: {0} — {1} ({2})', [e.override.reason, e.override.overriddenBy, formatDateTime(e.override.createdAt)])}</InfoNote>
        )}
        <section className="space-y-2 rounded-xl border border-border p-3">
          <h3 className="text-[13.5px] font-semibold">{tx('Potansiyel değerlendirmesi')}</h3>
          <div className="w-56"><SelectField label={tx('Potansiyel (1-3)')} value={pot} onChange={setPot} options={bandOptions} /></div>
          <TextAreaField label={tx('Gerekçe notu (isteğe bağlı)')} rows={2} maxLength={500} value={note} onChange={(ev) => setNote(ev.target.value)} hint={tx('Gözleme dayalı yazın; sağlık, aile gibi özel bilgi yazmayın.')} />
          <div className="flex flex-wrap items-center gap-3">
            <Button size="sm" onClick={() => savePot.mutate(undefined)} disabled={savePot.isPending}>{tx('Kaydet')}</Button>
            {canCalibrate && e.potential && (
              <label className="flex cursor-pointer items-center gap-2 text-[13px]">
                <Checkbox checked={e.potentialPublished} onCheckedChange={(v) => publish.mutate(v === true)} />
                {tx('Çalışanla paylaş (varsayılan: gizli)')}
              </label>
            )}
          </div>
        </section>
        {canCalibrate && (
          <section className="space-y-2 rounded-xl border border-border p-3">
            <h3 className="flex items-center gap-2 text-[13.5px] font-semibold"><ShieldCheck className="size-4 text-primary" />{' '}{tx('Kalibrasyon düzeltmesi (İK)')}</h3>
            <div className="grid grid-cols-2 gap-3">
              <SelectField label={tx('Performans bandı')} value={ov.perf} onChange={(v) => setOv({ ...ov, perf: v })} options={[1, 2, 3].map((b) => ({ value: String(b), label: `${b} · ${perfLabels[b]}` }))} />
              <SelectField label={tx('Potansiyel bandı')} value={ov.pot} onChange={(v) => setOv({ ...ov, pot: v })} options={bandOptions} />
            </div>
            <TextAreaField label={tx('Gerekçe (zorunlu, denetim kaydına yazılır)')} rows={2} value={ov.reason} onChange={(ev) => setOv({ ...ov, reason: ev.target.value })} />
            <Button size="sm" variant="outline" onClick={() => calibrate.mutate(undefined)} disabled={calibrate.isPending || ov.reason.trim().length < 10}>{tx('Hücreyi düzelt')}</Button>
          </section>
        )}
      </div>
    </Modal>
  )
}

function Grid({ data, onPick }: { data: NineBoxGrid; onPick: (e: NineBoxEmployee) => void }) {
  const byCell = new Map(data.cells.map((c) => [c.cell, c]))
  return (
    <div className="flex gap-3">
      <div className="flex w-6 items-center justify-center">
        <span className="-rotate-90 whitespace-nowrap text-[12px] font-medium text-muted-foreground">{tx('Potansiyel →')}</span>
      </div>
      <div className="flex-1 space-y-2">
        {[3, 2, 1].map((pot) => (
          <div key={pot} className="grid grid-cols-3 gap-2">
            {[1, 2, 3].map((perf) => {
              const c = byCell.get((pot - 1) * 3 + perf)!
              return (
                <div key={perf} className={cn('min-h-32 rounded-xl border p-3', cellTone(c.cell))}>
                  <p className="flex items-baseline justify-between gap-2 text-[12px] font-semibold">
                    <span>{c.label}</span><span className="tabular text-muted-foreground">{c.employees.length}</span>
                  </p>
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {c.employees.map((e) => (
                      <button key={e.employeeId} type="button" onClick={() => onPick(e)}
                        className={cn('cursor-pointer rounded-full border bg-card px-2.5 py-1 text-[12px] hover:border-primary/60', e.override && 'border-[hsl(var(--warning))]')}
                        title={e.override ? tx('Kalibre edildi') : undefined}>
                        {e.name}{e.score !== null && <span className="tabular ml-1 text-muted-foreground">{e.score}</span>}
                      </button>
                    ))}
                  </div>
                </div>
              )
            })}
          </div>
        ))}
        <div className="grid grid-cols-3 gap-2 text-center text-[12px] text-muted-foreground">
          {[1, 2, 3].map((p) => <span key={p}>{perfLabels[p]}</span>)}
        </div>
        <p className="text-center text-[12px] font-medium text-muted-foreground">{tx('Performans →')}</p>
      </div>
    </div>
  )
}

export function NineBoxPage() {
  const cycles = useCycles()
  const list = useMemo(() => (cycles.data ?? []).filter((c) => c.status !== 'Planned'), [cycles.data])
  const [cycleId, setCycleId] = useState('')
  useEffect(() => {
    if (!cycleId && list.length) setCycleId((list.find((c) => c.status === 'Open') ?? list[0]!).id)
  }, [list, cycleId])
  const q = useQuery({ queryKey: ['perf', 'nine-box', cycleId], queryFn: ({ signal }) => performanceExtrasApi.nineBox(cycleId, signal), enabled: !!cycleId })
  const [pick, setPick] = useState<NineBoxEmployee | null>(null)
  const current = pick && q.data ? [...q.data.cells.flatMap((c) => c.employees), ...q.data.unplaced].find((e) => e.employeeId === pick.employeeId) ?? pick : pick

  return (
    <div className="space-y-5">
      <PageHeader title={tx('9-kutu')} description={tx('Performans ve potansiyel ekseninde ekip görünümü; kalibrasyon toplantıları için.')} />
      <InfoNote>{q.data?.notice ?? tx('9-kutu tablosu yalnızca bir tartışma aracıdır; kararlar otomatik olarak bu tablodan üretilmez.')}</InfoNote>
      <div className="w-80">
        <SelectField label={tx('Dönem')} value={cycleId} onChange={setCycleId} options={list.map((c) => ({ value: c.id, label: c.name }))} hint={cycles.isPending ? tx('Yükleniyor') : undefined} />
      </div>
      {!cycleId ? (
        cycles.isPending ? <RowsSkeleton rows={3} /> : <EmptyState icon={Grid3x3} title={tx('Açık ya da kapanmış dönem yok')} detail={tx('9-kutu için en az bir dönemin açılmış olması gerekir.')} />
      ) : q.isPending ? <RowsSkeleton rows={6} columns={3} /> : q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : (
        <>
          <Panel>
            <PanelHead title={q.data.cycle.name} note={tx('Performans bantları: < {0} düşük, ≥ {1} yüksek (puanlama ayarındaki eşikler). Potansiyel yalnızca İK ve yöneticiye görünür.', [q.data.thresholds.low, q.data.thresholds.high])} />
            <PanelBody><Grid data={q.data} onPick={setPick} /></PanelBody>
          </Panel>
          {q.data.unplaced.length > 0 && (
            <Panel>
              <PanelHead title={tx('Yerleştirilemeyenler')} note={tx('Puanı ya da potansiyeli eksik çalışanlar. Potansiyel girmek için tıklayın.')} />
              <PanelBody className="flex flex-wrap gap-2">
                {q.data.unplaced.map((e) => (
                  <button key={e.employeeId} type="button" onClick={() => setPick(e)} className="cursor-pointer rounded-full border border-dashed border-border px-3 py-1 text-[12.5px] hover:border-primary/60">
                    {e.name}
                    <span className="ml-1 text-muted-foreground">{e.score === null ? tx('puan yok') : ''}{e.score === null && e.potential === null ? ' · ' : ''}{e.potential === null ? tx('potansiyel yok') : ''}</span>
                  </button>
                ))}
              </PanelBody>
            </Panel>
          )}
        </>
      )}
      {current && q.data && <EmployeeDialog key={current.employeeId} cycleId={cycleId} e={current} canCalibrate={q.data.canCalibrate} onClose={() => setPick(null)} />}
    </div>
  )
}
