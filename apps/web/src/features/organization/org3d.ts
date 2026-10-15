/**
 * 3B görünümlerin saf yerleşim hesapları — three.js/WebGL YOK (birim testi `org3d.test.ts`).
 *
 *  - `layered3d`: organizasyon şeması; her hiyerarşi seviyesi bir kat (y ekseni), kat içindeki
 *    konum radyal ağacın açı/yarıçapından (orgLayouts) gelir → x/z düzlemi.
 *  - `galaxyLayout`: şirket grubu; şirketler merkez, departmanlar seviyelerine göre yörüngede.
 *  - `forceLayout3d`: ekip ağı için küçük, deterministik kuvvet yönlendirmeli yerleşim.
 *  - `capNodes` / `labelCandidates`: performans bütçesi (çizilen düğüm ve etiket sınırı).
 *
 * Birimler piksel benzeri "sahne birimi"dir; kamera uzaklığını çizen taraf `radius`a göre ayarlar.
 */

import { hierarchy, partition } from 'd3-hierarchy'
import type { Department } from '@/api/types'
import { ROOT_ID, type OrgLayout } from './orgLayouts'

export interface Node3D {
  id: string
  name: string
  depth: number
  parentId: string | null
  x: number
  y: number
  z: number
  /** Küre yarıçapı (sahne birimi). */
  r: number
  /** Kök departman sırası (renk); şirket/merkez düğümünde -1. */
  colorIndex: number
  /** Alt departmanlar dahil kişi sayısı (bilinmiyorsa 0). */
  value: number
  childCount: number
  hiddenCount: number
  /** Galaksi: düğüm türü ve bağlı olduğu şirket. */
  kind?: 'company' | 'dept' | 'team'
  companyId?: string
}

export interface Link3D {
  source: string
  target: string
  /** Ekip ağı: bağ ağırlığı (kalınlık/opaklık). */
  weight?: number
}

export interface Ring3D {
  x: number
  y: number
  z: number
  r: number
}

export interface Scene3DData {
  nodes: Node3D[]
  links: Link3D[]
  rings?: Ring3D[]
  center: { x: number; y: number; z: number }
  /** Sınırlayıcı kürenin yarıçapı (kamera uzaklığı için). */
  radius: number
  /** Bütçe yüzünden çizilmeyen düğüm sayısı. */
  truncated: number
}

/** Katlar arası dikey uzaklık. */
export const LAYER_GAP = 140
/** Çizilen düğüm üst sınırı (LOD bütçesi); fazlası derinlik sırasına göre kırpılır. */
export const MAX_NODES_3D = 1500
/** Aynı anda gösterilen HTML etiket sayısı. */
export const MAX_LABELS_3D = 40

/** Kişi sayısından küre yarıçapı (alan ∝ kişi; aşırı büyük düğümleri sınırla). */
export function sizeOf(people: number, peopleKnown: boolean, base = 6): number {
  if (!peopleKnown) return base + 2
  return Math.min(40, base + 2.2 * Math.sqrt(Math.max(0, people)))
}

function sphereOf(nodes: Node3D[]): Pick<Scene3DData, 'center' | 'radius'> {
  if (nodes.length === 0) return { center: { x: 0, y: 0, z: 0 }, radius: 100 }
  let x0 = Infinity, y0 = Infinity, z0 = Infinity, x1 = -Infinity, y1 = -Infinity, z1 = -Infinity
  for (const n of nodes) {
    x0 = Math.min(x0, n.x - n.r); x1 = Math.max(x1, n.x + n.r)
    y0 = Math.min(y0, n.y - n.r); y1 = Math.max(y1, n.y + n.r)
    z0 = Math.min(z0, n.z - n.r); z1 = Math.max(z1, n.z + n.r)
  }
  const center = { x: (x0 + x1) / 2, y: (y0 + y1) / 2, z: (z0 + z1) / 2 }
  let radius = 1
  for (const n of nodes) radius = Math.max(radius, Math.hypot(n.x - center.x, n.y - center.y, n.z - center.z) + n.r)
  return { center, radius }
}

