/**
 * Organizasyon şeması görünüm durumu: yerleşim, renklendirme, odak, kapalı dallar,
 * matris çizgileri, yakınlık ve seçim. Durum adres çubuğunda tutulur; bağlantıyı
 * açan kişi aynı görünümü görür ("Bağlantıyı kopyala").
 *
 * Bu dosya saf TypeScript'tir (React/DOM/d3 yok): adres kodlama, görünür ağaç ve
 * klavye gezintisi burada; birim testi `orgViewState.test.ts`.
 */

import type { ChartDept, ChartModel } from './orgChartModel'

/** `layers3d`: 3B katmanlı görünüm (her hiyerarşi seviyesi bir kat; tembel yüklenen WebGL). */
export type OrgLayoutKind = 'vertical' | 'horizontal' | 'radial' | 'sunburst' | 'treemap' | 'list' | 'layers3d'
export type OrgEncoding = 'department' | 'tenure' | 'leave'

export const LAYOUT_KINDS: readonly OrgLayoutKind[] = ['vertical', 'horizontal', 'radial', 'sunburst', 'treemap', 'list', 'layers3d']
export const ENCODINGS: readonly OrgEncoding[] = ['department', 'tenure', 'leave']

export interface OrgViewState {
  layout: OrgLayoutKind
  encoding: OrgEncoding
  /** Odaklanılan departman (yalnızca onun alt ağacı çizilir). */
  focus: string | null
  /** Alt departmanları gizlenmiş departmanlar. null: varsayılan (büyük ağaçta derin dallar kapalı). */
  collapsed: Set<string> | null
  /** Matris (noktalı çizgi) bağları çizilsin mi. */
  matrix: boolean
  /** Yakınlaştırma oranı (1 = %100). */
  zoom: number
  /** Seçili departman (`d:<id>`) ya da kişi (`p:<id>`): köke giden yol vurgulanır. */
  selected: string | null
}

export const DEFAULT_VIEW: OrgViewState = {
  layout: 'vertical',
  encoding: 'department',
  focus: null,
  collapsed: null,
  matrix: false,
  zoom: 1,
  selected: null,
}

/* ------------------------------------------------------------------ adres */

/** Adres parametre adları (Türkçe, mevcut `gorunum=sema` ile aynı üslup). */
export const PARAM = {
  layout: 'yerlesim',
  encoding: 'renk',
  focus: 'odak',
  collapsed: 'kapali',
  matrix: 'matris',
  zoom: 'yakinlik',
  selected: 'sec',
} as const

const LAYOUT_TOKEN: Record<OrgLayoutKind, string> = {
  vertical: 'dikey',
  horizontal: 'yatay',
  radial: 'radyal',
  sunburst: 'halka',
  treemap: 'kutu',
  list: 'liste',
  layers3d: '3b',
}
const ENCODING_TOKEN: Record<OrgEncoding, string> = { department: 'departman', tenure: 'kidem', leave: 'izin' }

const invert = <K extends string>(m: Record<K, string>) =>
  Object.fromEntries(Object.entries(m).map(([k, v]) => [v, k])) as Record<string, K>
const LAYOUT_BY_TOKEN = invert(LAYOUT_TOKEN)
const ENCODING_BY_TOKEN = invert(ENCODING_TOKEN)

/** Kapalı dal listesinde kimliğin kısaltması: adres uzamasın diye UUID'nin ilk 8 karakteri. */
export const SHORT_ID = 8
const shortId = (id: string) => id.replace(/-/g, '').slice(0, SHORT_ID).toLowerCase()

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export const clampZoom = (z: number) => Math.min(4, Math.max(0.1, Math.round(z * 100) / 100))

/**
 * Durumu adres parametrelerine çevirir. Varsayılan değerler yazılmaz (null = parametreyi sil),
 * böylece varsayılan görünümün adresi sade kalır.
 */
