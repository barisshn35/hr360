import type { UseQueryResult } from '@tanstack/react-query'
import { ShieldAlert } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import type { PayrollAnomalies } from '@/api/mlInsights'
import { severityLabel, severityTone } from '@/lib/expenseAudit'
import { payrollFlagText, sortBySeverity } from '@/lib/mlInsights'
import { formatDateTime } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/**
 * Bordro denetimi (ML dalgası 2): dönem hesaplanınca her pusula çalışanın kendi geçmişi ve aynı
 * kademedeki eşleriyle (en az 5 kişi) karşılaştırılır. İşaretler hazırlayan ve onaylayan için bilgidir;
 * dönem kapatma engellenmez, pusulada hiçbir değer otomatik değişmez, çalışan bu işaretleri görmez.
 */
export function PayrollAnomalyPanel({ query, nameOf, closed }: {
  query: UseQueryResult<PayrollAnomalies>
  nameOf: (employeeId: string) => string
  closed: boolean
}) {
  const data = query.data
  const rows = sortBySeverity(data?.items ?? [])
  return (
    <Panel>
      <PanelHead
        title={tx('Bordro denetimi')}
        note={data?.checkedAt
          ? tx('Son denetim: {0}. Olağan dışı fazla mesai, ek ödeme, kesinti ve brüt; yıllık 270 saat fazla mesai sınırı.', [formatDateTime(data.checkedAt)])
          : tx('Olağan dışı fazla mesai, ek ödeme, kesinti ve brüt; yıllık 270 saat fazla mesai sınırı.')}
      />
      <PanelBody className="space-y-3">
        {query.isPending ? <RowsSkeleton rows={2} /> : query.isError ? (
          <p className="text-[13px] text-muted-foreground">{errMsg(query.error, tx('Denetim işaretleri okunamadı.'))}</p>
        ) : !data?.checkedAt ? (
          <p className="text-[13px] text-muted-foreground">{tx('Denetim bu hesaplamada çalışmadı (model servisine ulaşılamadı). Dönemi yeniden hesaplayarak tekrar deneyebilirsiniz; hesaplama sonucu bundan etkilenmez.')}</p>
        ) : rows.length === 0 ? (
          <p className="text-[13px] text-muted-foreground">{tx('Olağan dışı bir pusula bulunmadı.')}</p>
        ) : (
          <>
            <div role="status" className="flex items-start gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-3 text-[13px]">
              <ShieldAlert className="mt-0.5 size-4 shrink-0" />
              <span>{closed
                ? tx('{0} çalışanın pusulasında işaret var. Dönem kapandı; işaretler kayıt amaçlı gösterilir.', [rows.length])
                : tx('{0} çalışanın pusulasında işaret var. İşaretler yalnızca inceleme içindir; kapatmayı engellemez ve hiçbir tutarı değiştirmez.', [rows.length])}</span>
            </div>
            <ul className="divide-y divide-border text-[13px]">
              {rows.map((r) => (
                <li key={r.payslipId} className="py-2.5">
                  <p className="font-medium">{nameOf(r.employeeId)}</p>
                  <ul className="mt-1 space-y-1">
                    {r.flags.map((f, i) => (
                      <li key={`${f.code}-${i}`} className="flex flex-wrap items-center gap-1.5 text-[12.5px]">
                        <StatusBadge tone={severityTone[f.severity] ?? 'neutral'}>{severityLabel(f.severity)}</StatusBadge>
                        <span>{payrollFlagText(f)}</span>
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          </>
        )}
        <p className="text-[11.5px] text-muted-foreground">{tx('Yöntem: sağlam z-skoru (medyan/mutlak sapma), yeterli geçmişte IsolationForest. Çalışan kimliği model servisine gönderilmez.')}</p>
      </PanelBody>
    </Panel>
  )
}
