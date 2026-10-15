import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { AnimatePresence, LayoutGroup, motion } from 'motion/react'
import { Area, AreaChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { Crown, History, Pause, Play, RotateCcw } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { ErrorState, RowsSkeleton } from '@/components/ui/States'
import { governanceApi } from '@/api/governance'
import { Initials, Metric, TenantGate, isoDate } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'
import { auditEntityLabels } from '@/features/governance/auditLabels'
import { formatDate } from '@/lib/format'

const LONG = new Intl.DateTimeFormat(appLocale, { day: 'numeric', month: 'long', year: 'numeric' })
const SHORT = new Intl.DateTimeFormat(appLocale, { month: 'short', year: '2-digit' })
const ACTION: Record<string, string> = { Created: tx('oluşturuldu'), Updated: tx('güncellendi'), Deleted: tx('silindi') }

export function TimeMachinePage() {
  const tl = useQuery({ queryKey: ['time-machine', 'timeline'], queryFn: ({ signal }) => governanceApi.timeline(36, signal) })
  const months = tl.data?.series ?? []
  const [idx, setIdx] = useState<number | null>(null)
  const [playing, setPlaying] = useState(false)
  const cur = idx ?? months.length - 1
  const date = months[cur] ? (cur === months.length - 1 ? isoDate() : (() => { const d = new Date(months[cur].month); d.setMonth(d.getMonth() + 1); d.setDate(0); return isoDate(d) })()) : isoDate()
  const snap = useQuery({ queryKey: ['time-machine', date], queryFn: ({ signal }) => governanceApi.snapshot(date, signal), placeholderData: keepPreviousData })

  useEffect(() => {
    if (!playing) return
    const t = setInterval(() => setIdx((i) => {
      const next = (i ?? 0) + 1
      if (next >= months.length) { setPlaying(false); return months.length - 1 }
      return next
    }), 900)
    return () => clearInterval(t)
  }, [playing, months.length])

  const chart = useMemo(() => months.map((p) => ({ ...p, label: SHORT.format(new Date(p.month)) })), [months])
  const s = snap.data
  // Teknik varlık adları (ChatIdentity, AiUsage…) Türkçe etiketle gösterilir; etiketi olmayanlar "Diğer kayıtlar"da toplanır.
  const changes = useMemo(() => {
    const m = new Map<string, number>()
    for (const c of s?.changesSince ?? []) {
      const entity = auditEntityLabels[c.entityType] ?? tx('Diğer kayıtlar')
      const key = `${entity} ${ACTION[c.action] ?? c.action}`
      m.set(key, (m.get(key) ?? 0) + c.count)
    }
    return [...m.entries()].map(([label, count]) => ({ label, count })).sort((a, b) => b.count - a.count)
  }, [s])
  return (
    <TenantGate>
      <PageHeader title={tx('Zaman makinesi')} description={tx('Organizasyon geçmişteki bir tarihte nasıldı? Kaydırın ya da oynatın; kadro, departmanlar ve yöneticiler o güne göre yeniden kurulur.')} />
      {tl.isPending ? <RowsSkeleton /> : tl.isError ? <ErrorState message={(tl.error as Error).message} /> : (
        <div className="space-y-6">
          <Panel>
            <PanelBody className="space-y-4">
              <div className="flex flex-wrap items-center gap-3">
                <motion.div key={date} initial={{ opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }} className="flex items-center gap-2 text-[22px] font-semibold tracking-tight">
                  <History className="size-5 text-primary" /> {LONG.format(new Date(date))}
                </motion.div>
                <div className="ml-auto flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => { setIdx(0); setPlaying(true) }}><RotateCcw className="size-4" />{' '}{tx('Baştan oynat')}</Button>
                  <Button size="sm" onClick={() => setPlaying((p) => !p)}>{playing ? <><Pause className="size-4" />{' '}{tx('Durdur')}</> : <><Play className="size-4" />{' '}{tx('Oynat')}</>}</Button>
                </div>
              </div>
              <input type="range" min={0} max={Math.max(0, months.length - 1)} value={cur} onChange={(e) => { setPlaying(false); setIdx(Number(e.target.value)) }} className="w-full accent-[hsl(var(--primary))]" aria-label={tx('Tarih')} />
              <div className="h-40">
                <ResponsiveContainer>
                  <AreaChart data={chart}>
                    <defs><linearGradient id="tm" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="hsl(var(--primary))" stopOpacity={0.35} /><stop offset="100%" stopColor="hsl(var(--primary))" stopOpacity={0} /></linearGradient></defs>
                    <XAxis dataKey="label" fontSize={10} tickLine={false} axisLine={false} interval="preserveStartEnd" />
                    <YAxis allowDecimals={false} fontSize={10} width={24} tickLine={false} axisLine={false} />
                    <Tooltip contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12, fontSize: 12 }} />
                    <Area dataKey="headcount" name={tx('Çalışan')} stroke="hsl(var(--primary))" fill="url(#tm)" strokeWidth={2} isAnimationActive={false} />
                    {chart[cur] && <ReferenceLine x={chart[cur].label} stroke="hsl(var(--foreground))" strokeDasharray="4 3" />}
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </PanelBody>
          </Panel>
          {s && (
            <>
              <div className="grid gap-3 sm:grid-cols-3">
                <Metric label={tx('O tarihteki çalışan')} value={s.headcount} />
                <Metric label={tx('Bugün')} value={s.headcountToday} hint={tx('{0}{1} fark', [s.headcountToday - s.headcount >= 0 ? '+' : '', s.headcountToday - s.headcount])} />
                <Metric label={tx('Departman')} value={s.departments.length} />
              </div>
              <div className="grid gap-6 xl:grid-cols-[1fr_340px]">
                <LayoutGroup>
                  <div className="grid gap-4 md:grid-cols-2">
                    <AnimatePresence mode="popLayout">
                      {s.departments.map((d) => (
                        <motion.div key={d.departmentId ?? d.department} layout initial={{ opacity: 0, scale: 0.95 }} animate={{ opacity: 1, scale: 1 }} exit={{ opacity: 0, scale: 0.9 }} className="surface rounded-2xl border border-border p-4">
                          <div className="mb-3 flex items-center justify-between"><p className="text-[14px] font-semibold">{d.department}</p><span className="tabular rounded-full bg-primary/10 px-2 text-[12px] text-primary">{d.count}</span></div>
                          <div className="flex flex-wrap gap-2">
                            <AnimatePresence mode="popLayout">
                              {d.people.map((p) => (
                                <motion.div key={p.employeeId} layout layoutId={`tm-${p.employeeId}`} initial={{ opacity: 0, scale: 0.5 }} animate={{ opacity: 1, scale: 1 }} exit={{ opacity: 0, scale: 0.5 }} transition={{ type: 'spring', stiffness: 300, damping: 26 }}
                                  className="flex items-center gap-2 rounded-full border border-border bg-background/60 py-1 pr-3 pl-1" title={[p.position, tx('işe giriş {0}', [formatDate(p.hireDate)])].filter(Boolean).join(' · ')}>
                                  <Initials name={p.name} size={24} />
                                  <span className="text-[12px]">{p.name}</span>
                                  {p.isHead && <Crown className="size-3.5 text-amber-400" />}
                                </motion.div>
                              ))}
                            </AnimatePresence>
                          </div>
                        </motion.div>
                      ))}
                    </AnimatePresence>
                  </div>
                </LayoutGroup>
                <Panel>
                  <PanelHead title={tx('O günden bu yana değişenler')} note={tx('Denetim kaydından')} />
                  <PanelBody className="space-y-1.5">
                    {changes.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Kayıtlı değişiklik yok.')}</p> : changes.map((c) => (
                      <div key={c.label} className="flex justify-between text-[13px]"><span>{c.label}</span><span className="tabular text-muted-foreground">{c.count}</span></div>
                    ))}
                  </PanelBody>
                </Panel>
              </div>
            </>
          )}
        </div>
      )}
    </TenantGate>
  )
}