export function encodeViewState(state: OrgViewState): Record<string, string | null> {
  const collapsed =
    state.collapsed === null ? null : [...state.collapsed].map(shortId).sort().join('.')
  return {
    [PARAM.layout]: state.layout === DEFAULT_VIEW.layout ? null : LAYOUT_TOKEN[state.layout],
    [PARAM.encoding]: state.encoding === DEFAULT_VIEW.encoding ? null : ENCODING_TOKEN[state.encoding],
    [PARAM.focus]: state.focus,
    // Boş dize anlamlıdır: "hiçbir dal kapalı değil" (varsayılan otomatik kapatmadan farklı).
    [PARAM.collapsed]: collapsed,
    [PARAM.matrix]: state.matrix ? '1' : null,
    [PARAM.zoom]: Math.abs(state.zoom - 1) < 0.005 ? null : String(clampZoom(state.zoom)),
    [PARAM.selected]: state.selected,
  }
}

/**
 * Adresten durumu okur. `deptIds`: şemadaki departman kimlikleri — kısaltılmış kapalı dal
 * listesi ve odak bunlarla eşlenir; tanınmayan değerler sessizce yok sayılır (eski/bozuk bağlantı).
 */
export function decodeViewState(params: URLSearchParams, deptIds: Iterable<string>): OrgViewState {
  const ids = [...deptIds]
  const known = new Set(ids)

  const layout = LAYOUT_BY_TOKEN[params.get(PARAM.layout) ?? ''] ?? DEFAULT_VIEW.layout
  const encoding = ENCODING_BY_TOKEN[params.get(PARAM.encoding) ?? ''] ?? DEFAULT_VIEW.encoding

  const focusRaw = params.get(PARAM.focus)
  const focus = focusRaw && known.has(focusRaw) ? focusRaw : null

  let collapsed: Set<string> | null = null
  const rawCollapsed = params.get(PARAM.collapsed)
  if (rawCollapsed !== null) {
    const byShort = new Map<string, string>()
    for (const id of ids) byShort.set(shortId(id), id)
    collapsed = new Set(
      rawCollapsed
        .split('.')
        .map((s) => byShort.get(s.trim().toLowerCase()))
        .filter((x): x is string => Boolean(x)),
    )
  }

  const zoomNum = Number(params.get(PARAM.zoom))
  const zoom = params.get(PARAM.zoom) !== null && Number.isFinite(zoomNum) && zoomNum > 0 ? clampZoom(zoomNum) : 1

  const sel = params.get(PARAM.selected)
  let selected: string | null = null
  if (sel) {
    const [kind, id] = [sel.slice(0, 2), sel.slice(2)]
    if (kind === 'd:' && known.has(id)) selected = sel
    else if (kind === 'p:' && UUID_RE.test(id)) selected = sel
  }

  return { layout, encoding, focus, collapsed, matrix: params.get(PARAM.matrix) === '1', zoom, selected }
}

/** Mevcut parametrelere durum güncellemesini uygular (diğer parametreler — ör. `gorunum` — korunur). */
export function applyViewParams(current: URLSearchParams, state: OrgViewState): URLSearchParams {
  const next = new URLSearchParams(current)
  for (const [k, v] of Object.entries(encodeViewState(state))) {
    if (v === null) next.delete(k)
    else next.set(k, v)
  }
  return next
}

/* ------------------------------------------------------------------ görünür ağaç */

/** Bu sayıdan fazla departmanda, durum adreste yoksa 2. seviyeden derin dallar kapalı açılır. */
export const BIG_TREE = 120

/** Varsayılan kapalı dallar: büyük ağaçta derinliği ≥ 2 olan ve alt departmanı olanlar. */
export function defaultCollapsed(model: ChartModel): Set<string> {
  const out = new Set<string>()
  if (model.byId.size <= BIG_TREE) return out
  for (const n of model.byId.values()) if (n.depth >= 2 && n.children.length > 0) out.add(n.dept.id)
  return out
}

/** Çizilecek kökler: odak varsa yalnızca o departman. */
export function visibleRoots(model: ChartModel, focus: string | null): ChartDept[] {
  const f = focus ? model.byId.get(focus) : undefined
  return f ? [f] : model.roots
}

