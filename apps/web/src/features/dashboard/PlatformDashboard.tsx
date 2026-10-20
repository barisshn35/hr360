/**
 * Genel bakış — platform yöneticisi (kiracısız oturum).
 *
 * Platform yöneticisi hiçbir şirkete bağlı değildir: "kararınızı bekliyor", "İzin talebi / Masraf
 * bildir" kısayolları ya da kişisel modüller (Bordrolarım, Mülakatlarım) onun için anlamsızdır.
 * Bunun yerine platform özeti (kiracı sayıları, kurulumu süren / askıdaki şirketler) ve
 * platform ekranlarına kısayollar gösterilir. Veriler mevcut kiracı listesi ucundan gelir.
 */
import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { ArrowUpRight, Brain, Building2, ChevronRight, HardDrive, Settings2, Shield } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { StatCard, StatCardsSkeleton } from '@/components/ui/stat-card'
import { StatusBadge, type StatusTone } from '@/components/ui/status-badge'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/states'
import { useAuth } from '@/auth/useAuth'
import { useTenants } from '@/api/queries-tenant'
import { tenantStatusLabels, type TenantStatus } from '@/api/tenant'
import { formatNumber, formatRelativeToNow } from '@/lib/format'
import { tx, appLocale } from '@/lib/i18n'
import { useModules } from '@/lib/modules'
import { Panel } from '@/components/ui/panel'

const STATUS_TONE: Record<TenantStatus, StatusTone> = {
  Pending: 'warning',
  Active: 'success',
  Suspended: 'danger',
  Cancelled: 'neutral',
}

