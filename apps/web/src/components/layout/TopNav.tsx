/**
 * Panelin yüzen üst menüsü.
 *
 * Kaynaklar:
 *  - 21st.dev "Tubelight Navbar" (ayushmxxn, id 1432): etkin bölümün üstünde
 *    yanan "tüp ışığı" ve bölümler arasında kayan hap (layoutId).
 *  - 21st.dev "Navbar Menu" (aceternity, id 1024): üzerine gelince açılan,
 *    bölümler arasında biçim değiştirerek kayan mega menü paneli.
 *
 * HR360 uyarlamaları: bölümler rol/izne göre filtrelenen gerçek modül ağacı;
 * mega menüde her modül açıklamasıyla, alt sayfalar çip olarak; klavye ile
 * açılır (Enter/Space), Esc ve dışarı tıklama kapatır; dar ekranda tam
 * ekran animasyonlu menüye dönüşür.
 */

import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import {
  ArrowUpRight,
  Building2,
  Check,
  ChevronDown,
  LogOut,
  Menu,
  Moon,
  Search,
  Sun,
  UserCog,
  UserRound,
  Download,
  Megaphone,
  Eye,
  EyeOff,
  X,
} from 'lucide-react'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { NotificationBell } from '@/features/notification/NotificationBell'
import { useAuth } from '@/auth/useAuth'
import { primaryRole, roleLabels } from '@/auth/roles'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import {
  overviewItem,
  type NavGroupData,
  type NavItemData,
} from './nav-config'
import { useLocate, useNavGroups } from './use-nav'
import { useInstallPrompt } from '@/lib/pwa'
import { usePrivacyScreen } from '@/lib/privacy-screen'
import { tx } from '@/lib/i18n'
import { useEmployeePhoto } from '@/api/employeePhoto'
import { useMyEmployeeId } from '@/api/queries'
import { LanguageToggle } from '@/components/layout/LanguageToggle'
import { OPEN_WHATS_NEW_EVENT, WhatsNewButton } from '@/features/whats-new/WhatsNewPanel'

const SPRING = { type: 'spring' as const, mass: 0.5, damping: 14, stiffness: 120, restDelta: 0.001 }

function useVisibleGroups() {
  return useNavGroups()
}

/* ------------------------------- Marka ------------------------------- */

function Brand() {
  const { tenant } = useAuth()
  const name = tenant?.name ?? 'HR360'
  return (
    <Link to="/panel" className="group flex min-w-0 items-center gap-2.5 rounded-xl pr-2 outline-none focus-visible:ring-2 focus-visible:ring-primary/50">
      {tenant?.logoUrl ? (
        <img src={tenant.logoUrl} alt={name} className="h-8 max-w-[72px] shrink-0 rounded-lg bg-white/90 object-contain p-0.5" />
      ) : (
        <span className="relative flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-[10px] bg-gradient-to-br from-primary to-[hsl(var(--brand-deep))] shadow-[0_0_20px_-4px_hsl(var(--primary)/0.9),inset_0_1px_0_0_rgb(255_255_255/0.3)]">
          <img src="/icon-white.svg" alt="" aria-hidden="true" className="size-5 transition-transform duration-500 group-hover:rotate-[360deg]" />
        </span>
      )}
      <span className="hidden min-w-0 flex-col leading-none sm:flex">
        <span className="truncate text-[13.5px] font-semibold tracking-tight">{name}</span>
        <span className="mt-1 text-[10.5px] font-medium tracking-[0.12em] text-muted-foreground uppercase">
          {tx('HR360')}
        </span>
      </span>
    </Link>
  )
}

/* ------------------------------- Mega menü ------------------------------- */