/**
 * Performans bütçesi: en fazla `max` düğüm. Sığ seviyeler önce kalır (kök her zaman);
 * eşit derinlikte büyük (kişi) düğümler öncelikli. Ucu düşen bağlar da düşer.
 */
export function capNodes(nodes: Node3D[], links: Link3D[], max = MAX_NODES_3D): { nodes: Node3D[]; links: Link3D[]; truncated: number } {
  if (nodes.length <= max) return { nodes, links, truncated: 0 }
  const kept = [...nodes].sort((a, b) => a.depth - b.depth || b.value - a.value || a.id.localeCompare(b.id)).slice(0, max)
  const ids = new Set(kept.map((n) => n.id))
  const order = new Map(nodes.map((n, i) => [n.id, i]))
  kept.sort((a, b) => order.get(a.id)! - order.get(b.id)!)
  return { nodes: kept, links: links.filter((l) => ids.has(l.source) && ids.has(l.target)), truncated: nodes.length - max }
}

/**
 * Etiket gösterilecek düğümler: seçili, vurgulu yol ve arama eşleşmeleri önce; sonra sığ ve
 * kalabalık düğümler. Etiketler HTML olarak çizilir (yazı tipi dosyası/CDN gerekmez).
 */
export function labelCandidates(
  nodes: Node3D[],
  priority: { selected?: string | null; highlight?: ReadonlySet<string> },
  limit = MAX_LABELS_3D,
): string[] {
  const out: string[] = []
  const seen = new Set<string>()
  const push = (id: string) => {
    if (out.length < limit && !seen.has(id)) {
      seen.add(id)
      out.push(id)
    }
  }
  const known = new Set(nodes.map((n) => n.id))
  if (priority.selected && known.has(priority.selected)) push(priority.selected)
  for (const id of priority.highlight ?? []) if (known.has(id)) push(id)
  const rest = [...nodes].sort((a, b) => a.depth - b.depth || b.r - a.r || b.value - a.value)
  for (const n of rest) push(n.id)
  return out
}

/* ------------------------------------------------------------------ katmanlı organizasyon */

/**
 * Radyal yerleşimi 3B kata çevirir: (açı, yarıçap) → x/z, derinlik → y (kök üstte).
 * `layout` `computeLayout(model, 'layers3d' | 'radial', …)` çıktısıdır (odak/kapalı dallar uygulanmış).
 */
export function layered3d(layout: OrgLayout, opts: { peopleKnown: boolean; maxNodes?: number }): Scene3DData {
  const maxDepth = layout.nodes.reduce((m, n) => Math.max(m, n.depth), 0)
  const top = (maxDepth * LAYER_GAP) / 2
  const nodes: Node3D[] = layout.nodes.map((n) => {
    const angle = n.angle ?? 0
    const radius = n.radius ?? 0
    const isRoot = n.id === ROOT_ID
    return {
      id: n.id,
      name: n.name,
      depth: n.depth,
      parentId: n.parentId,
      x: radius * Math.sin(angle),
      y: top - n.depth * LAYER_GAP,
      z: -radius * Math.cos(angle),
      r: isRoot ? 18 : sizeOf(n.value, opts.peopleKnown),
      colorIndex: n.colorIndex,
      value: n.value,
      childCount: n.childCount,
      hiddenCount: n.hiddenCount,
    }
  })
  const capped = capNodes(nodes, layout.links, opts.maxNodes ?? MAX_NODES_3D)
  return { nodes: capped.nodes, links: capped.links, truncated: capped.truncated, ...sphereOf(capped.nodes) }
}

/* ------------------------------------------------------------------ şirket grubu (galaksi) */

export interface GalaxyCompany {
  id: string
  name: string
  departments: Department[]
}