export function PlatformDashboard() {
  const { user } = useAuth()
  const single = useModules().tenancy === 'single'
  const tenants = useTenants({}, !single)

  const stats = useMemo(() => {
    const list = tenants.data ?? []
    return {
      total: list.length,
      active: list.filter((t) => t.status === 'Active').length,
      pending: list.filter((t) => t.status === 'Pending').length,
      suspended: list.filter((t) => t.status === 'Suspended').length,
      employees: list.reduce((sum, t) => sum + (t.employeeCount ?? 0), 0),
    }
  }, [tenants.data])

  // Dikkat isteyenler önce (kurulumu süren, askıda), sonra en yeni kiracılar.
  const recent = useMemo(
    () =>
      [...(tenants.data ?? [])]
        .sort((a, b) => {
          const rank = (s: TenantStatus) => (s === 'Pending' ? 0 : s === 'Suspended' ? 1 : 2)
          return rank(a.status) - rank(b.status) || new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
        })
        .slice(0, 6),
    [tenants.data],
  )

  // Tek şirket kipinde kiracı listesi yok: bakım ekranlarına kısayol.
  const shortcuts: Array<{ to: string; label: string; icon: React.ElementType }> = single
    ? [
        { to: '/panel/platform/bakim', label: tx('Bakım'), icon: HardDrive },
        { to: '/panel/platform/sistem', label: tx('Sistem ayarları'), icon: Settings2 },
      ]
    : [
        { to: '/panel/platform/kiracilar', label: tx('Kiracılar'), icon: Shield },
        { to: '/panel/model-karti', label: tx('Model kartı'), icon: Brain },
      ]

  const firstName = user?.fullName?.split(' ')[0] ?? ''

  return (
    <div className="grid grid-cols-1 gap-4 md:grid-cols-6 xl:grid-cols-12">
      <Panel className="gap-0 p-6 sm:p-8 md:col-span-6 xl:col-span-12">
        <p className="text-[12.5px] text-muted-foreground first-letter:uppercase">
          {new Date().toLocaleDateString(appLocale, { weekday: 'long', day: 'numeric', month: 'long' })}
        </p>
        <h2 className="mt-3 text-[30px] leading-tight font-semibold tracking-[-0.03em]">
          {firstName ? tx('Hoş geldiniz, {0}', [firstName]) : tx('Hoş geldiniz')}
        </h2>
        <p className="mt-3 max-w-2xl text-[14.5px] leading-relaxed text-muted-foreground">
          {single
            ? tx('Tek şirket kipi: şirket ekranları için yonetici hesabıyla giriş yapın. Platform yöneticisi hesabı yalnızca acil durum ve bakım içindir.')
            : tx('Platform yöneticisi olarak hiçbir şirkete bağlı değilsiniz. Şirketleri ve kurulum durumlarını Kiracılar ekranından yönetirsiniz.')}
        </p>
        <div className="mt-6 flex flex-wrap gap-2">
          {shortcuts.map(({ to, label, icon: Icon }, i) => (
            <Button key={to} asChild variant={i === 0 ? 'default' : 'outline'} className="h-10 px-4">
              <Link to={to}>
                <Icon className="size-4" strokeWidth={1.75} />
                {label}
              </Link>
            </Button>
          ))}
        </div>
      </Panel>

      {!single && <>
      <div className="md:col-span-6 xl:col-span-12">
        {tenants.isPending ? (
          <StatCardsSkeleton count={4} />
        ) : (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Link to="/panel/platform/kiracilar" className="block rounded-2xl focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:outline-none">
              <StatCard label={tx('Toplam kiracı')} count={stats.total} format={formatNumber} icon={Building2} trend={tx('{0} aktif', [formatNumber(stats.active)])} trendDirection="flat" trendSense="neutral" className="h-full" />
            </Link>
            <StatCard label={tx('Hazırlanıyor')} count={stats.pending} format={formatNumber} trend={stats.pending > 0 ? tx('Kurulum sürüyor') : tx('Bekleyen yok')} trendDirection={stats.pending > 0 ? 'up' : 'flat'} trendSense="negative" attention={stats.pending > 0} />
            <StatCard label={tx('Askıda')} count={stats.suspended} format={formatNumber} trend={stats.suspended > 0 ? tx('Erişim kapalı') : tx('Askıda kiracı yok')} trendDirection={stats.suspended > 0 ? 'up' : 'flat'} trendSense="negative" />
            <StatCard label={tx('Toplam çalışan')} count={stats.employees} format={formatNumber} trend={tx('Tüm kiracılarda')} trendDirection="flat" trendSense="neutral" />
          </div>
        )}
      </div>

      <Panel className="gap-0 overflow-hidden py-0 md:col-span-6 xl:col-span-12">
        <div className="flex items-center justify-between border-b border-border px-5 py-4">
          <div>
            <p className="text-[14.5px] font-semibold tracking-tight">{tx('Kiracılar')}</p>
            <p className="mt-0.5 text-[12.5px] text-muted-foreground">{tx('Kurulumu süren ve askıdakiler en üstte')}</p>
          </div>
          <Link to="/panel/platform/kiracilar" className="group inline-flex items-center gap-1 text-[12.5px] font-medium text-primary">
            {tx('Tümü')}
            <ArrowUpRight className="size-3.5 transition-transform group-hover:translate-x-0.5 group-hover:-translate-y-0.5" />
          </Link>
        </div>
        {tenants.isPending ? (
          <RowsSkeleton rows={4} columns={2} />
        ) : tenants.isError ? (
          <ErrorState message={tenants.error instanceof Error ? tenants.error.message : undefined} onRetry={() => void tenants.refetch()} />
        ) : recent.length === 0 ? (
          <EmptyState icon={Building2} title={tx('Kiracı yok')} detail={tx('Platformda henüz kayıtlı şirket bulunmuyor.')} />
        ) : (
          <ul className="p-2">
            {recent.map((t) => (
              <li key={t.id}>
                <Link to="/panel/platform/kiracilar" className="group flex items-center gap-3 rounded-xl px-3 py-2.5 transition-colors hover:bg-accent">
                  <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-muted text-[13px] font-semibold ring-1 ring-border">
                    {t.name.charAt(0).toLocaleUpperCase('tr')}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[13.5px] font-medium">{t.name}</span>
                    <span className="block truncate text-[11.5px] text-muted-foreground">
                      {tx('{0} · kayıt {1}', [t.slug, formatRelativeToNow(t.createdAt)])}
                    </span>
                  </span>
                  <StatusBadge tone={STATUS_TONE[t.status] ?? 'neutral'}>{tenantStatusLabels[t.status]}</StatusBadge>
                  <ChevronRight className="size-4 text-muted-foreground/40 transition-transform group-hover:translate-x-0.5" />
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Panel>
      </>}
    </div>
  )
}
