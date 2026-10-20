import { ArrowRight, ExternalLink, Key, Monitor, Moon, SlidersHorizontal, Sun } from 'lucide-react'
import { Link } from 'react-router-dom'
import { PageHeader } from '@/components/layout/PageHeader'
import { DataField, Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/status-badge'
import { Callout } from '@/components/ui/callout'
import { useAuth } from '@/auth/useAuth'
import { permissionsFor, primaryRole, roleLabels, type Role } from '@/auth/roles'
import { tenantStatusLabels } from '@/api/tenant'
import { useTheme, type Theme } from '@/lib/theme'
import { formatDate, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { CompanyNamePanel, BrandingPanel } from './BrandingPanel'
import { tx } from '@/lib/i18n'
import { useModules } from '@/lib/modules'
import { PasswordMethodPanel } from '@/features/access/PasswordLinksPanel'
import { SampleDataPanel } from './SampleDataPanel'
import { SmsPanel } from './SmsPanel'
import { nativeShell } from '@/lib/nativeShell'
import { AndroidAppPanel } from './AndroidAppPanel'

const THEMES: Array<{ value: Theme; label: string; icon: React.ElementType }> = [
  { value: 'light', label: tx('Açık'), icon: Sun },
  { value: 'dark', label: tx('Koyu'), icon: Moon },
  { value: 'system', label: tx('Sistem'), icon: Monitor },
]

export function SettingsPage() {
  const { user, roles, tenant, tenantSlug, tenantError, accountUrl, logout, can } = useAuth()
  const { tenancy } = useModules()
  const { theme, setTheme } = useTheme()

  const permissions = [...permissionsFor(roles)].sort()

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Ayarlar')}
        description={tx('Hesabınız, şirketiniz ve arayüz tercihleri.')}
      />

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel>
          <PanelHead title={tx('Hesap')} note={tx('Kimlik bilgileri Keycloak\'tan gelir')} />
          <PanelBody className="space-y-4">
            <dl className="grid gap-x-6 gap-y-4 sm:grid-cols-2">
              <DataField label={tx('Ad soyad')}>{user?.fullName ?? '—'}</DataField>
              <DataField label={tx('Kullanıcı adı')}>
                <span className="font-mono text-[12px]">{user?.username ?? '—'}</span>
              </DataField>
              <DataField label="E-posta">{user?.email ?? tx('Tanımlı değil')}</DataField>
              <DataField label={tx('Rol')}>
                <StatusBadge tone="info">{roleLabels[primaryRole(roles)]}</StatusBadge>
              </DataField>
            </dl>

            <div className="flex flex-wrap gap-2 border-t border-border pt-4">
              {accountUrl !== '#' && (
                <Button variant="outline" size="sm" className="cursor-pointer" asChild>
                  <a href={accountUrl} target="_blank" rel="noreferrer">
                    {tx('Keycloak hesabım')}
                    <ExternalLink className="size-3.5" />
                  </a>
                </Button>
              )}
              <Button
                variant="outline"
                size="sm"
                className="cursor-pointer"
                onClick={() => logout()}
              >
                {tx('Oturumu kapat')}
              </Button>
            </div>

            <p className="text-[12px] leading-relaxed text-muted-foreground">
              {tx('Parola değişikliği ve iki adımlı doğrulama Keycloak hesap sayfasından yapılır; bu uygulama parolanızı hiçbir zaman görmez.')}
            </p>
          </PanelBody>
        </Panel>

        <Panel>
          <PanelHead title={tx('Şirket')} note={tx('Bağlı olduğunuz kiracı')} />
          <PanelBody className="space-y-4">
            {!tenantSlug && roles.includes('platform-admin') && tenancy === 'single' ? (
              <p className="text-[13px] leading-relaxed text-muted-foreground">
                {tx('Tek şirket kipi: şirket ekranları için yonetici hesabıyla giriş yapın. Platform yöneticisi hesabı yalnızca acil durum ve bakım içindir.')}
              </p>
            ) : !tenantSlug && roles.includes('platform-admin') ? (
              // Platform yöneticisi hiçbir kiracıya bağlı değildir; uyarı yanlış alarm olurdu (AppShell ile aynı kural).
              <p className="text-[13px] leading-relaxed text-muted-foreground">
                {tx('Platform yöneticisi olarak hiçbir şirkete bağlı değilsiniz. Şirketleri')}{' '}
                <Link to="/panel/platform/kiracilar" className="font-medium text-primary underline-offset-4 hover:underline">{tx('Kiracılar')}</Link>{' '}
                {tx('ekranından yönetirsiniz.')}
              </p>
            ) : !tenantSlug ? (
              <div role="alert">
                <Callout tone="warning">
                  {tx('Oturumunuzda')}{' '}<code className="font-mono text-[12px]">{tx('organization')}</code>{' '}{tx('claim\'i yok. Bu yüzden listeler boş görünür. Oturumu kapatıp yeniden girin.')}
                </Callout>
              </div>
            ) : tenantError ? (
              <p className="text-[13px] leading-relaxed text-muted-foreground">{tenantError}</p>
            ) : !tenant ? (
              <p className="text-[13px] text-muted-foreground">{tx('Kiracı bilgisi yükleniyor…')}</p>
            ) : (
              <>
                <dl className="grid gap-x-6 gap-y-4 sm:grid-cols-2">
                  <DataField label={tx('Şirket')}>{tenant.name}</DataField>
                  <DataField label={tx('Kısa ad')}>
                    <span className="font-mono text-[12px]">{tenant.slug}</span>
                  </DataField>
                  <DataField label={tx('Durum')}>
                    <StatusBadge tone={tenant.status === 'Active' ? 'success' : 'warning'}>
                      {tenantStatusLabels[tenant.status]}
                    </StatusBadge>
                  </DataField>
                  <DataField label={tx('Kayıt tarihi')}>{formatDate(tenant.createdAt)}</DataField>
                  <DataField label={tx('E-posta alan adı')}>{tenant.emailDomain ?? '—'}</DataField>
                  {tenant.employeeCount != null && (
                    <DataField label={tx('Çalışan sayısı')}>
                      <span className="tabular">{formatNumber(tenant.employeeCount)}</span>
                    </DataField>
                  )}
                </dl>
              </>
            )}
          </PanelBody>
        </Panel>
      </div>

      {can('employee:manage') && (
        <Panel>
          <PanelHead
            title={tx('Ekip ve roller')}
            note={tx('Çalışanlara giriş erişimi verin, rollerini yönetin')}
          />
          <PanelBody>
            <Link
              to="/panel/roller"
              className="flex items-center justify-between gap-3 rounded-md border border-border p-3 transition-colors hover:bg-muted/40"
            >
              <span className="flex items-center gap-2.5">
                <Key className="size-4 text-muted-foreground" />
                <span className="text-[13px]">
                  {tx('Tüm çalışanların rollerini tek tablodan yönetin')}
                </span>
              </span>
              <ArrowRight className="size-4 text-muted-foreground" />
            </Link>
          </PanelBody>
        </Panel>
      )}

      {can('employee:manage') && (
        <Panel>
          <PanelHead title={tx('Özel alanlar')} note={tx('Çalışan profiline ek bilgi alanları (form tasarımcısı)')} />
          <PanelBody>
            <Link
              to="/panel/ayarlar/ozel-alanlar"
              className="flex items-center justify-between gap-3 rounded-md border border-border p-3 transition-colors hover:bg-muted/40"
            >
              <span className="flex items-center gap-2.5">
                <SlidersHorizontal className="size-4 text-muted-foreground" />
                <span className="text-[13px]">
                  {tx('Alan ekleyin; KVKK bilgileri (hukuki sebep, amaç, saklama süresi) zorunludur')}
                </span>
              </span>
              <ArrowRight className="size-4 text-muted-foreground" />
            </Link>
          </PanelBody>
        </Panel>
      )}

      {tenant && can('tenant:manage') && (
        <>
          <CompanyNamePanel tenant={tenant} />
          <BrandingPanel tenant={tenant} />
          <SmsPanel />
          <PasswordMethodPanel canEdit />
          <SampleDataPanel />
        </>
      )}

      {!nativeShell() && <AndroidAppPanel />}

      {nativeShell() && (
        <Panel>
          <PanelHead title={tx('Uygulama')} note={tx('Bağlı sunucu: {0}', [nativeShell()!.getServer() || '—'])} />
          <PanelBody>
            <Button variant="outline" onClick={() => nativeShell()!.resetServer()}>{tx('Sunucu adresini değiştir')}</Button>
          </PanelBody>
        </Panel>
      )}

      <Panel>
        <PanelHead title={tx('Görünüm')} note={tx('Tercih bu tarayıcıda saklanır')} />
        <PanelBody>
          <div role="radiogroup" aria-label={tx('Tema')} className="flex flex-wrap gap-2">
            {THEMES.map((option) => {
              const Icon = option.icon
              const active = theme === option.value
              return (
                <button
                  key={option.value}
                  type="button"
                  role="radio"
                  aria-checked={active}
                  onClick={() => setTheme(option.value)}
                  className={cn(
                    'flex min-h-11 cursor-pointer items-center gap-2 rounded-md border px-4 text-[13px] font-medium transition-colors',
                    active
                      ? 'border-primary bg-primary/10 text-primary'
                      : 'border-border text-muted-foreground hover:text-foreground',
                  )}
                >
                  <Icon className="size-4" strokeWidth={1.75} />
                  {option.label}
                </button>
              )
            })}
          </div>
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead
          title={tx('İzinleriniz')}
          note={tx('Arayüzde neyi görebildiğinizi bu liste belirler')}
          action={
            <span className="tabular text-[12px] text-muted-foreground">
              {tx('{0} izin', [formatNumber(permissions.length)])}</span>
          }
        />
        <PanelBody className="space-y-4">
          <div className="flex flex-wrap gap-1.5">
            {roles.length === 0 ? (
              <p className="text-[13px] text-muted-foreground">
                {tx('Oturumunuza hiç rol atanmamış. Sistem yöneticinizle görüşün.')}
              </p>
            ) : (
              roles
                .filter((r): r is Role => r in roleLabels)
                .map((r) => (
                  <StatusBadge key={r} tone="neutral">
                    {roleLabels[r]}
                  </StatusBadge>
                ))
            )}
          </div>

          {/* Teknik izin anahtarları günlük kullanımda gürültü; destek için katlanmış durur. */}
          <details className="group border-t border-border pt-4">
            <summary className="cursor-pointer list-none text-[13px] font-medium text-muted-foreground transition-colors select-none hover:text-foreground [&::-webkit-details-marker]:hidden">
              <span className="inline-flex items-center gap-1.5">
                <span className="inline-block transition-transform group-open:rotate-90">›</span>
                {tx('Teknik izin listesi')}
              </span>
            </summary>
            <ul className="mt-3 flex flex-wrap gap-1.5">
              {permissions.map((p) => (
                <li
                  key={p}
                  className="rounded-md border border-border bg-muted/40 px-2 py-0.5 font-mono text-[11px] text-muted-foreground"
                >
                  {p}
                </li>
              ))}
            </ul>
          </details>

          <p className="text-[12px] leading-relaxed text-muted-foreground">
            {tx('Bu liste yalnızca arayüzün neyi gösterdiğini açıklar. Gerçek yetkilendirme her istekte sunucu tarafında yeniden denetlenir; buradaki bir izin backend\'de karşılığı yoksa işlem yine reddedilir.')}
          </p>
        </PanelBody>
      </Panel>
    </div>
  )
}
