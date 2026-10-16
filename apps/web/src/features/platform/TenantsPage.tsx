import { useMemo, useState } from 'react'
import { CircleCheck, CircleX, KeyRound, LoaderCircle, Plus, TriangleAlert } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { DataTable, type Column, type TableFilter } from '@/components/ui/DataTable'
import { StatCard, StatCardsSkeleton } from '@/components/ui/StatCard'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Modal } from '@/components/ui/Modal'
import { TextAreaField, TextField } from '@/components/ui/Field'
import { CenteredSpinner, ErrorState } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import {
  useCreateTenant,
  useReactivateTenant,
  useSuspendTenant,
  useTenant,
  useTenants,
} from '@/api/queries-tenant'
import {
  tenantStatusLabels,
  slugify,
  type ProvisioningLogEntry,
  type Tenant,
  type TenantStatus,
} from '@/api/tenant'
import { formatDate, formatDateTime, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'
import { GrantAccessModal, MyGrantsPanel } from './PlatformAccessPanels'
import { SampleDataPanel } from '@/features/settings/SampleDataPanel'
import { useMutation } from '@tanstack/react-query'
import { passwordLinkApi } from '@/api/password-links'
import { usePasswordLinkDialog } from '@/features/access/PasswordLinkCard'

const ALL = '__all__'

const STATUS_TONE: Record<TenantStatus, StatusTone> = {
  Pending: 'warning',
  Active: 'success',
  Suspended: 'danger',
  Cancelled: 'neutral',
}

/* --------------------------- Kurulum (provisioning) --------------------------- */

const STEP_TONE: Record<string, StatusTone> = {
  Succeeded: 'success',
  Failed: 'danger',
  Pending: 'warning',
}

const STEP_LABEL: Record<string, string> = {
  Succeeded: tx('Başarılı'),
  Failed: tx('Başarısız'),
  Pending: tx('Sürüyor'),
}

/**
 * Kayıt sırasında arka planda çalışan adımların zaman çizelgesi.
 *
 * Kiracı `Pending`te takılı kaldığında tek teşhis yolu bu — hangi adımın
 * patladığı ve hata mesajı burada görünür.
 */
function ProvisioningTimeline({ entries }: { entries: ProvisioningLogEntry[] }) {
  if (entries.length === 0) {
    return (
      <p className="text-[13px] leading-relaxed text-muted-foreground">
        {tx('Bu kiracı için kurulum kaydı yok. Kayıt eski bir sürümle yapılmış ya da servis günlük tutmuyor olabilir.')}
      </p>
    )
  }

  return (
    <ol className="relative space-y-5 pl-6">
      <span aria-hidden="true" className="absolute top-2 bottom-2 left-[7px] w-px bg-border" />
      {entries.map((entry, i) => {
        const tone = STEP_TONE[entry.status] ?? 'neutral'
        const Icon =
          entry.status === 'Succeeded'
            ? CircleCheck
            : entry.status === 'Failed'
              ? CircleX
              : LoaderCircle
        return (
          <li key={entry.id ?? `${entry.step}-${i}`} className="relative">
            <span
              aria-hidden="true"
              className={cn(
                'absolute top-0.5 -left-6 flex size-4 items-center justify-center rounded-full bg-card',
                tone === 'success'
                  ? 'text-[hsl(var(--success))]'
                  : tone === 'danger'
                    ? 'text-destructive'
                    : 'text-[hsl(var(--warning))]',
              )}
            >
              <Icon className={cn('size-4', entry.status === 'Pending' && 'animate-spin')} />
            </span>
            <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
              <p className="text-[13px] font-medium">{entry.step}</p>
              <StatusBadge tone={tone}>{STEP_LABEL[entry.status] ?? entry.status}</StatusBadge>
            </div>
            {entry.message && (
              <p
                className={cn(
                  'mt-1 text-[12px] leading-relaxed break-words',
                  tone === 'danger' ? 'text-destructive' : 'text-muted-foreground',
                )}
              >
                {entry.message}
              </p>
            )}
            {(entry.startedAt || entry.completedAt) && (
              <p className="tabular mt-1 text-[11px] text-muted-foreground">
                {entry.startedAt ? formatDateTime(entry.startedAt) : '—'}
                {entry.completedAt ? ` → ${formatDateTime(entry.completedAt)}` : ''}
              </p>
            )}
          </li>
        )
      })}
    </ol>
  )
}

/* --------------------------------- Eylemler --------------------------------- */

function SuspendModal({ tenant, onClose }: { tenant: Tenant | null; onClose: () => void }) {
  const toast = useToast()
  const suspend = useSuspendTenant()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | undefined>()

  if (!tenant) return null

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (reason.trim().length < 5) return setError(tx('Askıya alma gerekçesi en az 5 karakter olmalı.'))
    setError(undefined)
    suspend.mutate(
      { id: tenant!.id, reason: reason.trim() },
      {
        onSuccess: () => {
          toast.ok(tx('{0} askıya alındı', [tenant!.name]))
          setReason('')
          onClose()
        },
        onError: (e2: unknown) =>
          toast.stop(e2 instanceof Error ? e2.message : tx('Kiracı askıya alınamadı.')),
      },
    )
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Kiracıyı askıya al')}
      note={`${tenant.name} (${tenant.slug})`}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={suspend.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="suspend-tenant"
            variant="destructive"
            className="cursor-pointer"
            disabled={suspend.isPending}
          >
            {suspend.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Askıya al')}
          </Button>
        </>
      }
    >
      <form id="suspend-tenant" onSubmit={submit} noValidate className="space-y-4">
        <div className="flex items-start gap-2.5 rounded-lg border border-destructive/30 bg-destructive/5 p-3.5">
          <TriangleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-destructive" />
          <p className="text-[13px] leading-relaxed">
            {tx('Askıya alınan kiracının kullanıcıları oturum açamaz ve verilerine erişemez. Veriler silinmez; yeniden etkinleştirildiğinde erişim geri gelir.')}
          </p>
        </div>
        <TextAreaField
          id="suspend-reason"
          label={tx('Gerekçe')}
          rows={3}
          required
          hint={tx('Kayda geçer; destek görüşmelerinde referans alınır.')}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          error={error}
        />
      </form>
    </Modal>
  )
}

/* ------------------------------ Yeni kiracı ------------------------------ */

/**
 * Platform yöneticisi yeni şirket (kiracı) açar. Herkese açık kayıtla aynı akış: Keycloak
 * organizasyonu ve şirket yöneticisi oluşturulur, yöneticiye parola belirleme e-postası gider.
 * Demo verisi olmayan ve herkese açık kaydın kapalı olduğu kurulumda ilk şirket buradan açılır.
 */
function CreateTenantModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const create = useCreateTenant()
  const linkDialog = usePasswordLinkDialog()
  const [companyName, setCompanyName] = useState('')
  const [adminFullName, setAdminFullName] = useState('')
  const [adminEmail, setAdminEmail] = useState('')
  const [slug, setSlug] = useState('')
  const [error, setError] = useState<string | null>(null)

  if (!open) return null

  const close = () => {
    setCompanyName('')
    setAdminFullName('')
    setAdminEmail('')
    setSlug('')
    setError(null)
    onClose()
  }

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!companyName.trim()) return setError(tx('Şirket adı zorunlu.'))
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(adminEmail.trim())) return setError(tx('Geçerli bir e-posta adresi girin.'))
    setError(null)
    create.mutate(
      {
        companyName: companyName.trim(),
        adminEmail: adminEmail.trim(),
        adminFullName: adminFullName.trim() || undefined,
        slug: slug.trim() || undefined,
      },
      {
        onSuccess: (r) => {
          toast.ok(tx('{0} oluşturuldu.', [r?.companyName ?? companyName]))
          // Şirket yöneticisinin parola linki bir kez gösterilir (e-posta açıksa e-postayla da gider).
          linkDialog.show(r?.passwordLink, adminFullName.trim() || adminEmail.trim())
          close()
        },
        onError: (e2: unknown) => setError(e2 instanceof Error ? e2.message : tx('Kiracı oluşturulamadı.')),
      },
    )
  }

  return (
    <Modal
      open
      onClose={close}
      title={tx('Yeni kiracı')}
      note={tx('Şirket yöneticisi için süreli bir parola linki üretilir; e-posta açıksa e-postayla da gönderilir.')}
      footer={
        <>
          <Button variant="outline" className="cursor-pointer" onClick={close} disabled={create.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button type="submit" form="tenant-create" className="cursor-pointer" disabled={create.isPending}>
            {create.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kiracı oluştur')}
          </Button>
        </>
      }
    >
      <form id="tenant-create" onSubmit={submit} noValidate className="space-y-4">
        {error && (
          <p role="alert" className="rounded-md border border-destructive/30 bg-destructive/[0.08] p-2.5 text-[13px]">
            {error}
          </p>
        )}
        <TextField
          id="tenant-create-name"
          label={tx('Şirket adı')}
          required
          value={companyName}
          onChange={(e) => setCompanyName(e.target.value)}
        />
        <TextField
          id="tenant-create-slug"
          label={tx('Kısa ad')}
          hint={tx('Boş bırakılırsa şirket adından üretilir. Küçük harf, rakam ve tire.')}
          placeholder={slugify(companyName) || undefined}
          value={slug}
          onChange={(e) => setSlug(slugify(e.target.value))}
        />
        <TextField
          id="tenant-create-admin-name"
          label={tx('Yönetici adı soyadı')}
          value={adminFullName}
          onChange={(e) => setAdminFullName(e.target.value)}
        />
        <TextField
          id="tenant-create-admin-email"
          label={tx('Yönetici e-postası')}
          type="email"
          required
          value={adminEmail}
          onChange={(e) => setAdminEmail(e.target.value)}
        />
      </form>
    </Modal>
  )
}

/* -------------------------------- Detay çekmecesi -------------------------------- */

function TenantDetail({ tenantId, onClose }: { tenantId: string | null; onClose: () => void }) {
  const detail = useTenant(tenantId ?? undefined)
  const toast = useToast()
  const confirm = useConfirm()
  const linkDialog = usePasswordLinkDialog()
  const adminLink = useMutation({
    mutationFn: (id: string) => passwordLinkApi.tenantAdminLink(id),
    onSuccess: (v) => linkDialog.show(v, detail.data?.adminEmail ?? undefined),
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Link üretilemedi.')),
  })

  return (
    <Sheet open={Boolean(tenantId)} onOpenChange={(o) => !o && onClose()}>
      <SheetContent side="right" className="w-full overflow-y-auto sm:max-w-lg">
        <SheetHeader className="border-b border-border">
          <SheetTitle>{detail.data?.name ?? tx('Kiracı detayı')}</SheetTitle>
        </SheetHeader>

        <div className="space-y-6 px-4 pb-8">
          {detail.isPending ? (
            <CenteredSpinner label={tx('Kiracı yükleniyor')} />
          ) : detail.isError || !detail.data ? (
            <ErrorState
              title={tx('Kiracı bilgisi alınamadı')}
              message={detail.error instanceof Error ? detail.error.message : undefined}
              onRetry={() => void detail.refetch()}
            />
          ) : (
            <>
              <dl className="divide-y divide-border">
                {[
                  { label: tx('Kısa ad'), value: detail.data.slug, mono: true },
                  { label: tx('Durum'), value: tenantStatusLabels[detail.data.status] },
                  { label: tx('Çalışan sayısı'), value: formatNumber(detail.data.employeeCount ?? 0) },
                  { label: tx('Yönetici e-postası'), value: detail.data.adminEmail ?? '—' },
                  { label: tx('E-posta alan adı'), value: detail.data.emailDomain ?? '—' },
                  { label: tx('Vergi numarası'), value: detail.data.taxNumber ?? '—' },
                  { label: tx('Kayıt'), value: formatDateTime(detail.data.createdAt) },
                  ...(detail.data.suspendedAt
                    ? [
                        {
                          label: tx('Askıya alınma'),
                          value: formatDateTime(detail.data.suspendedAt),
                        },
                        {
                          label: tx('Askı gerekçesi'),
                          value: detail.data.suspensionReason ?? '—',
                        },
                      ]
                    : []),
                ].map((row) => (
                  <div
                    key={row.label}
                    className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-2.5"
                  >
                    <dt className="text-[12px] text-muted-foreground">{row.label}</dt>
                    <dd
                      className={cn(
                        'text-right text-[13px] break-all',
                        row.mono && 'font-mono',
                      )}
                    >
                      {row.value}
                    </dd>
                  </div>
                ))}
              </dl>

              <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border p-3">
                <p className="text-[12.5px] leading-relaxed text-muted-foreground">
                  {tx('Yönetici parola linkini kaybettiyse yeni bir süreli link üretin (açık linkler iptal olur).')}
                </p>
                <Button size="sm" variant="outline" disabled={adminLink.isPending}
                  onClick={async () => {
                    if (await confirm({ title: tx('Yönetici parola linki üretilsin mi?'), note: tx('Link size bir kez gösterilir; yöneticiye siz iletirsiniz.'), action: tx('Link üret'), destructive: false }))
                      adminLink.mutate(detail.data!.id)
                  }}>
                  {adminLink.isPending ? <LoaderCircle className="size-3.5 animate-spin" /> : <KeyRound className="size-3.5" />}
                  {tx('Yönetici parola linki')}
                </Button>
              </div>

              <div>
                <h3 className="mb-3 text-[14px] font-semibold">{tx('Kurulum kayıtları')}</h3>
                <ProvisioningTimeline entries={detail.data.provisioningLog ?? []} />
              </div>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}

/* ----------------------------------- Sayfa ----------------------------------- */

export function TenantsPage() {
  const toast = useToast()
  const [status, setStatus] = useState<string>(ALL)
  const [openId, setOpenId] = useState<string | null>(null)
  const [suspendFor, setSuspendFor] = useState<Tenant | null>(null)
  const [grantFor, setGrantFor] = useState<Tenant | null>(null)
  const [sampleFor, setSampleFor] = useState<Tenant | null>(null)
  const [creating, setCreating] = useState(false)

  // Tüm kiracılar bir kez çekilir; durum filtresi istemcide uygulanır. Böylece üstteki
  // istatistik kartları (toplam/aktif/…) filtreden bağımsız, tüm platformu gösterir.
  const tenants = useTenants()
  const rows = useMemo(
    () => (status === ALL ? tenants.data : tenants.data?.filter((t) => t.status === status)),
    [tenants.data, status],
  )
  const reactivate = useReactivateTenant()
  const confirm = useConfirm()
  const askReactivate = async (t: Tenant) => {
    const ok = await confirm({
      title: tx('{0} yeniden etkinleştirilsin mi?', [t.name]),
      note: tx('Askıya alma sırasında kapatılan kullanıcı hesapları yeniden açılır ve şirket platformu yeniden kullanabilir. Askı gerekçesi kayıttan silinir.'),
      action: tx('Yeniden etkinleştir'),
      destructive: false,
    })
    if (!ok) return
    reactivate.mutate(
      { id: t.id },
      {
        onSuccess: () => toast.ok(tx('{0} yeniden etkinleştirildi', [t.name])),
        onError: (e: unknown) =>
          toast.stop(e instanceof Error ? e.message : tx('Kiracı etkinleştirilemedi.')),
      },
    )
  }

  const stats = useMemo(() => {
    const list = tenants.data ?? []
    return {
      total: list.length,
      active: list.filter((t) => t.status === 'Active').length,
      pending: list.filter((t) => t.status === 'Pending').length,
      suspended: list.filter((t) => t.status === 'Suspended').length,
    }
  }, [tenants.data])

  const filters: TableFilter[] = [
    {
      id: 'status',
      label: tx('Durum'),
      value: status,
      onChange: setStatus,
      options: [
        { value: ALL, label: tx('Tüm durumlar') },
        ...(Object.keys(tenantStatusLabels) as TenantStatus[]).map((s) => ({
          value: s,
          label: tenantStatusLabels[s],
        })),
      ],
    },
  ]

  const columns: Array<Column<Tenant>> = [
    {
      id: 'name',
      header: tx('Şirket'),
      searchText: (t) => `${t.name} ${t.slug} ${t.adminEmail ?? ''}`,
      sortValue: (t) => t.name,
      exportText: (t) => t.name,
      cell: (t) => (
        <div className="flex items-center gap-3">
          <span className="flex size-9 shrink-0 items-center justify-center rounded-md bg-primary text-[13px] font-semibold text-primary-foreground">
            {t.name.charAt(0).toLocaleUpperCase(appLocale)}
          </span>
          <div className="min-w-0">
            <p className="truncate font-medium text-foreground">{t.name}</p>
            <p className="mt-0.5 truncate font-mono text-[12px] text-muted-foreground">{t.slug}</p>
          </div>
        </div>
      ),
    },
    {
      id: 'employees',
      header: tx('Çalışan'),
      align: 'right',
      hideBelow: 'md',
      sortValue: (t) => t.employeeCount ?? 0,
      exportText: (t) => String(t.employeeCount ?? 0),
      cell: (t) => <span className="tabular text-[12.5px] text-muted-foreground">{formatNumber(t.employeeCount ?? 0)}</span>,
    },
    {
      id: 'createdAt',
      header: tx('Kayıt'),
      align: 'right',
      hideBelow: 'lg',
      sortValue: (t) => new Date(t.createdAt).getTime(),
      exportText: (t) => formatDate(t.createdAt),
      cell: (t) => <span className="tabular text-muted-foreground">{formatDate(t.createdAt)}</span>,
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (t) => tenantStatusLabels[t.status] ?? '',
      exportText: (t) => tenantStatusLabels[t.status] ?? '',
      cell: (t) => (
        <StatusBadge tone={STATUS_TONE[t.status] ?? 'neutral'}>
          {tenantStatusLabels[t.status]}
        </StatusBadge>
      ),
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Kiracılar')}
        description={tx('Platformdaki tüm şirketler ve kurulum durumları. Bu sayfa yalnızca platform yönetimine açıktır.')}
        actions={
          <Button className="cursor-pointer" onClick={() => setCreating(true)}>
            <Plus className="size-4" />
            {tx('Yeni kiracı')}
          </Button>
        }
      />
      <CreateTenantModal open={creating} onClose={() => setCreating(false)} />

      {tenants.isPending ? (
        <StatCardsSkeleton count={4} />
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <StatCard
            label={tx('Toplam kiracı')}
            count={stats.total}
            format={formatNumber}
            trend={tx('Kayıtlı şirket')}
            trendDirection="flat"
            trendSense="neutral"
          />
          <StatCard
            label={tx('Aktif')}
            count={stats.active}
            format={formatNumber}
            trend={tx('Çalışıyor')}
            trendDirection="flat"
            trendSense="positive"
          />
          <StatCard
            label={tx('Hazırlanıyor')}
            count={stats.pending}
            format={formatNumber}
            trend={stats.pending > 0 ? tx('Kurulum sürüyor') : tx('Bekleyen yok')}
            trendDirection={stats.pending > 0 ? 'up' : 'flat'}
            trendSense="negative"
          />
          <StatCard
            label={tx('Askıda')}
            count={stats.suspended}
            format={formatNumber}
            trend={stats.suspended > 0 ? tx('Erişim kapalı') : tx('Askıda kiracı yok')}
            trendDirection={stats.suspended > 0 ? 'up' : 'flat'}
            trendSense="negative"
          />
        </div>
      )}

      <MyGrantsPanel />

      <DataTable
        rows={rows}
        rowKey={(t) => t.id}
        columns={columns}
        filters={filters}
        isLoading={tenants.isPending}
        error={tenants.error}
        onRetry={() => void tenants.refetch()}
        onRowClick={(t) => setOpenId(t.id)}
        searchPlaceholder={tx('Şirket adı, kısa ad veya e-posta')}
        exportFileName="kiracilar"
        pageSize={12}
        initialSort={{ columnId: 'createdAt', dir: 'desc' }}
        emptyTitle={tx('Kiracı yok')}
        emptyDetail={tx('Bu filtreye uyan kiracı bulunmuyor.')}
        rowActions={[
          { label: tx('Detay ve kurulum kaydı'), onSelect: (t) => setOpenId(t.id) },
          {
            label: tx('Süreli erişim izni aç'),
            hidden: (t) => t.status !== 'Active',
            onSelect: (t) => setGrantFor(t),
          },
          {
            label: tx('Örnek veriler'),
            hidden: (t) => t.status !== 'Active',
            onSelect: (t) => setSampleFor(t),
          },
          {
            label: tx('Askıya al'),
            destructive: true,
            hidden: (t) => t.status !== 'Active' && t.status !== 'Pending',
            onSelect: (t) => setSuspendFor(t),
          },
          {
            label: tx('Yeniden etkinleştir'),
            hidden: (t) => t.status !== 'Suspended',
            onSelect: (t) => void askReactivate(t),
          },
        ]}
      />

      <TenantDetail tenantId={openId} onClose={() => setOpenId(null)} />
      <SuspendModal tenant={suspendFor} onClose={() => setSuspendFor(null)} />
      <GrantAccessModal key={grantFor?.id ?? 'none-grant'} tenant={grantFor} onClose={() => setGrantFor(null)} />
      {sampleFor && (
        <Modal open onClose={() => setSampleFor(null)} title={sampleFor.name} size="lg">
          <SampleDataPanel slug={sampleFor.slug} />
        </Modal>
      )}
    </div>
  )
}
