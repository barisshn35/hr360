import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Plus, FileSpreadsheet } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Button } from '@/components/ui/button'
import { DataTable, type Column, type SortState, type TableFilter } from '@/components/ui/DataTable'
import { EmployeeStatusBadge } from '@/components/ui/ModuleBadges'
import { useAuth } from '@/auth/useAuth'
import { qk, useCompanies } from '@/api/queries'
import { employeeApi, type EmployeePageParams } from '@/api/employees'
import {
  EmployeeStatus,
  employeeStatusLabels,
  type Employee,
  type EmployeeStatusValue,
} from '@/api/types'
import { formatDate, fullName, initialsOf, normalizeSearch } from '@/lib/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { NewEmployeeModal } from './NewEmployeeModal'
import { ImportEmployeesModal } from './ImportEmployeesModal'
import { tx } from '@/lib/i18n'

type StatusFilter = 'all' | `${EmployeeStatusValue}`

const PAGE_SIZE = 12
/** Sunucunun sıralayabildiği sütunlar (departman adları organization-service'te). */
const SERVER_SORT: Record<string, EmployeePageParams['sort']> = { name: 'name', hireDate: 'hireDate', status: 'status' }

export function EmployeeListPage() {
  const { can } = useAuth()
  const navigate = useNavigate()
  const companies = useCompanies({ enabled: can('organization:view') })

  const [status, setStatus] = useState<StatusFilter>('all')
  // Sunucu tarafı sayfalama (G24): binlerce çalışanlı kiracıda tüm liste indirilmez.
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<SortState>({ columnId: 'name', dir: 'asc' })
  const query = useDebouncedValue(search.trim(), 300)
  const [modalOpen, setModalOpen] = useState(false)
  const [importModalOpen, setImportModalOpen] = useState(false)

  const canCreate = can('employee:manage') || can('employee:create')

  const departmentNames = useMemo(() => {
    const map = new Map<string, string>()
    for (const c of companies.data ?? []) {
      for (const d of c.departments ?? []) map.set(d.id, d.name)
    }
    return map
  }, [companies.data])

  const departmentOptions = useMemo(
    () =>
      (companies.data ?? []).flatMap((c) =>
        (c.departments ?? []).map((d) => ({
          id: d.id,
          label: (companies.data?.length ?? 0) > 1 ? `${c.name} · ${d.name}` : d.name,
        })),
      ),
    [companies.data],
  )

  // Departman adına göre arama: adlar organization-service'te, eşleşen kimlikler sunucuya gönderilir.
  const qDepartmentIds = useMemo(() => {
    if (!query) return undefined
    const needle = normalizeSearch(query)
    const ids = [...departmentNames].filter(([, name]) => normalizeSearch(name).includes(needle)).map(([id]) => id)
    return ids.length ? ids.slice(0, 200).join(',') : undefined
  }, [query, departmentNames])

  const params: Omit<EmployeePageParams, 'page' | 'pageSize'> = {
    q: query || undefined,
    qDepartmentIds,
    status: status === 'all' ? undefined : Number(status),
    sort: SERVER_SORT[sort.columnId] ?? 'name',
    dir: sort.dir,
  }
  const employees = useQuery({
    queryKey: [...qk.employees, 'page', page, params],
    queryFn: ({ signal }) => employeeApi.page({ ...params, page, pageSize: PAGE_SIZE }, signal),
    placeholderData: keepPreviousData,
  })
  const rows = employees.data?.items
  const noEmployeesAtAll = employees.data?.total === 0 && !query && status === 'all'

  /** CSV: filtreye uyan tüm kayıtlar, 200'lük sayfalarla. */
  const fetchAll = async () => {
    const all: Employee[] = []
    for (let p = 1; p <= 100; p++) {
      const res = await employeeApi.page({ ...params, page: p, pageSize: 200 })
      all.push(...res.items)
      if (all.length >= res.total || res.items.length === 0) break
    }
    return all
  }

  /** Açık atama varsa en yenisi; yoksa (ayrılan çalışan) en son başlayan atama — ilk kayıt değil. */
  const currentAssignment = (e: Employee) => {
    const list = [...(e.assignments ?? [])].sort(
      (a, b) => new Date(b.effectiveFrom).getTime() - new Date(a.effectiveFrom).getTime(),
    )
    return list.find((a) => !a.effectiveTo) ?? list[0]
  }

  const departmentOf = (e: Employee) => {
    const current = currentAssignment(e)
    if (!current) return tx('Atanmamış')
    return departmentNames.get(current.departmentId) ?? tx('Listede olmayan departman')
  }

  const filters: TableFilter[] = [
    {
      id: 'status',
      label: tx('Durum'),
      value: status,
      onChange: (v) => {
        setStatus(v as StatusFilter)
        setPage(1)
      },
      options: [
        { value: 'all', label: tx('Tüm durumlar') },
        { value: String(EmployeeStatus.Active), label: employeeStatusLabels[0] },
        { value: String(EmployeeStatus.OnLeave), label: employeeStatusLabels[1] },
        { value: String(EmployeeStatus.Terminated), label: employeeStatusLabels[2] },
      ],
    },
  ]

  const columns: Array<Column<Employee>> = [
    {
      id: 'name',
      header: tx('Çalışan'),
      searchText: (e) => `${fullName(e)} ${e.email ?? ''}`,
      sortValue: (e) => fullName(e),
      exportText: (e) => fullName(e),
      cell: (e) => (
        <div className="flex items-center gap-3">
          <Avatar className="size-9 shrink-0">
            <AvatarFallback className="bg-primary/10 text-[12px] font-semibold text-primary">
              {initialsOf(e.firstName, e.lastName)}
            </AvatarFallback>
          </Avatar>
          <div className="min-w-0">
            <p className="truncate font-medium text-foreground">{fullName(e)}</p>
            <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{e.email}</p>
          </div>
        </div>
      ),
    },
    {
      id: 'department',
      header: tx('Departman'),
      hideBelow: 'md',
      searchText: (e) => departmentOf(e),
      sortValue: (e) => departmentOf(e),
      exportText: (e) => departmentOf(e),
      cell: (e) => {
        const current = currentAssignment(e)
        return (
          <div className="min-w-0">
            <p className="truncate">{departmentOf(e)}</p>
            {current?.positionTitle && (
              <p className="truncate text-[12px] text-muted-foreground">{current.positionTitle}</p>
            )}
          </div>
        )
      },
    },
    {
      id: 'position',
      header: tx('Pozisyon'),
      hideBelow: 'lg',
      searchText: (e) => currentAssignment(e)?.positionTitle ?? '',
      sortValue: (e) => currentAssignment(e)?.positionTitle ?? '',
      exportText: (e) => currentAssignment(e)?.positionTitle ?? '—',
      cell: (e) => (
        <span className="text-muted-foreground">
          {currentAssignment(e)?.positionTitle ?? '—'}
        </span>
      ),
    },
    {
      id: 'hireDate',
      header: tx('İşe giriş'),
      align: 'right',
      hideBelow: 'sm',
      sortValue: (e) => new Date(e.hireDate).getTime(),
      exportText: (e) => formatDate(e.hireDate),
      cell: (e) => <span className="text-muted-foreground">{formatDate(e.hireDate)}</span>,
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (e) => employeeStatusLabels[e.status] ?? '',
      exportText: (e) => employeeStatusLabels[e.status] ?? 'Bilinmiyor',
      cell: (e) => <EmployeeStatusBadge status={e.status} />,
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Çalışanlar')}
        description={tx('Kayıtlar, departman atamaları ve çalışma durumu.')}
        actions={
          canCreate && (
            <div className="flex gap-2">
              <Button
                variant="outline"
                className="cursor-pointer"
                onClick={() => setImportModalOpen(true)}
              >
                <FileSpreadsheet className="size-4" />
                {tx('Excel\'den içe aktar')}
              </Button>
              <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
                <Plus className="size-4" />
                {tx('Yeni çalışan')}
              </Button>
            </div>
          )
        }
      />

      <DataTable
        rows={rows}
        rowKey={(e) => e.id}
        columns={columns}
        filters={filters}
        isLoading={employees.isPending}
        error={employees.error}
        onRetry={() => void employees.refetch()}
        onRowClick={(e) => navigate(`/panel/calisanlar/${e.id}`)}
        searchPlaceholder={tx('Ad, soyad, e-posta veya departman')}
        exportFileName="calisanlar"
        viewKey="employees"
        pageSize={PAGE_SIZE}
        server={{
          total: employees.data?.total ?? 0,
          page,
          onPageChange: setPage,
          onQueryChange: (v) => {
            setSearch(v)
            setPage(1)
          },
          sort,
          onSortChange: (next) => {
            setSort(next)
            setPage(1)
          },
          sortable: Object.keys(SERVER_SORT),
          fetchAll,
          fetching: employees.isPlaceholderData,
        }}
        emptyTitle={noEmployeesAtAll ? tx('Çalışan kaydı yok') : tx('Filtreye uyan kayıt yok')}
        emptyDetail={
          noEmployeesAtAll
            ? tx('İlk çalışanı ekleyin; atamasını kayıttan sonra yapabilirsiniz.')
            : tx('Durum filtresini değiştirin ya da aramayı temizleyin.')
        }
        emptyAction={
          canCreate && noEmployeesAtAll ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
              {tx('Yeni çalışan')}
            </Button>
          ) : undefined
        }
      />

      <NewEmployeeModal
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        departments={departmentOptions}
      />
      <ImportEmployeesModal
        open={importModalOpen}
        onClose={() => setImportModalOpen(false)}
        departments={departmentOptions}
      />
    </div>
  )
}
