import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ArrowLeft, ArrowRight, Plus, Waypoints } from 'lucide-react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { CenteredSpinner, EmptyState, ErrorState } from '@/components/ui/States'
import { SelectField } from '@/components/ui/Field'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { useAuth } from '@/auth/useAuth'
import { useCompany, useEmployees } from '@/api/queries'
import { qk } from '@/api/queries'
import { organizationApi } from '@/api/organization'
import { ApiError } from '@/api/client'
import type { Department } from '@/api/types'
import { formatDate, formatNumber, fullName } from '@/lib/format'
import { DepartmentTree } from './DepartmentTree'
import { NewDepartmentModal } from './NewDepartmentModal'
import { OrgChart } from './OrgChart'
import { DepartmentLinksModal, linkKindLabel, useDepartmentLinks } from './DepartmentLinksModal'
import { tx } from '@/lib/i18n'

type View = 'liste' | 'sema'

export function CompanyDetailPage() {
  const { companyId } = useParams<{ companyId: string }>()
  const { can } = useAuth()
  const company = useCompany(companyId)
  const employees = useEmployees({ enabled: can('employee:viewAll') })

  const [selected, setSelected] = useState<Department | null>(null)
  const [modalOpen, setModalOpen] = useState(false)
  const [linksOpen, setLinksOpen] = useState(false)
  const [view, setView] = useTabParam<View>('gorunum', 'liste')
  const queryClient = useQueryClient()

  const departments = company.data?.departments ?? []
  const canManage = can('organization:manage')
  const canCreate = canManage
  // Matris bağları liste görünümünde yalnızca yönetene gösterilir (okuma herkese açık; şemada da görünür).
  const links = useDepartmentLinks(companyId ?? '', canManage && view === 'liste' && Boolean(selected))

  const renameMutation = useMutation({
    // Backend PUT, departman basini da istekten alir; ad degistirirken mevcut bas
    // gonderilmezse bas silinir ve izin/masraf onaylari onaycisiz kalir.
    mutationFn: ({ id, name, headEmployeeId }: { id: string; name: string; headEmployeeId: string | null }) =>
      organizationApi.updateDepartment(id, name, headEmployeeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: qk.company(companyId ?? '') })
    },
  })

  const headMutation = useMutation({
    mutationFn: ({ id, name, headEmployeeId }: { id: string; name: string; headEmployeeId: string | null }) =>
      organizationApi.updateDepartment(id, name, headEmployeeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: qk.company(companyId ?? '') })
    },
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => organizationApi.deleteDepartment(id),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: qk.company(companyId ?? '') })
      // Silinen departman o an seciliyse detay panelini bosalt.
      setSelected((cur) => (cur?.id === id ? null : cur))
    },
  })

  /**
   * Backend 409 doner ("once su kayitlarin tasinmasi gerekiyor...").
   * ApiError'daki mesaji oldugu gibi tasiyoruz - DepartmentTree bu
   * mesaji silme dialogunda gosteriyor, kapatmiyor.
   */
  const handleDelete = async (department: Department) => {
    try {
      await deleteMutation.mutateAsync(department.id)
    } catch (err) {
      throw new Error(
        err instanceof ApiError ? err.message : tx('Departman silinemedi. Lütfen tekrar deneyin.'),
      )
    }
  }

  /** Seçili departmana atanmış, hâlâ aktif olan çalışanlar. */
  const members = useMemo(() => {
    if (!selected || !employees.data) return []
    return employees.data.filter((e) =>
      e.assignments?.some((a) => a.departmentId === selected.id && !a.effectiveTo),
    )
  }, [selected, employees.data])

  if (company.isPending) return <CenteredSpinner label={tx('Şirket yükleniyor')} />

  if (company.isError || !company.data) {
    return (
      <Panel>
        <ErrorState
          title={tx('Şirket bulunamadı')}
          message={company.error instanceof Error ? company.error.message : undefined}
          onRetry={() => void company.refetch()}
        />
      </Panel>
    )
  }

  const data = company.data
  const rootCount = departments.filter((d) => !d.parentDepartmentId).length

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/organizasyon">
          <ArrowLeft className="size-4" />
          {tx('Organizasyon')}
        </Link>
      </Button>

      <PageHeader
        title={data.name}
        description={tx('{0} departman, {1} kök seviyede. {2} tarihinde oluşturuldu.', [formatNumber(departments.length), formatNumber(rootCount), formatDate(data.createdAt)])}
        actions={
          canCreate && (
            <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
              <Plus className="size-4" />
              {tx('Yeni departman')}
            </Button>
          )
        }
      />

      <Tabs<View>
        label={tx('Organizasyon görünümü')}
        value={view}
        onChange={setView}
        tabs={[
          { key: 'liste', label: tx('Liste') },
          { key: 'sema', label: tx('Şema') },
        ]}
      />

      {view === 'sema' && companyId && <OrgChart companyId={companyId} companyName={data.name} />}

      {view === 'liste' && (
      <div className="grid gap-4 lg:grid-cols-[1.35fr_1fr]">
        <Panel>
          <PanelHead
            title={tx('Departman ağacı')}
            note={tx('Detayını görmek için bir departman seçin')}
            action={
              <span className="tabular text-[12px] text-muted-foreground">
                {tx('{0} kayıt', [formatNumber(departments.length)])}</span>
            }
          />
          <DepartmentTree
            departments={departments}
            selectedId={selected?.id}
            onSelect={setSelected}
            onRename={
              canManage
                ? (department, name) =>
                    renameMutation.mutate({ id: department.id, name, headEmployeeId: department.headEmployeeId ?? null })
                : undefined
            }
            onDelete={canManage ? handleDelete : undefined}
            emptyAction={
              canCreate ? (
                <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
                  {tx('İlk departmanı ekle')}
                </Button>
              ) : undefined
            }
          />
        </Panel>

        <Panel>
          <PanelHead title={selected ? selected.name : tx('Departman detayı')} />
          {!selected ? (
            <EmptyState
              title={tx('Departman seçilmedi')}
              detail={tx('Soldaki ağaçtan bir departman seçin; atanmış çalışanlar burada görünür.')}
            />
          ) : (
            <PanelBody className="space-y-5">
              <p className="text-[13px] text-muted-foreground">
                {selected.parentDepartmentId
                  ? tx('Üst departman: {0}', [departments.find((d) => d.id === selected.parentDepartmentId)?.name ?? tx('listede yok')])
                  : tx('Kök seviye departman')}
              </p>

              {canManage && (
                <SelectField
                  id="department-head"
                  label={tx('Departman başı')}
                  value={selected.headEmployeeId ?? ''}
                  onChange={(v) =>
                    headMutation.mutate({
                      id: selected.id,
                      name: selected.name,
                      headEmployeeId: v || null,
                    })
                  }
                  placeholder={tx('Departman başı seçin (opsiyonel)')}
                  options={members.map((e) => ({ value: e.id, label: fullName(e) }))}
                  disabled={members.length === 0}
                  hint={
                    members.length === 0
                      ? tx('Departman başı seçmek için önce bu departmana bir çalışan atayın.')
                      : tx('Sadece bu departmandaki çalışanlar seçilebilir.')
                  }
                />
              )}

              {can('employee:viewAll') && (
                <div>
                  <p className="mb-2 border-b border-border pb-2 text-[12px] text-muted-foreground">{tx('Atanmış çalışan ({0})', [formatNumber(members.length)])}</p>
                  {employees.isPending ? (
                    <p className="text-[13px] text-muted-foreground">{tx('Yükleniyor')}</p>
                  ) : members.length === 0 ? (
                    <p className="text-[13px] text-muted-foreground">
                      {tx('Bu departmana atanmış aktif çalışan yok.')}
                    </p>
                  ) : (
                    <ul className="divide-y divide-border">
                      {members.map((e) => {
                        const current = e.assignments?.find(
                          (a) => a.departmentId === selected.id && !a.effectiveTo,
                        )
                        return (
                          <li key={e.id}>
                            <Link
                              to={`/panel/calisanlar/${e.id}`}
                              className="flex cursor-pointer items-baseline justify-between gap-3 py-2.5 text-[13px] transition-opacity hover:opacity-65"
                            >
                              <span className="min-w-0 truncate font-medium">{fullName(e)}</span>
                              <span className="shrink-0 truncate text-[12px] text-muted-foreground">
                                {current?.positionTitle ?? tx('Pozisyon yok')}
                              </span>
                            </Link>
                          </li>
                        )
                      })}
                    </ul>
                  )}
                </div>
              )}

              {canManage && (
                <div>
                  <p className="mb-2 flex items-center justify-between gap-2 border-b border-border pb-2 text-[12px] text-muted-foreground">
                    <span className="flex items-center gap-1.5">
                      <Waypoints className="size-3.5" aria-hidden />
                      {tx('Matris bağları (noktalı çizgi)')}
                    </span>
                    <Button size="xs" variant="ghost" className="cursor-pointer" onClick={() => setLinksOpen(true)}>
                      {tx('Yönet')}
                    </Button>
                  </p>
                  {links.isPending ? (
                    <p className="text-[13px] text-muted-foreground">{tx('Yükleniyor')}</p>
                  ) : links.isError ? (
                    <p className="text-[13px] text-muted-foreground">{tx('Matris bağları okunamadı.')}</p>
                  ) : (
                    (() => {
                      const mine = (links.data ?? []).filter(
                        (l) => l.fromDepartmentId === selected.id || l.toDepartmentId === selected.id,
                      )
                      const name = (id: string) => departments.find((d) => d.id === id)?.name ?? tx('Başka şirkette')
                      return mine.length === 0 ? (
                        <p className="text-[13px] text-muted-foreground">{tx('Bu departmanın matris bağı yok.')}</p>
                      ) : (
                        <ul className="space-y-1.5 text-[13px]">
                          {mine.map((l) => (
                            <li key={l.id} className="flex flex-wrap items-center gap-1">
                              <span className="font-medium">{name(l.fromDepartmentId)}</span>
                              <ArrowRight className="size-3.5 text-muted-foreground" aria-label={tx('raporlar')} />
                              <span className="font-medium">{name(l.toDepartmentId)}</span>
                              <span className="text-[12px] text-muted-foreground">· {linkKindLabel(l.kind)}</span>
                            </li>
                          ))}
                        </ul>
                      )
                    })()
                  )}
                </div>
              )}

              <div className="border-t border-border pt-3">
                <p className="text-[11px] text-muted-foreground">{tx('Departman kimliği')}</p>
                <p className="mt-1 font-mono text-[11px] break-all text-muted-foreground">
                  {selected.id}
                </p>
              </div>

              {canCreate && (
                <Button
                  size="sm"
                  variant="outline"
                  className="w-full cursor-pointer"
                  onClick={() => setModalOpen(true)}
                >
                  {tx('Bu departmanın altına ekle')}
                </Button>
              )}
            </PanelBody>
          )}
        </Panel>
      </div>
      )}

      {companyId && canManage && (
        <DepartmentLinksModal
          open={linksOpen}
          onClose={() => setLinksOpen(false)}
          companyId={companyId}
          departments={departments}
          fromId={selected?.id ?? null}
        />
      )}

      {companyId && (
        <NewDepartmentModal
          open={modalOpen}
          onClose={() => setModalOpen(false)}
          companyId={companyId}
          departments={departments}
          defaultParentId={selected?.id ?? null}
        />
      )}
    </div>
  )
}