/** Görünür departmanlar, ön-sıra (pre-order) dizilişinde (kapalı dalların altı hariç). */
export function visibleOrder(roots: ChartDept[], collapsed: ReadonlySet<string>): ChartDept[] {
  const out: ChartDept[] = []
  const stack = [...roots].reverse()
  while (stack.length) {
    const n = stack.pop()!
    out.push(n)
    if (!collapsed.has(n.dept.id)) for (let i = n.children.length - 1; i >= 0; i--) stack.push(n.children[i])
  }
  return out
}

/** Bir departmanın alt ağacındaki departman sayısı (kendisi hariç). */
export function descendantCount(node: ChartDept): number {
  let n = 0
  const stack = [...node.children]
  while (stack.length) {
    const c = stack.pop()!
    n++
    stack.push(...c.children)
  }
  return n
}

/** `id` departmanı `rootId` alt ağacında mı (kendisi dahil). */
export function isInSubtree(model: ChartModel, rootId: string, id: string): boolean {
  const seen = new Set<string>()
  let cur = model.byId.get(id)?.dept
  while (cur && !seen.has(cur.id)) {
    if (cur.id === rootId) return true
    seen.add(cur.id)
    cur = cur.parentDepartmentId ? model.byId.get(cur.parentDepartmentId)?.dept : undefined
  }
  return false
}

/* ------------------------------------------------------------------ klavye gezintisi */

/**
 * Ok tuşlarının anlamı:
 *  - `tree`: WAI-ARIA ağaç deseni (liste, halka, ağaç haritası, radyal): ↑/↓ önceki/sonraki görünür düğüm,
 *    → aç ya da ilk çocuk, ← kapat ya da ebeveyn.
 *  - `down`: yukarıdan aşağı ağaç: ↑ ebeveyn, ↓ ilk çocuk (kapalıysa açar), ←/→ kardeşler.
 *  - `right`: soldan sağa ağaç: ← ebeveyn, → ilk çocuk, ↑/↓ kardeşler.
 * Home/End ilk/son görünür düğüm.
 */
export type NavMode = 'tree' | 'down' | 'right'

export interface NavResult {
  /** Odağın gideceği departman. */
  move?: string
  /** Açılıp kapanacak departman. */
  toggle?: string
}

export function navigate(
  key: string,
  currentId: string,
  roots: ChartDept[],
  collapsed: ReadonlySet<string>,
  mode: NavMode,
): NavResult | null {
  const order = visibleOrder(roots, collapsed)
  const idx = order.findIndex((n) => n.dept.id === currentId)
  if (idx < 0) return order[0] ? { move: order[0].dept.id } : null
  const node = order[idx]
  const parentOf = (n: ChartDept) => {
    const pid = n.dept.parentDepartmentId
    return pid ? order.find((x) => x.dept.id === pid && x.children.includes(n)) : undefined
  }
  const siblings = () => {
    const p = parentOf(node)
    return p ? p.children : roots
  }
  const sibling = (delta: number) => {
    const s = siblings()
    const i = s.indexOf(node)
    return s[i + delta]?.dept.id
  }
  const isOpen = node.children.length > 0 && !collapsed.has(node.dept.id)
  const firstChild = () => (isOpen ? { move: node.children[0].dept.id } : node.children.length ? { toggle: node.dept.id } : null)
  const parent = () => {
    const p = parentOf(node)
    return p ? { move: p.dept.id } : null
  }
  const wrap = (id: string | undefined) => (id ? { move: id } : null)

  if (key === 'Home') return wrap(order[0]?.dept.id)
  if (key === 'End') return wrap(order[order.length - 1]?.dept.id)

  if (mode === 'tree') {
    switch (key) {
      case 'ArrowDown':
        return wrap(order[idx + 1]?.dept.id)
      case 'ArrowUp':
        return wrap(order[idx - 1]?.dept.id)
      case 'ArrowRight':
        return firstChild()
      case 'ArrowLeft':
        return isOpen ? { toggle: node.dept.id } : parent()
    }
    return null
  }
  const [toParent, toChild, prev, next] =
    mode === 'down' ? ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'] : ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown']
  if (key === toParent) return parent()
  if (key === toChild) return firstChild()
  if (key === prev) return wrap(sibling(-1))
  if (key === next) return wrap(sibling(1))
  return null
}
