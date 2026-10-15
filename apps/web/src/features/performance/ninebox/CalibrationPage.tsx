/**
 * Dalga 11 / 79: kalibrasyon oturumu — `/panel/performans/kalibrasyon`.
 *
 * İK bir dönem için oturum açar; o anki 9-kutu yerleşimi başlangıç olarak kopyalanır. Kişiler
 * sürükle-bırak (ya da kişi penceresindeki seçimlerle — klavye erişimi) taşınır; her taşıma karar notu
 * ister ve değişiklik günlüğüne yazılır. Her satır İK onayı ister; tümü onaylanınca oturum sonuçlanır ve
 * değişen hücreler 9-kutu düzeltmesi olarak kaydedilir. KVKK: otomatik karar yok; yalnızca İK görür.
 */
import { useEffect, useMemo, useState, type DragEvent } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, GripVertical, History, Scale, ShieldCheck } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useCycles } from '@/api/performance'
import { potentialLabels } from '@/api/performanceExtras'
import { bandsOf, calibrationApi, cellOf, type CalibrationDetail, type CalibrationItem, type CalibrationStatus } from '@/api/performanceGrowth'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const perfLabels: Record<number, string> = { 1: tx('Beklentinin altında'), 2: tx('Beklentiyi karşılıyor'), 3: tx('Beklentinin üstünde') }
const statusText: Record<CalibrationStatus, [string, StatusTone]> = {
  Open: [tx('Açık'), 'info'],
  Finalized: [tx('Sonuçlandı'), 'success'],
  Cancelled: [tx('İptal edildi'), 'neutral'],
}
const actionText: Record<string, string> = {
  Moved: tx('Taşındı'),
  Confirmed: tx('Onaylandı'),
  Unconfirmed: tx('Onay geri alındı'),
  Finalized: tx('Oturum sonuçlandı'),
}

interface PendingMove { item: CalibrationItem; toCell: number }

