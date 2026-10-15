import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Scale } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { mlInsightsApi } from '@/api/mlInsights'
import { formatGap } from '@/lib/mlInsights'
import { formatDateTime, formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/**
 * Ücret adaleti analizi (ML dalgası 2, madde 48): meşru etkenler (kademe/unvan, kıdem, departman) sabitken
 * açıklanamayan ücret farkı — departman ve kıdem bandı kırılımında. Cinsiyet/yaş tutulmadığından o
 * kırılımlar yoktur. Yalnızca İK ve şirket yöneticisi; her çalıştırma erişim kaydına yazılır. Kişi
 * bazında sonuç gösterilmez; 5'ten küçük gruplar gizlidir. Karar değil, inceleme başlangıcıdır.
 */
export function PayEquityPanel() {
  const [run, setRun] = useState(false)
  const q = useQuery({ queryKey: ['compensation', 'pay-equity'], queryFn: ({ signal }) => mlInsightsApi.payEquity(signal), enabled: run, retry: false, staleTime: 5 * 60_000 })
  const r = q.data?.report
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Scale className="size-4 text-primary" />{' '}{tx('Ücret adaleti analizi')}</span>}
        note={tx('Meşru etkenler (ücret kademesi/unvan, kıdem, departman) sabitken kalan ücret farkı. Çalıştırma erişim kaydına yazılır.')} />
      <PanelBody className="space-y-4">
        {!run ? (
          <div className="space-y-3">
            <InfoNote>{tx('Cinsiyet, yaş ve uyruk veri modelinde tutulmadığından (KVKK veri en aza indirme) analiz yalnızca departman ve kıdem bandı kırılımındadır. 5 kişiden küçük gruplar gösterilmez; kişi bazında sonuç verilmez.')}</InfoNote>
            <Button onClick={() => setRun(true)}><Scale className="size-4" /> {tx('Analizi çalıştır')}</Button>
          </div>
        ) : q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? (
          <p className="text-[13px] text-muted-foreground">{errMsg(q.error, tx('Analiz yapılamadı.'))}</p>
        ) : r && q.data && (
          <>
            <div className="grid gap-3 sm:grid-cols-4">
              <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('Çalışan')}</p><p className="text-lg font-semibold tabular-nums">{formatNumber(r.employees)}</p></div>
              <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('Açıklanan varyans (R²)')}</p><p className="text-lg font-semibold tabular-nums">{formatNumber(r.model.r2)}</p></div>
              <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('Kıdem etkisi / yıl')}</p><p className="text-lg font-semibold tabular-nums">{formatGap(r.model.tenure_effect_pct_per_year)}</p></div>
              <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('±2σ dışında kalan')}</p><p className="text-lg font-semibold tabular-nums">{formatNumber(r.outside_2sd)}</p></div>
            </div>
            {r.reports.map((rep) => (
              <div key={rep.attribute}>
                <p className="mb-1.5 text-[13px] font-medium">{rep.attribute === 'department' ? tx('Departmana göre açıklanamayan fark') : tx('Kıdem bandına göre açıklanamayan fark')}</p>
                <p className="mb-2 text-[11.5px] text-muted-foreground">{rep.attribute === 'department' ? tx('Kontrol: ücret kademesi/unvan ve kıdem.') : tx('Kontrol: ücret kademesi/unvan ve departman.')} R² {formatNumber(rep.r2)}</p>
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[420px] text-[12.5px]">
                    <thead><tr className="text-left text-muted-foreground">
                      <th className="py-1.5 pr-3 font-medium">{rep.attribute === 'department' ? tx('Departman') : tx('Kıdem (yıl)')}</th>
                      <th className="py-1.5 pr-3 text-right font-medium">{tx('Kişi')}</th>
                      <th className="py-1.5 pr-3 text-right font-medium">{tx('Fark')}</th>
                      <th className="py-1.5 pr-3 text-right font-medium">{tx('%95 güven aralığı')}</th>
                    </tr></thead>
                    <tbody className="divide-y divide-border">
                      {rep.groups.map((g) => (
                        <tr key={g.group}>
                          <td className="py-1.5 pr-3">{g.group === 'Diğer' ? tx('Diğer') : g.group}</td>
                          <td className="py-1.5 pr-3 text-right tabular-nums">{g.people}</td>
                          <td className="py-1.5 pr-3 text-right"><StatusBadge tone={g.significant ? (Math.abs(g.gap_pct) >= 5 ? 'warning' : 'info') : 'neutral'}>{formatGap(g.gap_pct)}</StatusBadge></td>
                          <td className="py-1.5 pr-3 text-right tabular-nums text-muted-foreground">{formatGap(g.ci_low_pct)} … {formatGap(g.ci_high_pct)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                {rep.hidden_people > 0 && <p className="mt-1 text-[11.5px] text-muted-foreground">{tx('{0} kişi, 5 kişiden küçük gruplar gizlendiği için tabloda yok.', [rep.hidden_people])}</p>}
              </div>
            ))}
            <ul className="list-disc space-y-1 pl-5 text-[12px] text-muted-foreground">
              <li>{tx('Fark: aynı kademe/unvan ve kıdemdeki (ya da departmandaki) benzerlerine göre grubun ortalama ücret farkı. Güven aralığı sıfırı kapsamıyorsa anlamlı kabul edilir; nedenini insan incelemesi belirler.')}</li>
              <li>{tx('Cinsiyet, yaş ve uyruk veri modelinde tutulmadığından bu kırılımlar yapılamaz.')}</li>
              {q.data.excludedCurrency > 0 && <li>{tx('{0} kayıt farklı para biriminde olduğu için dışarıda bırakıldı (kur çevrimi yapılmaz).', [q.data.excludedCurrency])}</li>}
              <li>{tx('Oluşturulma: {0}', [formatDateTime(q.data.generatedAt)])}</li>
            </ul>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}
