/**
 * Madde 44: vardiya planı önerisi (OR-Tools CP-SAT; çözücü yoksa/süre aşılırsa açgözlü sezgisel).
 * Planlayıcı ekip, dönem ve gün x vardiya talebini girer; öneri mevcut atamalarla fark olarak gösterilir.
 * Otomatik uygulama YOKTUR: planlayıcı satırları seçip "Seçilenleri uygula" der. Kişisel veri model
 * servisine takma adla gider (ad, kimlik, izin türü gitmez).
 */
import { useMemo, useState } from 'react'
import { Plus, Sparkles, Trash2 } from 'lucide-react'
import { useShiftTeams } from '@/api/queries-shift-engine'
import { useShifts } from '@/api/queries'
import { applyBody, shiftOptimizerApi, type DemandRow, type Proposal, type ProposalDiffRow } from '@/api/shiftOptimizer'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote } from '@/components/ui/States'
import { isoDayLabels } from '@/api/opsPlus'
import { formatDate, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { isoDate, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'

/** Çözücü durumu (OR-Tools "OPTIMAL"/"FEASIBLE"…) Türkçe etiket; bilinmeyen durum olduğu gibi. */
function solverStatusLabel(status: string): string {
  switch (status) {
    case 'OPTIMAL': return tx('En iyi çözüm')
    case 'FEASIBLE': return tx('Uygun çözüm')
    case 'INFEASIBLE': return tx('Çözüm yok')
    case 'UNKNOWN': return tx('Süre doldu')
    default: return status.startsWith('GREEDY') ? tx('Sezgisel yerleştirme') : status
  }
}

const plusDays = (n: number) => isoDate(new Date(Date.now() + n * 864e5))
const keyOf = (r: ProposalDiffRow) => `${r.employeeId}|${r.date}`
const KIND: Record<ProposalDiffRow['kind'], { label: string; tone: 'success' | 'info' | 'danger' | 'neutral' }> = {
  add: { label: tx('Yeni'), tone: 'success' },
  change: { label: tx('Değişiklik'), tone: 'info' },
  remove: { label: tx('Kaldır'), tone: 'danger' },
  same: { label: tx('Aynı'), tone: 'neutral' },
}

export function OptimizerView() {
  const teams = useShiftTeams()
  const shifts = useShifts()
  const [teamId, setTeamId] = useState('')
  const [from, setFrom] = useState(plusDays(1))
  const [to, setTo] = useState(plusDays(14))
  const [rows, setRows] = useState<DemandRow[]>([])
  const [proposal, setProposal] = useState<Proposal | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [showSame, setShowSame] = useState(false)
  const [warnings, setWarnings] = useState<Array<{ employeeId: string; date: string; message: string }>>([])

  const shiftName = (id: string) => shifts.data?.find((s) => s.id === id)?.name ?? '—'
  const validRows = rows.filter((r) => r.shiftId && r.required >= 0)
  const propose = useAction(() => shiftOptimizerApi.propose({ from, to, teamId, demand: validRows }), {
    onDone: (p) => {
      setProposal(p)
      setSelected(new Set(p.diff.filter((r) => r.kind !== 'same').map(keyOf)))
    },
  })
  const apply = useAction(() => shiftOptimizerApi.apply(applyBody(proposal!.diff, selected, keyOf)), {
    success: (r) => tx('{0} yeni, {1} değişen, {2} kaldırılan atama uygulandı', [r.created, r.updated, r.removed]),
    invalidate: [['timeshift'], ['shift-swap']],
    onDone: (r) => { setWarnings(r.warnings); setProposal(null) },
  })

  const visible = useMemo(() => (proposal?.diff ?? []).filter((r) => showSame || r.kind !== 'same'), [proposal, showSame])
  const selectedCount = (proposal?.diff ?? []).filter((r) => r.kind !== 'same' && selected.has(keyOf(r))).length
  const toggle = (k: string, on: boolean) => setSelected((prev) => { const n = new Set(prev); if (on) n.add(k); else n.delete(k); return n })
  const update = (i: number, patch: Partial<DemandRow>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)))

  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><Sparkles className="size-4 text-primary" />{' '}{tx('Vardiya planı önerisi')}</span>}
          note={tx('Kurallar: 11 saat dinlenme, haftalık 45 / günlük 11 saat, gece 7,5 saat, en çok 6 gün üst üste (şirket ayarı). Tercihler ve gece/hafta sonu adaleti gözetilir.')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-3 md:grid-cols-3">
            <SelectField label={tx('Ekip')} value={teamId} onChange={setTeamId}
              options={[{ value: '', label: tx('Ekip seçin') }, ...(teams.data ?? []).map((t) => ({ value: t.id, label: t.name }))]} />
            <TextField label={tx('Başlangıç')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
            <TextField label={tx('Bitiş (en çok 31 gün)')} type="date" value={to} onChange={(e) => setTo(e.target.value)} />
          </div>
          <div className="space-y-2">
            <p className="text-[13px] font-medium">{tx('Talep (her gün için gereken kişi)')}</p>
            {rows.length === 0 && <p className="text-[12.5px] text-muted-foreground">{tx('Vardiya tanımı başına kaç kişi gerektiğini ekleyin.')}</p>}
            {rows.map((r, i) => (
              <div key={i} className="grid gap-2 rounded-xl border border-border p-3 md:grid-cols-[1.2fr_90px_1.6fr_1fr_auto] md:items-end">
                <SelectField label={tx('Vardiya')} value={r.shiftId} onChange={(v) => update(i, { shiftId: v })}
                  options={[{ value: '', label: tx('Seçin') }, ...(shifts.data ?? []).map((s) => ({ value: s.id, label: `${s.name} ${s.startTime.slice(0, 5)}–${s.endTime.slice(0, 5)}` }))]} />
                <TextField label={tx('Kişi')} type="number" min={0} max={500} value={String(r.required)} onChange={(e) => update(i, { required: Math.max(0, Math.min(500, Number(e.target.value) || 0)) })} />
                <div>
                  <span className="mb-1.5 block text-[13px]">{tx('Günler (boş: her gün)')}</span>
                  <div className="flex flex-wrap gap-1">
                    {[1, 2, 3, 4, 5, 6, 7].map((d) => {
                      const on = r.weekdays?.includes(d) ?? false
                      return (
                        <button key={d} type="button" aria-pressed={on} onClick={() => update(i, { weekdays: on ? (r.weekdays ?? []).filter((x) => x !== d) : [...(r.weekdays ?? []), d] })}
                          className={cn('h-8 w-10 cursor-pointer rounded-md border text-[12px]', on ? 'border-primary bg-primary/15' : 'border-border')}>{isoDayLabels[d]}</button>
                      )
                    })}
                  </div>
                </div>
                <TextField label={tx('Beceri (ekip etiketi, isteğe bağlı)')} value={r.skill ?? ''} maxLength={100} onChange={(e) => update(i, { skill: e.target.value || null })} />
                <Button size="icon" variant="ghost" aria-label={tx('Satırı sil')} onClick={() => setRows(rows.filter((_, j) => j !== i))}><Trash2 className="size-4" /></Button>
              </div>
            ))}
            <Button variant="outline" size="sm" onClick={() => setRows([...rows, { shiftId: shifts.data?.[0]?.id ?? '', required: 1, weekdays: [], skill: null }])}>
              <Plus className="size-4" /> {tx('Talep satırı ekle')}
            </Button>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <Button disabled={!teamId || validRows.length === 0 || !from || !to || propose.isPending} onClick={() => propose.mutate(undefined)}>
              <Sparkles className="size-4" /> {propose.isPending ? tx('Hesaplanıyor (en çok 10 sn)…') : tx('Öneri oluştur')}
            </Button>
            <span className="text-[12px] text-muted-foreground">{tx('Öneri kaydedilmez; uygulamak için aşağıdan seçip onaylayın.')}</span>
          </div>
          <InfoNote>{tx('Model servisine ad ve çalışan kimliği gitmez: kişiler takma adla, yalnızca "o gün müsait değil" (izin/tatil, nedeni olmadan), vardiya tercihleri ve ekip etiketi gönderilir.')}</InfoNote>
        </PanelBody>
      </Panel>

      {warnings.length > 0 && (
        <Panel>
          <PanelHead title={tx('Uygulama sonrası kural uyarıları')} />
          <PanelBody>
            <ul className="list-disc space-y-0.5 pl-5 text-[12.5px] text-[hsl(var(--warning))]">{warnings.map((w, i) => <li key={i}>{formatDate(w.date)} · {txServer(w.message)}</li>)}</ul>
          </PanelBody>
        </Panel>
      )}

      {proposal && (
        <Panel>
          <PanelHead title={tx('Öneri ({0})', [proposal.solver === 'cp-sat' ? 'CP-SAT' : tx('sezgisel')])}
            note={tx('{0} · {1} sn · gece farkı {2}, hafta sonu farkı {3}, yük farkı {4}, tercih uyuşmazlığı {5}', [solverStatusLabel(proposal.status), formatNumber(proposal.seconds),
              formatNumber(proposal.fairness.nightSpread), formatNumber(proposal.fairness.weekendSpread), formatNumber(proposal.fairness.loadSpread), formatNumber(proposal.fairness.preferenceConflicts)])} />
          <PanelBody className="space-y-3">
            {proposal.unusableShifts.length > 0 && (
              <p className="text-[12.5px] text-destructive">
                {tx('Kurallara aykırı olduğu için kullanılmayan vardiyalar: {0}', [proposal.unusableShifts.map((u) => `${shiftName(u.shiftId)} (${u.reason === 'night' ? tx('gece 7,5 saat') : tx('günlük süre')})`).join(', ')])}
              </p>
            )}
            {proposal.uncovered.length > 0 && (
              <div role="alert" className="rounded-lg border border-[hsl(var(--warning))]/40 p-2 text-[12.5px]">
                <p className="font-medium">{tx('Karşılanamayan talep')}</p>
                <p className="text-muted-foreground">{proposal.uncovered.slice(0, 30).map((u) => `${formatDate(u.date)} ${shiftName(u.shiftId)}${u.skill ? ` [${u.skill}]` : ''}: ${u.missing}`).join(' · ')}</p>
              </div>
            )}
            <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={showSame} onCheckedChange={(v) => setShowSame(v === true)} /> {tx('Değişmeyenleri de göster')}</label>
            {visible.length === 0 ? <EmptyState title={tx('Değişiklik yok')} detail={tx('Mevcut atamalar öneriyle aynı.')} /> : (
              <div className="max-h-[28rem] overflow-y-auto rounded-xl border border-border">
                <table className="w-full text-[12.5px]">
                  <thead className="sticky top-0 bg-muted/60 text-left text-muted-foreground">
                    <tr><th className="w-8 px-3 py-2"><span className="sr-only">{tx('Seç')}</span></th><th className="px-3 py-2">{tx('Tarih')}</th><th className="px-3 py-2">{tx('Çalışan')}</th><th className="px-3 py-2">{tx('Şu an')}</th><th className="px-3 py-2">{tx('Öneri')}</th><th className="px-3 py-2">{tx('Tür')}</th></tr>
                  </thead>
                  <tbody className="divide-y divide-border">
                    {visible.map((r) => (
                      <tr key={keyOf(r)}>
                        <td className="px-3 py-1.5">{r.kind !== 'same' && <Checkbox aria-label={tx('Seç')} checked={selected.has(keyOf(r))} onCheckedChange={(v) => toggle(keyOf(r), v === true)} />}</td>
                        <td className="px-3 py-1.5">{formatDate(r.date)}</td>
                        <td className="px-3 py-1.5">{r.name ?? '—'}</td>
                        <td className="px-3 py-1.5">{r.currentShift ?? '—'}</td>
                        <td className="px-3 py-1.5 font-medium">{r.proposedShift ?? '—'}</td>
                        <td className="px-3 py-1.5"><StatusBadge tone={KIND[r.kind].tone}>{KIND[r.kind].label}</StatusBadge></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <div className="flex gap-2">
              <Button disabled={selectedCount === 0 || apply.isPending} onClick={() => apply.mutate(undefined)}>{tx('Seçilenleri uygula ({0})', [selectedCount])}</Button>
              <Button variant="outline" onClick={() => setProposal(null)}>{tx('Öneriyi kapat')}</Button>
            </div>
          </PanelBody>
        </Panel>
      )}
    </div>
  )
}
