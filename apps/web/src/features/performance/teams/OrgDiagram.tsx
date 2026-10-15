/**
 * Organizasyon diyagramı — herkese açık, salt okunur.
 *
 * İki düzen: "Ağaç" (yukarıdan aşağı şema, yakınlaştır/sürükle) ve "Liste"
 * (soldan girintili; mobilde varsayılan). Alt departmanlar iç içe çizilir.
 * Arama bir kişiyi, ekibi ya da departmanı bulur, yolunu vurgular ve ekrana
 * kaydırır. Ekip kartına tıklamak ekip ayrıntısını yan panelde açar.
 *
 * Düğümler bileşen değil düz fonksiyon olarak çizilir: render içinde
 * tanımlanan bileşen her durum değişiminde yeniden bağlanır ve giriş
 * animasyonu tekrar oynardı.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { Building2, ChevronDown, CornerDownRight, LocateFixed, Maximize2, Minus, Network, Plus, Rows3, Search, UserRoundX, X } from 'lucide-react'
import { useMyEmployeeId } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { Chip, Segmented } from '../components/controls'
import { AvatarStack, PersonAvatar } from '../components/people'
import { walkDepartments, type OrgCompany, type OrgDepartment, type OrgTeam } from './useOrgTree'
import { tx, appLocale } from '@/lib/i18n'
import './orgchart.css'

type Layout = 'tree' | 'list'

const DEPT_COLORS = ['--chart-2', '--chart-3', '--chart-5', '--chart-4', '--chart-1']
const deptColor = (i: number) => `hsl(var(${DEPT_COLORS[i % DEPT_COLORS.length]}))`

const norm = (s: string) => s.toLocaleLowerCase(appLocale)

export type SheetTarget = { kind: 'team'; team: OrgTeam; department: OrgDepartment } | { kind: 'unassigned'; department: OrgDepartment }

function counts(d: OrgDepartment) {
  return tx('{0} ekip', [d.teamCount]) + (d.employeeCount !== null ? tx(' · {0} çalışan', [d.employeeCount]) : '')
}

export function OrgDiagram({
  tree,
  onOpen,
  highlightTeamId,
}: {
  tree: OrgCompany[]
  onOpen: (target: SheetTarget) => void
  highlightTeamId?: string | null
}) {
  const [layout, setLayout] = useState<Layout>(() => (typeof window !== 'undefined' && window.innerWidth < 768 ? 'list' : 'tree'))
  const [query, setQuery] = useState('')
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [hoverTeam, setHoverTeam] = useState<string | null>(null)
  const [zoom, setZoom] = useState(1)
  const viewport = useRef<HTMLDivElement>(null)
  const canvas = useRef<HTMLDivElement>(null)
  const me = useMyEmployeeId()

  const allDepts = useMemo(() => tree.flatMap((c) => walkDepartments(c.departments)), [tree])

  /* ------------------------------ arama eşleşmesi ------------------------------ */
  const q = norm(query.trim())
  const match = useMemo(() => {
    const teams = new Set<string>()
    const people = new Set<string>()
    const depts = new Set<string>()
    if (!q) return { teams, people, depts, any: false }
    for (const d of allDepts) {
      if (norm(d.name).includes(q)) depts.add(d.id)
      for (const t of d.teams) {
        if (norm(t.team.name).includes(q)) teams.add(t.team.id)
        for (const m of t.members)
          if (norm(m.name).includes(q)) {
            people.add(m.id)
            teams.add(t.team.id)
          }
      }
      for (const u of d.unassigned ?? []) if (norm(u.name).includes(q)) people.add(u.id)
    }
    return { teams, people, depts, any: teams.size + people.size + depts.size > 0 }
  }, [q, allDepts])

  const focusTeam = hoverTeam ?? highlightTeamId ?? null
  const teamHit = (t: OrgTeam) => t.team.id === focusTeam || match.teams.has(t.team.id)
  const unHit = (d: OrgDepartment) => (d.unassigned ?? []).some((u) => match.people.has(u.id))
  const deptHit = (d: OrgDepartment): boolean => match.depts.has(d.id) || d.teams.some(teamHit) || unHit(d) || d.children.some(deptHit)

  /* Arama sonucu ilk eşleşmeye kaydır. */
  useEffect(() => {
    if (!match.any) return
    const t = window.setTimeout(() => {
      viewport.current?.querySelector('[data-match="true"]')?.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'center' })
    }, 120)
    return () => window.clearTimeout(t)
  }, [match, layout])

  /* Vurgulanan ekibe (ör. yönetimden gelinmişse) kaydır. */
  useEffect(() => {
    if (!highlightTeamId) return
    const t = window.setTimeout(() => {
      viewport.current?.querySelector(`[data-team="${highlightTeamId}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'center' })
    }, 400)
    return () => window.clearTimeout(t)
  }, [highlightTeamId, layout])

  const toggle = (id: string) =>
    setCollapsed((s) => {
      const n = new Set(s)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
    })

  /* ----------------------------- yakınlaştır / sürükle ----------------------------- */
  const fit = useCallback(() => {
    const vp = viewport.current
    const cv = canvas.current
    if (!vp || !cv) return
    const natural = cv.scrollWidth / zoom
    setZoom(Math.max(0.4, Math.min(1, (vp.clientWidth - 32) / natural)))
  }, [zoom])

  /* İlk açılışta şema ekrandan genişse bir kez sığdır (en fazla %60'a kadar küçült). */
  const fitted = useRef(false)
  useEffect(() => {
    if (fitted.current || layout !== 'tree' || !tree.length) return
    const t = window.setTimeout(() => {
      const vp = viewport.current
      const cv = canvas.current
      if (!vp || !cv) return
      fitted.current = true
      const ratio = (vp.clientWidth - 32) / cv.scrollWidth
      if (ratio < 1) setZoom(Math.max(0.6, +ratio.toFixed(2)))
    }, 50)
    return () => window.clearTimeout(t)
  }, [layout, tree])

  const drag = useRef<{ x: number; y: number; left: number; top: number } | null>(null)
  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (layout !== 'tree' || (e.target as HTMLElement).closest('button,a,input')) return
    const vp = viewport.current!
    drag.current = { x: e.clientX, y: e.clientY, left: vp.scrollLeft, top: vp.scrollTop }
    vp.setPointerCapture(e.pointerId)
  }
  const onPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!drag.current) return
    const vp = viewport.current!
    vp.scrollLeft = drag.current.left - (e.clientX - drag.current.x)
    vp.scrollTop = drag.current.top - (e.clientY - drag.current.y)
  }
  const onPointerUp = () => {
    drag.current = null
  }

  const findMe = () => {
    if (!me.employeeId) return
    for (const d of allDepts) {
      const hit = [...d.teams.flatMap((t) => t.members), ...(d.unassigned ?? [])].find((p) => p.id === me.employeeId)
      if (hit) {
        setQuery(hit.name)
        return
      }
    }
  }

  const dim = (hit: boolean) => (match.any && !hit ? 'opacity-35 saturate-50' : '')

  /* --------------------------------- düğümler --------------------------------- */

  const companyNode = (c: OrgCompany) => (
    <motion.div
      initial={{ opacity: 0, y: 10, scale: 0.97 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: 0.5, ease: [0.16, 1, 0.3, 1] }}
      className="relative w-[250px] overflow-hidden rounded-2xl border border-primary/30 bg-card p-4 text-center shadow-lg"
    >
      <div aria-hidden className="absolute inset-x-0 top-0 h-1 bg-gradient-to-r from-primary via-[hsl(var(--chart-2))] to-[hsl(var(--chart-3))]" />
      <span className="mx-auto flex size-10 items-center justify-center rounded-xl bg-primary/10 text-primary">
        <Building2 className="size-5" aria-hidden />
      </span>
      <p className="mt-2 text-[15px] font-semibold">{c.name}</p>
      <p className="tabular mt-0.5 text-[11px] text-muted-foreground">
        {tx('{0} departman · {1} ekip{2}', [c.departmentCount, c.teamCount, c.employeeCount !== null ? tx(' · {0} çalışan', [c.employeeCount]) : ''])}
      </p>
    </motion.div>
  )

  const deptNode = (d: OrgDepartment) => {
    const isCollapsed = collapsed.has(d.id)
    const sub = d.depth > 0
    return (
      <button
        type="button"
        onClick={() => toggle(d.id)}
        aria-expanded={!isCollapsed}
        data-match={match.depts.has(d.id) || undefined}
        className={cn(
          'group relative overflow-hidden rounded-xl border px-3.5 py-3 text-left shadow-sm transition-[box-shadow,opacity,border-color] hover:shadow-md',
          sub ? 'w-[190px] bg-muted/40' : 'w-[200px] bg-card',
          match.depts.has(d.id) ? 'border-primary ring-2 ring-primary/30' : 'border-border',
          dim(deptHit(d)),
        )}
      >
        <span aria-hidden className={cn('absolute inset-y-0 left-0', sub ? 'w-0.5 opacity-70' : 'w-1')} style={{ background: deptColor(d.colorIndex) }} />
        {sub && (
          <span className="mb-0.5 flex items-center gap-1 text-[10px] font-medium text-muted-foreground">
            <CornerDownRight className="size-3" aria-hidden />
            {tx('Alt departman')}
          </span>
        )}
        <span className="flex items-center justify-between gap-2">
          <span className="truncate text-[13px] font-semibold">{d.name}</span>
          <ChevronDown className={cn('size-4 shrink-0 text-muted-foreground transition-transform', isCollapsed && '-rotate-90')} aria-hidden />
        </span>
        <span className="tabular mt-0.5 block text-[11px] text-muted-foreground">{counts(d)}</span>
      </button>
    )
  }

  const teamNode = (t: OrgTeam, d: OrgDepartment) => {
    const hit = match.teams.has(t.team.id)
    const focused = focusTeam === t.team.id
    return (
      <motion.button
        type="button"
        data-team={t.team.id}
        data-match={hit || undefined}
        onClick={() => onOpen({ kind: 'team', team: t, department: d })}
        onMouseEnter={() => setHoverTeam(t.team.id)}
        onMouseLeave={() => setHoverTeam(null)}
        whileHover={{ y: -3 }}
        className={cn(
          'relative w-[210px] rounded-xl border bg-card p-3 text-left shadow-sm transition-[box-shadow,opacity,border-color] hover:shadow-lg',
          !t.team.isActive && 'border-dashed bg-muted/40',
          hit || focused ? 'border-primary ring-2 ring-primary/25' : 'border-border',
          dim(hit),
        )}
      >
        <span className="flex items-center gap-2">
          <span className="size-2 shrink-0 rounded-full" style={{ background: deptColor(d.colorIndex) }} />
          <span className="truncate text-[13px] font-semibold">{t.team.name}</span>
          {!t.team.isActive && <Chip className="ml-auto">{tx('Pasif')}</Chip>}
        </span>
        <span className="mt-2 flex items-center gap-2">
          {t.lead ? (
            <>
              <PersonAvatar id={t.lead.id} name={t.lead.name} size="xs" />
              <span className="min-w-0">
                <span className="block text-[10px] text-muted-foreground">{tx('Takım lideri')}</span>
                <span className={cn('block truncate text-[12px] font-medium', match.people.has(t.lead.id) && 'text-primary')}>{t.lead.name}</span>
              </span>
            </>
          ) : (
            <Chip tone="muted">{tx('Lidersiz')}</Chip>
          )}
        </span>
        <span className="mt-2.5 flex items-center justify-between gap-2 border-t border-border pt-2">
          {t.members.length ? (
            <AvatarStack people={t.members.map((m) => ({ id: m.id, name: m.name }))} max={6} />
          ) : (
            <span className="text-[11px] text-muted-foreground">{tx('Henüz üye yok')}</span>
          )}
          <span className="tabular text-[11px] text-muted-foreground">{tx('{0} üye', [t.members.length])}</span>
        </span>
        {t.members.some((m) => match.people.has(m.id)) && (
          <span className="mt-1.5 block truncate text-[11px] text-primary">
            {t.members.filter((m) => match.people.has(m.id)).map((m) => m.name).join(', ')}
          </span>
        )}
      </motion.button>
    )
  }

  const unassignedNode = (d: OrgDepartment) => {
    const list = d.unassigned ?? []
    const hit = unHit(d)
    return (
      <button
        type="button"
        data-match={hit || undefined}
        onClick={() => onOpen({ kind: 'unassigned', department: d })}
        className={cn(
          'w-[180px] rounded-xl border border-dashed bg-background/60 p-3 text-left transition-[opacity,border-color] hover:border-primary/40',
          hit ? 'border-primary ring-2 ring-primary/25' : 'border-border',
          dim(hit),
        )}
      >
        <span className="flex items-center gap-1.5 text-[12px] font-medium text-muted-foreground">
          <UserRoundX className="size-3.5" aria-hidden />
          {tx('Ekipte olmayanlar')}
        </span>
        <span className="mt-2 flex items-center justify-between gap-2">
          <AvatarStack people={list.map((u) => ({ id: u.id, name: u.name }))} max={4} />
          <span className="tabular text-[11px] text-muted-foreground">{list.length}</span>
        </span>
        {hit && (
          <span className="mt-1.5 block truncate text-[11px] text-primary">
            {list.filter((u) => match.people.has(u.id)).map((u) => u.name).join(', ')}
          </span>
        )}
      </button>
    )
  }

  const delay = (depth: number, i: number): CSSProperties => ({ animationDelay: `${Math.min(depth, 4) * 110 + i * 45}ms` })

  type Kid = { kind: 'dept'; d: OrgDepartment } | { kind: 'team'; t: OrgTeam } | { kind: 'un' }
  const kidsOf = (d: OrgDepartment): Kid[] => [
    ...d.children.map((c) => ({ kind: 'dept' as const, d: c })),
    ...d.teams.map((t) => ({ kind: 'team' as const, t })),
    ...(d.unassigned?.length ? [{ kind: 'un' as const }] : []),
  ]

  /* ---------------------------------- ağaç düzeni -------------------------------- */

  const treeBranch = (d: OrgDepartment, index: number, depth: number): React.ReactNode => {
    const kids = kidsOf(d)
    return (
      <li key={d.id} className={cn(deptHit(d) && 'on-path')} style={delay(depth, index)}>
        {deptNode(d)}
        {!collapsed.has(d.id) && kids.length > 0 && (
          <ul>
            {kids.map((k, ki) =>
              k.kind === 'dept' ? (
                treeBranch(k.d, ki, depth + 1)
              ) : k.kind === 'team' ? (
                <li key={k.t.team.id} className={cn(teamHit(k.t) && 'on-path')} style={delay(depth + 1, ki)}>
                  {teamNode(k.t, d)}
                </li>
              ) : (
                <li key={`${d.id}-un`} className={cn(unHit(d) && 'on-path')} style={delay(depth + 1, ki)}>
                  {unassignedNode(d)}
                </li>
              ),
            )}
          </ul>
        )}
      </li>
    )
  }

  const treeView = (
    <div className="org min-w-max px-6 py-8">
      <ul>
        {tree.map((c) => (
          <li key={c.id} className={cn((focusTeam || match.any) && 'on-path')}>
            {companyNode(c)}
            {c.departments.length > 0 && <ul>{c.departments.map((d, di) => treeBranch(d, di, 1))}</ul>}
          </li>
        ))}
      </ul>
    </div>
  )

  /* --------------------------------- liste düzeni -------------------------------- */

  const listBranch = (d: OrgDepartment): React.ReactNode => (
    <li key={d.id} className={cn(deptHit(d) && 'on-path')}>
      <div className="pt-1">{deptNode(d)}</div>
      <AnimatePresence initial={false}>
        {!collapsed.has(d.id) && (
          <motion.ul initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden">
            {kidsOf(d).map((k) =>
              k.kind === 'dept' ? (
                listBranch(k.d)
              ) : k.kind === 'team' ? (
                <li key={k.t.team.id} className={cn(teamHit(k.t) && 'on-path')}>
                  {teamNode(k.t, d)}
                </li>
              ) : (
                <li key={`${d.id}-un`}>{unassignedNode(d)}</li>
              ),
            )}
          </motion.ul>
        )}
      </AnimatePresence>
    </li>
  )

  const listView = (
    <div className="org-list px-4 py-5">
      <ul>
        {tree.map((c) => (
          <li key={c.id}>
            <div className="flex items-center gap-3 rounded-xl border border-primary/25 bg-card p-3 shadow-sm">
              <span className="flex size-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
                <Building2 className="size-4.5" aria-hidden />
              </span>
              <div className="min-w-0">
                <p className="truncate text-[14px] font-semibold">{c.name}</p>
                <p className="tabular text-[11px] text-muted-foreground">
                  {tx('{0} departman · {1} ekip{2}', [c.departmentCount, c.teamCount, c.employeeCount !== null ? tx(' · {0} çalışan', [c.employeeCount]) : ''])}
                </p>
              </div>
            </div>
            <ul>{c.departments.map((d) => listBranch(d))}</ul>
          </li>
        ))}
      </ul>
    </div>
  )

  return (
    <div className="overflow-hidden rounded-2xl border border-border bg-card">
      {/* Araç çubuğu */}
      <div className="flex flex-col gap-2 border-b border-border p-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex flex-1 items-center gap-2">
          <label className="relative block w-full sm:max-w-xs">
            <span className="sr-only">{tx('Kişi, ekip ya da departman ara')}</span>
            <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={tx('Kişi, ekip ya da departman ara')}
              className="h-9 w-full rounded-md border border-input bg-background pr-8 pl-9 text-[13px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
            />
            {query && (
              <button type="button" onClick={() => setQuery('')} aria-label={tx('Aramayı temizle')} className="absolute top-1/2 right-2 -translate-y-1/2 rounded p-0.5 text-muted-foreground hover:bg-muted">
                <X className="size-3.5" />
              </button>
            )}
          </label>
          {me.employeeId && (
            <Button variant="outline" size="sm" onClick={findMe} title={tx('Beni diyagramda göster')}>
              <LocateFixed aria-hidden />
              <span className="hidden sm:inline">{tx('Beni bul')}</span>
            </Button>
          )}
        </div>
        <div className="flex items-center gap-2">
          <AnimatePresence>
            {q && (
              <motion.span initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} className="text-[12px] text-muted-foreground">
                {match.any ? tx('{0} kişi · {1} ekip', [match.people.size, match.teams.size]) : tx('Eşleşme yok')}
              </motion.span>
            )}
          </AnimatePresence>
          {layout === 'tree' && (
            <div className="flex items-center rounded-md border border-border">
              <Button size="icon-sm" variant="ghost" onClick={() => setZoom((z) => Math.max(0.4, +(z - 0.1).toFixed(2)))} aria-label={tx('Uzaklaştır')}>
                <Minus aria-hidden />
              </Button>
              <span className="tabular w-11 text-center text-[12px] font-medium">%{Math.round(zoom * 100)}</span>
              <Button size="icon-sm" variant="ghost" onClick={() => setZoom((z) => Math.min(1.5, +(z + 0.1).toFixed(2)))} aria-label={tx('Yakınlaştır')}>
                <Plus aria-hidden />
              </Button>
              <Button size="icon-sm" variant="ghost" onClick={fit} aria-label={tx('Ekrana sığdır')} title={tx('Ekrana sığdır')}>
                <Maximize2 aria-hidden />
              </Button>
            </div>
          )}
          <Segmented
            ariaLabel={tx('Diyagram düzeni')}
            size="sm"
            value={layout}
            onChange={setLayout}
            options={[
              { value: 'tree', label: <span className="inline-flex items-center gap-1"><Network className="size-3.5" aria-hidden />{tx('Ağaç')}</span> },
              { value: 'list', label: <span className="inline-flex items-center gap-1"><Rows3 className="size-3.5" aria-hidden />{tx('Liste')}</span> },
            ]}
          />
        </div>
      </div>

      {/* Tuval */}
      <div
        ref={viewport}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        className={cn(
          'relative max-h-[72vh] min-h-[420px] overflow-auto',
          layout === 'tree' && 'cursor-grab bg-[radial-gradient(hsl(var(--border))_1px,transparent_1px)] [background-size:18px_18px] active:cursor-grabbing',
        )}
      >
        <div ref={canvas} style={layout === 'tree' ? ({ zoom } as CSSProperties) : undefined}>
          {layout === 'tree' ? treeView : listView}
        </div>
      </div>

      <p className="border-t border-border px-4 py-2 text-[11px] text-muted-foreground">
        {layout === 'tree'
          ? tx('Sürükleyerek gezinin · departmana tıklayıp daraltın · ekibe tıklayıp üyelerini görün.')
          : tx('Departmana tıklayıp daraltın · ekibe tıklayıp üyelerini görün.')}
      </p>
    </div>
  )
}
