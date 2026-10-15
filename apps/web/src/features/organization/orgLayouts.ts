/**
 * Organizasyon şeması yerleşimleri — çizimden bağımsız, saf hesap.
 *
 * Girdi `ChartModel` (departman ağacı), çıktı konumlandırılmış düğümler ve
 * bağlantılar (`OrgLayout`). SVG görünümü, dışa aktarma (PNG/SVG/PDF) ve küçük
 * harita bu çıktıyı kullanır; 3B katmanlı görünüm (`org3d.ts`) radyal yerleşimin açı/yarıçapını
 * yatay düzleme, derinliği dikey eksene taşıyarak yeniden kullanır.
 *
 * Koordinatlar piksel benzeri "yerleşim birimi"dir; ölçek/kaydırma çizen tarafta.
 * Düğümler departmandır (kişi düğümü yok): 5000 kişilik kiracıda da düğüm sayısı
 * departman sayısı kadar kalır. Hiyerarşi hesabı d3-hierarchy ile.
 */

import { hierarchy, partition, tree, treemap, treemapSquarify, type HierarchyNode } from 'd3-hierarchy'
import type { ChartDept, ChartModel } from './orgChartModel'
import { descendantCount, type OrgLayoutKind } from './orgViewState'

/** Şirket (sanal kök) düğümünün kimliği. */
export const ROOT_ID = '__company__'

/** Düğüm-bağlantı yerleşimlerinde kutu boyutu. */
export const NODE_W = 196
export const NODE_H = 56
const GAP_X = 28
const GAP_Y = 64
const LIST_ROW = 34
const LIST_INDENT = 22

export interface LayoutNode {
  /** Departman kimliği ya da `ROOT_ID`. */
  id: string
  name: string
  depth: number
  parentId: string | null
  /** Düğüm merkezi. */
  x: number
  y: number
  /** Kaplanan dikdörtgen (sol üst + boyut) — vurgu, isabet ve kırpma (culling) için. */
  box: { x: number; y: number; w: number; h: number }
  /** Radyal/halka: açı (radyan, 0 = yukarı, saat yönü) ve yarıçap. */
  angle?: number
  radius?: number
  /** Halka (sunburst) dilimi. */
  arc?: { a0: number; a1: number; r0: number; r1: number }
  /** Alt departmanlar dahil kişi sayısı. */
  value: number
  /** Yalnızca bu departmandaki kişi sayısı. */
  own: number
  /** Kök departmanın sırası (departman rengi). Şirket düğümünde -1. */
  colorIndex: number
  /** Toplam alt departman sayısı (kapalı olsa da). */
  childCount: number
  /** Kapalı dal yüzünden çizilmeyen alt departman sayısı. */
  hiddenCount: number
  /** Bu yerleşimde çizilen çocuk sayısı. */
  shownChildren: number
}

export interface LayoutLink {
  source: string
  target: string
}

export interface OrgLayout {
  kind: OrgLayoutKind
  nodes: LayoutNode[]
  links: LayoutLink[]
  byId: Map<string, LayoutNode>
  bounds: { x0: number; y0: number; x1: number; y1: number }
}

export interface LayoutOptions {
  /** Odak departmanı: yalnızca onun alt ağacı (şirket düğümü olmadan). */
  focus?: string | null
  collapsed?: ReadonlySet<string>
  /** Şirket düğümünün etiketi. */
  rootLabel?: string
  /** Kişi sayısı bilinmiyor (çalışan rolü): alanlar departman sayısına göre. */
  peopleKnown?: boolean
}

interface Datum {
  id: string
  name: string
  own: number
  total: number
  colorIndex: number
  childCount: number
  hiddenCount: number
  children: Datum[]
}

function toDatum(n: ChartDept, collapsed: ReadonlySet<string>): Datum {
  const closed = collapsed.has(n.dept.id)
  return {
    id: n.dept.id,
    name: n.dept.name,
    own: n.members.length,
    total: n.total,
    colorIndex: n.colorIndex,
    childCount: n.children.length,
    hiddenCount: closed ? descendantCount(n) : 0,
    children: closed ? [] : n.children.map((c) => toDatum(c, collapsed)),
  }
}

/** Görünür ağacı tek köklü veri yapısına çevirir (odak yoksa şirket düğümü kök olur). */
export function buildLayoutTree(model: ChartModel, opts: LayoutOptions = {}): Datum {
  const collapsed = opts.collapsed ?? new Set<string>()
  const f = opts.focus ? model.byId.get(opts.focus) : undefined
  if (f) return toDatum(f, collapsed)
  const children = model.roots.map((r) => toDatum(r, collapsed))
  return {
    id: ROOT_ID,
    name: opts.rootLabel ?? '',
    own: 0,
    total: model.roots.reduce((s, r) => s + r.total, 0),
    colorIndex: -1,
    childCount: children.length,
    hiddenCount: 0,
    children,
  }
}

