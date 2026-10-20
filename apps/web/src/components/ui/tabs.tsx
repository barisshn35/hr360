import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

export interface TabDef<T extends string> {
  key: T
  label: string
  /** Sağda küçük bir sayı gösterir (ör. bekleyen adet). */
  count?: number
}

/**
 * Alt çizgili sekme şeridi.
 *
 * Seçim URL'de tutulur (bkz. useTabParam), böylece derin bağlantı ve geri
 * tuşu doğru çalışır; varsayılan sekme adrese yazılmaz, adres temiz kalır.
 */
export function Tabs<T extends string>({
  tabs,
  value,
  onChange,
  label,
}: {
  tabs: Array<TabDef<T>>
  value: T
  onChange: (next: T) => void
  label: string
}) {
  // Etkin sekmenin zemini sekmeler arasında yaylanarak kayar
  // (21st.dev "Pill Morph Tabs", ruixen.ui, id 7878); aynı sayfada iki şerit
  // olabileceği için kimlik benzersiz.
  const indicatorId = useId()
  const reduced = useReducedMotion()
  // Sekmeler sığmadığında (ör. KVKK ekranında 13 sekme) şerit kayar; kenarlarda soluklaşma ve ok
  // düğmeleri gizli sekme olduğunu gösterir, etkin sekme görünür alana getirilir.
  const strip = useRef<HTMLDivElement>(null)
  const [edges, setEdges] = useState({ left: false, right: false })
  const measure = useCallback(() => {
    const el = strip.current
    if (!el) return
    const left = el.scrollLeft > 2
    const right = el.scrollLeft + el.clientWidth < el.scrollWidth - 2
    setEdges((p) => (p.left === left && p.right === right ? p : { left, right }))
  }, [])
  useEffect(() => {
    const el = strip.current
    if (!el) return
    measure()
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(measure) : null
    ro?.observe(el)
    return () => ro?.disconnect()
  }, [measure, tabs.length])
  useEffect(() => {
    const el = strip.current
    const btn = el?.querySelector<HTMLElement>('[aria-selected="true"]')
    if (!el || !btn) return
    if (btn.offsetLeft < el.scrollLeft) el.scrollTo({ left: btn.offsetLeft - 8 })
    else if (btn.offsetLeft + btn.offsetWidth > el.scrollLeft + el.clientWidth)
      el.scrollTo({ left: btn.offsetLeft + btn.offsetWidth - el.clientWidth + 8 })
  }, [value])
  const nudge = (dir: -1 | 1) => {
    const el = strip.current
    if (el) el.scrollBy({ left: dir * el.clientWidth * 0.7, behavior: reduced ? 'auto' : 'smooth' })
  }
  return (
    <div className="relative inline-flex max-w-full">
    <div
      ref={strip}
      role="tablist"
      aria-label={label}
      onScroll={measure}
      className="no-scrollbar inline-flex max-w-full gap-1 overflow-x-auto rounded-2xl border border-border/80 bg-card/50 p-1 shadow-[inset_0_1px_0_0_hsl(var(--edge-light))] backdrop-blur-xl"
    >
      {tabs.map((tab) => {
        const active = value === tab.key
        return (
          <button
            key={tab.key}
            role="tab"
            type="button"
            aria-selected={active}
            onClick={() => onChange(tab.key)}
            className={cn(
              'relative flex h-9 shrink-0 cursor-pointer items-center gap-2 rounded-xl px-3.5',
              'text-[13px] whitespace-nowrap transition-colors outline-none focus-visible:ring-2 focus-visible:ring-primary/40',
              active ? 'font-medium text-foreground' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {active && (
              <motion.span
                aria-hidden="true"
                layoutId={reduced ? undefined : `tab-${indicatorId}`}
                transition={{ type: 'spring', stiffness: 420, damping: 32 }}
                className="absolute inset-0 -z-0 rounded-xl bg-accent shadow-[0_4px_14px_-6px_rgb(0_0_0/0.6),inset_0_1px_0_0_hsl(var(--edge-light))] ring-1 ring-border"
              />
            )}
            <span className="relative z-10">{tab.label}</span>
            {tab.count !== undefined && tab.count > 0 && (
              <span
                className={cn(
                  'tabular relative z-10 rounded-full px-1.5 py-px text-[10.5px] font-semibold',
                  // Dolu zemin: açık temada da ≥ 4,5:1 (yarı saydam zemin 3,85:1 kalıyordu).
                  active ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground',
                )}
              >
                {tab.count}
              </span>
            )}
          </button>
        )
      })}
    </div>
      {edges.left && (
        <button type="button" tabIndex={-1} aria-label={tx('Önceki sekmeler')} onClick={() => nudge(-1)}
          className="absolute inset-y-px left-px z-20 flex w-9 cursor-pointer items-center justify-start rounded-l-2xl bg-gradient-to-r from-card via-card/90 to-transparent pl-1 text-muted-foreground hover:text-foreground">
          <ChevronLeft className="size-4" />
        </button>
      )}
      {edges.right && (
        <button type="button" tabIndex={-1} aria-label={tx('Sonraki sekmeler')} onClick={() => nudge(1)}
          className="absolute inset-y-px right-px z-20 flex w-9 cursor-pointer items-center justify-end rounded-r-2xl bg-gradient-to-l from-card via-card/90 to-transparent pr-1 text-muted-foreground hover:text-foreground">
          <ChevronRight className="size-4" />
        </button>
      )}
    </div>
  )
}

/**
 * Sekme durumunu URL'de tutan yardımcı.
 * `defaultKey` adrese yazılmaz — varsayılan görünümün adresi sade kalır.
 */
export function useTabParam<T extends string>(param: string, defaultKey: T) {
  const [searchParams, setSearchParams] = useSearchParams()
  const value = (searchParams.get(param) as T | null) ?? defaultKey

  const setValue = (next: T) => {
    const params = new URLSearchParams(searchParams)
    if (next === defaultKey) params.delete(param)
    else params.set(param, next)
    setSearchParams(params, { replace: true })
  }

  return [value, setValue] as const
}
