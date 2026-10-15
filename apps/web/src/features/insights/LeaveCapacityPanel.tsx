import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Bar, BarChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { SelectField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { mlInsightsApi } from '@/api/mlInsights'
import { capacityTone, mapeLabel } from '@/lib/mlInsights'
import { formatDate, formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/**
 * Mevsimsellikli izin tahmini ve ekip kapasitesi (ML dalgası 2, madde 45): haftanın günü, ay ve
 * köprü/tatil komşuluğu etkileri; geri test (MAPE). Yalnızca toplu sayılar — kişi bazında tahmin yok;
 * 5 kişiden küçük ekipler "Diğer" altında birleşir.
 */
export function LeaveCapacityPanel() {
  const [weeks, setWeeks] = useState(12)
  const q = useQuery({ queryKey: ['ml', 'leave-forecast', weeks], queryFn: ({ signal }) => mlInsightsApi.leaveForecast(weeks, signal), retry: false, staleTime: 5 * 60_000 })
  const d = q.data
  const f = d?.available ? d.forecast : null
  const bt = f?.backtest
  return (
    <Panel>
      <PanelHead
        title={tx('Haftalık izin tahmini ve ekip kapasitesi')}
        note={tx('Son iki yıldaki onaylı izinlerden: haftanın günü, ay ve köprü/tatil komşuluğu etkileri; resmî tatiller iş günü sayılmaz.')}
        action={<div className="w-36"><SelectField label={tx('Ufuk')} value={String(weeks)} onChange={(v) => setWeeks(Number(v))}
          options={[{ value: '8', label: tx('{0} hafta', [8]) }, { value: '12', label: tx('{0} hafta', [12]) }, { value: '26', label: tx('{0} hafta', [26]) }]} /></div>}
      />
      <PanelBody className="space-y-5">
        {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? (
          <p className="text-[13px] text-muted-foreground">{errMsg(q.error, tx('Tahmin yapılamadı.'))}</p>
        ) : !f ? (
          <InfoNote>{d && !d.available ? d.reason : tx('Tahmin için veri yok.')}</InfoNote>
        ) : (
          <>
            <div className="grid gap-3 sm:grid-cols-3">
              <div className="rounded-xl border border-border p-3">
                <p className="text-[12px] text-muted-foreground">{tx('Taban izinli oranı (iş günü)')}</p>
                <p className="text-lg font-semibold tabular-nums">%{formatNumber(f.effects.base_rate_pct)}</p>
              </div>
              <div className="rounded-xl border border-border p-3">
                <p className="text-[12px] text-muted-foreground">{tx('Köprü günü etkisi')}</p>
                <p className="text-lg font-semibold tabular-nums">×{formatNumber(f.effects.bridge_factor)}</p>
              </div>
              <div className="rounded-xl border border-border p-3">
                <p className="text-[12px] text-muted-foreground">{tx('Geri test hatası (MAPE, son {0} hafta)', [bt?.weeks ?? 8])}</p>
                <p className="text-lg font-semibold tabular-nums">{bt?.mape != null ? `%${formatNumber(bt.mape)}` : '—'} <span className="text-[12px] font-normal text-muted-foreground">{mapeLabel(bt?.mape)}</span></p>
                {bt && <p className="text-[11.5px] text-muted-foreground">{tx('WAPE %{0}; mevsimselliksiz düz ortalama: MAPE %{1}', [formatNumber(bt.wape ?? NaN), formatNumber(bt.baseline_mape ?? NaN)])}</p>}
              </div>
            </div>
            <div className="h-56">
              <ResponsiveContainer>
                <BarChart data={f.weeks.map((w) => ({ week: formatDate(w.week_start), pct: w.expected_absent_pct, avg: w.expected_absent_avg }))}>
                  <XAxis dataKey="week" fontSize={11} tickLine={false} axisLine={false} />
                  <YAxis fontSize={11} width={32} tickLine={false} axisLine={false} unit="%" />
                  <Tooltip contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12 }}
                    formatter={(v, name) => name === 'pct' ? [`%${formatNumber(Number(v))}`, tx('Beklenen izinli oranı')] : [formatNumber(Number(v)), String(name)]} />
                  <Bar dataKey="pct" fill="hsl(var(--primary))" radius={[6, 6, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </div>
            <div className="flex flex-wrap gap-1.5 text-[12px]">
              {f.effects.day_of_week.map((x) => (
                <span key={x.day} className="rounded-full bg-muted px-2.5 py-0.5">{x.label} ×{formatNumber(x.factor)}</span>
              ))}
              {!f.effects.monthly_seasonality && <span className="text-muted-foreground">{tx('Ay etkisi için en az 12 aylık geçmiş gerekir.')}</span>}
            </div>
            {f.weeks.some((w) => w.bridge_days.length > 0) && (
              <p className="text-[12.5px] text-muted-foreground">{tx('Köprü günleri: {0}', [f.weeks.flatMap((w) => w.bridge_days).map((x) => formatDate(x)).join(', ')])}</p>
            )}
            <div>
              <p className="mb-2 text-[13px] font-medium">{tx('Ekip kapasitesi (beklenen izinli oranı, en yoğun hafta)')}</p>
              {f.teams.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Gösterilecek ekip yok (5 kişiden küçük ekipler gösterilmez).')}</p> : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[520px] text-[12.5px]">
                    <thead><tr className="text-left text-muted-foreground">
                      <th className="py-1.5 pr-3 font-medium">{tx('Ekip')}</th>
                      <th className="py-1.5 pr-3 text-right font-medium">{tx('Kişi')}</th>
                      {f.teams[0].weeks.slice(0, 6).map((w) => <th key={w.week_start} className="py-1.5 pr-2 text-right font-medium">{formatDate(w.week_start)}</th>)}
                    </tr></thead>
                    <tbody className="divide-y divide-border">
                      {f.teams.map((t) => (
                        <tr key={t.team}>
                          <td className="py-1.5 pr-3">{t.team}</td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{t.headcount}</td>
                          {t.weeks.slice(0, 6).map((w) => (
                            <td key={w.week_start} className="py-1.5 pr-2 text-right">
                              <StatusBadge tone={capacityTone(w.expected_absent_pct)}>%{formatNumber(w.expected_absent_pct)}</StatusBadge>
                            </td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              {f.hidden_people > 0 && <p className="mt-1.5 text-[11.5px] text-muted-foreground">{tx('{0} kişi, 5 kişiden küçük gruplar gizlendiği için tabloda yok.', [f.hidden_people])}</p>}
            </div>
            <p className="text-[11.5px] text-muted-foreground">{tx('Tahmin onaylı izinlerin geçmiş örüntüsüne dayanır; henüz girilmemiş ya da onay bekleyen izinleri içermez. Kişi bazında tahmin yapılmaz.')}</p>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}