const boundsOf = (nodes: LayoutNode[]) => {
  let x0 = Infinity
  let y0 = Infinity
  let x1 = -Infinity
  let y1 = -Infinity
  for (const n of nodes) {
    x0 = Math.min(x0, n.box.x)
    y0 = Math.min(y0, n.box.y)
    x1 = Math.max(x1, n.box.x + n.box.w)
    y1 = Math.max(y1, n.box.y + n.box.h)
  }
  return nodes.length ? { x0, y0, x1, y1 } : { x0: 0, y0: 0, x1: 0, y1: 0 }
}

function baseNode(h: HierarchyNode<Datum>): Omit<LayoutNode, 'x' | 'y' | 'box'> {
  const d = h.data
  return {
    id: d.id,
    name: d.name,
    depth: h.depth,
    parentId: h.parent?.data.id ?? null,
    value: d.total,
    own: d.own,
    colorIndex: d.colorIndex,
    childCount: d.childCount,
    hiddenCount: d.hiddenCount,
    shownChildren: h.children?.length ?? 0,
  }
}

const linksOf = (root: HierarchyNode<Datum>): LayoutLink[] =>
  root.links().map((l) => ({ source: l.source.data.id, target: l.target.data.id }))

/** Alan ağırlığı: kişi sayısı; boş departman da görünür kalsın diye küçük bir taban. */
function weightOf(d: Datum, peopleKnown: boolean): number {
  if (!peopleKnown) return 1
  // Kapalı dal tek yaprak gibi davranır: alanı tüm alt ağacın kişi sayısı.
  const people = d.children.length === 0 ? d.total : d.own
  return people > 0 ? people : d.children.length === 0 ? 0.35 : 0
}

/* ------------------------------------------------------------------ yerleşimler */

function nodeLink(data: Datum, horizontal: boolean): Pick<OrgLayout, 'nodes' | 'links'> {
  const root = hierarchy(data)
  const breadth = horizontal ? NODE_H + 18 : NODE_W + GAP_X
  const depthStep = horizontal ? NODE_W + GAP_Y + 24 : NODE_H + GAP_Y
  tree<Datum>()
    .nodeSize([breadth, depthStep])
    .separation((a, b) => (a.parent === b.parent ? 1 : 1.15))(root)
  const nodes: LayoutNode[] = root.descendants().map((h) => {
    // d3: x = genişlik ekseni, y = derinlik ekseni.
    const x = horizontal ? (h.y ?? 0) : (h.x ?? 0)
    const y = horizontal ? (h.x ?? 0) : (h.y ?? 0)
    return { ...baseNode(h), x, y, box: { x: x - NODE_W / 2, y: y - NODE_H / 2, w: NODE_W, h: NODE_H } }
  })
  return { nodes, links: linksOf(root) }
}

function radial(data: Datum): Pick<OrgLayout, 'nodes' | 'links'> {
  const root = hierarchy(data)
  const leaves = root.leaves().length
  const height = Math.max(1, root.height)
  // Dış halkanın çevresi yaprak başına ~34 birim yer bırakacak kadar büyük olmalı.
  const outer = Math.max(height * 150, (leaves * 34) / (2 * Math.PI))
  const ring = outer / height
  tree<Datum>()
    .size([2 * Math.PI, height])
    .separation((a, b) => (a.parent === b.parent ? 1 : 2) / Math.max(1, a.depth))(root)
  const R = 34
  const nodes: LayoutNode[] = root.descendants().map((h) => {
    const angle = h.x ?? 0
    const radius = (h.y ?? 0) * ring
    const x = radius * Math.sin(angle)
    const y = -radius * Math.cos(angle)
    return { ...baseNode(h), x, y, angle, radius, box: { x: x - R, y: y - R, w: 2 * R, h: 2 * R } }
  })
  return { nodes, links: linksOf(root) }
}

