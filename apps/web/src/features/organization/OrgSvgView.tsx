/**
 * Organizasyon şemasının SVG görünümleri: yatay ağaç, radyal ağaç, halka (sunburst)
 * ve ağaç haritası (treemap). Yerleşim `orgLayouts.computeLayout` ile hesaplanır;
 * bu bileşen yalnızca çizer, kaydırır/yakınlaştırır ve klavyeyle gezdirir.
 *
 * Büyük ağaç: görünür alanın dışındaki düğümler hiç çizilmez (kırpma), uzaklaştıkça
 * etiketler düşer (ayrıntı seviyesi). Tembel yüklenir (d3-hierarchy ana pakete girmez).
 */

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import type { DepartmentLink } from '@/api/types'
import type { ChartModel } from './orgChartModel'
import { arcPath, computeLayout, intersects, linkPath, matrixPath, NODE_H, NODE_W, ROOT_ID, type LayoutNode } from './orgLayouts'
import { clampZoom, navigate, visibleRoots, type NavMode } from './orgViewState'
import { OrgMinimap } from './OrgMinimap'
import { OrgSelectionCard } from './OrgSelectionCard'

export type SvgLayoutKind = 'horizontal' | 'radial' | 'sunburst' | 'treemap'

export interface OrgSvgViewProps {
  model: ChartModel
  kind: SvgLayoutKind
  rootLabel: string
  focus: string | null
  collapsed: ReadonlySet<string>
  peopleKnown: boolean
  colorOf: (deptId: string) => string
  subtitleOf: (deptId: string) => string
  /** Köke giden vurgulu yol (departman kimlikleri). */
  path: ReadonlySet<string>
  /** Arama eşleşmeleri (departman kimlikleri; kişi aramasında kişinin departmanı). */
  matches: ReadonlySet<string>
  /** Seçili departman. */
  selectedDept: string | null
  matrix: DepartmentLink[] | null
  zoom: number
  /** Adresten gelen yakınlık kullanılsın mı (yoksa ilk açılışta sığdırılır). */
  zoomFromUrl: boolean
  onZoom: (z: number) => void
  onToggle: (deptId: string) => void
  onSelect: (deptId: string) => void
  onFocus: (deptId: string) => void
  /** Bu düğüme git (arama sonucu). `seq` her istekte artar. */
  centerRequest: { id: string; seq: number } | null
  /** Dışarıdan "sığdır" isteği. */
  fitRequest: number
  /** Kısa süre parlayan departmanlar (zaman kaydırıcısında kişi sayısı değişenler); `pulseSeq` her değişimde artar. */
  pulse?: ReadonlySet<string>
  pulseSeq?: number
}

interface View {
  k: number
  x: number
  y: number
}

const reducedMotion = () =>
  typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

