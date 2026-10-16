import { useMemo, useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { DataTable, type Column, type SortState } from '@/components/ui/DataTable'
import { LeaveStatusBadge } from '@/components/ui/ModuleBadges'
import { ProgressRing } from '@/components/ui/Progress'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import type { StatusTone } from '@/components/ui/StatusBadge'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { useDirectory } from '@/api/directory'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { leaveApi, type LeaveRequestPageParams } from '@/api/leave'
import { useLeaveBalances, useMyEmployeeId } from '@/api/queries'
import {
  leaveStatusLabels,
  leaveTypeLabels,
  type LeaveBalance,
  type LeaveRequest,
  type LeaveStatus,
} from '@/api/types'
import { formatDate, formatNumber, normalizeSearch } from '@/lib/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { NewBalanceModal } from './NewBalanceModal'
import { HolidaysModal } from './HolidaysModal'
import { StatutoryModal } from './StatutoryModal'
import { LeaveSettingsModal } from './LeaveSettingsModal'
import { NewLeaveRequestModal } from './NewLeaveRequestModal'
import { TeamCalendar } from './TeamCalendar'
import { BridgeDays } from './BridgeDays'
import { CancelApprovedLeaveModal } from './CancelApprovedLeaveModal'
import { OfflineDrafts } from '@/components/OfflineDrafts'
import { useNewParam } from '@/lib/useNewParam'
import { tx, appLocale } from '@/lib/i18n'

/** Dar ekranda (tarih sütunu gizliyken) tür hücresinin altında gösterilen kısa aralık: "05.10–07.10". */
function shortRange(start: string, end: string): string {
  const f = new Intl.DateTimeFormat(appLocale, { day: '2-digit', month: '2-digit' })
  const a = new Date(start)
  const b = new Date(end)
  if (Number.isNaN(a.getTime()) || Number.isNaN(b.getTime())) return ''
  return start.slice(0, 10) === end.slice(0, 10) ? f.format(a) : `${f.format(a)}–${f.format(b)}`
}

type TabKey = LeaveStatus | 'all'

const PAGE_SIZE = 10
/** Tablo sütunu → sunucu sıralama alanı (G24). */
const SERVER_SORT: Record<string, LeaveRequestPageParams['sort']> = {
  type: 'type',
  range: 'startDate',
  days: 'days',
  status: 'status',
}

const TABS: Array<TabDef<TabKey>> = [
  { key: 'Submitted', label: leaveStatusLabels.Submitted },
  { key: 'Approved', label: leaveStatusLabels.Approved },
  { key: 'Rejected', label: leaveStatusLabels.Rejected },
  { key: 'Cancelled', label: tx('İptal') },
  { key: 'all', label: tx('Tümü') },
]

/** Bakiye halkası: kalan gün vurgulu, kullanılan ve onaydaki ayrı okunur. */
function BalanceCard({ balance }: { balance: LeaveBalance }) {
  const tone: StatusTone =
    balance.remainingDays <= 0 ? 'danger' : balance.remainingDays <= 3 ? 'warning' : 'success'

  return (
    <div className="flex items-center gap-4 rounded-lg border border-border p-4">
      <ProgressRing
        value={balance.remainingDays}
        max={balance.entitledDays || 1}
        tone={tone}
        label={tx('{0} kalan gün', [leaveTypeLabels[balance.type]])}
      >
        <span className="tabular text-[19px] leading-none font-bold">
          {formatNumber(balance.remainingDays)}
        </span>
        <span className="text-[10px] text-muted-foreground">{tx('gün')}</span>
      </ProgressRing>

      <div className="min-w-0">
        <p className="text-[14px] font-semibold">{leaveTypeLabels[balance.type]}</p>
        <dl className="mt-1.5 space-y-0.5 text-[12px] text-muted-foreground">
          <div className="flex gap-2">
            <dt>{tx('Hak edilen')}</dt>
            <dd className="tabular font-medium text-foreground">
              {formatNumber(balance.entitledDays)}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt>{tx('Kullanılan')}</dt>
            <dd className="tabular font-medium text-foreground">
              {formatNumber(balance.usedDays)}
            </dd>
          </div>
          {balance.pendingDays > 0 && (
            <div className="flex gap-2">
              <dt>{tx('Onayda')}</dt>
              <dd className="tabular font-medium text-[hsl(var(--warning))]">
                {formatNumber(balance.pendingDays)}
              </dd>
            </div>
          )}
        </dl>
      </div>
    </div>
  )
}

export function LeavePage() {
  const { can, roles } = useAuth()
  const { employeeId: myEmployeeId } = useMyEmployeeId()
  // İptal backend'de yalnızca talep sahibine ve İK'ya açık.
  const hr = isHr(roles)
  const canCancel = (r: { employeeId: string }) => hr || r.employeeId === myEmployeeId
  const confirm = useConfirm()
  // Madde 44: onaylı, henüz başlamamış izin — İK/yönetici iptal eder, çalışan iptal ister.
  const isManager = hr || roles.includes('manager')
  const todayIso = (() => {
    const d = new Date()
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
  })()
  const approvedFuture = (r: LeaveRequest) => r.status === 'Approved' && r.startDate.slice(0, 10) > todayIso
  const [approvedCancel, setApprovedCancel] = useState<{ request: LeaveRequest; mode: 'cancel' | 'request' } | null>(null)
  // İK listede herkesin, yönetici ekibinin talebini görür: kimin talebi olduğu dizin
  // önbelleğinden (yalnızca ad) çözülür.
  const showWho = hr || roles.includes('manager')
  const directory = useDirectory(showWho)
  const nameOf = useMemo(() => {
    const m = new Map((directory.data ?? []).map((d) => [d.id, d.fullName]))
    return (id: string) => m.get(id) ?? '—'
  }, [directory.data])
  const toast = useToast()
  const queryClient = useQueryClient()
  const [tab, setTab] = useTabParam<TabKey>('durum', 'Submitted')
  const [employeeId, setEmployeeId] = useState('')
  const [modalOpen, setModalOpen] = useState(false)
  const [prefill, setPrefill] = useState<{ start: string; end: string } | null>(null)
  const [balanceOpen, setBalanceOpen] = useState(false)
  useNewParam(can('leave:create'), () => setModalOpen(true))
  const [holidaysOpen, setHolidaysOpen] = useState(false)
  const [statutoryOpen, setStatutoryOpen] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)

  const year = new Date().getFullYear()
  const balances = useLeaveBalances(employeeId || undefined, year, Boolean(employeeId))
  // Sunucu tarafı sayfalama (G24): talepler sayfa sayfa gelir; arama ve sıralama sunucuda.
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<SortState | null>(null)
  const query = useDebouncedValue(search.trim(), 300)
  const [prevFilter, setPrevFilter] = useState(`${tab}|${employeeId}`)
  if (prevFilter !== `${tab}|${employeeId}`) {
    setPrevFilter(`${tab}|${employeeId}`)
    setPage(1)
  }
  // İzin türü adları arayüzde çevrildiğinden, aramayla eşleşen türler sunucuya ayrıca gönderilir.
  const qTypes = useMemo(() => {
    if (!query) return undefined
    const needle = normalizeSearch(query)
    const types = Object.entries(leaveTypeLabels)
      .filter(([, label]) => normalizeSearch(label).includes(needle))
      .map(([key]) => key)
    return types.length ? types.join(',') : undefined
  }, [query])
  const params: Omit<LeaveRequestPageParams, 'page' | 'pageSize'> = {
    employeeId: employeeId || undefined,
    status: tab === 'all' ? undefined : tab,
    q: query || undefined,
    qTypes,
    sort: sort ? SERVER_SORT[sort.columnId] : undefined,
    dir: sort?.dir,
  }
  const requests = useQuery({
    queryKey: ['leave', 'requests', 'page', page, params],
    queryFn: ({ signal }) => leaveApi.pageRequests({ ...params, page, pageSize: PAGE_SIZE }, signal),
    placeholderData: keepPreviousData,
  })
  const fetchAll = async () => {
    const all: LeaveRequest[] = []
    for (let p = 1; p <= 100; p++) {
      const res = await leaveApi.pageRequests({ ...params, page: p, pageSize: 200 })
      all.push(...res.items)
      if (all.length >= res.total || res.items.length === 0) break
    }
    return all
  }

  const cancel = useMutation({
    mutationFn: (id: string) => leaveApi.cancelRequest(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['leave'] })
      toast.ok(tx('Talep iptal edildi'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Talep iptal edilemedi.')),
  })

  const rows = requests.data?.items

  const columns: Array<Column<LeaveRequest>> = [
    ...(showWho
      ? [{
          id: 'employee',
          header: tx('Çalışan'),
          searchText: (r: LeaveRequest) => nameOf(r.employeeId),
          cell: (r: LeaveRequest) => <span className="font-medium text-foreground">{nameOf(r.employeeId)}</span>,
        } satisfies Column<LeaveRequest>]
      : []),
    {
      id: 'type',
      header: tx('İzin türü'),
      searchText: (r) => `${leaveTypeLabels[r.type]} ${r.reason ?? ''}`,
      sortValue: (r) => leaveTypeLabels[r.type],
      cell: (r) => (
        <div className="min-w-0">
          <p className="font-medium text-foreground">{leaveTypeLabels[r.type]}</p>
          {r.reason && <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{r.reason}</p>}
          {/* Mobilde tarih sütunu gizli: kısa tarih burada. */}
          <p className="tabular mt-0.5 text-[12px] text-muted-foreground sm:hidden">{shortRange(r.startDate, r.endDate)}</p>
        </div>
      ),
    },
    {
      id: 'range',
      header: tx('Tarih aralığı'),
      hideBelow: 'sm',
      sortValue: (r) => new Date(r.startDate).getTime(),
      exportText: (r) => `${formatDate(r.startDate)} – ${formatDate(r.endDate)}`,
      cell: (r) => (
        <span className="tabular text-muted-foreground">
          {formatDate(r.startDate)} – {formatDate(r.endDate)}
        </span>
      ),
    },
    {
      id: 'days',
      header: tx('Gün'),
      align: 'right',
      sortValue: (r) => r.days,
      // CSV'de de arayüzle aynı yerel ondalık ("0,4"; ayırıcı ";" olduğundan çakışmaz).
      exportText: (r) => formatNumber(r.days),
      // Kısmi gün izni: saat ya da "yarım gün" ayrıca gösterilir.
      cell: (r) => r.hours ? `${formatNumber(r.days)} (${tx('{0} sa', [formatNumber(r.hours)])})`
        : r.days > 0 && r.days < 1 && r.startDate === r.endDate ? `${formatNumber(r.days)} (${tx('yarım gün')})` : formatNumber(r.days),
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (r) => leaveStatusLabels[r.status],
      exportText: (r) => leaveStatusLabels[r.status],
      cell: (r) =>
        r.status === 'Submitted' && !r.workflowRequestId ? (
          // Onay akışı açılamadı (ör. departman başı atanmamış ya da talep sahibi kendisi):
          // talep kimsenin Onay kutusuna düşmez, İK sonuçlandırır.
          <div className="flex flex-col items-end gap-0.5">
            <LeaveStatusBadge status={r.status} />
            <span className="text-[11.5px] text-[hsl(var(--warning))]" title={tx('Onay akışı açılamadı; talebi İK sonuçlandırır.')}>
              {tx('Onaycı bulunamadı')}
            </span>
          </div>
        ) : (
          <LeaveStatusBadge status={r.status} />
        ),
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('İzin')}
        description={tx('İzin talepleri ve yıllık bakiyeler. Onay, Onay kutusu üzerinden ilerler.')}
        actions={
          <>
            {can('leave:manageBalance') && (
              <Button variant="outline" className="cursor-pointer" onClick={() => setHolidaysOpen(true)}>
                {tx('Resmi tatiller')}
              </Button>
            )}
            {can('leave:manageBalance') && (
              <Button variant="outline" className="cursor-pointer" onClick={() => setStatutoryOpen(true)}>
                {tx('Yasal hak ve devir')}
              </Button>
            )}
            {can('leave:manageBalance') && (
              <Button variant="outline" className="cursor-pointer" onClick={() => setSettingsOpen(true)}>
                {tx('İzin ayarları')}
              </Button>
            )}
            {can('leave:manageBalance') && (
              <Button
                variant="outline"
                className="cursor-pointer"
                onClick={() => setBalanceOpen(true)}
              >
                <Plus className="size-4" />
                {tx('Bakiye tanımla')}
              </Button>
            )}
            {can('leave:create') && (
              <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
                <Plus className="size-4" />
                {tx('Yeni izin talebi')}
              </Button>
            )}
          </>
        }
      />

      <OfflineDrafts kind="leave" />

      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_380px]">
        <TeamCalendar />
        <BridgeDays onPick={can('leave:create') ? (s) => { setPrefill({ start: s.leaveDates[0], end: s.leaveDates[s.leaveDates.length - 1] }); setModalOpen(true) } : undefined} />
      </div>

      <div className="max-w-sm">
        <EmployeePicker
          value={employeeId}
          onChange={setEmployeeId}
          hint={tx('Bakiyeleri görmek için çalışan seçin.')}
        />
      </div>

      {employeeId && (
        <Panel>
          <PanelHead title={tx('{0} bakiyeleri', [year])} note={tx('Kalan gün halkanın içinde')} />
          {balances.isPending ? (
            <RowsSkeleton rows={2} columns={3} />
          ) : balances.isError ? (
            <ErrorState
              message={balances.error instanceof Error ? balances.error.message : undefined}
              onRetry={() => void balances.refetch()}
            />
          ) : (balances.data?.length ?? 0) === 0 ? (
            <EmptyState
              title={tx('Bakiye tanımlı değil')}
              detail={tx('Bu çalışan için bu yıla ait izin bakiyesi girilmemiş.')}
              action={
                can('leave:manageBalance') ? (
                  <Button size="sm" className="cursor-pointer" onClick={() => setBalanceOpen(true)}>
                    {tx('Bakiye tanımla')}
                  </Button>
                ) : undefined
              }
            />
          ) : (
            <PanelBody>
              <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                {balances.data!.map((b) => (
                  <BalanceCard key={b.id} balance={b} />
                ))}
              </div>
            </PanelBody>
          )}
        </Panel>
      )}

      <Tabs tabs={TABS} value={tab} onChange={setTab} label={tx('İzin talebi durumu')} />

      <DataTable
        rows={rows}
        rowKey={(r) => r.id}
        columns={columns}
        isLoading={requests.isPending}
        error={requests.error}
        onRetry={() => void requests.refetch()}
        searchPlaceholder={tx('İzin türü veya gerekçe ara')}
        exportFileName="izin-talepleri"
        viewKey="leave-requests"
        pageSize={PAGE_SIZE}
        server={{
          total: requests.data?.total ?? 0,
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
          fetching: requests.isPlaceholderData,
        }}
        emptyTitle={tx('Bu durumda izin talebi yok')}
        emptyDetail={
          employeeId
            ? tx('Seçili çalışan için bu durumda kayıt bulunmuyor.')
            : tx('Başka bir durum sekmesi seçin ya da yeni bir talep oluşturun.')
        }
        rowActions={[
          {
            label: tx('Talebi iptal et'),
            destructive: true,
            hidden: (r) =>
              !(r.status === 'Submitted' || r.status === 'Draft') || cancel.isPending || !canCancel(r),
            onSelect: async (r) => {
              if (await confirm({
                title: tx('İzin talebi iptal edilsin mi?'),
                note: tx('{0} · {1} – {2}. Bekleyen onay akışı kapanır ve ayrılan gün bakiyeye geri döner.', [leaveTypeLabels[r.type], formatDate(r.startDate), formatDate(r.endDate)]),
                action: tx('Talebi iptal et'),
              })) cancel.mutate(r.id)
            },
          },
          {
            label: tx('Onaylı izni iptal et'),
            destructive: true,
            hidden: (r) => !approvedFuture(r) || !isManager || r.employeeId === myEmployeeId,
            onSelect: (r) => setApprovedCancel({ request: r, mode: 'cancel' }),
          },
          {
            label: tx('İzin iptali iste'),
            hidden: (r) => !approvedFuture(r) || r.employeeId !== myEmployeeId,
            onSelect: (r) => setApprovedCancel({ request: r, mode: 'request' }),
          },
        ]}
        notice={
          <InfoNote>
            <strong className="font-semibold text-foreground">
              {tx('Onay bu ekrandan verilmez.')}
            </strong>{' '}{tx('Talep gönderilince onay zinciri', [])}{' '}<em>{tx('Onay kutusu')}</em>{' '}{hr
              ? tx('üzerinden ilerler; onaylandığında izin kaydı ve bakiye kendiliğinden güncellenir. Bekleyen talepleri buradan iptal edebilirsiniz.')
              : tx('üzerinden ilerler; onaylandığında izin kaydı ve bakiye kendiliğinden güncellenir. Buradan yalnızca kendi bekleyen talebinizi iptal edebilirsiniz.')}
          </InfoNote>
        }
      />

      <CancelApprovedLeaveModal
        request={approvedCancel?.request ?? null}
        mode={approvedCancel?.mode ?? 'cancel'}
        onClose={() => setApprovedCancel(null)}
      />

      <NewLeaveRequestModal
        open={modalOpen}
        onClose={() => { setModalOpen(false); setPrefill(null) }}
        defaultEmployeeId={employeeId}
        defaultStart={prefill?.start}
        defaultEnd={prefill?.end}
      />

      <HolidaysModal open={holidaysOpen} onClose={() => setHolidaysOpen(false)} year={year} />
      {statutoryOpen && <StatutoryModal onClose={() => setStatutoryOpen(false)} />}
      {settingsOpen && <LeaveSettingsModal onClose={() => setSettingsOpen(false)} />}

      <NewBalanceModal
        open={balanceOpen}
        onClose={() => setBalanceOpen(false)}
        defaultEmployeeId={employeeId}
        defaultYear={year}
      />
    </div>
  )
}