/** Galaksi yörünge aralıkları. */
const ORBIT0 = 70
const ORBIT_STEP = 48
/** Derin yörüngeler biraz aşağıda: galaksi düz bir disk gibi görünmesin (sığ bir kase). */
const orbitY = (depth: number) => -(depth - 1) * 16

interface GDatum {
  id: string
  name: string
  children: GDatum[]
}

/** Şirketin departman ağacı (döngüye dayanıklı: ziyaret edilen düğüme geri dönülmez). */
function companyTree(c: GalaxyCompany): GDatum {
  const ids = new Set(c.departments.map((d) => d.id))
  const kids = new Map<string, Department[]>()
  const roots: Department[] = []
  for (const d of c.departments) {
    const p = d.parentDepartmentId
    if (p && p !== d.id && ids.has(p)) kids.set(p, [...(kids.get(p) ?? []), d])
    else roots.push(d)
  }
  const seen = new Set<string>()
  const build = (d: Department): GDatum => {
    seen.add(d.id)
    const children = (kids.get(d.id) ?? []).filter((x) => !seen.has(x.id)).sort((a, b) => a.name.localeCompare(b.name, 'tr-TR'))
    return { id: d.id, name: d.name, children: children.map(build) }
  }
  const top = [...roots].sort((a, b) => a.name.localeCompare(b.name, 'tr-TR')).map(build)
  // Yalnızca döngüde kalanlar: kaybolmasın, köke alınır.
  for (const d of c.departments) if (!seen.has(d.id)) top.push(build(d))
  return { id: `company:${c.id}`, name: c.name, children: top }
}

/**
 * Şirketler merkez, departmanlar seviyesine göre iç içe yörüngelerde. Alt ağaç aynı açı
 * diliminde kalır (partition), yörüngeler seviyeye göre hafifçe eğiktir.
 * Boyut: kişi sayısı (`headcountOf`) biliniyorsa ona, bilinmiyorsa departman sayısına göre.
 */