function MegaPanel({ group, onNavigate }: { group: NavGroupData; onNavigate: () => void }) {
  const { pathname } = useLocation()
  const here = useLocate(pathname)
  const Icon = group.icon
  const cols = group.items.length > 4 ? 'grid-cols-2' : group.items.length > 1 ? 'grid-cols-2' : 'grid-cols-1'

  return (
    <div className="flex w-[min(760px,calc(100vw-2rem))] gap-4 p-3">
      {/* Tanıtım kutusu */}
      <div className="relative hidden w-56 shrink-0 overflow-hidden rounded-xl border border-border bg-gradient-to-br from-accent to-transparent p-4 md:block">
        <div className="absolute -right-10 -bottom-10 size-40 rounded-full border border-dashed border-foreground/10" />
        <div className="absolute -right-4 -bottom-4 size-24 rounded-full border border-foreground/10" />
        <span className="animate-float relative flex size-10 items-center justify-center rounded-xl bg-primary text-primary-foreground shadow-[0_0_24px_-4px_hsl(var(--primary))]">
          <Icon className="size-5" strokeWidth={1.75} />
        </span>
        <p className="relative mt-4 text-[15px] font-semibold tracking-tight">{group.heading}</p>
        <p className="relative mt-1.5 text-[12.5px] leading-relaxed text-muted-foreground">{group.tagline}</p>
      </div>

      <ul className={cn('grid flex-1 gap-1', cols)}>
        {group.items.map((item, i) => {
          const target = item.path ?? item.children?.[0]?.path ?? '/panel'
          const active = here.item?.id === item.id || here.parent?.id === item.id
          return (
            <motion.li
              key={item.id}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: 0.03 * i, duration: 0.25 }}
            >
              <Link
                to={target}
                onClick={onNavigate}
                className={cn(
                  'group/item flex gap-3 rounded-xl p-2.5 transition-colors',
                  active ? 'bg-accent' : 'hover:bg-accent',
                )}
              >
                <span
                  className={cn(
                    'flex size-9 shrink-0 items-center justify-center rounded-lg border transition-all duration-300 group-hover/item:scale-110 group-hover/item:rotate-[-6deg]',
                    active
                      ? 'border-primary/40 bg-primary/15 text-primary'
                      : 'border-border bg-card text-foreground/70 group-hover/item:border-primary/40 group-hover/item:text-primary',
                  )}
                >
                  <item.icon className="size-[18px]" strokeWidth={1.7} />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="flex items-center gap-1 text-[13.5px] font-medium">
                    {item.title}
                    <ArrowUpRight className="size-3.5 -translate-x-1 opacity-0 transition-all group-hover/item:translate-x-0 group-hover/item:opacity-60" />
                  </span>
                  {item.description && (
                    <span className="mt-0.5 block truncate text-[12px] text-muted-foreground">{item.description}</span>
                  )}
                </span>
              </Link>
              {item.children && item.children.length > 1 && (
                <div className="mt-0.5 mb-1 flex flex-wrap gap-1 pl-[52px]">
                  {item.children.map((child) => (
                    <Link
                      key={child.id}
                      to={child.path!}
                      onClick={onNavigate}
                      className={cn(
                        'rounded-full border px-2 py-0.5 text-[11.5px] transition-colors',
                        here.item?.id === child.id
                          ? 'border-primary/40 bg-primary/15 text-primary'
                          : 'border-border text-muted-foreground hover:border-primary/30 hover:text-foreground',
                      )}
                    >
                      {child.title}
                    </Link>
                  ))}
                </div>
              )}
            </motion.li>
          )
        })}
      </ul>
    </div>
  )
}