function MoveDialog({ sessionId, move, cellLabel, onClose }: { sessionId: string; move: PendingMove; cellLabel: (c: number) => string; onClose: () => void }) {
  const [note, setNote] = useState('')
  const [target, setTarget] = useState(String(move.toCell))
  const b = bandsOf(Number(target))
  const save = useAction(() => calibrationApi.move(sessionId, { employeeId: move.item.employeeId, ...b, note: note.trim() }), {
    success: (r) => tx('Taşındı: {0}', [r.label]), invalidate: [['perf', 'calibration']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={move.item.name} note={tx('{0} → {1}', [cellLabel(move.item.cell), cellLabel(Number(target))])}
      footer={<Button onClick={() => save.mutate(undefined)} disabled={save.isPending || note.trim().length < 10}>{tx('Taşı')}</Button>}>
      <div className="space-y-3">
        <SelectField label={tx('Hedef hücre')} value={target} onChange={setTarget}
          options={Array.from({ length: 9 }, (_, i) => i + 1).map((c) => ({ value: String(c), label: `${c} · ${cellLabel(c)}` }))} />
        <TextAreaField label={tx('Karar notu (zorunlu, değişiklik günlüğüne yazılır)')} rows={3} maxLength={1000} value={note}
          onChange={(e) => setNote(e.target.value)} hint={tx('Toplantıdaki gerekçeyi gözleme dayalı yazın; sağlık, aile gibi özel bilgi yazmayın.')} />
      </div>
    </Modal>
  )
}

function ItemDialog({ data, item, onMove, onClose }: { data: CalibrationDetail; item: CalibrationItem; onMove: (toCell: number) => void; onClose: () => void }) {
  const open = data.session.status === 'Open'
  const confirm = useAction((confirmed: boolean) => calibrationApi.confirm(data.session.id, { employeeIds: [item.employeeId], confirmed }), {
    success: tx('Onay durumu güncellendi'), invalidate: [['perf', 'calibration']], onDone: onClose,
  })
  const [perf, setPerf] = useState(String(item.performanceBand))
  const [pot, setPot] = useState(String(item.potentialBand))
  const label = (c: number) => data.cells.find((x) => x.cell === c)?.label ?? String(c)
  const history = data.changes.filter((c) => c.employeeId === item.employeeId)
  return (
    <Modal open onClose={onClose} size="lg" title={item.name} note={[item.positionTitle, item.department].filter(Boolean).join(' · ')}>
      <div className="space-y-4">
        <dl className="grid grid-cols-2 gap-3 text-[13px]">
          <div><dt className="text-muted-foreground">{tx('Nihai puan')}</dt><dd className="tabular font-semibold">{item.score ?? '—'}</dd></div>
          <div><dt className="text-muted-foreground">{tx('Başlangıç / güncel hücre')}</dt><dd>{label(item.originalCell)} / {label(item.cell)}</dd></div>
          <div><dt className="text-muted-foreground">{tx('Onay')}</dt><dd>{item.confirmed ? tx('{0} — {1}', [item.confirmedBy ?? '', formatDateTime(item.confirmedAt)]) : tx('Onay bekliyor')}</dd></div>
          {item.decisionNote && <div className="col-span-2"><dt className="text-muted-foreground">{tx('Karar notu')}</dt><dd>{item.decisionNote}</dd></div>}
        </dl>
        {open && !item.isSelf && (
          <section className="space-y-2 rounded-xl border border-border p-3">
            <h3 className="text-[13.5px] font-semibold">{tx('Hücreyi değiştir')}</h3>
            <div className="grid grid-cols-2 gap-3">
              <SelectField label={tx('Performans bandı')} value={perf} onChange={setPerf} options={[1, 2, 3].map((b) => ({ value: String(b), label: `${b} · ${perfLabels[b]}` }))} />
              <SelectField label={tx('Potansiyel bandı')} value={pot} onChange={setPot} options={[1, 2, 3].map((b) => ({ value: String(b), label: `${b} · ${potentialLabels[b]}` }))} />
            </div>
            <Button size="sm" variant="outline" disabled={cellOf(Number(perf), Number(pot)) === item.cell} onClick={() => onMove(cellOf(Number(perf), Number(pot)))}>{tx('Karar notuyla taşı')}</Button>
          </section>
        )}
        {open && !item.isSelf && (
          <div className="flex gap-2">
            {item.confirmed
              ? <Button size="sm" variant="outline" onClick={() => confirm.mutate(false)} disabled={confirm.isPending}>{tx('Onayı geri al')}</Button>
              : <Button size="sm" onClick={() => confirm.mutate(true)} disabled={confirm.isPending}><CheckCircle2 className="size-4" />{tx('Nihai hücreyi onayla')}</Button>}
          </div>
        )}
        {item.isSelf && <InfoNote>{tx('Kendi satırınızı taşıyamaz ya da onaylayamazsınız; başka bir İK yetkilisi onaylamalıdır.')}</InfoNote>}
        {history.length > 0 && (
          <section>
            <h3 className="mb-1 text-[13px] font-semibold">{tx('Geçmiş')}</h3>
            <ul className="space-y-1 text-[12.5px]">
              {history.map((h) => (
                <li key={h.id}>{formatDateTime(h.changedAt)} · {h.changedBy} · {actionText[h.action] ?? h.action}{h.action === 'Moved' && h.fromCell && h.toCell ? ` (${h.fromCell} → ${h.toCell})` : ''}{h.note ? ` — ${h.note}` : ''}</li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </Modal>
  )
}

function SessionBoard({ id }: { id: string }) {
  const q = useQuery({ queryKey: ['perf', 'calibration', 'session', id], queryFn: ({ signal }) => calibrationApi.get(id, signal) })
  const [pick, setPick] = useState<string | null>(null)
  const [move, setMove] = useState<PendingMove | null>(null)
  const [dragOver, setDragOver] = useState<number | null>(null)
  const confirmDlg = useConfirm()
  const inv = [['perf', 'calibration'], ['perf', 'nine-box']]
  const confirmAll = useAction((ids: string[]) => calibrationApi.confirm(id, { employeeIds: ids, confirmed: true }), {
    success: (r) => tx('{0} satır onaylandı', [r.changed]), invalidate: inv,
  })
  const finalize = useAction(() => calibrationApi.finalize(id), {
    success: (r) => tx('Oturum sonuçlandı: {0} değişiklik 9-kutuya yazıldı', [r.changed]), invalidate: inv,
  })
  const cancel = useAction(() => calibrationApi.cancel(id), { success: tx('Oturum iptal edildi'), invalidate: inv })

  if (q.isPending) return <RowsSkeleton rows={6} columns={3} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const data = q.data
  const open = data.session.status === 'Open'
  const label = (c: number) => data.cells.find((x) => x.cell === c)?.label ?? String(c)
  const byCell = new Map<number, CalibrationItem[]>()
  for (const it of data.items) byCell.set(it.cell, [...(byCell.get(it.cell) ?? []), it])
  const confirmed = data.items.filter((i) => i.confirmed).length
  const pendingIds = data.items.filter((i) => !i.confirmed && !i.isSelf).map((i) => i.employeeId)
  const picked = data.items.find((i) => i.employeeId === pick) ?? null

  const onDrop = (cell: number) => (e: DragEvent) => {
    e.preventDefault()
    setDragOver(null)
    const emp = e.dataTransfer.getData('text/plain')
    const item = data.items.find((i) => i.employeeId === emp)
    if (item && item.cell !== cell && open && !item.isSelf) setMove({ item, toCell: cell })
  }

  return (
    <div className="space-y-4">
      <Panel>
        <PanelHead
          title={data.session.name}
          note={tx('{0} · {1} / {2} satır onaylı · açan: {3}', [data.session.cycleName ?? '', confirmed, data.items.length, data.session.createdBy])}
          action={<StatusBadge tone={statusText[data.session.status][1]}>{statusText[data.session.status][0]}</StatusBadge>}
        />
        <PanelBody className="space-y-3">
          {open ? (
            <>
              <p className="text-[12.5px] text-muted-foreground">{tx('Kişiyi sürükleyip başka bir hücreye bırakın (ya da kişiye tıklayıp hücre seçin). Her taşıma karar notu ister ve onayı sıfırlar.')}</p>
              <div className="flex flex-wrap gap-2">
                <Button size="sm" variant="outline" disabled={pendingIds.length === 0 || confirmAll.isPending}
                  onClick={async () => {
                    if (await confirmDlg({ title: tx('Onaysız satırlar onaylansın mı?'), note: tx('{0} çalışanın güncel hücresi nihai olarak onaylanır. Her satırı toplantıda gözden geçirdiğinizden emin olun.', [pendingIds.length]), action: tx('Onayla') }))
                      confirmAll.mutate(pendingIds)
                  }}>
                  <ShieldCheck className="size-4" />{tx('Onaysızları onayla ({0})', [pendingIds.length])}
                </Button>
                <Button size="sm" disabled={confirmed !== data.items.length || data.items.length === 0 || finalize.isPending}
                  onClick={async () => {
                    if (await confirmDlg({ title: tx('Oturum sonuçlandırılsın mı?'), note: tx('Değişen hücreler gerekçeleriyle 9-kutu düzeltmesi olarak kaydedilir; oturum kilitlenir.'), action: tx('Sonuçlandır') }))
                      finalize.mutate(undefined)
                  }}>
                  {tx('Sonuçlandır')}
                </Button>
                <Button size="sm" variant="ghost" disabled={cancel.isPending}
                  onClick={async () => { if (await confirmDlg({ title: tx('Oturum iptal edilsin mi?'), note: tx('Taşımalar 9-kutuya yazılmaz; günlük saklanır.'), action: tx('İptal et') })) cancel.mutate(undefined) }}>
                  {tx('Oturumu iptal et')}
                </Button>
              </div>
            </>
          ) : (
            <InfoNote>{data.session.finalizedAt ? tx('Sonuçlandıran: {0} — {1}', [data.session.finalizedBy ?? '', formatDateTime(data.session.finalizedAt)]) : tx('Oturum kapalı; değişiklik yapılamaz.')}</InfoNote>
          )}
          <div className="flex gap-3">
            <div className="flex w-6 items-center justify-center">
              <span className="-rotate-90 whitespace-nowrap text-[12px] font-medium text-muted-foreground">{tx('Potansiyel →')}</span>
            </div>
            <div className="flex-1 space-y-2">
              {[3, 2, 1].map((pot) => (
                <div key={pot} className="grid grid-cols-3 gap-2">
                  {[1, 2, 3].map((perf) => {
                    const cell = cellOf(perf, pot)
                    const people = byCell.get(cell) ?? []
                    return (
                      <div key={perf}
                        onDragOver={(e) => { if (open) { e.preventDefault(); setDragOver(cell) } }}
                        onDragLeave={() => setDragOver((c) => (c === cell ? null : c))}
                        onDrop={onDrop(cell)}
                        className={cn('min-h-32 rounded-xl border bg-card/40 p-3 transition-colors', dragOver === cell && 'border-primary bg-primary/10')}>
                        <p className="flex items-baseline justify-between gap-2 text-[12px] font-semibold">
                          <span>{cell} · {label(cell)}</span><span className="tabular text-muted-foreground">{people.length}</span>
                        </p>
                        <div className="mt-2 flex flex-wrap gap-1.5">
                          {people.map((i) => (
                            <button key={i.employeeId} type="button" draggable={open && !i.isSelf}
                              onDragStart={(e) => { e.dataTransfer.setData('text/plain', i.employeeId); e.dataTransfer.effectAllowed = 'move' }}
                              onClick={() => setPick(i.employeeId)}
                              className={cn('flex cursor-pointer items-center gap-1 rounded-full border bg-card px-2.5 py-1 text-[12px] hover:border-primary/60',
                                i.cell !== i.originalCell && 'border-[hsl(var(--warning))]', i.confirmed && 'ring-1 ring-[hsl(var(--success))]/50')}
                              title={i.cell !== i.originalCell ? tx('Başlangıç: {0}', [label(i.originalCell)]) : undefined}>
                              {open && !i.isSelf && <GripVertical className="size-3 text-muted-foreground" aria-hidden />}
                              {i.name}
                              {i.confirmed && <CheckCircle2 className="size-3 text-[hsl(var(--success))]" aria-label={tx('Onaylı')} />}
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
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><History className="size-4" />{tx('Değişiklik günlüğü')}</span>} note={tx('Taşıma, onay ve sonuçlandırma kayıtları (silinemez).')} />
        <PanelBody>
          {data.changes.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Henüz değişiklik yok.')}</p> : (
            <ul className="space-y-1.5 text-[12.5px]">
              {data.changes.map((c) => (
                <li key={c.id} className="flex flex-wrap gap-x-2">
                  <span className="tabular text-muted-foreground">{formatDateTime(c.changedAt)}</span>
                  <span className="font-medium">{c.changedBy}</span>
                  <span>{actionText[c.action] ?? c.action}</span>
                  {c.name && <span>· {c.name}</span>}
                  {c.action === 'Moved' && <span className="tabular">({c.fromCell} → {c.toCell})</span>}
                  {c.note && <span className="text-muted-foreground">— {c.note}</span>}
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      {picked && !move && <ItemDialog data={data} item={picked} onClose={() => setPick(null)} onMove={(toCell) => { setPick(null); setMove({ item: picked, toCell }) }} />}
      {move && <MoveDialog sessionId={id} move={move} cellLabel={label} onClose={() => setMove(null)} />}
    </div>
  )
}

function HrCalibration() {
  const cycles = useCycles()
  const list = useMemo(() => (cycles.data ?? []).filter((c) => c.status !== 'Planned'), [cycles.data])
  const [cycleId, setCycleId] = useState('')
  useEffect(() => {
    if (!cycleId && list.length) setCycleId((list.find((c) => c.status === 'Open') ?? list[0]!).id)
  }, [list, cycleId])
  const sessions = useQuery({ queryKey: ['perf', 'calibration', 'list', cycleId], queryFn: ({ signal }) => calibrationApi.list(cycleId, signal), enabled: !!cycleId })
  const [sessionId, setSessionId] = useState('')
  useEffect(() => {
    const rows = sessions.data ?? []
    if (rows.length && !rows.some((s) => s.id === sessionId)) setSessionId((rows.find((s) => s.status === 'Open') ?? rows[0]!).id)
    if (!rows.length && sessionId) setSessionId('')
  }, [sessions.data, sessionId])
  const [name, setName] = useState('')
  const create = useAction(() => calibrationApi.create({ cycleId, name: name.trim() }), {
    success: (r) => tx('Oturum açıldı: {0} çalışan yerleştirildi, {1} çalışan puan/potansiyel eksik olduğu için dışarıda', [r.placed, r.unplaced]),
    invalidate: [['perf', 'calibration']],
    onDone: (r) => { setSessionId(r.id); setName('') },
  })
  const hasOpen = (sessions.data ?? []).some((s) => s.status === 'Open')

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-72">
          <SelectField label={tx('Dönem')} value={cycleId} onChange={(v) => { setCycleId(v); setSessionId('') }} options={list.map((c) => ({ value: c.id, label: c.name }))} hint={cycles.isPending ? tx('Yükleniyor') : undefined} />
        </div>
        {(sessions.data?.length ?? 0) > 0 && (
          <div className="w-80">
            <SelectField label={tx('Oturum')} value={sessionId} onChange={setSessionId}
              options={(sessions.data ?? []).map((s) => ({ value: s.id, label: `${s.name} · ${statusText[s.status][0]} · ${s.confirmed}/${s.total}` }))} />
          </div>
        )}
        {cycleId && !hasOpen && (
          <div className="flex items-end gap-2">
            <div className="w-64"><TextField label={tx('Yeni oturum adı')} value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder={tx('Ör. Mühendislik kalibrasyonu')} /></div>
            <Button onClick={() => create.mutate(undefined)} disabled={create.isPending || name.trim().length < 3}>{tx('Oturum aç')}</Button>
          </div>
        )}
      </div>
      {!cycleId ? (
        cycles.isPending ? <RowsSkeleton rows={3} /> : <EmptyState icon={Scale} title={tx('Açık ya da kapanmış dönem yok')} detail={tx('Kalibrasyon için en az bir dönemin açılmış olması gerekir.')} />
      ) : sessions.isPending ? <RowsSkeleton rows={4} /> : sessions.isError ? <ErrorState message={errMsg(sessions.error)} onRetry={() => void sessions.refetch()} />
        : sessionId ? <SessionBoard key={sessionId} id={sessionId} />
        : <EmptyState icon={Scale} title={tx('Bu dönem için kalibrasyon oturumu yok')} detail={tx('Oturum açıldığında 9-kutudaki güncel yerleşim başlangıç olarak kopyalanır.')} />}
    </div>
  )
}

export function CalibrationPage() {
  const { roles } = useAuth()
  const hr = isHr(roles, 'ext-performance-manage')
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Kalibrasyon oturumu')} description={tx('Dönem sonu 9-kutu yerleşimini toplantıda gözden geçirin; her nihai hücre İK onayı ister.')} />
      <InfoNote>{tx('9-kutu tablosu yalnızca bir tartışma aracıdır; terfi, ücret ya da işten çıkarma gibi kararlar otomatik olarak bu tablodan üretilmez. Kalibrasyon toplantısında insan değerlendirmesiyle kullanın.')}</InfoNote>
      {hr ? <HrCalibration /> : (
        <EmptyState icon={Scale} title={tx('Kalibrasyon oturumunu İK yürütür')} detail={tx('Ekibinizin yerleşimini 9-kutu ekranında görebilir, potansiyel değerlendirmesi girebilirsiniz.')}
          action={<Button asChild variant="outline"><Link to="/panel/performans/dokuz-kutu">{tx('9-kutuya git')}</Link></Button>} />
      )}
    </div>
  )
}
