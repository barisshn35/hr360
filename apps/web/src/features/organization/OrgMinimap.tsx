import { useRef, type PointerEvent as ReactPointerEvent } from 'react'
import { tx } from '@/lib/i18n'

export interface MinimapItem {
  id: string
  x: number
  y: number
  w: number
  h: number
  color: string
}

/**
 * Küçük harita: büyük şemada tüm ağacın kuşbakışı görünümü ve görünür alanın
 * çerçevesi. Tıklayınca/sürükleyince o noktaya gidilir. Yalnızca fare/dokunma
 * kolaylığıdır (klavyeyle gezinme düğümler üzerinden yapılır), ekran okuyucudan gizli.
 */
export function OrgMinimap({
  items,
  bounds,
  view,
  highlight,
  onJump,
}: {
  items: MinimapItem[]
  bounds: { x0: number; y0: number; x1: number; y1: number }
  /** İçerik koordinatlarında görünür alan. */
  view: { x: number; y: number; w: number; h: number }
  highlight?: ReadonlySet<string>
  onJump: (cx: number, cy: number) => void
}) {
  const ref = useRef<SVGSVGElement>(null)
  const dragging = useRef(false)
  const pad = 12
  const vb = { x: bounds.x0 - pad, y: bounds.y0 - pad, w: bounds.x1 - bounds.x0 + 2 * pad, h: bounds.y1 - bounds.y0 + 2 * pad }
  if (vb.w <= 0 || vb.h <= 0) return null
  const ratio = vb.w / vb.h
  const W = 176
  const H = Math.max(56, Math.min(132, W / ratio))
  const stroke = Math.max(vb.w / W, vb.h / H)

  const jump = (e: ReactPointerEvent<SVGSVGElement>) => {
    const svg = ref.current
    const m = svg?.getScreenCTM()
    if (!svg || !m) return
    const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(m.inverse())
    onJump(p.x, p.y)
  }

  return (
    <div
      aria-hidden
      className="org-minimap pointer-events-auto absolute right-3 bottom-3 z-10 hidden overflow-hidden rounded-lg border border-border bg-card/90 shadow-md backdrop-blur sm:block"
      title={tx('Küçük harita: gitmek istediğiniz yere tıklayın')}
    >
      <svg
        ref={ref}
        width={W}
        height={H}
        viewBox={`${vb.x} ${vb.y} ${vb.w} ${vb.h}`}
        preserveAspectRatio="xMidYMid meet"
        className="block cursor-pointer touch-none"
        onPointerDown={(e) => {
          dragging.current = true
          e.currentTarget.setPointerCapture(e.pointerId)
          jump(e)
        }}
        onPointerMove={(e) => dragging.current && jump(e)}
        onPointerUp={() => (dragging.current = false)}
        onPointerCancel={() => (dragging.current = false)}
      >
        {items.map((it) => (
          <rect
            key={it.id}
            x={it.x}
            y={it.y}
            width={Math.max(it.w, stroke * 2)}
            height={Math.max(it.h, stroke * 2)}
            fill={highlight?.has(it.id) ? 'hsl(var(--primary))' : it.color}
            fillOpacity={highlight?.has(it.id) ? 1 : 0.55}
          />
        ))}
        <rect
          x={view.x}
          y={view.y}
          width={view.w}
          height={view.h}
          fill="hsl(var(--primary) / 0.08)"
          stroke="hsl(var(--primary))"
          strokeWidth={stroke * 1.5}
        />
      </svg>
    </div>
  )
}
