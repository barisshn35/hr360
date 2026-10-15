import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ArrowLeft, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { CenteredSpinner, EmptyState, ErrorState } from '@/components/ui/States'
import { EmployeeStatusBadge } from '@/components/ui/ModuleBadges'
import { useAuth } from '@/auth/useAuth'
import { useCompanies, useEmployee } from '@/api/queries'
import { formatDate, formatNumber, fullName } from '@/lib/format'
import { cn } from '@/lib/utils'
import { NewAssignmentModal } from './NewAssignmentModal'
import { AttritionRiskPanel } from './AttritionRiskCard'
import { ExtraInfoPanel } from '@/features/governance/CustomFields'
import { tx } from '@/lib/i18n'
import { ApiError } from '@/api/client'

export function EmployeeDetailPage() {
  const { employeeId } = useParams<{ employeeId: string }>()
  const { can } = useAuth()
  const employee = useEmployee(employeeId)
  const companies = useCompanies({ enabled: can('organization:view') })
  const [assignOpen, setAssignOpen] = useState(false)

  const departmentNames = useMemo(() => {
    const map = new Map<string, string>()
    for (const c of companies.data ?? []) {
      for (const d of c.departments ?? []) map.set(d.id, `${d.name}, ${c.name}`)
    }
    return map
  }, [companies.data])

  const assignments = useMemo(
    () =>
      [...(employee.data?.assignments ?? [])].sort(
        (a, b) => new Date(b.effectiveFrom).getTime() - new Date(a.effectiveFrom).getTime(),
      ),
    [employee.data],
  )

  if (employee.isPending) return <CenteredSpinner label={tx('Çalışan yükleniyor')} />

  // Geçersiz/silinmiş kimlik: "Not Found" ve yeniden dene yerine anlamlı boş durum.
  if (employee.error instanceof ApiError && (employee.error.status === 404 || employee.error.status === 400)) {
    return (
      <Panel>
        <EmptyState
          title={tx('Çalışan bulunamadı')}
          detail={tx('Bağlantı hatalı olabilir ya da kayıt silinmiş.')}
          action={<Button asChild size="sm" variant="outline"><Link to="/panel/calisanlar">{tx('Çalışanlar')}</Link></Button>}
        />
      </Panel>
    )
  }

  if (employee.isError || !employee.data) {
    return (
      <Panel>
        <ErrorState
          title={tx('Çalışan bulunamadı')}
          message={employee.error instanceof Error ? employee.error.message : undefined}
          onRetry={() => void employee.refetch()}
        />
      </Panel>
    )
  }

  const data = employee.data
  const canManage = can('employee:manage')
  const activeCount = assignments.filter((a) => !a.effectiveTo).length

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/calisanlar">
          <ArrowLeft className="size-4" />
          {tx('Çalışanlar')}
        </Link>
      </Button>

      <PageHeader
        title={fullName(data)}
        description={tx('{0} tarihinde işe başladı. {1} aktif atama.', [formatDate(data.hireDate), formatNumber(activeCount)])}
        actions={
          canManage && (
            <Button className="cursor-pointer" onClick={() => setAssignOpen(true)}>
              <Plus className="size-4" />
              {tx('Yeni atama')}
            </Button>
          )
        }
      />

      <div className="grid gap-4 lg:grid-cols-[1.45fr_1fr]">
        <div className="space-y-4">
          <Panel>
            <PanelHead title={tx('İletişim')} action={<EmployeeStatusBadge status={data.status} />} />
            <PanelBody>
              <dl className="divide-y divide-border">
                <div className="flex items-baseline justify-between gap-4 pb-2.5">
                  <dt className="text-[12px] text-muted-foreground">{tx('E-posta')}</dt>
                  <dd className="min-w-0 truncate text-right text-[13px]">
                    <a
                      href={`mailto:${data.email}`}
                      className="cursor-pointer underline-offset-2 transition-opacity hover:opacity-65 hover:underline"
                    >
                      {data.email}
                    </a>
                  </dd>
                </div>
                <div className="flex items-baseline justify-between gap-4 py-2.5">
                  <dt className="text-[12px] text-muted-foreground">{tx('Telefon')}</dt>
                  <dd className="text-right text-[13px]">
                    {data.phone ? (
                      <a
                        href={`tel:${data.phone}`}
                        className="cursor-pointer underline-offset-2 transition-opacity hover:opacity-65 hover:underline"
                      >
                        {data.phone}
                      </a>
                    ) : (
                      <span className="text-muted-foreground">{tx('Girilmemiş')}</span>
                    )}
                  </dd>
                </div>
                <div className="flex items-baseline justify-between gap-4 pt-2.5">
                  <dt className="text-[12px] text-muted-foreground">{tx('İşe giriş')}</dt>
                  <dd className="tabular text-right text-[13px]">{formatDate(data.hireDate)}</dd>
                </div>
              </dl>
            </PanelBody>
          </Panel>

          <Panel>
            <PanelHead
              title={tx('Atama geçmişi')}
              note={tx('En yeni atama en üstte')}
              action={
                <span className="tabular text-[12px] text-muted-foreground">
                  {tx('{0} kayıt', [formatNumber(assignments.length)])}</span>
              }
            />
            {assignments.length === 0 ? (
              <EmptyState
                title={tx('Atama yok')}
                detail={tx('Bu çalışan henüz bir departmana atanmadı.')}
                action={
                  canManage ? (
                    <Button size="sm" className="cursor-pointer" onClick={() => setAssignOpen(true)}>
                      {tx('İlk atamayı yap')}
                    </Button>
                  ) : undefined
                }
              />
            ) : (
              <PanelBody>
                {/* Zaman çizelgesi: geçmiş de sıralı bir zincirdir. */}
                <ol className="relative">
                  {assignments.map((a, i) => {
                    const active = !a.effectiveTo
                    const isLast = i === assignments.length - 1
                    return (
                      <li key={a.id} className="relative flex gap-4">
                        <div className="flex w-3 shrink-0 flex-col items-center">
                          <span
                            aria-hidden="true"
                            className={cn(
                              'mt-1.5 size-2.5 shrink-0 rounded-full',
                              active ? 'bg-primary' : 'border border-border bg-background',
                            )}
                          />
                          {!isLast && <span aria-hidden="true" className="w-px flex-1 bg-border" />}
                        </div>
                        <div className={isLast ? 'min-w-0 pb-0' : 'min-w-0 pb-6'}>
                          <p className="text-[14px] font-medium">
                            {departmentNames.get(a.departmentId) ?? tx('Listede olmayan departman')}
                          </p>
                          {a.positionTitle && (
                            <p className="mt-0.5 text-[13px] text-muted-foreground">
                              {a.positionTitle}
                            </p>
                          )}
                          <p className="tabular mt-1 text-[12px] text-muted-foreground">
                            {formatDate(a.effectiveFrom)}
                            {a.effectiveTo
                              ? ` – ${formatDate(a.effectiveTo)}`
                              : tx(' tarihinden bu yana')}
                          </p>
                        </div>
                      </li>
                    )
                  })}
                </ol>
              </PanelBody>
            )}
          </Panel>
        </div>

        <div className="space-y-4">
          {canManage && <AttritionRiskPanel employee={data} />}
          <ExtraInfoPanel employeeId={data.id} />

          <Panel>
            <PanelBody>
              <p className="text-[11px] text-muted-foreground">{tx('Çalışan kimliği')}</p>
              <p className="mt-1 font-mono text-[11px] break-all text-muted-foreground">{data.id}</p>
            </PanelBody>
          </Panel>
        </div>
      </div>

      {employeeId && (
        <NewAssignmentModal
          open={assignOpen}
          onClose={() => setAssignOpen(false)}
          employeeId={employeeId}
          hireDate={employee.data?.hireDate}
        />
      )}
    </div>
  )
}
