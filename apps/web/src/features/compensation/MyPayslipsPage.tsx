import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Printer, ReceiptText } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { payrollApi, type Payslip } from '@/api/payroll'
import { formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'
import { errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { PayslipBreakdown, monthName } from './PayrollPage'
import { printPayslip } from './payslipPrint'
import { MyAdvancesPanel } from './PayrollExtras'
import { MyEPayslipBox } from './PayrollTr'

/** Çalışanın kendi bordro pusulaları (yalnızca kapanmış dönemler). */
export function MyPayslipsPage() {
  const { user } = useAuth()
  const q = useQuery({ queryKey: ['payroll', 'mine'], queryFn: ({ signal }) => payrollApi.myPayslips(signal) })
  const [sel, setSel] = useState<Payslip | null>(null)
  const current = sel ?? q.data?.[0] ?? null
  const name = user?.fullName ?? ''
  return (
    <>
      <PageHeader title={tx('Bordrolarım')} description={tx('Kapanmış dönemlerin bordro pusulaları. Yazdırabilir ya da PDF olarak kaydedebilirsiniz.')} />
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : !q.data.length ? (
        <EmptyState icon={ReceiptText} title={tx('Henüz pusulanız yok')} detail={tx('Bordro dönemi kapandığında pusulanız burada görünür.')} />
      ) : (
        <div className="grid gap-5 lg:grid-cols-[260px_1fr]">
          <Panel>
            <PanelHead title={tx('Dönemler')} />
            <PanelBody className="p-0">
              <ul className="divide-y divide-border">
                {q.data.map((s) => (
                  <li key={s.id}>
                    <button type="button" onClick={() => setSel(s)}
                      className={cn('flex w-full items-center justify-between px-4 py-2.5 text-left text-[13px] hover:bg-accent/40', current?.id === s.id && 'bg-accent/60 font-medium')}>
                      <span>{monthName(s.month)} {s.year}</span>
                      <span className="tabular-nums text-muted-foreground">{formatMoney(s.net, s.currency)}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </PanelBody>
          </Panel>
          {current && (
            <Panel>
              <PanelHead title={tx('{0} {1} pusulası', [monthName(current.month), current.year])}
                action={<Button size="sm" variant="outline" onClick={() => printPayslip(current, name)}><Printer className="size-4" /> {tx('Yazdır / PDF')}</Button>} />
              <PanelBody><PayslipBreakdown s={current} /><MyEPayslipBox key={current.id} payslipId={current.id} /></PanelBody>
            </Panel>
          )}
        </div>
      )}
      <div className="mt-4"><InfoNote>{tx('Pusulanızı yalnızca siz ve bordro yetkilisi görebilir.')}</InfoNote></div>
      <div className="mt-5"><MyAdvancesPanel /></div>
    </>
  )
}