function sunburst(data: Datum, peopleKnown: boolean): Pick<OrgLayout, 'nodes' | 'links'> {
  const root = hierarchy(data).sum((d) => weightOf(d, peopleKnown))
  const levels = root.height + 1
  const CENTER = 70
  const RING = 92
  partition<Datum>().size([2 * Math.PI, levels])(root)
  const nodes: LayoutNode[] = root.descendants().map((h) => {
    const r = h as typeof h & { x0: number; x1: number; y0: number; y1: number }
    const a0 = r.x0
    const a1 = r.x1
    const r0 = r.depth === 0 ? 0 : CENTER + (r.y0 - 1) * RING
    const r1 = r.depth === 0 ? CENTER : CENTER + (r.y1 - 1) * RING
    const mid = (a0 + a1) / 2
    const rm = r.depth === 0 ? 0 : (r0 + r1) / 2
    const x = rm * Math.sin(mid)
    const y = -rm * Math.cos(mid)
    return {
      ...baseNode(h),
      x,
      y,
      angle: mid,
      radius: rm,
      arc: { a0, a1, r0, r1 },
      // Kırpma için dilimin yaklaşık kutusu (dış yarıçaplı kare yeterince güvenli).
      box: { x: x - (r1 - r0) / 2 - 8, y: y - (r1 - r0) / 2 - 8, w: r1 - r0 + 16, h: r1 - r0 + 16 },
    }
  })
  const outer = CENTER + (levels - 1) * RING
  // Sınırlar tüm daireyi kapsasın (sığdırma için).
  nodes[0].box = { x: -outer, y: -outer, w: 2 * outer, h: 2 * outer }
  return { nodes, links: linksOf(root) }
}

function treemapLayout(data: Datum, peopleKnown: boolean): Pick<OrgLayout, 'nodes' | 'links'> {
  const root = hierarchy(data)
    .sum((d) => weightOf(d, peopleKnown))
    .sort((a, b) => (b.value ?? 0) - (a.value ?? 0))
  const count = root.descendants().length
  // Alan düğüm sayısıyla büyür: kalabalık ağaçta küçük kutular okunur kalsın.
  const W = Math.round(Math.max(960, Math.sqrt(count) * 150))
  const H = Math.round(W * 0.62)
  treemap<Datum>().tile(treemapSquarify).size([W, H]).paddingOuter(3).paddingTop(20).paddingInner(3).round(true)(root)
  const nodes: LayoutNode[] = root.descendants().map((h) => {
    const r = h as typeof h & { x0: number; x1: number; y0: number; y1: number }
    const w = Math.max(0, r.x1 - r.x0)
    const hh = Math.max(0, r.y1 - r.y0)
    return { ...baseNode(h), x: r.x0 + w / 2, y: r.y0 + hh / 2, box: { x: r.x0, y: r.y0, w, h: hh } }
  })
  return { nodes, links: linksOf(root) }
}

function listLayout(data: Datum): Pick<OrgLayout, 'nodes' | 'links'> {
  const root = hierarchy(data)
  const nodes: LayoutNode[] = []
  let row = 0
  root.eachBefore((h) => {
    const x = h.depth * LIST_INDENT
    const y = row++ * LIST_ROW
    nodes.push({ ...baseNode(h), x: x + 120, y: y + LIST_ROW / 2, box: { x, y, w: 300, h: LIST_ROW - 4 } })
  })
  return { nodes, links: linksOf(root) }
}

/** Seçilen yerleşimi hesaplar. Aynı girdi → aynı çıktı (rastgelelik yok). */
export function computeLayout(model: ChartModel, kind: OrgLayoutKind, opts: LayoutOptions = {}): OrgLayout {
  const data = buildLayoutTree(model, opts)
  const peopleKnown = opts.peopleKnown ?? true
  const res =
    kind === 'horizontal'
      ? nodeLink(data, true)
      : kind === 'radial' || kind === 'layers3d'
        ? // 3B katmanlı görünüm radyal ağacın açı/yarıçapını kullanır; derinlik üçüncü eksene çıkar (org3d.ts).
          radial(data)
        : kind === 'sunburst'
          ? sunburst(data, peopleKnown)
          : kind === 'treemap'
            ? treemapLayout(data, peopleKnown)
            : kind === 'list'
              ? listLayout(data)
              : nodeLink(data, false)
  const byId = new Map(res.nodes.map((n) => [n.id, n]))
  return { kind, ...res, byId, bounds: boundsOf(res.nodes) }
}

/* ------------------------------------------------------------------ çizim yardımcıları (saf) */

/** Ağaç bağlantısının SVG yolu (yerleşime göre eğri). */
export function linkPath(kind: OrgLayoutKind, s: LayoutNode, t: LayoutNode): string {
  if (kind === 'horizontal') {
    const sx = s.x + NODE_W / 2
    const tx = t.x - NODE_W / 2
    const mx = (sx + tx) / 2
    return `M${sx},${s.y}C${mx},${s.y} ${mx},${t.y} ${tx},${t.y}`
  }
  if (kind === 'radial') {
    // Kutupsal ara noktalarla yumuşak eğri.
    const rm = ((s.radius ?? 0) + (t.radius ?? 0)) / 2
    const p1 = polar(s.angle ?? 0, rm)
    const p2 = polar(t.angle ?? 0, rm)
    return `M${s.x},${s.y}C${p1.x},${p1.y} ${p2.x},${p2.y} ${t.x},${t.y}`
  }
  // Dikey ve diğerleri: yukarıdan aşağı.
  const sy = s.y + NODE_H / 2
  const ty = t.y - NODE_H / 2
  const my = (sy + ty) / 2
  return `M${s.x},${sy}C${s.x},${my} ${t.x},${my} ${t.x},${ty}`
}

