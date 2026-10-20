import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Gift } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { SelectField } from '@/components/ui/field'
import { payrollExtrasApi } from '@/api/payrollExtras'
import type { Payslip } from '@/api/payroll'
import { formatMoney } from '@/lib/format'
import { tx } from '@/lib/i18n'

export interface RewardParts { net: number; taxes: number; employer: number; benefits: number; total: number; months: number; currency: string }

/** Yıllık toplam kazanç: pusulalardan net, vergi/kesinti (brüt − net), işveren SGK + işsizlik payı; yan haklar ayrıca. */
export function totalRewards(slips: Payslip[], benefits: number): RewardParts {
  const net = slips.reduce((a, s) => a + s.net, 0)
  const gross = slips.reduce((a, s) => a + s.gross, 0)
  const employer = slips.reduce((a, s) => a + s.sgkEmployer + s.unemploymentEmployer, 0)
  return { net, taxes: gross - net, employer, benefits, total: gross + employer + benefits, months: slips.length, currency: slips[0]?.currency ?? 'TRY' }
}

/**
 * Toplam kazanç beyanı (yenilik haritası #4): şirketin çalışan için bir yılda harcadığı toplam; maaş
 * dışında işveren primleri ve yan hakları da görünür. Yalnızca kapanmış dönemlerin pusulaları sayılır.
 */
export function TotalRewards({ slips }: { slips: Payslip[] }) {
  const years = [...new Set(slips.map((s) => s.year))].sort((a, b) => b - a)
  const [year, setYear] = useState(String(years[0] ?? new Date().getFullYear()))
  const ben = useQuery({ queryKey: ['benefits', Number(year)], queryFn: ({ signal }) => payrollExtrasApi.benefits(Number(year), signal), retry: false })
  const p = useMemo(() => totalRewards(slips.filter((s) => String(s.year) === year), ben.data?.election?.total ?? 0), [slips, year, ben.data])
  if (!years.length) return null
  const parts = [
    { key: 'net', label: tx('Net ücret (elinize geçen)'), value: p.net, cls: 'bg-primary' },
    { key: 'taxes', label: tx('Vergi, SGK ve kesintileriniz'), value: p.taxes, cls: 'bg-primary/50' },
    { key: 'employer', label: tx('İşveren SGK ve işsizlik payı'), value: p.employer, cls: 'bg-sky-500/70' },
    { key: 'benefits', label: tx('Yan haklar'), value: p.benefits, cls: 'bg-amber-400/80' },
  ].filter((x) => x.value > 0)
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Gift className="size-4 text-primary" />{tx('Toplam kazanç beyanı')}</span>}
        note={tx('{0} ayın kapanmış bordrosuna göre, şirketin sizin için yaptığı toplam harcama.', [p.months])}
        action={<div className="w-28"><SelectField label={tx('Yıl')} value={year} onChange={setYear} options={years.map((y) => ({ value: String(y), label: String(y) }))} /></div>} />
      <PanelBody className="space-y-4">
        <p className="text-[30px] font-semibold tracking-tight tabular-nums">{formatMoney(p.total, p.currency)}</p>
        <div className="flex h-3 overflow-hidden rounded-full bg-muted" role="img" aria-label={tx('Toplam kazancın dağılımı')}>
          {parts.map((x) => <span key={x.key} className={x.cls} style={{ width: `${(x.value / p.total) * 100}%` }} />)}
        </div>
        <ul className="grid gap-2 sm:grid-cols-2">
          {parts.map((x) => (
            <li key={x.key} className="flex items-center gap-2 text-[13px]">
              <span className={`size-2.5 shrink-0 rounded-full ${x.cls}`} />
              <span className="min-w-0 flex-1 text-muted-foreground">{x.label}</span>
              <span className="font-medium tabular-nums">{formatMoney(x.value, p.currency)}</span>
            </li>
          ))}
        </ul>
      </PanelBody>
    </Panel>
  )
}