function SectionTabs() {
  const groups = useVisibleGroups()
  const { pathname } = useLocation()
  const here = useLocate(pathname)
  const [open, setOpen] = useState<string | null>(null)
  const closeTimer = useRef<number | undefined>(undefined)
  const reduced = useReducedMotion()

  const activeId = here.group?.id ?? (here.item?.id === 'overview' ? 'overview' : null)

  useEffect(() => setOpen(null), [pathname])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(null)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const enter = (id: string) => {
    window.clearTimeout(closeTimer.current)
    setOpen(id)
  }
  const leave = () => {
    closeTimer.current = window.setTimeout(() => setOpen(null), 140)
  }

  const tabs: Array<{ id: string; label: string; group?: NavGroupData }> = [
    { id: 'overview', label: overviewItem.title },
    ...groups.map((g) => ({ id: g.id, label: g.heading, group: g })),
  ]

  return (
    <nav aria-label={tx('Ana menü')} className="relative hidden lg:block" onMouseLeave={leave}>
      <ul className="flex items-center gap-0.5">
        {tabs.map((tab) => {
          const isActive = activeId === tab.id
          const isOpen = open === tab.id
          const cls = cn(
            'relative flex cursor-pointer items-center gap-1 rounded-full px-3.5 py-1.5 text-[13px] font-medium transition-colors outline-none focus-visible:ring-2 focus-visible:ring-primary/50',
            isActive ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
          )
          const lamp = isActive && (
            <motion.span
              layoutId="nav-lamp"
              transition={{ type: 'spring', stiffness: 300, damping: 30 }}
              className="absolute inset-0 -z-10 rounded-full bg-accent ring-1 ring-border"
            >
              <span className="absolute -top-[9px] left-1/2 h-[3px] w-8 -translate-x-1/2 rounded-b-full bg-primary">
                <span className="absolute -top-2 -left-2 h-6 w-12 rounded-full bg-primary/30 blur-md" />
                <span className="absolute -top-1 h-6 w-8 rounded-full bg-primary/30 blur-md" />
              </span>
            </motion.span>
          )
          return (
            <li key={tab.id} className="relative" onMouseEnter={() => (tab.group ? enter(tab.id) : leave())}>
              {tab.group ? (
                <button
                  type="button"
                  aria-expanded={isOpen}
                  aria-haspopup="true"
                  onClick={() => setOpen(isOpen ? null : tab.id)}
                  className={cls}
                >
                  {lamp}
                  {tab.label}
                  <ChevronDown className={cn('size-3.5 opacity-60 transition-transform duration-300', isOpen && 'rotate-180')} />
                </button>
              ) : (
                <Link to="/panel" className={cls}>
                  {lamp}
                  {tab.label}
                </Link>
              )}
            </li>
          )
        })}
      </ul>

      <AnimatePresence>
        {open && (
          <motion.div
            initial={reduced ? false : { opacity: 0, scale: 0.92, y: 10 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.96, y: 6, transition: { duration: 0.15 } }}
            transition={SPRING}
            style={{ x: '-50%' }}
            className="absolute top-[calc(100%+14px)] left-1/2 z-50 pt-1"
            onMouseEnter={() => window.clearTimeout(closeTimer.current)}
          >
            <motion.div
              layoutId="mega-panel"
              transition={SPRING}
              className="overflow-hidden rounded-2xl border border-border bg-popover shadow-popover dark:shadow-[inset_0_1px_0_0_hsl(var(--edge-light)),0_30px_80px_-20px_rgb(0_0_0/0.9)]"
            >
              <motion.div layout>
                {groups
                  .filter((g) => g.id === open)
                  .map((g) => (
                    <MegaPanel key={g.id} group={g} onNavigate={() => setOpen(null)} />
                  ))}
              </motion.div>
            </motion.div>
          </motion.div>
        )}
      </AnimatePresence>
    </nav>
  )
}

/* ------------------------------- Sağ küme ------------------------------- */

function ThemeToggle() {
  const { isDark, toggle } = useTheme()
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={isDark ? tx('Açık temaya geç') : tx('Koyu temaya geç')}
      className="relative flex size-9 cursor-pointer items-center justify-center overflow-hidden rounded-full text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
    >
      <AnimatePresence mode="wait" initial={false}>
        <motion.span
          key={isDark ? 'sun' : 'moon'}
          initial={{ y: -20, rotate: -90, opacity: 0 }}
          animate={{ y: 0, rotate: 0, opacity: 1 }}
          exit={{ y: 20, rotate: 90, opacity: 0 }}
          transition={{ duration: 0.25 }}
        >
          {isDark ? <Sun className="size-[18px]" strokeWidth={1.75} /> : <Moon className="size-[18px]" strokeWidth={1.75} />}
        </motion.span>
      </AnimatePresence>
    </button>
  )
}