export function OrgSvgView(props: OrgSvgViewProps) {
  const { model, kind, focus, collapsed, peopleKnown, colorOf, subtitleOf, path, matches, selectedDept, matrix } = props
  const layout = useMemo(
    () => computeLayout(model, kind, { focus, collapsed, rootLabel: props.rootLabel, peopleKnown }),
    [model, kind, focus, collapsed, props.rootLabel, peopleKnown],
  )

  /* ------------------------------------------------------------ boyut + dönüşüm */
  const box = useRef<HTMLDivElement>(null)
  const [measured, setMeasured] = useState<{ w: number; h: number } | null>(null)
  const size = measured ?? { w: 800, h: 520 }
  useLayoutEffect(() => {
    const el = box.current
    if (!el) return
    const update = () => setMeasured({ w: el.clientWidth || 800, h: el.clientHeight || 520 })
    update()
    const ro = new ResizeObserver(update)
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const [view, setView] = useState<View>({ k: props.zoom, x: 0, y: 0 })
  const [animate, setAnimate] = useState(false)
  const viewRef = useRef(view)
  viewRef.current = view

  const setViewAnimated = useCallback((v: View, anim: boolean) => {
    setAnimate(anim && !reducedMotion())
    setView(v)
  }, [])

  const fit = useCallback(
    (anim = true) => {
      const b = layout.bounds
      const bw = Math.max(1, b.x1 - b.x0)
      const bh = Math.max(1, b.y1 - b.y0)
      const k = clampZoom(Math.min(1.2, Math.max(0.1, Math.min((size.w - 48) / bw, (size.h - 48) / bh))))
      setViewAnimated({ k, x: size.w / 2 - ((b.x0 + b.x1) / 2) * k, y: size.h / 2 - ((b.y0 + b.y1) / 2) * k }, anim)
      props.onZoom(k)
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [layout, size.w, size.h, setViewAnimated],
  )

  const centerOn = useCallback(
    (cx: number, cy: number, k = viewRef.current.k, anim = true) => {
      setViewAnimated({ k, x: size.w / 2 - cx * k, y: size.h / 2 - cy * k }, anim)
    },
    [size.w, size.h, setViewAnimated],
  )

  // Yerleşim türü/odak değişince sığdır; ilk açılışta adresteki yakınlık varsa onu kullan.
  const first = useRef(true)
  useEffect(() => {
    if (!measured) return
    if (first.current && props.zoomFromUrl) {
      first.current = false
      const b = layout.bounds
      centerOn((b.x0 + b.x1) / 2, (b.y0 + b.y1) / 2, props.zoom, false)
      return
    }
    first.current = false
    fit(false)
    // Yalnızca yerleşim türü, odak ve kutu boyutu (ilk ölçüm) değişince.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind, focus, measured !== null])

  // Dışarıdan yakınlık (araç çubuğundaki +/−): merkez sabit kalarak ölçekle.
  useEffect(() => {
    const v = viewRef.current
    if (Math.abs(props.zoom - v.k) < 0.001) return
    const cx = (size.w / 2 - v.x) / v.k
    const cy = (size.h / 2 - v.y) / v.k
    centerOn(cx, cy, props.zoom, true)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.zoom])

  useEffect(() => {
    if (props.fitRequest) fit(true)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.fitRequest])

  useEffect(() => {
    const req = props.centerRequest
    if (!req) return
    const n = layout.byId.get(req.id)
    if (n) centerOn(n.x, n.y, Math.max(viewRef.current.k, kind === 'treemap' || kind === 'sunburst' ? viewRef.current.k : 0.8))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.centerRequest?.seq, layout])

  /* ------------------------------------------------------------ kaydırma / tekerlek */
  const drag = useRef<{ px: number; py: number; x: number; y: number; moved: boolean } | null>(null)
  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.button !== 0 || (e.target as HTMLElement).closest('button,a,.org-minimap')) return
    drag.current = { px: e.clientX, py: e.clientY, x: view.x, y: view.y, moved: false }
  }
  const onPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    const d = drag.current
    if (!d) return
    const dx = e.clientX - d.px
    const dy = e.clientY - d.py
    if (!d.moved && Math.hypot(dx, dy) < 4) return
    if (!d.moved) e.currentTarget.setPointerCapture(e.pointerId)
    d.moved = true
    setViewAnimated({ k: view.k, x: d.x + dx, y: d.y + dy }, false)
  }
  const endDrag = () => {
    window.setTimeout(() => (drag.current = null), 0)
  }
  const wasDrag = () => Boolean(drag.current?.moved)

  useEffect(() => {
    const el = box.current
    if (!el) return
    const onWheel = (e: WheelEvent) => {
      e.preventDefault()
      const v = viewRef.current
      if (e.ctrlKey || e.metaKey) {
        // Kıstırma (trackpad) ve Ctrl+tekerlek: imleç etrafında yakınlaştır.
        const r = el.getBoundingClientRect()
        const mx = e.clientX - r.left
        const my = e.clientY - r.top
        const k = clampZoom(v.k * Math.exp(-e.deltaY * 0.0025))
        setAnimate(false)
        setView({ k, x: mx - ((mx - v.x) / v.k) * k, y: my - ((my - v.y) / v.k) * k })
        props.onZoom(k)
      } else {
        setAnimate(false)
        setView({ k: v.k, x: v.x - e.deltaX, y: v.y - e.deltaY })
      }
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  /* ------------------------------------------------------------ kırpma + ayrıntı seviyesi */
  const visibleRect = { x: -view.x / view.k, y: -view.y / view.k, w: size.w / view.k, h: size.h / view.k }
  const margin = 80 / view.k
  const shown = layout.nodes.filter((n) => n.depth === 0 || intersects(n.box, visibleRect, margin))
  const shownIds = new Set(shown.map((n) => n.id))
  const labels = view.k >= 0.45

  /* ------------------------------------------------------------ klavye */
  const roots = useMemo(() => visibleRoots(model, focus), [model, focus])
  const [active, setActive] = useState<string | null>(null)
  const [hasFocus, setHasFocus] = useState(false)
  const activeId = active && layout.byId.has(active) ? active : (roots[0]?.dept.id ?? null)
  const pendingFocus = useRef(false)
  const mode: NavMode = kind === 'horizontal' ? 'right' : 'tree'

  useEffect(() => {
    if (!pendingFocus.current || !activeId) return
    pendingFocus.current = false
    document.getElementById(`org-svg-node-${activeId}`)?.focus({ preventScroll: true })
  })

  const ensureVisible = (id: string) => {
    const n = layout.byId.get(id)
    if (!n) return
    if (!intersects(n.box, visibleRect, -20 / view.k)) centerOn(n.x, n.y)
  }

  const onKeyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    if (!activeId) return
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      props.onSelect(activeId)
      return
    }
    if (e.key === 'f' || e.key === 'F') {
      e.preventDefault()
      props.onFocus(activeId)
      return
    }
    const r = navigate(e.key, activeId, roots, collapsed, mode)
    if (!r) return
    e.preventDefault()
    if (r.toggle) props.onToggle(r.toggle)
    if (r.move) {
      setActive(r.move)
      pendingFocus.current = true
      ensureVisible(r.move)
    }
  }

  const select = (id: string) => {
    if (wasDrag() || id === ROOT_ID) return
    setActive(id)
    props.onSelect(id)
  }

  /* ------------------------------------------------------------ çizim */
  const primary = 'hsl(var(--primary))'
  const border = 'hsl(var(--border))'

  const nodeEl = (n: LayoutNode) => {
    const isRoot = n.id === ROOT_ID
    const color = isRoot ? primary : colorOf(n.id)
    const onPath = path.has(n.id)
    const sel = n.id === selectedDept
    const hit = matches.has(n.id)
    const focused = hasFocus && n.id === activeId
    const strokeW = sel || focused ? 3 : onPath || hit ? 2.25 : 1
    const stroke = sel || onPath || hit || focused ? primary : border
    const name = n.name || props.rootLabel
    const sub = isRoot ? tx('{0} kişi', [formatNumber(n.value)]) : subtitleOf(n.id)
    const aria = isRoot
      ? undefined
      : {
          id: `org-svg-node-${n.id}`,
          role: 'treeitem' as const,
          'aria-level': n.depth + (layout.byId.has(ROOT_ID) ? 0 : 1),
          'aria-label': `${name}, ${sub}${n.hiddenCount ? tx(', {0} alt departman gizli', [n.hiddenCount]) : ''}`,
          'aria-expanded': n.childCount > 0 ? n.hiddenCount === 0 : undefined,
          'aria-selected': sel,
          tabIndex: n.id === activeId ? 0 : -1,
          onFocus: () => {
            setActive(n.id)
            setHasFocus(true)
          },
          onBlur: () => setHasFocus(false),
        }
    const common = {
      className: cnNode(isRoot),
      onClick: () => select(n.id),
      'data-on-path': onPath || undefined,
      ...aria,
    }

    if (kind === 'sunburst' && n.arc) {
      const { a0, a1, r0, r1 } = n.arc
      const span = (a1 - a0) * ((r0 + r1) / 2) * view.k
      return (
        <g key={n.id} {...common} aria-hidden={isRoot || undefined}>
          <path d={arcPath(a0, a1, r0, r1)} fill={color} fillOpacity={isRoot ? 0.14 : hit || sel ? 1 : 0.82} stroke={sel || focused || onPath ? primary : 'hsl(var(--card))'} strokeWidth={(sel || focused ? 3 : onPath ? 2 : 1) / view.k} />
          {(isRoot || span > 44) && labels && (
            <text x={n.x} y={n.y} dy="0.35em" textAnchor="middle" className="org-svg-label" style={{ fontSize: 11 / Math.max(view.k, 0.6) }}>
              {clip(name, isRoot ? 18 : Math.max(3, Math.floor(span / 7)))}
            </text>
          )}
          <title>{`${name} · ${sub}`}</title>
        </g>
      )
    }
    if (kind === 'treemap') {
      const b = n.box
      const wpx = b.w * view.k
      return (
        <g key={n.id} {...common} aria-hidden={isRoot || undefined}>
          <rect x={b.x} y={b.y} width={b.w} height={b.h} rx={4} fill={color} fillOpacity={isRoot ? 0.06 : hit || sel ? 0.55 : 0.26} stroke={sel || focused || onPath || hit ? primary : color} strokeWidth={strokeW / view.k} />
          {wpx > 46 && b.h * view.k > 16 && (
            <text x={b.x + 5 / view.k} y={b.y + 14 / view.k} className="org-svg-label font-semibold" style={{ fontSize: 11 / view.k }}>
              {clip(name, Math.floor(wpx / 7))}
              {wpx > 160 && <tspan className="org-svg-sub" dx={6 / view.k}>{sub}</tspan>}
            </text>
          )}
          <title>{`${name} · ${sub}`}</title>
        </g>
      )
    }
    if (kind === 'radial') {
      const r = isRoot ? 16 : 9
      return (
        <g key={n.id} {...common} aria-hidden={isRoot || undefined}>
          <circle cx={n.x} cy={n.y} r={r} fill={color} stroke={stroke} strokeWidth={strokeW + 1} />
          {n.hiddenCount > 0 && <circle cx={n.x} cy={n.y} r={r + 4} fill="none" stroke={color} strokeDasharray="3 3" />}
          {(labels || isRoot || n.depth <= 1) && (
            <text x={n.x} y={n.y + r + 13} textAnchor="middle" className="org-svg-label">
              {clip(name, 22)}
            </text>
          )}
          <title>{`${name} · ${sub}`}</title>
        </g>
      )
    }
    // Yatay ağaç: kutu.
    const x = n.x - NODE_W / 2
    const y = n.y - NODE_H / 2
    return (
      <g key={n.id} {...common} aria-hidden={isRoot || undefined}>
        <rect x={x} y={y} width={NODE_W} height={NODE_H} rx={10} className="org-svg-card" stroke={stroke} strokeWidth={strokeW} />
        <rect x={x} y={y + 6} width={4} height={NODE_H - 12} rx={2} fill={color} />
        {labels ? (
          <>
            <text x={x + 14} y={y + 23} className="org-svg-label font-semibold" style={{ fontSize: 13 }}>
              {clip(name, 24)}
            </text>
            <text x={x + 14} y={y + 41} className="org-svg-sub">
              {sub}
            </text>
          </>
        ) : null}
        {n.childCount > 0 && !isRoot && (
          <g
            className="org-svg-toggle"
            onClick={(e) => {
              e.stopPropagation()
              if (!wasDrag()) props.onToggle(n.id)
            }}
          >
            <circle cx={x + NODE_W} cy={n.y} r={9} fill="hsl(var(--card))" stroke={n.hiddenCount ? primary : border} />
            <text x={x + NODE_W} y={n.y} dy="0.35em" textAnchor="middle" className="org-svg-sub" style={{ fontSize: 10 }}>
              {n.hiddenCount ? `+${n.hiddenCount}` : '−'}
            </text>
          </g>
        )}
      </g>
    )
  }

  const linkEls =
    kind === 'horizontal' || kind === 'radial'
      ? layout.links
          .filter((l) => shownIds.has(l.source) || shownIds.has(l.target))
          .map((l) => {
            const s = layout.byId.get(l.source)!
            const t = layout.byId.get(l.target)!
            const on = path.has(l.source) && path.has(l.target)
            return (
              <path
                key={`${l.source}>${l.target}`}
                d={linkPath(kind, s, t)}
                fill="none"
                stroke={on ? primary : border}
                strokeWidth={on ? 2.5 : 1.5}
              />
            )
          })
      : null

  const matrixEls = (matrix ?? [])
    .map((m) => ({ m, d: matrixPath(layout, m.fromDepartmentId, m.toDepartmentId) }))
    .filter((x): x is { m: DepartmentLink; d: string } => Boolean(x.d))
    .map(({ m, d }) => (
      <path
        key={m.id}
        d={d}
        className={m.kind === 'Project' ? 'org-matrix org-matrix-project' : 'org-matrix'}
        markerEnd={`url(#org-arrow-${m.kind === 'Project' ? 'p' : 'f'})`}
        strokeWidth={2 / Math.max(view.k, 0.5)}
      >
        <title>{matrixTitle(m, layout.byId.get(m.fromDepartmentId)?.name, layout.byId.get(m.toDepartmentId)?.name)}</title>
      </path>
    ))

  const minimapItems = useMemo(
    () =>
      layout.nodes
        .filter((n) => n.id !== ROOT_ID)
        .map((n) =>
          n.arc
            ? { id: n.id, x: n.x - 6, y: n.y - 6, w: 12, h: 12, color: colorOf(n.id) }
            : { id: n.id, x: n.box.x, y: n.box.y, w: n.box.w, h: n.box.h, color: colorOf(n.id) },
        ),
    [layout, colorOf],
  )

  const selNode = selectedDept ? layout.byId.get(selectedDept) : undefined

  return (
    <div
      ref={box}
      className="org-svg-view relative h-[72vh] min-h-[420px] cursor-grab touch-none overflow-hidden bg-[radial-gradient(hsl(var(--border))_1px,transparent_1px)] [background-size:18px_18px] select-none active:cursor-grabbing"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={endDrag}
      onPointerCancel={endDrag}
    >
      <svg
        width={size.w}
        height={size.h}
        role="tree"
        aria-label={tx('Organizasyon şeması: ok tuşlarıyla gezinin, Enter ile seçin, F ile odaklanın')}
        onKeyDown={onKeyDown}
        className="block"
      >
        <defs>
          <marker id="org-arrow-f" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">
            <path d="M0,0L10,5L0,10z" className="org-matrix-head" />
          </marker>
          <marker id="org-arrow-p" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">
            <path d="M0,0L10,5L0,10z" className="org-matrix-head org-matrix-head-project" />
          </marker>
        </defs>
        <g
          className={animate ? 'org-svg-stage org-svg-anim' : 'org-svg-stage'}
          style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.k})` }}
        >
          {linkEls}
          {shown.map(nodeEl)}
          {matrixEls}
          {props.pulse &&
            shown
              .filter((n) => props.pulse!.has(n.id))
              .map((n) =>
                n.arc ? (
                  <path key={`pulse-${n.id}-${props.pulseSeq ?? 0}`} d={arcPath(n.arc.a0, n.arc.a1, n.arc.r0, n.arc.r1)} className="org-pulse-ring" />
                ) : (
                  <rect
                    key={`pulse-${n.id}-${props.pulseSeq ?? 0}`}
                    x={n.box.x - 4}
                    y={n.box.y - 4}
                    width={n.box.w + 8}
                    height={n.box.h + 8}
                    rx={kind === 'radial' ? n.box.w : 12}
                    className="org-pulse-ring"
                  />
                ),
              )}
        </g>
      </svg>

      {selNode && selNode.id !== ROOT_ID && (
        <OrgSelectionCard
          name={selNode.name}
          subtitle={subtitleOf(selNode.id)}
          childCount={selNode.childCount}
          hiddenCount={selNode.hiddenCount}
          onFocus={() => props.onFocus(selNode.id)}
          onToggle={() => props.onToggle(selNode.id)}
        />
      )}

      {layout.nodes.length > 15 && (
        <OrgMinimap
          items={minimapItems}
          bounds={layout.bounds}
          view={visibleRect}
          highlight={path}
          onJump={(cx, cy) => centerOn(cx, cy, view.k, false)}
        />
      )}
    </div>
  )
}

const cnNode = (root: boolean) => (root ? 'org-svg-node org-svg-root' : 'org-svg-node')

const clip = (s: string, n: number) => (s.length > n ? `${s.slice(0, Math.max(1, n - 1))}…` : s)

export function matrixTitle(m: DepartmentLink, from?: string, to?: string): string {
  const kind = m.kind === 'Project' ? tx('Proje bağı') : tx('Fonksiyonel bağ')
  return `${kind}: ${from ?? '?'} → ${to ?? '?'}${m.note ? ` (${m.note})` : ''}`
}

export default OrgSvgView