export function galaxyLayout(
  companies: GalaxyCompany[],
  headcountOf: (deptId: string) => number,
  peopleKnown: boolean,
  maxNodes = MAX_NODES_3D,
): Scene3DData {
  const systems = [...companies]
    .sort((a, b) => a.name.localeCompare(b.name, 'tr-TR'))
    .map((c) => {
      const root = hierarchy(companyTree(c))
      // Alt ağaç toplamı: departmanın kendi kişileri + altındakiler.
      root.sum((d) => (d.id.startsWith('company:') ? 0 : peopleKnown ? Math.max(headcountOf(d.id), 0) : 0))
      const totals = new Map(root.descendants().map((h) => [h.data.id, h.value ?? 0]))
      const deptCount = root.descendants().length - 1
      // Açı dilimleri yaprak sayısına göre (eşit): kalabalık departman açıyı tekelleştirmesin.
      const layout = hierarchy(companyTree(c)).count()
      partition<GDatum>().size([2 * Math.PI, root.height + 1])(layout)
      const outer = ORBIT0 + Math.max(0, root.height - 1) * ORBIT_STEP
      return { c, layout, totals, deptCount, outer }
    })

  // Şirket merkezleri bir çember üzerinde; çevre, sistemlerin çaplarının toplamı kadar.
  const gap = 60
  const circumference = systems.reduce((s, x) => s + 2 * x.outer + gap, 0)
  const ringR = systems.length <= 1 ? 0 : circumference / (2 * Math.PI)
  let acc = 0
  const nodes: Node3D[] = []
  const links: Link3D[] = []
  const rings: Ring3D[] = []
  systems.forEach((s, i) => {
    const span = 2 * s.outer + gap
    const a = systems.length <= 1 ? 0 : ((acc + span / 2) / circumference) * 2 * Math.PI
    acc += span
    const cx = ringR * Math.sin(a)
    const cz = -ringR * Math.cos(a)
    const companyTotal = s.totals.get(`company:${s.c.id}`) ?? 0
    nodes.push({
      id: `company:${s.c.id}`,
      name: s.c.name,
      depth: 0,
      parentId: null,
      x: cx,
      y: 0,
      z: cz,
      r: peopleKnown ? Math.min(46, 12 + 2.4 * Math.sqrt(companyTotal)) : Math.min(46, 12 + 2.4 * Math.sqrt(s.deptCount)),
      colorIndex: -1,
      value: companyTotal,
      childCount: s.layout.children?.length ?? 0,
      hiddenCount: 0,
      kind: 'company',
      companyId: s.c.id,
    })
    for (let depth = 1; depth <= s.layout.height; depth++) rings.push({ x: cx, y: orbitY(depth), z: cz, r: ORBIT0 + (depth - 1) * ORBIT_STEP })
    // Kök departmanların sırası renk dizini olur (organizasyon şemasındaki departman rengiyle aynı mantık).
    const rootIndex = new Map((s.layout.children ?? []).map((h, k) => [h.data.id, k]))
    for (const h of s.layout.descendants()) {
      if (h.depth === 0) continue
      const p = h as typeof h & { x0: number; x1: number }
      const angle = (p.x0 + p.x1) / 2 + i * 0.37
      const orbit = ORBIT0 + (h.depth - 1) * ORBIT_STEP
      let top = h
      while (top.depth > 1 && top.parent) top = top.parent
      const people = s.totals.get(h.data.id) ?? 0
      const subtree = h.descendants().length
      nodes.push({
        id: h.data.id,
        name: h.data.name,
        depth: h.depth,
        parentId: h.parent && h.parent.depth > 0 ? h.parent.data.id : `company:${s.c.id}`,
        x: cx + orbit * Math.sin(angle),
        y: orbitY(h.depth),
        z: cz - orbit * Math.cos(angle),
        r: peopleKnown ? Math.min(22, 3.5 + 1.7 * Math.sqrt(people)) : Math.min(22, 4 + 1.6 * Math.sqrt(subtree)),
        colorIndex: rootIndex.get(top.data.id) ?? 0,
        value: people,
        childCount: h.children?.length ?? 0,
        hiddenCount: 0,
        kind: 'dept',
        companyId: s.c.id,
      })
      links.push({ source: h.parent && h.parent.depth > 0 ? h.parent.data.id : `company:${s.c.id}`, target: h.data.id })
    }
  })
  const capped = capNodes(nodes, links, maxNodes)
  return { nodes: capped.nodes, links: capped.links, rings, truncated: capped.truncated, ...sphereOf(capped.nodes) }
}

/* ------------------------------------------------------------------ kuvvet yönlendirmeli 3B */

export interface ForceEdge {
  source: string
  target: string
  weight: number
}

/**
 * Küçük, deterministik 3B kuvvet simülasyonu (ekip ağı; düğüm sayısı yüzlerle sınırlı).
 * Başlangıç Fibonacci küresi (rastgelelik yok → aynı veri aynı resim), itme O(n²),
 * bağlar ağırlıkla güçlenen yay, merkeze hafif çekim, soğuyan adım.
 */
