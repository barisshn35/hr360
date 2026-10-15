import { useQuery } from '@tanstack/react-query'
import { ShieldAlert } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import { useDirectory } from '@/api/directory'
import { mlInsightsApi } from '@/api/mlInsights'
import { severityLabel, severityTone } from '@/lib/expenseAudit'
import { payrollFlagText, sortBySeverity } from '@/lib/mlInsights'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/**
 * Puantaj denetimi (ML dalgası 2): son iki haftada çalışanın kendi olağan haftalarına göre ani saat
 * artışı, 45 saati aşan hafta ve tekrarlayan eksik giriş/çıkış. Yalnızca İK; bilgilendirme amaçlıdır,
 * hiçbir kayıt değişmez ve kesinti uygulanmaz.
 */
export function TimesheetAnomalyPanel() {
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['timesheet', 'anomalies'], queryFn: ({ signal }) => mlInsightsApi.timesheetAnomalies(12, signal), retry: false, staleTime: 5 * 60_000 })
  const rows = sortBySeverity(q.data?.items ?? [])
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><ShieldAlert className="size-4 text-primary" />{' '}{tx('Puantaj denetimi')}</span>}
        note={q.data?.weeksEvaluated.length ? tx('Değerlendirilen haftalar: {0}. Karşılaştırma: çalışanın son 12 haftası.', [q.data.weeksEvaluated.join(', ')]) : tx('Son iki hafta, çalışanın son 12 haftasıyla karşılaştırılır.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{errMsg(q.error, tx('Puantaj denetimi yapılamadı.'))}</p>
          : rows.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Olağan dışı bir hafta bulunmadı.')}</p> : (
            <ul className="divide-y divide-border text-[13px]">
              {rows.map((r) => (
                <li key={`${r.employeeId}-${r.week}`} className="py-2.5">
                  <p className="font-medium">{nameOf(r.employeeId)} <span className="text-[12px] font-normal text-muted-foreground">· {r.week}</span></p>
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
          )}
        <p className="text-[11.5px] text-muted-foreground">{tx('İşaretler bilgilendirme amaçlıdır; otomatik kesinti ya da yaptırım uygulanmaz. Çalışan kimliği model servisine gönderilmez.')}</p>
      </PanelBody>
    </Panel>
  )
}
