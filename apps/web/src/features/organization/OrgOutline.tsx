import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { ChevronRight, Crosshair, Crown, Waypoints } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import type { ChartDept } from './orgChartModel'
import { descendantCount, navigate } from './orgViewState'

/**
 * Liste (anahat) görünümü: girintili ağaç. WAI-ARIA ağaç deseni — ↑/↓ önceki/sonraki,
 * → aç/ilk alt birim, ← kapat/üst birim, Home/End, Enter seç, F odaklan. Ekran okuyucu
 * ve küçük ekran için şemanın en erişilebilir hâli.
 */
export function OrgOutline({
  roots,
  collapsed,
  onToggle,
  selectedDept,
  path,
  matches,
  colorOf,
  subtitleOf,
  headOf,
  linkCount,
  onSelect,
  onFocus,
}: {
  roots: ChartDept[]
  collapsed: ReadonlySet<string>
  onToggle: (id: string) => void
  selectedDept: string | null
  path: ReadonlySet<string>
  matches: ReadonlySet<string>
  colorOf: (id: string) => string
  subtitleOf: (id: string) => string
  headOf: (id: string) => string | null
  linkCount: (id: string) => number
  onSelect: (id: string) => void
  onFocus: (id: string) => void
}) {
  const [active, setActive] = useState<string | null>(null)
  const activeId = active ?? selectedDept ?? roots[0]?.dept.id ?? null
  const moveFocus = useRef(false)

  useEffect(() => {
    if (!moveFocus.current || !activeId) return
    moveFocus.current = false
    document.getElementById(`org-outline-${activeId}`)?.focus()
  })

  // Seçim dışarıdan (arama) değişince o satıra kaydır.
  useEffect(() => {
    if (!selectedDept) return
    setActive(selectedDept)
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
    document.getElementById(`org-outline-${selectedDept}`)?.scrollIntoView({ block: 'nearest', behavior: reduce ? 'auto' : 'smooth' })
  }, [selectedDept])

  const onKeyDown = (e: KeyboardEvent<HTMLUListElement>) => {
    if (!activeId) return
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      onSelect(activeId)
      return
    }
    if (e.key === 'f' || e.key === 'F') {
      e.preventDefault()
      onFocus(activeId)
      return
    }
    const r = navigate(e.key, activeId, roots, collapsed, 'tree')
    if (!r) return
    e.preventDefault()
    if (r.toggle) onToggle(r.toggle)
    if (r.move) {
      setActive(r.move)
      moveFocus.current = true
    }
  }

  const renderItem = (node: ChartDept, level: number) => {
    const id = node.dept.id
    const hasKids = node.children.length > 0
    const open = hasKids && !collapsed.has(id)
    const head = headOf(id)
    const links = linkCount(id)
    const sel = selectedDept === id
    return (
      <li
        key={id}
        id={`org-outline-${id}`}
        role="treeitem"
        aria-level={level}
        aria-expanded={hasKids ? open : undefined}
        aria-selected={sel}
        tabIndex={id === activeId ? 0 : -1}
        onFocus={(e) => {
          if (e.target === e.currentTarget) setActive(id)
        }}
        className="outline-none [&:focus-visible>div]:ring-2 [&:focus-visible>div]:ring-ring"
      >
        <div
          onClick={(e) => {
            e.stopPropagation()
            setActive(id)
            onSelect(id)
          }}
          className={cn(
            'flex cursor-pointer items-center gap-2 rounded-md py-1.5 pr-2 text-[13px] hover:bg-muted/60',
            sel && 'bg-primary/8',
            path.has(id) && !sel && 'bg-primary/[0.04]',
            matches.has(id) && 'ring-1 ring-primary/60',
          )}
          style={{ paddingLeft: 6 + (level - 1) * 18 }}
        >
          <span
            aria-hidden
            onClick={(e) => {
              if (!hasKids) return
              e.stopPropagation()
              onToggle(id)
            }}
            className={cn('flex size-5 shrink-0 items-center justify-center rounded text-muted-foreground', hasKids ? 'hover:bg-muted' : 'opacity-0')}
          >
            <ChevronRight className={cn('size-3.5 transition-transform motion-reduce:transition-none', open && 'rotate-90')} />
          </span>
          <span aria-hidden className="size-2.5 shrink-0 rounded-sm" style={{ background: colorOf(id) }} />
          <span className={cn('min-w-0 truncate', path.has(id) ? 'font-semibold' : 'font-medium')}>{node.dept.name}</span>
          {head && (
            <span className="hidden min-w-0 items-center gap-1 truncate text-[12px] text-muted-foreground sm:flex">
              <Crown className="size-3 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
              {head}
            </span>
          )}
          <span className="ml-auto flex shrink-0 items-center gap-2 text-[12px] text-muted-foreground tabular">
            {links > 0 && (
              <span className="flex items-center gap-0.5" title={tx('{0} matris bağı', [links])}>
                <Waypoints className="size-3" aria-hidden />
                {links}
              </span>
            )}
            {subtitleOf(id)}
            {hasKids && !open && <span className="text-primary">{tx('+{0} alt birim', [descendantCount(node)])}</span>}
          </span>
        </div>
        {open && (
          <ul role="group">
            {node.children.map((c) => renderItem(c, level + 1))}
          </ul>
        )}
      </li>
    )
  }

  const sel = selectedDept ? findNode(roots, selectedDept) : undefined

  return (
    <div className="max-h-[72vh] min-h-[320px] overflow-auto p-2 sm:p-3">
      {sel && (
        <div className="sticky top-0 z-10 mb-2 flex flex-wrap items-center gap-1.5 rounded-lg border border-border bg-card/95 p-1.5 pl-3 text-[12px] shadow-sm backdrop-blur">
          <span className="min-w-0 truncate font-medium">{sel.dept.name}</span>
          <Button size="sm" variant="ghost" className="h-7 px-2" onClick={() => onFocus(sel.dept.id)}>
            <Crosshair className="size-3.5" aria-hidden /> {tx('Odaklan')}
          </Button>
        </div>
      )}
      <ul role="tree" aria-label={tx('Departman anahattı')} onKeyDown={onKeyDown}>
        {roots.map((r) => renderItem(r, 1))}
      </ul>
    </div>
  )
}

function findNode(roots: ChartDept[], id: string): ChartDept | undefined {
  const stack = [...roots]
  while (stack.length) {
    const n = stack.pop()!
    if (n.dept.id === id) return n
    stack.push(...n.children)
  }
  return undefined
}