export const polar = (angle: number, r: number) => ({ x: r * Math.sin(angle), y: -r * Math.cos(angle) })

/** Halka dilimi yolu (a0..a1 açı, r0..r1 yarıçap). */
export function arcPath(a0: number, a1: number, r0: number, r1: number, pad = 0.004): string {
  const span = a1 - a0
  const p = span > 2 * pad ? pad : 0
  const s = a0 + p
  const e = a1 - p
  if (e - s >= 2 * Math.PI - 1e-6) {
    // Tam daire (tek çocuk ya da kök): iki yarım yay.
    const o1 = polar(0, r1)
    const o2 = polar(Math.PI, r1)
    const outer = `M${o1.x},${o1.y}A${r1},${r1} 0 1 1 ${o2.x},${o2.y}A${r1},${r1} 0 1 1 ${o1.x},${o1.y}Z`
    if (r0 <= 0) return outer
    const i1 = polar(0, r0)
    const i2 = polar(Math.PI, r0)
    return `${outer}M${i1.x},${i1.y}A${r0},${r0} 0 1 0 ${i2.x},${i2.y}A${r0},${r0} 0 1 0 ${i1.x},${i1.y}Z`
  }
  const large = e - s > Math.PI ? 1 : 0
  const a = polar(s, r1)
  const b = polar(e, r1)
  if (r0 <= 0) return `M0,0L${a.x},${a.y}A${r1},${r1} 0 ${large} 1 ${b.x},${b.y}Z`
  const c = polar(e, r0)
  const d = polar(s, r0)
  return `M${a.x},${a.y}A${r1},${r1} 0 ${large} 1 ${b.x},${b.y}L${c.x},${c.y}A${r0},${r0} 0 ${large} 0 ${d.x},${d.y}Z`
}

/** Bağlantı noktası: düğüm kutusunun hedefe bakan kenarı (matris eğrileri için). */
function anchor(n: LayoutNode, toward: LayoutNode): { x: number; y: number } {
  const dx = toward.x - n.x
  const dy = toward.y - n.y
  if (n.arc) return { x: n.x, y: n.y }
  if (n.radius !== undefined) {
    // Radyal düğüm dairedir: ok ucu dairenin kenarında dursun.
    const len = Math.hypot(dx, dy) || 1
    return { x: n.x + (dx / len) * 13, y: n.y + (dy / len) * 13 }
  }
  const hw = n.box.w / 2
  const hh = n.box.h / 2
  if (Math.abs(dx) * hh > Math.abs(dy) * hw) return { x: n.x + Math.sign(dx) * hw, y: n.y }
  return { x: n.x, y: n.y + Math.sign(dy) * hh }
}

/**
 * Matris (noktalı çizgi) bağının eğrisi. İki ucu da görünmüyorsa null (kapalı dal / odak dışı).
 * Eğri, ağaç bağlantılarıyla karışmasın diye düz çizginin yanına bükülür.
 */
export function matrixPath(layout: OrgLayout, fromId: string, toId: string): string | null {
  const a = layout.byId.get(fromId)
  const b = layout.byId.get(toId)
  if (!a || !b) return null
  const p = anchor(a, b)
  const q = anchor(b, a)
  const mx = (p.x + q.x) / 2
  const my = (p.y + q.y) / 2
  const dx = q.x - p.x
  const dy = q.y - p.y
  const len = Math.hypot(dx, dy) || 1
  const bend = Math.min(160, len * 0.25)
  // Radyal/halka yerleşimde merkeze doğru bük (daire dışına taşmasın).
  const sign = layout.kind === 'radial' || layout.kind === 'sunburst' ? (mx * -dy + my * dx > 0 ? -1 : 1) : 1
  const cx = mx + sign * (-dy / len) * bend
  const cy = my + sign * (dx / len) * bend
  return `M${p.x},${p.y}Q${cx},${cy} ${q.x},${q.y}`
}

/** Kutu görünür alanla kesişiyor mu (kırpma/sanallaştırma). */
export function intersects(
  box: LayoutNode['box'],
  view: { x: number; y: number; w: number; h: number },
  margin = 0,
): boolean {
  return (
    box.x + box.w >= view.x - margin &&
    box.x <= view.x + view.w + margin &&
    box.y + box.h >= view.y - margin &&
    box.y <= view.y + view.h + margin
  )
}
