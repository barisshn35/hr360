import { useMemo, useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle, Plus, Printer, ScanLine } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DataTable, type Column, type SortState } from '@/components/ui/data-table'
import { AssetStatusBadge } from '@/components/ui/module-badges'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/tabs'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { EmployeePicker } from '@/components/ui/employee-picker'
import { useToast } from '@/components/ui/toast'
import { useConfirm } from '@/components/ui/confirm'
import { useAuth } from '@/auth/useAuth'
import { onboardingApi, type AssetPageParams } from '@/api/onboarding'
import { useEmployees } from '@/api/queries'
import {
  assetStatusLabels,
  assetTypeLabels,
  type Asset,
  type AssetStatus,
  type AssetType,
} from '@/api/types'
import { formatDate, fullName, normalizeSearch } from '@/lib/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { localISODate } from '@/lib/dates'
import { tx } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { AssetDuePanels, ExpectedReturnModal, MaintenanceModal, QrLabelsModal } from './AssetOps'

type TabKey = AssetStatus | 'all'

const PAGE_SIZE = 12
/** Sunucunun sıralayabildiği sütunlar (zimmetli kişinin adı employee-service'te). */
const SERVER_SORT: Record<string, AssetPageParams['sort']> = { tag: 'tag', type: 'type', status: 'status' }

const TABS: Array<TabDef<TabKey>> = [
  { key: 'all', label: tx('Tümü') },
  { key: 'Available', label: tx('Boşta') },
  { key: 'Assigned', label: tx('Zimmetli') },
  { key: 'Maintenance', label: tx('Bakımda') },
  { key: 'Retired', label: tx('Hurda') },
  { key: 'Lost', label: tx('Kayıp') },
]

function NewAssetModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [assetTag, setAssetTag] = useState('')
  const [type, setType] = useState<AssetType>('Laptop')
  const [model, setModel] = useState('')
  const [serialNumber, setSerial] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      onboardingApi.createAsset({
        assetTag: assetTag.trim(),
        type,
        model: model.trim() || undefined,
        serialNumber: serialNumber.trim() || undefined,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(tx('Zimmet kaydı oluşturuldu'))
      onClose()
      setAssetTag('')
      setModel('')
      setSerial('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kayıt oluşturulamadı.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (assetTag.trim().length < 2) return setError(tx('Demirbaş no en az 2 karakter olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni demirbaş')}
      note={tx('Kayıt boşta olarak açılır; zimmetlemek ayrı bir adım.')}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="new-asset"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      <form id="new-asset" onSubmit={submit} noValidate className="space-y-4">
        <TextField
          id="asset-tag"
          label={tx('Demirbaş no')}
          required
          value={assetTag}
          onChange={(e) => setAssetTag(e.target.value)}
          error={error}
        />
        <SelectField
          id="asset-type"
          label={tx('Tür')}
          value={type}
          onChange={(v) => setType(v as AssetType)}
          options={(Object.keys(assetTypeLabels) as AssetType[]).map((t) => ({
            value: t,
            label: assetTypeLabels[t],
          }))}
        />
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="asset-model"
            label={tx('Model')}
            hint={tx('İsteğe bağlı')}
            value={model}
            onChange={(e) => setModel(e.target.value)}
          />
          <TextField
            id="asset-serial"
            label={tx('Seri no')}
            hint={tx('İsteğe bağlı')}
            className="tabular"
            value={serialNumber}
            onChange={(e) => setSerial(e.target.value)}
          />
        </div>
      </form>
    </Modal>
  )
}

/** Demirbaş bilgisini düzeltme (etiket, tür, model, seri no). Durum ayrı pencereden değişir. */
function EditAssetModal({ asset, onClose }: { asset: Asset; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [assetTag, setAssetTag] = useState(asset.assetTag)
  const [type, setType] = useState<AssetType>(asset.type)
  const [model, setModel] = useState(asset.model ?? '')
  const [serialNumber, setSerial] = useState(asset.serialNumber ?? '')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      onboardingApi.updateAsset(asset.id, {
        assetTag: assetTag.trim(),
        type,
        model: model.trim() || undefined,
        serialNumber: serialNumber.trim() || undefined,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(tx('Demirbaş güncellendi'))
      onClose()
    },
    // Etiket çakışması (409) alanın altında gösterilir; pencere açık kalır.
    onError: (e: unknown) => setError(e instanceof Error ? e.message : tx('Kaydedilemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (assetTag.trim().length < 2) return setError(tx('Demirbaş no en az 2 karakter olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Demirbaşı düzenle')}
      note={`${asset.assetTag}, ${assetStatusLabels[asset.status]}`}
      footer={
        <>
          <Button variant="outline" className="cursor-pointer" onClick={onClose} disabled={mutation.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button type="submit" form="edit-asset" className="cursor-pointer" disabled={mutation.isPending}>
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      <form id="edit-asset" onSubmit={submit} noValidate className="space-y-4">
        <TextField id="edit-asset-tag" label={tx('Demirbaş no')} required value={assetTag} onChange={(e) => setAssetTag(e.target.value)} error={error} />
        <SelectField
          id="edit-asset-type"
          label={tx('Tür')}
          value={type}
          onChange={(v) => setType(v as AssetType)}
          options={(Object.keys(assetTypeLabels) as AssetType[]).map((t) => ({ value: t, label: assetTypeLabels[t] }))}
        />
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField id="edit-asset-model" label={tx('Model')} hint={tx('İsteğe bağlı')} value={model} onChange={(e) => setModel(e.target.value)} />
          <TextField id="edit-asset-serial" label={tx('Seri no')} hint={tx('İsteğe bağlı')} className="tabular" value={serialNumber} onChange={(e) => setSerial(e.target.value)} />
        </div>
      </form>
    </Modal>
  )
}

type ManualStatus = Exclude<AssetStatus, 'Assigned'>
const MANUAL_STATUSES: ManualStatus[] = ['Available', 'Maintenance', 'Retired', 'Lost']

/**
 * Elle durum değişikliği: Boşta ↔ Bakımda / Hurda / Kayıp. Zimmetli demirbaşta menüde görünmez
 * (önce iade alınır); sunucu da 409 ile engeller.
 */
function StatusModal({ asset, onClose }: { asset: Asset; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const options = MANUAL_STATUSES.filter((s) => s !== asset.status)
  const [status, setStatus] = useState<ManualStatus>(options[0])
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () => onboardingApi.setAssetStatus(asset.id, status, note.trim() || undefined),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(tx('Durum değiştirildi: {0}', [assetStatusLabels[status]]))
      onClose()
    },
    onError: (e: unknown) => setError(e instanceof Error ? e.message : tx('Durum değiştirilemedi.')),
  })

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Durum değiştir')}
      note={tx('{0} · şu an {1}', [asset.assetTag, assetStatusLabels[asset.status]])}
      footer={
        <>
          <Button variant="outline" className="cursor-pointer" onClick={onClose} disabled={mutation.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button className="cursor-pointer" disabled={mutation.isPending || note.length > 500} onClick={() => mutation.mutate()}>
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <SelectField
          id="asset-status"
          label={tx('Yeni durum')}
          value={status}
          onChange={(v) => setStatus(v as ManualStatus)}
          options={options.map((s) => ({ value: s, label: assetStatusLabels[s] }))}
        />
        <TextAreaField
          id="asset-status-note"
          label={tx('Açıklama')}
          rows={2}
          hint={tx('İsteğe bağlı; denetim kaydına yazılır.')}
          value={note}
          maxLength={500}
          onChange={(e) => setNote(e.target.value)}
        />
        {error && <p role="alert" className="text-[13px] text-destructive">{error}</p>}
      </div>
    </Modal>
  )
}

function AssignModal({ asset, onClose }: { asset: Asset | null; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [employeeId, setEmployeeId] = useState('')
  const [assignedOn, setAssignedOn] = useState(localISODate())
  const [expectedReturnOn, setExpectedReturnOn] = useState('')
  const [notes, setNotes] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      onboardingApi.assignAsset(asset!.id, {
        employeeId,
        assignedOn,
        notes: notes.trim() || undefined,
        expectedReturnOn: expectedReturnOn || undefined,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(tx('Zimmet atandı'))
      onClose()
      setEmployeeId('')
      setNotes('')
      setExpectedReturnOn('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Zimmet atanamadı.')),
  })

  if (!asset) return null

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!employeeId) return setError(tx('Çalışan seçilmeli.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Zimmet ata')}
      note={`${asset.assetTag}, ${assetTypeLabels[asset.type]}`}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="assign-asset"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Ata')}
          </Button>
        </>
      }
    >
      <form id="assign-asset" onSubmit={submit} noValidate className="space-y-4">
        <EmployeePicker
          id="assign-employee"
          value={employeeId}
          onChange={setEmployeeId}
          hint={error}
        />
        <TextField
          id="assign-date"
          label={tx('Zimmet tarihi')}
          type="date"
          required
          value={assignedOn}
          onChange={(e) => setAssignedOn(e.target.value)}
        />
        <TextField
          id="assign-expected"
          label={tx('Beklenen iade tarihi')}
          type="date"
          hint={tx('İsteğe bağlı. Tarihten önce ve geçince hatırlatma gider.')}
          value={expectedReturnOn}
          onChange={(e) => setExpectedReturnOn(e.target.value)}
        />
        <TextAreaField
          id="assign-notes"
          label={tx('Not')}
          rows={2}
          hint={tx('İsteğe bağlı')}
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
        />
      </form>
    </Modal>
  )
}

function ReturnModal({ asset, onClose }: { asset: Asset | null; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [returnedOn, setReturnedOn] = useState(localISODate())
  const [condition, setCondition] = useState('')
  const [markAsRetired, setRetired] = useState(false)

  const mutation = useMutation({
    mutationFn: () =>
      onboardingApi.returnAsset(asset!.id, {
        returnedOn,
        condition: condition.trim() || undefined,
        markAsRetired,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(markAsRetired ? tx('İade alındı, hurdaya ayrıldı') : tx('İade alındı'))
      onClose()
      setCondition('')
      setRetired(false)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İade kaydedilemedi.')),
  })

  if (!asset) return null

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('İade al')}
      note={`${asset.assetTag}, ${assetTypeLabels[asset.type]}`}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            className="cursor-pointer"
            disabled={mutation.isPending}
            onClick={() => mutation.mutate()}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('İadeyi kaydet')}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <TextField
          id="return-date"
          label={tx('İade tarihi')}
          type="date"
          required
          value={returnedOn}
          onChange={(e) => setReturnedOn(e.target.value)}
        />
        <TextField
          id="return-condition"
          label={tx('Durum')}
          hint={tx('İsteğe bağlı. Örn. çizik var, kutusu eksik.')}
          value={condition}
          onChange={(e) => setCondition(e.target.value)}
        />
        <label className="flex min-h-11 cursor-pointer items-start gap-2.5">
          <Checkbox
            checked={markAsRetired}
            onCheckedChange={(v) => setRetired(v === true)}
            className="mt-0.5"
          />
          <span className="text-[13px]">
            {tx('Hurdaya ayır')}
            <span className="block text-[12px] text-muted-foreground">
              {tx('İşaretlenmezse demirbaş yeniden boşta görünür.')}
            </span>
          </span>
        </label>
      </div>
    </Modal>
  )
}

export function AssetsPage() {
  const { can } = useAuth()
  const [tab, setTab] = useTabParam<TabKey>('durum', 'all')
  const [newOpen, setNewOpen] = useState(false)
  const [assignFor, setAssignFor] = useState<Asset | null>(null)
  const [returnFor, setReturnFor] = useState<Asset | null>(null)
  const [labelsFor, setLabelsFor] = useState<Asset[] | null>(null)
  const [maintFor, setMaintFor] = useState<Asset | null>(null)
  const [expFor, setExpFor] = useState<Asset | null>(null)
  const [editFor, setEditFor] = useState<Asset | null>(null)
  const [statusFor, setStatusFor] = useState<Asset | null>(null)
  const toast = useToast()
  const confirm = useConfirm()
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: (a: Asset) => onboardingApi.deleteAsset(a.id),
    onSuccess: (_, a) => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(tx('{0} silindi', [a.assetTag]))
    },
    // 409: zimmet/bakım geçmişi var — sunucu "hurdaya ayırın" önerir.
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Demirbaş silinemedi.')),
  })
  const askDelete = async (a: Asset) => {
    const ok = await confirm({
      title: tx('{0} silinsin mi?', [a.assetTag]),
      note: tx('Yalnızca hiç zimmetlenmemiş ve bakım kaydı olmayan (ör. yanlış açılmış) demirbaş silinebilir. Geçmişi olan demirbaşı hurdaya ayırın. Silme geri alınamaz.'),
      action: tx('Sil'),
    })
    if (ok) remove.mutate(a)
  }

  const employees = useEmployees({ enabled: can('employee:viewAll') })

  const canManage = can('asset:manage')

  const nameOf = useMemo(() => {
    const map = new Map<string, string>()
    for (const e of employees.data ?? []) map.set(e.id, fullName(e))
    return (id: string) => map.get(id) ?? `${id.slice(0, 8)}…`
  }, [employees.data])

  // Sunucu tarafı sayfalama: envanter sayfa sayfa gelir; arama ve sıralama sunucuda.
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<SortState | null>(null)
  const query = useDebouncedValue(search.trim(), 300)
  const [prevTab, setPrevTab] = useState(tab)
  if (prevTab !== tab) {
    setPrevTab(tab)
    setPage(1)
  }
  // Tür adları arayüzde çevrilir, kişi adları employee-service'te: eşleşenler sunucuya ayrıca gönderilir.
  const qTypes = useMemo(() => {
    if (!query) return undefined
    const needle = normalizeSearch(query)
    const types = Object.entries(assetTypeLabels).filter(([, label]) => normalizeSearch(label).includes(needle)).map(([key]) => key)
    return types.length ? types.join(',') : undefined
  }, [query])
  const qEmployeeIds = useMemo(() => {
    if (!query) return undefined
    const needle = normalizeSearch(query)
    const ids = (employees.data ?? []).filter((e) => normalizeSearch(fullName(e)).includes(needle)).map((e) => e.id)
    return ids.length ? ids.slice(0, 200).join(',') : undefined
  }, [query, employees.data])
  const params: Omit<AssetPageParams, 'page' | 'pageSize'> = {
    status: tab === 'all' ? undefined : tab,
    q: query || undefined,
    qTypes,
    qEmployeeIds,
    sort: sort ? SERVER_SORT[sort.columnId] : undefined,
    dir: sort?.dir,
  }
  const assets = useQuery({
    queryKey: ['onboarding', 'assets', 'page', page, params],
    queryFn: ({ signal }) => onboardingApi.pageAssets({ ...params, page, pageSize: PAGE_SIZE }, signal),
    placeholderData: keepPreviousData,
  })
  const total = assets.data?.total ?? 0

  /** CSV ve toplu etiket: filtreye uyan tüm kayıtlar, 200'lük sayfalarla. */
  const fetchAll = async () => {
    const all: Asset[] = []
    for (let p = 1; p <= 100; p++) {
      const res = await onboardingApi.pageAssets({ ...params, page: p, pageSize: 200 })
      all.push(...res.items)
      if (all.length >= res.total || res.items.length === 0) break
    }
    return all
  }
  const [labelsLoading, setLabelsLoading] = useState(false)
  const printAll = async () => {
    setLabelsLoading(true)
    try {
      setLabelsFor(await fetchAll())
    } catch (e) {
      toast.stop(e instanceof Error ? e.message : tx('Demirbaşlar yüklenemedi.'))
    } finally {
      setLabelsLoading(false)
    }
  }

  const columns: Array<Column<Asset>> = [
    {
      id: 'tag',
      header: tx('Demirbaş'),
      searchText: (a) => `${a.assetTag} ${assetTypeLabels[a.type]} ${a.model ?? ''}`,
      sortValue: (a) => a.assetTag,
      exportText: (a) => a.assetTag,
      cell: (a) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">{a.assetTag}</p>
          <p className="mt-0.5 truncate text-[12px] text-muted-foreground">
            {assetTypeLabels[a.type]}
            {a.model ? `, ${a.model}` : ''}
          </p>
        </div>
      ),
    },
    {
      id: 'type',
      header: tx('Tür'),
      hideBelow: 'lg',
      sortValue: (a) => assetTypeLabels[a.type] ?? '',
      exportText: (a) => assetTypeLabels[a.type] ?? '',
      cell: (a) => <span className="text-muted-foreground">{assetTypeLabels[a.type]}</span>,
    },
    {
      id: 'serial',
      header: tx('Seri no'),
      hideBelow: 'lg',
      searchText: (a) => a.serialNumber ?? '',
      exportText: (a) => a.serialNumber ?? '—',
      cell: (a) => <span className="tabular text-muted-foreground">{a.serialNumber ?? '—'}</span>,
    },
    {
      id: 'holder',
      header: tx('Zimmetli'),
      hideBelow: 'md',
      searchText: (a) => (a.assignedEmployeeId ? nameOf(a.assignedEmployeeId) : ''),
      sortValue: (a) => (a.assignedEmployeeId ? nameOf(a.assignedEmployeeId) : ''),
      exportText: (a) => (a.assignedEmployeeId ? nameOf(a.assignedEmployeeId) : '—'),
      cell: (a) =>
        a.assignedEmployeeId ? (
          <div className="min-w-0">
            <p className="truncate">{nameOf(a.assignedEmployeeId)}</p>
            {a.assignedOn && (
              <p className="tabular text-[12px] text-muted-foreground">
                {formatDate(a.assignedOn)}
              </p>
            )}
          </div>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (a) => assetStatusLabels[a.status] ?? '',
      exportText: (a) => assetStatusLabels[a.status] ?? '',
      cell: (a) => <AssetStatusBadge status={a.status} />,
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Zimmet')}
        description={tx('Demirbaş envanteri, atama ve iade kayıtları.')}
        actions={
          <>
            <Button variant="outline" className="cursor-pointer" asChild>
              <Link to="/panel/zimmet/tara"><ScanLine className="size-4" />{tx('QR okut')}</Link>
            </Button>
            {canManage && (
              <Button variant="outline" className="cursor-pointer" onClick={() => void printAll()} disabled={total === 0 || labelsLoading}>
                {labelsLoading ? <LoaderCircle className="size-4 animate-spin" /> : <Printer className="size-4" />}
                {tx('Etiketleri yazdır')}
              </Button>
            )}
            {canManage && (
              <Button className="cursor-pointer" onClick={() => setNewOpen(true)}>
                <Plus className="size-4" />
                {tx('Yeni demirbaş')}
              </Button>
            )}
          </>
        }
      />

      <Tabs tabs={TABS} value={tab} onChange={setTab} label={tx('Zimmet durumu')} />

      <DataTable
        rows={assets.data?.items}
        rowKey={(a) => a.id}
        columns={columns}
        isLoading={assets.isPending}
        error={assets.error}
        onRetry={() => void assets.refetch()}
        searchPlaceholder={tx('Demirbaş no, model, seri no veya kişi')}
        exportFileName="zimmet-envanteri"
        pageSize={PAGE_SIZE}
        server={{
          total,
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
          fetching: assets.isPlaceholderData,
        }}
        emptyTitle={tx('Bu durumda demirbaş yok')}
        emptyDetail={canManage ? tx('Başka bir durum sekmesi seçin ya da yeni bir demirbaş ekleyin.') : tx('Başka bir durum sekmesi seçin.')}
        emptyAction={
          canManage ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setNewOpen(true)}>
              {tx('Yeni demirbaş')}
            </Button>
          ) : undefined
        }
        rowActions={
          canManage
            ? [
                {
                  label: tx('Zimmet ata'),
                  hidden: (a) => a.status !== 'Available',
                  onSelect: (a) => setAssignFor(a),
                },
                {
                  label: tx('İade al'),
                  hidden: (a) => a.status !== 'Assigned',
                  onSelect: (a) => setReturnFor(a),
                },
                {
                  label: tx('Beklenen iade tarihi'),
                  hidden: (a) => a.status !== 'Assigned',
                  onSelect: (a) => setExpFor(a),
                },
                { label: tx('QR etiketi'), onSelect: (a) => setLabelsFor([a]) },
                { label: tx('Bakım kayıtları'), onSelect: (a) => setMaintFor(a) },
                { label: tx('Düzenle'), onSelect: (a) => setEditFor(a) },
                {
                  label: tx('Durum değiştir'),
                  hidden: (a) => a.status === 'Assigned',
                  onSelect: (a) => setStatusFor(a),
                },
                {
                  label: tx('Sil'),
                  destructive: true,
                  hidden: (a) => a.status === 'Assigned',
                  onSelect: (a) => void askDelete(a),
                },
              ]
            : undefined
        }
      />

      {canManage && <AssetDuePanels />}

      <NewAssetModal open={newOpen} onClose={() => setNewOpen(false)} />
      {labelsFor && <QrLabelsModal assets={labelsFor} onClose={() => setLabelsFor(null)} />}
      {maintFor && <MaintenanceModal asset={maintFor} onClose={() => setMaintFor(null)} />}
      {expFor && <ExpectedReturnModal asset={expFor} onClose={() => setExpFor(null)} />}
      {editFor && <EditAssetModal key={editFor.id} asset={editFor} onClose={() => setEditFor(null)} />}
      {statusFor && <StatusModal key={statusFor.id} asset={statusFor} onClose={() => setStatusFor(null)} />}
      <AssignModal asset={assignFor} onClose={() => setAssignFor(null)} />
      <ReturnModal asset={returnFor} onClose={() => setReturnFor(null)} />
    </div>
  )
}
