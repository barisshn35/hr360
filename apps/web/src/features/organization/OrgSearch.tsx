import { useId, useMemo, useState, type KeyboardEvent } from 'react'
import { Building2, Search, UserRound, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { appLocale, tx } from '@/lib/i18n'
import type { ChartModel } from './orgChartModel'

export interface SearchHit {
  kind: 'd' | 'p'
  id: string
  label: string
  detail: string
}

const norm = (s: string) => s.toLocaleLowerCase(appLocale)
const MAX = 8

/** Şemada departman ve (yetki varsa) kişi arama sonuçları. */
export function searchChart(model: ChartModel, q: string, peopleKnown: boolean, pathOf: (deptId: string) => string): SearchHit[] {
  const n = norm(q.trim())
  if (n.length < 2) return []
  const out: SearchHit[] = []
  for (const node of model.byId.values()) {
    if (norm(node.dept.name).includes(n)) out.push({ kind: 'd', id: node.dept.id, label: node.dept.name, detail: pathOf(node.dept.id) })
    if (out.length >= MAX) break
  }
  if (peopleKnown) {
    const people: SearchHit[] = []
    for (const node of model.byId.values()) {
      for (const p of node.members) {
        if (people.length >= MAX) break
        if (norm(p.name).includes(n)) people.push({ kind: 'p', id: p.id, label: p.name, detail: [p.title, node.dept.name].filter(Boolean).join(' · ') })
      }
    }
    out.push(...people)
  }
  return out
}

/**
 * Şema içi arama kutusu (combobox): yazarken eşleşmeler listelenir; seçilen
 * departmana ya da kişiye gidilir ve köke giden yolu vurgulanır.
 */
export function OrgSearch({
  query,
  setQuery,
  hits,
  matchCount,
  peopleKnown,
  onPick,
}: {
  query: string
  setQuery: (q: string) => void
  hits: SearchHit[]
  /** Şemada vurgulanan toplam eşleşme. */
  matchCount: number
  peopleKnown: boolean
  onPick: (hit: SearchHit) => void
}) {
  const id = useId()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const listId = `${id}-list`
  const show = open && query.trim().length >= 2

  const pick = (h: SearchHit | undefined) => {
    if (!h) return
    onPick(h)
    setOpen(false)
  }

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setOpen(true)
      setActive((a) => Math.min(hits.length - 1, a + 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((a) => Math.max(0, a - 1))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      pick(hits[active] ?? hits[0])
    } else if (e.key === 'Escape') {
      if (show) {
        e.preventDefault()
        setOpen(false)
      }
    }
  }

  const placeholder = useMemo(() => (peopleKnown ? tx('Kişi ya da departman ara') : tx('Departman ara')), [peopleKnown])

  return (
    <div className="relative flex w-full items-center gap-2 sm:w-auto">
      <div className="relative w-full sm:w-80">
        <label htmlFor={`${id}-input`} className="sr-only">
          {placeholder}
        </label>
        <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <input
          id={`${id}-input`}
          role="combobox"
          aria-expanded={show}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={show && hits[active] ? `${id}-opt-${active}` : undefined}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value)
            setActive(0)
            setOpen(true)
          }}
          onFocus={() => setOpen(true)}
          onBlur={() => window.setTimeout(() => setOpen(false), 120)}
          onKeyDown={onKeyDown}
          placeholder={placeholder}
          autoComplete="off"
          className="h-9 w-full rounded-md border border-input bg-background pr-8 pl-9 text-[13px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
        />
        {query && (
          <button
            type="button"
            onClick={() => setQuery('')}
            aria-label={tx('Aramayı temizle')}
            className="absolute top-1/2 right-2 -translate-y-1/2 rounded p-0.5 text-muted-foreground hover:bg-muted"
          >
            <X className="size-3.5" />
          </button>
        )}
        <ul
          id={listId}
          role="listbox"
          aria-label={tx('Arama sonuçları')}
          className={cn(
            'absolute top-full right-0 left-0 z-30 mt-1 max-h-80 overflow-auto rounded-lg border border-border bg-popover p-1 shadow-lg',
            !show && 'hidden',
          )}
        >
          {hits.length === 0 ? (
            <li role="presentation" className="px-2 py-2 text-[12.5px] text-muted-foreground">
              {tx('Eşleşme yok')}
            </li>
          ) : (
            hits.map((h, i) => (
              <li
                key={`${h.kind}${h.id}`}
                id={`${id}-opt-${i}`}
                role="option"
                aria-selected={i === active}
                onMouseDown={(e) => {
                  e.preventDefault()
                  pick(h)
                }}
                onMouseEnter={() => setActive(i)}
                className={cn('flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-[13px]', i === active && 'bg-muted')}
              >
                {h.kind === 'd' ? (
                  <Building2 className="size-4 shrink-0 text-muted-foreground" aria-hidden />
                ) : (
                  <UserRound className="size-4 shrink-0 text-muted-foreground" aria-hidden />
                )}
                <span className="min-w-0">
                  <span className="block truncate font-medium">{h.label}</span>
                  <span className="block truncate text-[11.5px] text-muted-foreground">{h.detail}</span>
                </span>
              </li>
            ))
          )}
        </ul>
      </div>
      {query.trim().length >= 2 && (
        <span className="shrink-0 text-[12px] text-muted-foreground" aria-live="polite">
          {matchCount ? tx('{0} eşleşme', [matchCount]) : tx('Eşleşme yok')}
        </span>
      )}
    </div>
  )
}
