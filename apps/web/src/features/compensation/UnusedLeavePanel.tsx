import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { SelectField } from '@/components/ui/field'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { apiFetch } from '@/api/client'
import { downloadWorkbook } from '@/lib/spreadsheet'
import { formatMoney, formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'

interface UnusedLeave {
  year: number
  items: { employeeId: string; name: string; department: string | null; remainingDays: number; pendingDays: number; dailyGross: number | null; currency: string | null; amount: number | null }[]
  totals: { currency: string; days: number; amount: number }[]
  missingSalary: number
}

/** Kullanılmayan yıllık izin karşılığı (yenilik haritası #16): muhasebenin yıl sonu karşılık hesabı. */
export function UnusedLeavePanel() {
  const now = new Date().getFullYear()
  const [year, setYear] = useState(String(now))
  const q = useQuery({ queryKey: ['compensation', 'unused-leave', year], queryFn: ({ signal }) => apiFetch<UnusedLeave>(`/api/compensation/compensation/reports/unused-leave?year=${year}`, { signal }) })
  const d = q.data
  const money = (v: number | null, c: string | null) => (v == null ? '—' : formatMoney(v, c ?? 'TRY'))
  const exportXlsx = () => d && void downloadWorkbook(`kullanilmayan-izin-${d.year}.xlsx`, {
    [tx('Karşılık')]: d.items.map((i) => ({
      [tx('Çalışan')]: i.name, [tx('Departman')]: i.department ?? '', [tx('Kalan gün')]: i.remainingDays, [tx('Bekleyen gün')]: i.pendingDays,
      [tx('Günlük brüt')]: i.dailyGross ?? '', [tx('Para birimi')]: i.currency ?? '', [tx('Karşılık')]: i.amount ?? '',
    })),
  })
  return (
    <Panel>
      <PanelHead title={tx('Kullanılmayan izin karşılığı')}
        note={tx('Kalan yıllık izin günü × günlük brüt ücret (aylık brüt / 30). Bekleyen talepler düşülmez, ayrıca gösterilir.')}
        action={<div className="flex items-end gap-2">
          <div className="w-28"><SelectField label={tx('Yıl')} value={year} onChange={setYear} options={[now - 1, now, now + 1].map((y) => ({ value: String(y), label: String(y) }))} /></div>
          <Button variant="outline" size="sm" disabled={!d?.items.length} onClick={exportXlsx}><Download className="size-4" />{tx('Excel')}</Button>
        </div>} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState message={q.error instanceof Error ? q.error.message : undefined} onRetry={() => void q.refetch()} /> : (
          <>
            <div className="flex flex-wrap gap-3">
              {d!.totals.map((t) => (
                <div key={t.currency} className="rounded-xl border border-border px-4 py-2.5">
                  <p className="text-[12px] text-muted-foreground">{tx('Toplam karşılık ({0} gün)', [formatNumber(t.days)])}</p>
                  <p className="text-[20px] font-semibold tabular-nums">{formatMoney(t.amount, t.currency)}</p>
                </div>
              ))}
            </div>
            {d!.missingSalary > 0 && <InfoNote>{tx('{0} çalışanın güncel ücret kaydı yok; tutarları hesaplanamadı.', [d!.missingSalary])}</InfoNote>}
            <div className="overflow-x-auto">
              <table className="w-full text-[13px]">
                <thead className="text-left text-[11.5px] text-muted-foreground">
                  <tr><th className="py-2 font-normal">{tx('Çalışan')}</th><th className="py-2 font-normal">{tx('Departman')}</th><th className="py-2 text-right font-normal">{tx('Kalan gün')}</th><th className="py-2 text-right font-normal">{tx('Bekleyen')}</th><th className="py-2 text-right font-normal">{tx('Günlük brüt')}</th><th className="py-2 text-right font-normal">{tx('Karşılık')}</th></tr>
                </thead>
                <tbody>
                  {d!.items.map((i) => (
                    <tr key={i.employeeId} className="border-t border-border">
                      <td className="py-2 font-medium">{i.name}</td><td className="py-2 text-muted-foreground">{i.department ?? '—'}</td>
                      <td className="py-2 text-right tabular-nums">{formatNumber(i.remainingDays)}</td><td className="py-2 text-right tabular-nums text-muted-foreground">{i.pendingDays ? formatNumber(i.pendingDays) : '—'}</td>
                      <td className="py-2 text-right tabular-nums">{money(i.dailyGross, i.currency)}</td><td className="py-2 text-right font-semibold tabular-nums">{money(i.amount, i.currency)}</td>
                    </tr>
                  ))}
                  {!d!.items.length && <tr><td colSpan={6} className="py-6 text-center text-muted-foreground">{tx('Bu yıl için kalan izin bakiyesi yok.')}</td></tr>}
                </tbody>
              </table>
            </div>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}