export function forceLayout3d(
  ids: readonly string[],
  edges: readonly ForceEdge[],
  opts: { iterations?: number; spread?: number } = {},
): Map<string, { x: number; y: number; z: number }> {
  const n = ids.length
  const out = new Map<string, { x: number; y: number; z: number }>()
  if (n === 0) return out
  const spread = opts.spread ?? 60
  const R0 = spread * Math.cbrt(n)
  const px = new Float64Array(n)
  const py = new Float64Array(n)
  const pz = new Float64Array(n)
  const golden = Math.PI * (3 - Math.sqrt(5))
  for (let i = 0; i < n; i++) {
    const y = n === 1 ? 0 : 1 - (2 * i) / (n - 1)
    const rr = Math.sqrt(Math.max(0, 1 - y * y))
    px[i] = R0 * rr * Math.cos(golden * i)
    py[i] = R0 * y
    pz[i] = R0 * rr * Math.sin(golden * i)
  }
  const index = new Map(ids.map((id, i) => [id, i]))
  const springs = edges
    .map((e) => ({ a: index.get(e.source), b: index.get(e.target), w: Math.max(0, e.weight) }))
    .filter((e): e is { a: number; b: number; w: number } => e.a !== undefined && e.b !== undefined && e.a !== e.b && e.w > 0)
  const maxW = springs.reduce((m, e) => Math.max(m, e.w), 1)

  // Büyük ağda adım sayısı düşer (O(n²) itme), en az 60 adım.
  const iterations = opts.iterations ?? Math.max(60, Math.min(300, Math.round(60000 / Math.max(1, n))))
  const repulse = spread * spread * 2.2
  const fx = new Float64Array(n)
  const fy = new Float64Array(n)
  const fz = new Float64Array(n)
  for (let it = 0; it < iterations; it++) {
    const alpha = 1 - it / iterations
    fx.fill(0)
    fy.fill(0)
    fz.fill(0)
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        let dx = px[i] - px[j]
        let dy = py[i] - py[j]
        let dz = pz[i] - pz[j]
        let d2 = dx * dx + dy * dy + dz * dz
        if (d2 < 1e-6) {
          // Çakışan düğümler: deterministik küçük ayrık.
          dx = ((i - j) % 3) + 0.1
          dy = 0.1
          dz = 0.1
          d2 = dx * dx + dy * dy + dz * dz
        }
        const d = Math.sqrt(d2)
        const f = repulse / d2
        const ux = (dx / d) * f
        const uy = (dy / d) * f
        const uz = (dz / d) * f
        fx[i] += ux; fy[i] += uy; fz[i] += uz
        fx[j] -= ux; fy[j] -= uy; fz[j] -= uz
      }
    }
    for (const s of springs) {
      const dx = px[s.b] - px[s.a]
      const dy = py[s.b] - py[s.a]
      const dz = pz[s.b] - pz[s.a]
      const d = Math.sqrt(dx * dx + dy * dy + dz * dz) || 1e-3
      const strength = 0.05 + 0.25 * (s.w / maxW)
      const rest = spread * (1.6 - 0.8 * (s.w / maxW))
      const f = (d - rest) * strength
      const ux = (dx / d) * f
      const uy = (dy / d) * f
      const uz = (dz / d) * f
      fx[s.a] += ux; fy[s.a] += uy; fz[s.a] += uz
      fx[s.b] -= ux; fy[s.b] -= uy; fz[s.b] -= uz
    }
    const maxStep = spread * 0.6 * alpha + 0.5
    for (let i = 0; i < n; i++) {
      // Merkeze çekim: bağlantısız düğümler uzaklara savrulmasın.
      fx[i] -= px[i] * 0.02
      fy[i] -= py[i] * 0.02
      fz[i] -= pz[i] * 0.02
      const len = Math.sqrt(fx[i] * fx[i] + fy[i] * fy[i] + fz[i] * fz[i])
      const k = len > maxStep ? maxStep / len : 1
      px[i] += fx[i] * k
      py[i] += fy[i] * k
      pz[i] += fz[i] * k
    }
  }
  // Ağırlık merkezini başlangıca taşı.
  let cx = 0, cy = 0, cz = 0
  for (let i = 0; i < n; i++) { cx += px[i]; cy += py[i]; cz += pz[i] }
  cx /= n; cy /= n; cz /= n
  ids.forEach((id, i) => out.set(id, { x: px[i] - cx, y: py[i] - cy, z: pz[i] - cz }))
  return out
}

/** Hazır düğüm listesinden sahne verisi (ekip ağı gibi dışarıda konumlanan düğümler). */
export function sceneOf(nodes: Node3D[], links: Link3D[], maxNodes = MAX_NODES_3D): Scene3DData {
  const capped = capNodes(nodes, links, maxNodes)
  return { nodes: capped.nodes, links: capped.links, truncated: capped.truncated, ...sphereOf(capped.nodes) }
}
