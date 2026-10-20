/**
 * Genel bakış — bento düzeni.
 *
 * 21st.dev kaynakları: "Bento Grid" (kokonutd / arihantcodes) yerleşimi,
 * "Glowing Effect" kenarlar (Card üzerinden), "Border Beam" (karşılama kutusu),
 * "Orbiting Circles" (modül yörüngesi), "Number Ticker" (CountUp), "Sparkline",
 * "Animated List" deseni (sıradaki talepler). Tüm sayılar gerçek veriden.
 */

import { Fragment, Suspense, lazy, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ArrowUpRight,
  Building2,
  CalendarDays,
  ChevronRight,
  Clock,
  Inbox,
  LayoutDashboard,
  SlidersHorizontal,
  TriangleAlert,
  UserPlus,
  Users,
  Wallet,
  Zap,
} from 'lucide-react'
import { AnimatePresence, motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { StatCard, StatCardsSkeleton, type StatCardProps } from '@/components/ui/stat-card'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/status-badge'
import { WorkflowStatusBadge } from '@/components/ui/module-badges'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/states'
import { Sparkline } from '@/components/ui/sparkline'
import type { SeriesPoint } from '@/components/ui/metric-chart'
import { BorderBeam } from '@/components/fx/border-beam'
import { OrbitingCircles } from '@/components/fx/orbiting-circles'
import { GradientText } from '@/components/fx/shiny-text'
import { TextReveal } from '@/components/fx/text-reveal'
import { Spotlight } from '@/components/fx/spotlight'
import { flattenItems } from '@/components/layout/nav-config'
import { useNavGroups } from '@/components/layout/use-nav'
import { useModules } from '@/lib/modules'
import { useAuth } from '@/auth/useAuth'
import { PinnedReportsWidget } from '@/features/insights/SavedReports'
import { WinnerSpotlight } from '@/features/community/eom/EomWinnerCard'
import { TodosWidget } from '@/features/todos/TodosPage'
import { TeamBoardWidget } from './TeamBoardWidget'
import { useEomMe, useEomWinners } from '@/api/eom'
import { PlatformDashboard } from './PlatformDashboard'
import { DashboardCustomizer } from './DashboardCustomizer'
import { WIDGET_IDS, defaultLayout, resolveLayout, type DashboardPref, type DashboardRole, type WidgetId } from './dashboardLayout'
import { useUiPref } from '@/lib/uiPrefs'
import { useNewParamFlag } from '@/lib/useNewParam'
import {
  useCompanies,
  useEmployees,
  useJobPostings,
  useLeaveRequests,
  useMyEmployeeId,
  useOverdueWorkflows,
  useWorkflows,
} from '@/api/queries'
import { EmployeeStatus, workflowTypeLabels } from '@/api/types'
import { formatDate, formatNumber, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { CountUp, EASE } from '@/motion/primitives'
import { localISODate } from '@/lib/dates'
import { tx, appLocale } from '@/lib/i18n'
import { Panel } from '@/components/ui/panel'

/**
 * Recharts tek başına ~390 kB. Genel bakış ilk açılan ekran olduğu için
 * grafik kartı ayrı chunk'a alındı: panel açılırken grafik arkadan gelir.
 */
const ProgressMetricCard = lazy(() => import('@/components/ui/progress-metric-card'))

/** Son N günün gün gün talep sayısı — grafiğin verisi uydurma değil, gerçek. */
function dailySeries(dates: string[], days: number): SeriesPoint[] {
  const buckets = new Map<string, number>()
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(today)
    d.setDate(d.getDate() - i)
    buckets.set(localISODate(d), 0)
  }
  for (const value of dates) {
    // Zaman damgası UTC gelir; kullanıcının yerel gününe çevrilir.
    const key = value.length <= 10 ? value : localISODate(new Date(value))
    if (buckets.has(key)) buckets.set(key, buckets.get(key)! + 1)
  }
  return [...buckets.entries()].map(([date, value]) => ({ date, value }))
}

function greeting(now = new Date()) {
  const h = now.getHours()
  if (h < 5) return tx('İyi geceler')
  if (h < 12) return tx('Günaydın')
  if (h < 18) return tx('İyi günler')
  return tx('İyi akşamlar')
}

/** Saniyede bir ilerleyen saat — karşılama kutusunun canlı köşesi. */
function LiveClock() {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(id)
  }, [])
  const hh = now.toLocaleTimeString(appLocale, { hour: '2-digit', minute: '2-digit' })
  const ss = String(now.getSeconds()).padStart(2, '0')
  return (
    <span className="tabular font-mono text-[13px] text-muted-foreground">
      {hh}
      <span className="text-primary">:{ss}</span>
    </span>
  )
}

