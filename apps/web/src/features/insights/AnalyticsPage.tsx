import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Area, Bar, BarChart, CartesianGrid, Cell, ComposedChart, Legend, Line, LineChart, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { SelectField } from '@/components/ui/field'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { StatCard } from '@/components/ui/stat-card'
import { governanceApi } from '@/api/governance'
import { formatMoney } from '@/lib/format'
import { TenantGate } from '@/features/shared/kit'
import { tx, appLocale, pct } from '@/lib/i18n'

const TT = { background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12, fontSize: 12 }
const COLORS = ['hsl(var(--primary))', 'hsl(var(--info))', 'hsl(var(--warning))', '#a78bfa', '#f43f5e', '#22d3ee', '#84cc16']
const MONTH = new Intl.DateTimeFormat(appLocale, { month: 'short', year: '2-digit' })
const m = (s: string) => MONTH.format(new Date(s))
const leaveTr: Record<string, string> = { Annual: tx('Yıllık'), Sick: tx('Hastalık'), Unpaid: tx('Ücretsiz'), Maternity: tx('Doğum'), Paternity: tx('Babalık'), Marriage: tx('Evlilik'), Bereavement: tx('Vefat') }

export function AnalyticsPage() {
  const [months, setMonths] = useState('12')
  const q = useQuery({ queryKey: ['analytics', Number(months)], queryFn: ({ signal }) => governanceApi.analytics(Number(months), signal) })
  const leaveByMonth = useMemo(() => {
    const types = [...new Set(q.data?.leave.map((l) => l.type) ?? [])]
    const rows = (q.data?.timeline ?? []).map((t) => {
      const row: Record<string, number | string> = { month: m(t.month) }
      types.forEach((ty) => (row[leaveTr[ty] ?? ty] = q.data!.leave.filter((l) => l.month === t.month && l.type === ty).reduce((a, b) => a + Number(b.days), 0)))
      return row
    })
    return { rows, types: types.map((t) => leaveTr[t] ?? t) }
  }, [q.data])
  const k = q.data?.kpis
  return (
    <TenantGate>
      <PageHeader title={tx('Analitik')} description={tx('Kadro, işe alım ve ayrılış, izin, fazla mesai ve masraf eğilimleri — operasyonel veriden türetilen analitik görünümlerden.')} actions={<div className="w-40"><SelectField label={tx('Dönem')} value={months} onChange={setMonths} options={[{ value: '6', label: tx('Son 6 ay') }, { value: '12', label: tx('Son 12 ay') }, { value: '24', label: tx('Son 24 ay') }]} /></div>} />
      {q.isPending ? <RowsSkeleton rows={6} /> : q.isError ? <ErrorState message={(q.error as Error).message} onRetry={() => q.refetch()} /> : (
        <div className="space-y-6">
          <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <StatCard label={tx('Aktif çalışan')} count={k!.headcount} series={q.data.timeline.map((t) => t.headcount)} />
            <StatCard label={tx('İşe alım / ayrılış')} value={`${k!.hires} / ${k!.exits}`} />
            <StatCard label={tx('Devir hızı')} value={pct(k!.turnoverPercent.toLocaleString(appLocale))} attention={k!.turnoverPercent > 15} />
            <StatCard label={tx('Onaylı izin günü')} count={Number(k!.leaveDays)} />
          </div>
          <div className="grid gap-6 xl:grid-cols-2">
            <Panel>
              <PanelHead title={tx('Kadro eğilimi')} note={tx('Ay sonu aktif çalışan, işe alım ve ayrılış')} />
              <PanelBody className="h-72">
                <ResponsiveContainer>
                  <ComposedChart data={q.data.timeline.map((t) => ({ ...t, month: m(t.month) }))}>
                    <defs><linearGradient id="hc" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="hsl(var(--primary))" stopOpacity={0.4} /><stop offset="100%" stopColor="hsl(var(--primary))" stopOpacity={0} /></linearGradient></defs>
                    <CartesianGrid stroke="hsl(var(--border))" strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="month" fontSize={11} tickLine={false} axisLine={false} />
                    <YAxis allowDecimals={false} fontSize={11} width={28} tickLine={false} axisLine={false} />
                    <Tooltip contentStyle={TT} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                    <Area type="monotone" dataKey="headcount" name={tx('Çalışan')} stroke="hsl(var(--primary))" fill="url(#hc)" strokeWidth={2} animationDuration={1200} />
                    {/* Tema belirteçleri: açık temada beyaz zeminde ≥3:1 (sabit #38bdf8 ≈2,1:1 kalıyordu), koyu temada açık ton. */}
                    <Line type="monotone" dataKey="hires" name={tx('İşe alım')} stroke="hsl(var(--info))" strokeWidth={2} />
                    <Line type="monotone" dataKey="exits" name={tx('Ayrılış')} stroke="hsl(var(--destructive))" strokeWidth={2} />
                  </ComposedChart>
                </ResponsiveContainer>
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={tx('İzin günleri (türe göre)')} />
              <PanelBody className="h-72">
                <ResponsiveContainer>
                  <BarChart data={leaveByMonth.rows}>
                    <CartesianGrid stroke="hsl(var(--border))" strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="month" fontSize={11} tickLine={false} axisLine={false} />
                    <YAxis fontSize={11} width={28} tickLine={false} axisLine={false} />
                    <Tooltip contentStyle={TT} cursor={{ fill: 'hsl(var(--muted)/0.4)' }} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                    {leaveByMonth.types.map((t, i) => <Bar key={t} dataKey={t} stackId="a" fill={COLORS[i % COLORS.length]} radius={i === leaveByMonth.types.length - 1 ? [6, 6, 0, 0] : 0} />)}
                  </BarChart>
                </ResponsiveContainer>
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={tx('Departman dağılımı')} note={smallGroupNote(q.data.hiddenPeople?.departments, q.data.minGroup)} />
              <PanelBody className="h-72">
                <ResponsiveContainer>
                  <PieChart>
                    <Pie data={q.data.departments.map((d) => ({ ...d, department: d.department === 'Diğer' ? tx('Diğer') : d.department }))} dataKey="headcount" nameKey="department" innerRadius={60} outerRadius={100} paddingAngle={3} animationDuration={1000}>
                      {q.data.departments.map((d, i) => (
                        // Recharts dilimi role="img" çizer: ekran okuyucu için ad (WCAG 1.1.1).
                        <Cell key={i} fill={COLORS[i % COLORS.length]} aria-label={`${d.department}: ${d.headcount}`} />
                      ))}
                    </Pie>
                    <Tooltip contentStyle={TT} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                  </PieChart>
                </ResponsiveContainer>
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={tx('Kıdem dağılımı')} note={smallGroupNote(q.data.hiddenPeople?.tenure, q.data.minGroup)} />
              <PanelBody className="h-72">
                <ResponsiveContainer>
                  <BarChart data={q.data.tenure.map((t) => ({ ...t, bucket: tenureLabel(t.bucket) }))} layout="vertical">
                    <XAxis type="number" allowDecimals={false} fontSize={11} tickLine={false} axisLine={false} />
                    <YAxis type="category" dataKey="bucket" fontSize={11} width={70} tickLine={false} axisLine={false} />
                    <Tooltip contentStyle={TT} cursor={{ fill: 'hsl(var(--muted)/0.4)' }} />
                    <Bar dataKey="count" name={tx('Kişi')} fill="hsl(var(--primary))" radius={[0, 6, 6, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={tx('Çalışma ve fazla mesai (saat)')} />
              <PanelBody className="h-72">
                <ResponsiveContainer>
                  <LineChart data={q.data.overtime.map((o) => ({ ...o, month: m(o.month) }))}>
                    <CartesianGrid stroke="hsl(var(--border))" strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="month" fontSize={11} tickLine={false} axisLine={false} />
                    <YAxis fontSize={11} width={36} tickLine={false} axisLine={false} />
                    <Tooltip contentStyle={TT} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                    <Line dataKey="workedHours" name={tx('Çalışılan')} stroke="hsl(var(--info))" strokeWidth={2} />
                    <Line dataKey="overtimeHours" name={tx('Fazla mesai')} stroke="hsl(var(--warning))" strokeWidth={2} />
                  </LineChart>
                </ResponsiveContainer>
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={tx('Onaylı masraf')} note={formatMoney(k!.expenseTotal)} />
              <PanelBody className="h-72">
                <ResponsiveContainer>
                  <BarChart data={q.data.expense.map((e) => ({ ...e, month: m(e.month) }))}>
                    <XAxis dataKey="month" fontSize={11} tickLine={false} axisLine={false} />
                    <YAxis fontSize={11} width={48} tickLine={false} axisLine={false} tickFormatter={(v) => `${Math.round(v / 1000)}b`} />
                    <Tooltip contentStyle={TT} formatter={(v) => formatMoney(Number(v))} cursor={{ fill: 'hsl(var(--muted)/0.4)' }} />
                    <Bar dataKey="amount" name={tx('Tutar')} fill="#a78bfa" radius={[6, 6, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </PanelBody>
            </Panel>
          </div>
          <InfoNote>{tx('Veriler veritabanındaki')}{' '}<code className="font-mono text-[12px]">{tx('analytics_*')}</code>{' '}{tx('görünümlerinden gelir; Metabase/Power BI gibi bir BI aracı aynı görünümlere doğrudan bağlanabilir.')}</InfoNote>
        </div>
      )}
    </TenantGate>
  )
}

/** KVKK: küçük grupların gizlendiğini söyler (değer gösterilmez). */
function smallGroupNote(hidden: number | undefined, min = 5): string | undefined {
  return hidden ? tx('{0} kişi, {1} kişiden küçük gruplarda olduğu için gösterilmiyor (KVKK).', [String(hidden), String(min)]) : undefined
}

/** Sunucunun kıdem aralığı etiketleri (statik anahtar: çeviri denetimi bulabilsin). */
function tenureLabel(bucket: string): string {
  const labels: Record<string, string> = {
    '0-1 yıl': tx('0-1 yıl'), '1-3 yıl': tx('1-3 yıl'), '3-5 yıl': tx('3-5 yıl'),
    '5-10 yıl': tx('5-10 yıl'), '10+ yıl': tx('10+ yıl'), 'Diğer': tx('Diğer'),
  }
  return labels[bucket] ?? bucket
}