function AccountMenu() {
  const { user, roles, logout, accountUrl, tenant, canSwitchTenant, availableTenants, switchTenant } = useAuth()
  const install = useInstallPrompt()
  const [privacy, setPrivacy] = usePrivacyScreen()
  const photo = useEmployeePhoto(useMyEmployeeId(!!tenant).employeeId)
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={tx('{0} — Hesap menüsü', [user?.initials ?? tx('HR')])}
          className="group relative ml-0.5 flex cursor-pointer items-center rounded-full outline-none focus-visible:ring-2 focus-visible:ring-primary/50"
        >
          <span className="absolute -inset-[3px] rounded-full bg-[conic-gradient(from_0deg,hsl(var(--primary)),transparent_40%,hsl(var(--brand-glow)),transparent_80%,hsl(var(--primary)))] opacity-40 transition-opacity group-hover:opacity-90 motion-safe:animate-[spin_6s_linear_infinite]" />
          {photo ? (
            <img src={photo} alt="" className="relative size-8 rounded-full object-cover ring-2 ring-background" />
          ) : (
            <span className="relative flex size-8 items-center justify-center rounded-full bg-card text-[11.5px] font-semibold text-foreground ring-2 ring-background">
              {user?.initials ?? tx('HR')}
            </span>
          )}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="font-normal">
          <span className="block truncate text-[13px] font-semibold">{user?.fullName ?? tx('Kullanıcı')}</span>
          <span className="mt-0.5 block truncate text-[12px] text-muted-foreground">{user?.email ?? user?.username}</span>
          <span className="mt-1.5 inline-flex rounded-full bg-primary/10 px-2 py-0.5 text-[11px] font-medium text-primary">
            {roleLabels[primaryRole(roles)]}
          </span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {canSwitchTenant && availableTenants.length > 0 && (
          <>
            <DropdownMenuSub>
              <DropdownMenuSubTrigger>
                <Building2 className="size-4" strokeWidth={1.5} />{tx('Kiracı: {0}', [tenant?.name ?? tx('seçilmedi')])}
              </DropdownMenuSubTrigger>
              <DropdownMenuSubContent className="max-h-72 w-56 overflow-y-auto">
                {availableTenants.map((t) => (
                  <DropdownMenuItem key={t.slug} onSelect={() => switchTenant(t.slug)}>
                    <span className="flex-1 truncate">{t.name}</span>
                    {tenant?.slug === t.slug && <Check className="size-3.5 text-primary" />}
                  </DropdownMenuItem>
                ))}
              </DropdownMenuSubContent>
            </DropdownMenuSub>
            <DropdownMenuSeparator />
          </>
        )}
        <DropdownMenuItem asChild>
          <Link to="/panel/profil">
            <UserRound className="size-4" strokeWidth={1.5} />
            {tx('Profilim')}
          </Link>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <Link to="/panel/ayarlar">
            <UserCog className="size-4" strokeWidth={1.5} />
            {tx('Ayarlar')}
          </Link>
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={(e) => { e.preventDefault(); setPrivacy(!privacy) }}>
          {privacy ? <EyeOff className="size-4" strokeWidth={1.5} /> : <Eye className="size-4" strokeWidth={1.5} />}
          {tx('Ekran paylaşım modu')}{' '}{privacy ? tx('açık') : tx('kapalı')}
        </DropdownMenuItem>
        {install && (
          <DropdownMenuItem onSelect={() => void install()}>
            <Download className="size-4" strokeWidth={1.5} />
            {tx('Uygulama olarak yükle')}
          </DropdownMenuItem>
        )}
        <DropdownMenuItem onSelect={() => window.dispatchEvent(new CustomEvent(OPEN_WHATS_NEW_EVENT))}>
          <Megaphone className="size-4" strokeWidth={1.5} />
          {tx('Yenilikler')}
        </DropdownMenuItem>
        {accountUrl !== '#' && (
          <DropdownMenuItem asChild>
            <a href={accountUrl} target="_blank" rel="noreferrer">
              <UserCog className="size-4" strokeWidth={1.5} />
              {tx('Keycloak hesabım')}
            </a>
          </DropdownMenuItem>
        )}
        <DropdownMenuSeparator />
        <DropdownMenuItem variant="destructive" onSelect={() => logout()}>
          <LogOut className="size-4" strokeWidth={1.5} />
          {tx('Oturumu kapat')}
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <p className="px-2 py-1.5 text-[11px] text-muted-foreground" data-testid="app-version">
          {tx('HR360 sürüm {0}', [__APP_VERSION__])}
        </p>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/* ------------------------------- Mobil menü ------------------------------- */

function MobileMenu({ open, onClose }: { open: boolean; onClose: () => void }) {
  const groups = useVisibleGroups()
  const { pathname } = useLocation()
  const here = useLocate(pathname)
  const navigate = useNavigate()

  useEffect(() => {
    if (!open) return
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = prev
    }
  }, [open])

  const go = (item: NavItemData) => {
    const target = item.path ?? item.children?.[0]?.path
    if (target) navigate(target)
    onClose()
  }

  // Üst menünün yığın bağlamından çıkıp rıhtımın da üstünde açılsın.
  return createPortal(
    <AnimatePresence>
      {open && (
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          className="fixed inset-0 z-[60] overflow-y-auto bg-background/95 backdrop-blur-xl lg:hidden"
        >
          <div className="flex items-center justify-between px-5 pt-5">
            <Brand />
            <button
              type="button"
              aria-label={tx('Menüyü kapat')}
              onClick={onClose}
              className="flex size-10 cursor-pointer items-center justify-center rounded-full border border-border"
            >
              <X className="size-5" />
            </button>
          </div>
          <nav aria-label={tx('Modüller')} className="space-y-7 px-5 pt-8 pb-32">
            {[{ id: 'overview', heading: tx('Başlangıç'), items: [overviewItem] } as unknown as NavGroupData, ...groups].map(
              (group, gi) => (
                <motion.section
                  key={group.id}
                  initial={{ opacity: 0, y: 16 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ delay: 0.05 * gi, duration: 0.35 }}
                >
                  <p className="mb-2.5 text-[11px] font-medium tracking-[0.14em] text-muted-foreground uppercase">{group.heading}</p>
                  <div className="grid grid-cols-2 gap-2">
                    {group.items.map((item) => {
                      const active = here.item?.id === item.id || here.parent?.id === item.id
                      return (
                        <button
                          key={item.id}
                          type="button"
                          onClick={() => go(item)}
                          className={cn(
                            'flex cursor-pointer items-center gap-2.5 rounded-xl border p-3 text-left text-[13.5px] transition-colors',
                            active ? 'border-primary/40 bg-accent text-foreground' : 'border-border bg-card/60',
                          )}
                        >
                          <item.icon className="size-[18px] shrink-0 text-primary" strokeWidth={1.7} />
                          <span className="truncate">{item.title}</span>
                        </button>
                      )
                    })}
                  </div>
                </motion.section>
              ),
            )}
          </nav>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body,
  )
}

/* ------------------------------- Üst menü ------------------------------- */

export function TopNav({ onOpenCommandPalette }: { onOpenCommandPalette: () => void }) {
  const [mobileOpen, setMobileOpen] = useState(false)
  const { pathname } = useLocation()
  const reduced = useReducedMotion()
  const [scrolled, setScrolled] = useState(false)

  useEffect(() => setMobileOpen(false), [pathname])
  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 8)
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  return (
    <>
      <motion.header
        initial={reduced ? false : { y: -40, opacity: 0 }}
        animate={{ y: 0, opacity: 1 }}
        transition={{ duration: 0.6, ease: [0.16, 1, 0.3, 1] }}
        className="sticky top-0 z-40 px-3 pt-3 sm:px-5"
      >
        <div
          className={cn(
            'mx-auto flex h-[58px] max-w-[1480px] items-center gap-3 rounded-2xl border px-2.5 transition-[background-color,box-shadow,border-color] duration-300 sm:px-3',
            'backdrop-blur-xl',
            scrolled
              ? 'border-border bg-background/75 shadow-[0_18px_50px_-20px_rgb(0_0_0/0.75),inset_0_1px_0_0_hsl(var(--edge-light))]'
              : 'border-border/50 bg-background/35',
          )}
        >
          <Brand />
          <div className="flex flex-1 justify-center">
            <SectionTabs />
          </div>

          <div className="flex items-center gap-0.5">
            <button
              type="button"
              onClick={onOpenCommandPalette}
              className="mr-1 hidden h-9 cursor-pointer items-center gap-2 rounded-full border border-border bg-card/50 pr-1.5 pl-3 text-[13px] text-muted-foreground transition-colors hover:border-primary/40 hover:text-foreground xl:flex"
            >
              <Search className="size-4" strokeWidth={1.6} />
              <span className="w-24 text-left">{tx('Ara…')}</span>
              <kbd className="rounded-full border border-border bg-muted px-2 py-0.5 font-mono text-[10px]">{tx('⌘K')}</kbd>
            </button>
            <button
              type="button"
              aria-label={tx('Ara')}
              onClick={onOpenCommandPalette}
              className="flex size-9 cursor-pointer items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-accent hover:text-foreground xl:hidden"
            >
              <Search className="size-[18px]" strokeWidth={1.75} />
            </button>
            <NotificationBell />
            <WhatsNewButton className="hidden sm:flex" />
            <LanguageToggle className="hidden sm:flex" />
            <ThemeToggle />
            <AccountMenu />
            <button
              type="button"
              aria-label={tx('Menüyü aç')}
              onClick={() => setMobileOpen(true)}
              className="ml-1 flex size-9 cursor-pointer items-center justify-center rounded-full border border-border text-foreground lg:hidden"
            >
              <Menu className="size-[18px]" />
            </button>
          </div>
        </div>
      </motion.header>
      <MobileMenu open={mobileOpen} onClose={() => setMobileOpen(false)} />
    </>
  )
}