/** Bento hücresi: giriş animasyonu + ızgara yerleşimi. */
function Tile({ className, children, i = 0 }: { className?: string; children: React.ReactNode; i?: number }) {
  const reduced = useReducedMotion()
  return (
    <motion.div
      initial={reduced ? false : { opacity: 0, y: 18, scale: 0.98 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: 0.6, delay: 0.06 * i, ease: EASE }}
      className={cn('min-w-0', className)}
    >
      {children}
    </motion.div>
  )
}

type Kpi = StatCardProps & { key: string; to: string }

/**
 * Kiracısız platform yöneticisi şirket panosu yerine platform özetini görür (PlatformDashboard);
 * şirkete bağlı herkes (platform yöneticisi bir kiracıyla girse bile) şirket panosunu görür.
 */
export function DashboardPage() {
  const { roles, tenantSlug } = useAuth()
  if (!tenantSlug && roles.includes('platform-admin')) return <PlatformDashboard />
  return <TenantDashboard />
}

function TenantDashboard() {
  const { user, can } = useAuth()
  const reduced = useReducedMotion()
  const canSeeEmployees = can('employee:viewAll')
  const canDecide = can('workflow:decide')
  const canWorkflow = can('workflow:view')

  const employees = useEmployees({ enabled: canSeeEmployees })
  const companies = useCompanies({ enabled: can('organization:view') })
  const pending = useWorkflows({ status: 'Pending' }, { enabled: canWorkflow })
  const allWorkflows = useWorkflows({}, { enabled: canWorkflow })
  const overdue = useOverdueWorkflows({ enabled: canDecide })
  const { isEnabled, canQuery } = useModules()
  const postings = useJobPostings('Published', canQuery('recruitment'))
  const leave = useLeaveRequests({}, can('leave:view'))

  const activeCount = useMemo(
    () => employees.data?.filter((e) => e.status === EmployeeStatus.Active).length ?? 0,
    [employees.data],
  )
  const departmentCount = useMemo(
    () => companies.data?.reduce((sum, c) => sum + (c.departments?.length ?? 0), 0) ?? 0,
    [companies.data],
  )
  const overdueCount = overdue.data?.length ?? 0
  const pendingCount = pending.data?.length ?? 0
  // Rol metni: kişinin kendi açtığı talepler "bekleyen talebiniz", etkin adımında onaycı (ya da
  // vekil) olduğu talepler "kararınızı bekliyor" diye ayrı sayılır.
  const { employeeId: myEmployeeId } = useMyEmployeeId(canWorkflow)
  const { mineCount, decideCount } = useMemo(() => {
    let mine = 0
    let decide = 0
    for (const w of pending.data ?? []) {
      if (myEmployeeId && w.requesterEmployeeId === myEmployeeId) {
        mine++
        continue
      }
      const active = [...(w.steps ?? [])].sort((a, b) => a.order - b.order).find((st) => st.decision === 'Pending')
      if (myEmployeeId && active && (active.approverEmployeeId === myEmployeeId || active.delegatedToEmployeeId === myEmployeeId)) decide++
    }
    return { mineCount: mine, decideCount: decide }
  }, [pending.data, myEmployeeId])

  const leaveThisMonth = useMemo(() => {
    const now = new Date()
    return (
      leave.data?.filter((r) => {
        const d = new Date(r.startDate)
        return d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth()
      }).length ?? 0
    )
  }, [leave.data])

  const requestSeries = useMemo(
    () => dailySeries((allWorkflows.data ?? []).map((w) => w.createdAt), 30),
    [allWorkflows.data],
  )
  const last14 = requestSeries.slice(-14)

  /** Süresi geçenler önce, sonra en eski talep. */
  const queue = useMemo(
    () =>
      [...(pending.data ?? [])]
        .sort((a, b) => {
          const aLate = a.slaDueAt ? new Date(a.slaDueAt).getTime() < Date.now() : false
          const bLate = b.slaDueAt ? new Date(b.slaDueAt).getTime() < Date.now() : false
          if (aLate !== bLate) return aLate ? -1 : 1
          return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()
        })
        .slice(0, 6),
    [pending.data],
  )

  const navTree = useNavGroups()
  const modules = useMemo(() => flattenItems(navTree), [navTree])

  const firstName = user?.fullName?.split(' ')[0] ?? ''
  const statsLoading = (canSeeEmployees && employees.isPending) || (canWorkflow && pending.isPending)

  const kpis: Kpi[] = [
    canSeeEmployees && {
      key: 'employees',
      to: '/panel/calisanlar',
      label: tx('Çalışan'),
      count: employees.data?.length ?? 0,
      icon: Users,
      trend: tx('{0} aktif', [formatNumber(activeCount)]),
      trendSense: 'neutral' as const,
      compareLabel: tx('kadroda'),
    },
    can('organization:view') && {
      key: 'companies',
      to: '/panel/organizasyon',
      label: tx('Şirket'),
      count: companies.data?.length ?? 0,
      icon: Building2,
      trend: tx('{0} departman', [formatNumber(departmentCount)]),
      trendSense: 'neutral' as const,
    },
    can('recruitment:view') && isEnabled('recruitment') && {
      key: 'postings',
      to: '/panel/ise-alim',
      label: tx('Yayındaki ilan'),
      count: postings.data?.length ?? 0,
      icon: UserPlus,
      trend: tx('Açık pozisyon'),
      trendSense: 'neutral' as const,
    },
    can('leave:view') && {
      key: 'leave',
      to: '/panel/izin',
      label: tx('Bu ay izin'),
      count: leaveThisMonth,
      icon: CalendarDays,
      trend: tx('Bu ay başlayan'),
      trendSense: 'neutral' as const,
    },
  ].filter(Boolean) as Kpi[]

  const quickActions = [
    can('leave:view') && { to: '/panel/izin', label: tx('İzin talebi'), icon: CalendarDays },
    can('expense:view') && isEnabled('expense') && { to: '/panel/masraf', label: tx('Masraf bildir'), icon: Wallet },
    canWorkflow && { to: '/panel/onaylar', label: tx('Onay kutusu'), icon: Inbox },
  ].filter(Boolean) as Array<{ to: string; label: string; icon: React.ElementType }>

  const orbit = modules.slice(0, 8)

  // Ayın çalışanı: modül açık ve gösterilecek kazanan ya da kullanılacak oy varsa.
  const eomWinners = useEomWinners()
  const eomMe = useEomMe()
  const eomVisible = eomWinners.data?.enabled === true
    && ((eomWinners.data.units ?? []).length > 0 || (eomMe.data?.units ?? []).some((u) => u.action !== null))

  const dashboardRole: DashboardRole = can('employee:viewAll') ? 'hr' : canDecide ? 'manager' : 'employee'
  const roleDefaults = defaultLayout(dashboardRole)
  const dashPref = useUiPref<DashboardPref | null>('dashboard', null)
  const availableWidgets = WIDGET_IDS.filter((id) => ({
    welcome: true,
    team: canDecide,
    todos: true,
    eom: eomVisible,
    pending: canWorkflow,
    overdue: canDecide,
    kpis: statsLoading || kpis.length > 0,
    chart: canWorkflow,
    queue: canWorkflow,
    organization: can('organization:view'),
    modules: true,
    pinned: can('performance:manage'),
  })[id])
  const layout = resolveLayout(availableWidgets, dashPref.value, roleDefaults)
  const visibleWidgets = layout.order.filter((id) => !layout.hidden.has(id))
  const orgVisible = visibleWidgets.includes('organization')
  const [editing, setEditing] = useState(false)
  useNewParamFlag('duzenle', () => setEditing(true))

  // Dalga 12 (madde 89): kişiye özel kart sırası/gizleme; kayıt yoksa role göre varsayılan.
  const widgets: Record<WidgetId, React.ReactNode> = {
    welcome: (
      <>
        {/* ------------------------------ Karşılama ------------------------------ */}
        <Tile i={0} className="md:col-span-6 xl:col-span-8 xl:row-span-2">
          <Panel className="h-full gap-0 overflow-hidden py-0">
            <BorderBeam size={260} duration={14} />
            <Spotlight size={420} />
            <div className="relative grid h-full gap-6 p-6 sm:p-8 lg:grid-cols-[1fr_auto]">
              <div className="flex min-w-0 flex-col">
                <div className="flex flex-wrap items-center gap-2.5 text-[12.5px] text-muted-foreground">
                  <span className="first-letter:uppercase">
                    {new Date().toLocaleDateString(appLocale, { weekday: 'long', day: 'numeric', month: 'long' })}
                  </span>
                  <span className="text-border">•</span>
                  <LiveClock />
                </div>

                <h2 className="mt-5 text-[34px] leading-[1.05] font-semibold tracking-[-0.04em] sm:text-[46px]">
                  <TextReveal text={`${greeting()},`} />
                  <br />
                  <GradientText>{firstName || tx('hoş geldiniz')}</GradientText>
                </h2>

                <p className="mt-4 max-w-lg text-[14.5px] leading-relaxed text-muted-foreground">
                  {overdueCount > 0 ? (
                    <>
                      <span className="font-medium text-[hsl(var(--warning))]">
                        {tx('{0} talebin süresi geçti.', [formatNumber(overdueCount)])}</span>{' '}{tx('Onay kutusunda sıradaki adımlar sizi bekliyor.', [])}</>
                  ) : decideCount > 0 || mineCount > 0 ? (
                    [
                      decideCount > 0 && tx('{0} talep kararınızı bekliyor; hepsi süresinde.', [formatNumber(decideCount)]),
                      mineCount > 0 && tx('Onay bekleyen {0} talebiniz var.', [formatNumber(mineCount)]),
                    ]
                      .filter(Boolean)
                      .join(' ')
                  ) : pendingCount > 0 && canDecide ? (
                    tx('Şirkette onay sürecinde {0} talep var.', [formatNumber(pendingCount)])
                  ) : (
                    tx('Bugün bekleyen bir işiniz yok. Şirketin nabzı aşağıda.')
                  )}
                </p>

                {quickActions.length > 0 && (
                  <div className="mt-auto flex flex-wrap gap-2 pt-7">
                    {quickActions.map(({ to, label, icon: Icon }, i) => (
                      <motion.div
                        key={to}
                        initial={reduced ? false : { opacity: 0, y: 8 }}
                        animate={{ opacity: 1, y: 0 }}
                        transition={{ delay: 0.4 + i * 0.08 }}
                        whileHover={reduced ? undefined : { y: -2 }}
                      >
                        <Button asChild variant={i === 0 ? 'default' : 'outline'} className="h-10 px-4">
                          <Link to={to}>
                            <Icon className="size-4" strokeWidth={1.75} />
                            {label}
                          </Link>
                        </Button>
                      </motion.div>
                    ))}
                  </div>
                )}
              </div>

              {/* Modül yörüngesi */}
              <div aria-hidden="true" className="relative hidden size-[300px] items-center justify-center self-center lg:flex">
                <span className="absolute size-28 rounded-full bg-primary/15 blur-3xl" />
                <span className="animate-float relative flex size-20 items-center justify-center rounded-3xl bg-gradient-to-br from-primary to-[hsl(var(--brand-deep))] shadow-[0_0_50px_-6px_hsl(var(--primary))]">
                  <img src="/icon-white.svg" alt="" className="size-11" />
                </span>
                {orbit.slice(0, 3).map((m, i) => (
                  <OrbitingCircles key={m.id} radius={88} duration={22} angle={i * 120} path={i === 0} className="size-10 rounded-xl border border-border bg-card text-foreground/75 shadow-lg">
                    <m.icon className="size-[18px]" strokeWidth={1.6} />
                  </OrbitingCircles>
                ))}
                {orbit.slice(3, 8).map((m, i) => (
                  <OrbitingCircles key={m.id} radius={140} duration={34} reverse angle={i * 72} path={i === 0} className="size-9 rounded-xl border border-border bg-card/90 text-muted-foreground shadow-lg">
                    <m.icon className="size-4" strokeWidth={1.6} />
                  </OrbitingCircles>
                ))}
              </div>
            </div>
          </Panel>
        </Tile>

      </>
    ),
    pending: (
      <>
        {/* ---------------------------- Bekleyen onay ---------------------------- */}
        {canWorkflow && (
          <Tile i={1} className="md:col-span-3 xl:col-span-4">
            <Link to="/panel/onaylar" className="group block h-full rounded-2xl focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:outline-none">
              <Panel className="h-full gap-0 overflow-hidden p-6">
                <Spotlight />
                <div className="flex items-start justify-between">
                  <div>
                    <p className="text-[13px] font-medium text-muted-foreground">{canDecide ? tx('Bekleyen onay') : tx('Bekleyen talepleriniz')}</p>
                    <p className="tabular mt-2 text-[44px] leading-none font-semibold tracking-[-0.05em]">
                      <CountUp to={pendingCount} format={(v) => formatNumber(Math.round(v))} />
                    </p>
                  </div>
                  <span className="flex size-11 items-center justify-center rounded-2xl bg-muted text-primary ring-1 ring-border transition-transform duration-500 group-hover:rotate-12">
                    <Inbox className="size-5" strokeWidth={1.7} />
                  </span>
                </div>
                {last14.some((p) => p.value > 0) ? (
                  <Sparkline
                    data={last14.map((p) => p.value)}
                    labels={last14.map((p) => formatDate(p.date))}
                    height={56}
                    className="mt-5"
                    aria-label={tx('Son 14 günde açılan talepler')}
                  />
                ) : (
                  <div className="mt-5 h-14" />
                )}
                <p className="mt-3 flex items-center gap-1 text-[12.5px] text-muted-foreground">
                  {tx('Son 14 günde açılan talepler')}
                  <ArrowUpRight className="ml-auto size-4 text-primary opacity-0 transition-opacity group-hover:opacity-100" />
                </p>
              </Panel>
            </Link>
          </Tile>
        )}

      </>
    ),
    overdue: (
      <>
        {/* ---------------------------- Süresi geçen ---------------------------- */}
        {canDecide && (
          <Tile i={2} className="md:col-span-3 xl:col-span-4">
            <Link
              to="/panel/onaylar?durum=gecikmis"
              className="block h-full rounded-2xl focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:outline-none"
            >
              <StatCard
                label={tx('Süresi geçen')}
                count={overdueCount}
                format={formatNumber}
                icon={overdueCount > 0 ? TriangleAlert : Clock}
                trend={overdueCount > 0 ? tx('Öncelikli') : tx('Tümü süresinde')}
                trendDirection={overdueCount > 0 ? 'up' : 'flat'}
                trendSense="negative"
                attention={overdueCount > 0}
                className="h-full"
              />
            </Link>
          </Tile>
        )}

      </>
    ),
    kpis: (
      <>
        {/* --------------------------------- KPI --------------------------------- */}
        {statsLoading ? (
          <div className="md:col-span-6 xl:col-span-12">
            <StatCardsSkeleton count={4} />
          </div>
        ) : (
          kpis.map(({ key, to, ...card }, i) => (
            <Tile key={key} i={3 + i} className="md:col-span-3 xl:col-span-3">
              <Link to={to} className="block h-full rounded-2xl focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:outline-none">
                <StatCard {...card} format={formatNumber} className="h-full" />
              </Link>
            </Tile>
          ))
        )}

      </>
    ),
    chart: (
      <>
        {/* ------------------------------- Grafik ------------------------------- */}
        {canWorkflow && (
          <Tile i={7} className="md:col-span-6 xl:col-span-8">
            <Suspense fallback={<div className="surface h-[260px] animate-pulse rounded-2xl" />}>
              <ProgressMetricCard
                title={tx('Açılan talepler')}
                size="sm"
                accent="violet"
                deltaLabel={tx('düne göre')}
                unit="talep"
                loading={allWorkflows.isPending}
                data={requestSeries}
                dateFormatter={(d) => formatDate(d)}
                periodOptions={[
                  { label: tx('Son 7 gün'), points: 7 },
                  { label: tx('Son 14 gün'), points: 14 },
                  { label: tx('Son 30 gün') },
                ]}
                period={tx('Son 14 gün')}
                className="h-full"
              />
            </Suspense>
          </Tile>
        )}

      </>
    ),
    queue: (
      <>
        {/* --------------------------- Sıradaki talepler --------------------------- */}
        {canWorkflow && (
          <Tile i={8} className="md:col-span-6 xl:col-span-4 xl:row-span-2">
            <Panel className="h-full gap-0 overflow-hidden py-0">
              <div className="flex items-center justify-between border-b border-border px-5 py-4">
                <div>
                  <p className="flex items-center gap-2 text-[14.5px] font-semibold tracking-tight">
                    <Zap className="size-4 text-primary" />
                    {tx('Sıradaki talepler')}
                  </p>
                  <p className="mt-0.5 text-[12.5px] text-muted-foreground">{tx('Süresi geçenler en üstte')}</p>
                </div>
                <Link to="/panel/onaylar" className="group inline-flex items-center gap-1 text-[12.5px] font-medium text-primary">
                  {tx('Tümü')}
                  <ArrowUpRight className="size-3.5 transition-transform group-hover:translate-x-0.5 group-hover:-translate-y-0.5" />
                </Link>
              </div>
              {pending.isPending ? (
                <RowsSkeleton rows={5} columns={2} />
              ) : pending.isError ? (
                <ErrorState
                  message={pending.error instanceof Error ? pending.error.message : undefined}
                  onRetry={() => void pending.refetch()}
                />
              ) : queue.length === 0 ? (
                <EmptyState title={tx('Kuyruk boş')} detail={tx('Karar bekleyen talep yok.')} />
              ) : (
                <ul className="space-y-2 p-3">
                  <AnimatePresence initial>
                    {queue.map((w, i) => {
                      const late = w.slaDueAt ? new Date(w.slaDueAt).getTime() < Date.now() : false
                      return (
                        <motion.li
                          key={w.id}
                          layout
                          initial={reduced ? false : { opacity: 0, scale: 0.9, y: 24 }}
                          animate={{ opacity: 1, scale: 1, y: 0 }}
                          transition={{ type: 'spring', stiffness: 350, damping: 40, delay: 0.5 + i * 0.12 }}
                        >
                          <Link
                            to={`/panel/onaylar/${w.id}`}
                            className="group flex items-center gap-3 rounded-xl border border-border/70 bg-card/50 p-3 transition-all hover:-translate-y-0.5 hover:border-foreground/20 hover:bg-accent"
                          >
                            <span
                              className={cn(
                                'flex size-9 shrink-0 items-center justify-center rounded-xl',
                                late ? 'bg-destructive/15 text-destructive' : 'bg-muted text-foreground/70',
                              )}
                            >
                              {late ? <TriangleAlert className="size-4" /> : <Inbox className="size-4" />}
                            </span>
                            <span className="min-w-0 flex-1">
                              <span className="block truncate text-[13px] font-medium">
                                {w.subject || workflowTypeLabels[w.type]}
                              </span>
                              <span className="mt-0.5 block truncate text-[11.5px] text-muted-foreground">
                                {workflowTypeLabels[w.type]} · {formatRelativeToNow(w.createdAt)}
                              </span>
                            </span>
                            {w.slaDueAt ? (
                              <StatusBadge tone={late ? 'danger' : 'neutral'}>
                                {late ? tx('Gecikti') : formatRelativeToNow(w.slaDueAt)}
                              </StatusBadge>
                            ) : (
                              <WorkflowStatusBadge status={w.status} />
                            )}
                          </Link>
                        </motion.li>
                      )
                    })}
                  </AnimatePresence>
                </ul>
              )}
            </Panel>
          </Tile>
        )}

      </>
    ),
    organization: (
      <>
        {/* ----------------------------- Organizasyon ----------------------------- */}
        {can('organization:view') && (
          <Tile i={9} className="md:col-span-6 xl:col-span-4">
            <Panel className="h-full gap-0 overflow-hidden py-0">
              <div className="border-b border-border px-5 py-4">
                <p className="text-[14.5px] font-semibold tracking-tight">{tx('Organizasyon')}</p>
                <p className="mt-0.5 text-[12.5px] text-muted-foreground">{tx('Şirketler ve departmanlar')}</p>
              </div>
              {companies.isPending ? (
                <RowsSkeleton rows={3} columns={2} />
              ) : companies.isError ? (
                <ErrorState
                  message={companies.error instanceof Error ? companies.error.message : undefined}
                  onRetry={() => void companies.refetch()}
                />
              ) : (companies.data?.length ?? 0) === 0 ? (
                <EmptyState icon={Building2} title={tx('Şirket kaydı yok')} detail={tx('Organizasyon sayfasından ilk şirketi ekleyin.')} />
              ) : (
                <ul className="p-2">
                  {companies.data!.slice(0, 5).map((c) => (
                    <li key={c.id}>
                      <Link
                        to={`/panel/organizasyon/${c.id}`}
                        className="group flex items-center gap-3 rounded-xl px-3 py-2.5 transition-colors hover:bg-accent"
                      >
                        <span className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-muted text-[13px] font-semibold text-foreground ring-1 ring-border transition-transform group-hover:scale-110">
                          {c.name.charAt(0).toUpperCase()}
                        </span>
                        <span className="min-w-0 flex-1 truncate text-[13.5px] font-medium">{c.name}</span>
                        <span className="tabular rounded-full bg-muted px-2 py-0.5 text-[11px] text-muted-foreground">
                          {tx('{0} dept.', [formatNumber(c.departments?.length ?? 0)])}</span>
                        <ChevronRight className="size-4 text-muted-foreground/40 transition-transform group-hover:translate-x-0.5" />
                      </Link>
                    </li>
                  ))}
                </ul>
              )}
            </Panel>
          </Tile>
        )}

      </>
    ),
    modules: (
      <>
        {/* ---------------------------- Modül fırlatıcı ---------------------------- */}
        <Tile i={10} className={cn('md:col-span-6', orgVisible ? 'xl:col-span-4' : 'xl:col-span-8')}>
          <Panel className="h-full gap-0 overflow-hidden py-0">
            <div className="border-b border-border px-5 py-4">
              <p className="text-[14.5px] font-semibold tracking-tight">{tx('Modülleriniz')}</p>
              <p className="mt-0.5 text-[12.5px] text-muted-foreground">{tx('Yetkinize göre açık olan alanlar')}</p>
            </div>
            <ul className="grid grid-cols-3 gap-1.5 p-3 sm:grid-cols-4">
              {modules.slice(0, 12).map((m, i) => (
                <motion.li
                  key={m.id}
                  initial={reduced ? false : { opacity: 0, scale: 0.8 }}
                  animate={{ opacity: 1, scale: 1 }}
                  transition={{ delay: 0.6 + i * 0.03, type: 'spring', stiffness: 400, damping: 25 }}
                >
                  <Link
                    to={m.path!}
                    className="group flex flex-col items-center gap-1.5 rounded-xl px-1 py-3 text-center transition-colors hover:bg-accent"
                  >
                    <span className="flex size-10 items-center justify-center rounded-xl border border-border bg-card text-foreground/70 transition-all duration-300 group-hover:-translate-y-1 group-hover:border-primary/40 group-hover:text-primary">
                      <m.icon className="size-[18px]" strokeWidth={1.6} />
                    </span>
                    <span className="line-clamp-1 text-[11.5px] text-muted-foreground group-hover:text-foreground">{m.title}</span>
                  </Link>
                </motion.li>
              ))}
            </ul>
          </Panel>
        </Tile>

      </>
    ),
    team: (
      <Tile i={1} className="md:col-span-6 xl:col-span-4 xl:row-span-2">
        <TeamBoardWidget />
      </Tile>
    ),
    todos: (
      <Tile i={1} className="md:col-span-6 xl:col-span-4 xl:row-span-2">
        <TodosWidget />
      </Tile>
    ),
    eom: (
      <Tile i={1} className="md:col-span-6 xl:col-span-4 xl:row-span-2">
        <WinnerSpotlight compact />
      </Tile>
    ),
    pinned: (
      <>
        {/* ------------------- Sabitlenmiş raporlar (G4; boşsa görünmez) ------------------- */}
        {can('performance:manage') && <PinnedReportsWidget className="md:col-span-6 xl:col-span-12" />}
      </>
    ),
  }

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <Button size="sm" variant="ghost" className="cursor-pointer text-muted-foreground" onClick={() => setEditing(true)}>
          <SlidersHorizontal className="size-4" />
          {tx('Paneli düzenle')}
        </Button>
      </div>
      {visibleWidgets.length === 0 ? (
        <EmptyState icon={LayoutDashboard} title={tx('Tüm kartlar gizli')} detail={tx('Paneli düzenle düğmesiyle göstermek istediğiniz kartları seçin.')} />
      ) : (
        <div className="grid grid-flow-dense grid-cols-1 gap-4 md:grid-cols-6 xl:grid-cols-12">
          {visibleWidgets.map((id) => <Fragment key={id}>{widgets[id]}</Fragment>)}
        </div>
      )}
      <DashboardCustomizer
        open={editing}
        onClose={() => setEditing(false)}
        available={availableWidgets}
        layout={layout}
        defaults={roleDefaults}
        ready={dashPref.ready}
        onSave={(next) => void dashPref.set(next)}
      />
    </div>
  )
}
