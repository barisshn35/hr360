/**
 * Ortak 3B sahne (three.js) — organizasyon katmanları, şirket grubu galaksisi ve ekip ağı aynı
 * bileşeni kullanır. Yerleşim saf hesaptır (`org3d.ts`); burası yalnızca çizer, döndürür/
 * yakınlaştırır/kaydırır (OrbitControls) ve düğüm seçtirir.
 *
 * - Tembel yüklenir: three.js yalnızca bir 3B görünüm açıldığında indirilir (ana pakete girmez).
 *   three doğrudan (imperatif) kullanılır: @react-three/fiber'in genel JSX tür genişletmesi
 *   uygulamadaki `React.ElementType` kullanımlarını bozuyordu; ayrıca paket küçülür.
 * - CSP uyumlu: eval/satır içi betik yok; etiketler HTML (yazı tipi dosyası/CDN gerekmez).
 * - Düğümler tek InstancedMesh (binlerce düğümde tek çizim çağrısı); bağlar tek LineSegments
 *   ya da (ağırlıklı ağda) örneklenmiş silindir. Çizim yalnızca bir şey değişince yapılır.
 * - Azaltılmış hareket: otomatik dönme, kamera ve konum geçişleri kapalı.
 * - Sökülürken tüm geometri/malzeme, denetimler ve WebGL bağlamı bırakılır.
 */

import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import {
  AmbientLight,
  BufferAttribute,
  BufferGeometry,
  Color,
  CylinderGeometry,
  DirectionalLight,
  InstancedMesh,
  LineBasicMaterial,
  LineDashedMaterial,
  LineSegments,
  Material,
  Matrix4,
  Mesh,
  MeshBasicMaterial,
  MeshLambertMaterial,
  Object3D,
  PerspectiveCamera,
  Quaternion,
  Raycaster,
  Scene,
  SphereGeometry,
  Spherical,
  Vector2,
  Vector3,
  WebGLRenderer,
} from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { cn } from '@/lib/utils'
import type { Node3D, Scene3DData } from './org3d'
import { prefersReducedMotion } from './webgl'

export interface SceneLinkExtra {
  source: string
  target: string
}

export interface Scene3DProps {
  data: Scene3DData
  /** Düğüm rengi (CSS rengi; `hsl(var(--…))` gibi değişkenler de olur). */
  colorOf: (n: Node3D) => string
  /** Etiket ve araç ipucu. */
  labelOf: (n: Node3D) => { title: string; sub?: string }
  /** Etiketi çizilecek düğümler (öncelik sırasıyla; `labelCandidates`). */
  labelIds: readonly string[]
  selected: string | null
  /** Vurgulanan düğümler (köke giden yol, arama eşleşmeleri). */
  highlight: ReadonlySet<string>
  /** Kısa süre parlayan düğümler (zaman kaydırıcısında değişenler). `pulseSeq` her değişimde artar. */
  pulse?: ReadonlySet<string>
  pulseSeq?: number
  /** Ağaç bağları ince çizgi; ağırlıklı ağ bağları kalınlığı ağırlıkla artan silindir. */
  linkStyle?: 'line' | 'tube'
  /** Matris (noktalı) bağları gibi ek, kesikli çizgiler. */
  extraLinks?: SceneLinkExtra[]
  onPick: (id: string) => void
  centerRequest?: { id: string; seq: number } | null
  fitRequest?: number
  /** Araç çubuğundaki yakınlık (oran değişince kamera yaklaşır/uzaklaşır). */
  zoom?: number
  autoRotate?: boolean
  ariaLabel: string
  className?: string
}

/* ------------------------------------------------------------------ renk çözümleme */

/**
 * CSS rengini (değişkenli olabilir) RGB'ye çevirir: görünmez bir öğede hesaplanan değer 1×1
 * tuvale boyanıp okunur — tarayıcının desteklediği her renk sözdizimi (oklch dahil) çalışır.
 * Yarı saydam renkler tema zemini üzerine karıştırılır.
 */
function colorResolver(host: HTMLElement) {
  const cache = new Map<string, Color>()
  const probe = document.createElement('span')
  probe.style.display = 'none'
  host.appendChild(probe)
  const canvas = document.createElement('canvas')
  canvas.width = 1
  canvas.height = 1
  const ctx = canvas.getContext('2d', { willReadFrequently: true })
  const dark = document.documentElement.classList.contains('dark')
  const resolve = (css: string): Color => {
    const hit = cache.get(css)
    if (hit) return hit
    let out = new Color(0x8a94a6)
    try {
      probe.style.color = ''
      probe.style.color = css
      const computed = getComputedStyle(probe).color
      if (ctx && computed) {
        ctx.clearRect(0, 0, 1, 1)
        ctx.fillStyle = '#000'
        ctx.fillStyle = computed
        ctx.fillRect(0, 0, 1, 1)
        const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data
        const k = a / 255
        const bg = dark ? 22 : 246
        out = new Color().setRGB((r * k + bg * (1 - k)) / 255, (g * k + bg * (1 - k)) / 255, (b * k + bg * (1 - k)) / 255, 'srgb')
      }
    } catch {
      // Hesaplanamayan renk: nötr gri.
    }
    cache.set(css, out)
    return out
  }
  return { resolve, dispose: () => probe.remove() }
}

/* ------------------------------------------------------------------ motor */

const UP = new Vector3(0, 1, 0)
const tmpObj = new Object3D()
const tmpMat = new Matrix4()
const tmpV = new Vector3()
const tmpQ = new Quaternion()

interface EngineCallbacks {
  onPick: (id: string) => void
  onHover: (h: { id: string; x: number; y: number } | null) => void
  onLost: () => void
  onInteract: () => void
}

interface StyleState {
  selected: string | null
  highlight: ReadonlySet<string>
  pulse: string[]
  pulseStart: number
}

class Engine {
  readonly renderer: WebGLRenderer
  readonly scene = new Scene()
  readonly camera = new PerspectiveCamera(45, 1, 1, 20000)
  readonly controls: OrbitControls
  private readonly host: HTMLElement
  private readonly cb: EngineCallbacks
  private readonly reduced: boolean
  private readonly ro: ResizeObserver
  private raf = 0
  private width = 1
  private height = 1
  private colors: ReturnType<typeof colorResolver>

  private data: Scene3DData | null = null
  private index = new Map<string, number>()
  /** Çizilen konum/yarıçap (x, y, z, r) — geçiş sırasında hedefe yaklaşır. */
  private cur = new Float32Array(0)
  private settled = true
  private colorOf: (n: Node3D) => string = () => '#888'
  private linkStyle: 'line' | 'tube' = 'line'
  private links: Array<{ a: number; b: number; w: number; s: string; t: string }> = []
  private extra: Array<{ a: number; b: number }> = []
  private maxW = 1
  private style: StyleState = { selected: null, highlight: new Set(), pulse: [], pulseStart: 0 }

  private nodes: InstancedMesh | null = null
  private lines: LineSegments | null = null
  private tubes: InstancedMesh | null = null
  private extraLines: LineSegments | null = null
  private rings: LineSegments | null = null
  private halos: InstancedMesh | null = null
  private pulses: InstancedMesh | null = null

  private anim: { p0: Vector3; p1: Vector3; t0: Vector3; t1: Vector3; start: number; dur: number } | null = null
  private fitted = false
  /** HTML etiket öğeleri (React kabı doldurur). */
  labelEls: Map<string, HTMLElement>

  private down: { x: number; y: number } | null = null
  private readonly raycaster = new Raycaster()
  private readonly pointer = new Vector2()

  constructor(host: HTMLElement, cb: EngineCallbacks, reduced: boolean, labelEls: Map<string, HTMLElement>) {
    this.host = host
    this.labelEls = labelEls
    this.cb = cb
    this.reduced = reduced
    this.colors = colorResolver(host)
    this.renderer = new WebGLRenderer({ antialias: true, alpha: true, powerPreference: 'low-power' })
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
    this.renderer.setClearColor(0x000000, 0)
    const el = this.renderer.domElement
    el.style.display = 'block'
    el.style.width = '100%'
    el.style.height = '100%'
    el.setAttribute('aria-hidden', 'true')
    host.prepend(el)

    this.scene.add(new AmbientLight(0xffffff, 1.6))
    const d1 = new DirectionalLight(0xffffff, 1.8)
    d1.position.set(1, 2, 1.5)
    const d2 = new DirectionalLight(0xffffff, 0.5)
    d2.position.set(-1, -0.5, -1)
    this.scene.add(d1, d2)

    this.controls = new OrbitControls(this.camera, el)
    this.controls.enableDamping = !reduced
    this.controls.dampingFactor = 0.12
    this.controls.screenSpacePanning = true
    this.controls.rotateSpeed = 0.7
    this.controls.autoRotateSpeed = 0.6
    this.controls.addEventListener('change', this.request)
    this.controls.addEventListener('start', this.onStart)

    el.addEventListener('pointerdown', this.onDown)
    el.addEventListener('pointerup', this.onUp)
    el.addEventListener('pointermove', this.onMove)
    el.addEventListener('pointerleave', this.onLeave)
    el.addEventListener('webglcontextlost', this.onContextLost)

    this.ro = new ResizeObserver(() => this.resize())
    this.ro.observe(host)
    this.resize()
  }

  /* ---------------- yaşam döngüsü */

  dispose() {
    cancelAnimationFrame(this.raf)
    this.ro.disconnect()
    const el = this.renderer.domElement
    el.removeEventListener('pointerdown', this.onDown)
    el.removeEventListener('pointerup', this.onUp)
    el.removeEventListener('pointermove', this.onMove)
    el.removeEventListener('pointerleave', this.onLeave)
    el.removeEventListener('webglcontextlost', this.onContextLost)
    this.controls.removeEventListener('change', this.request)
    this.controls.removeEventListener('start', this.onStart)
    this.controls.dispose()
    this.clearObjects()
    this.scene.clear()
    this.colors.dispose()
    this.renderer.dispose()
    this.renderer.forceContextLoss()
    el.remove()
  }

  private disposeObject(o: Mesh | LineSegments | InstancedMesh | null) {
    if (!o) return
    this.scene.remove(o)
    o.geometry.dispose()
    const m = o.material as Material | Material[]
    if (Array.isArray(m)) m.forEach((x) => x.dispose())
    else m.dispose()
    if (o instanceof InstancedMesh) o.dispose()
  }

  private clearObjects() {
    for (const o of [this.nodes, this.lines, this.tubes, this.extraLines, this.rings, this.halos, this.pulses]) this.disposeObject(o)
    this.nodes = this.lines = this.tubes = this.extraLines = this.rings = this.halos = this.pulses = null
  }

  private resize() {
    const w = Math.max(1, this.host.clientWidth)
    const h = Math.max(1, this.host.clientHeight)
    if (w === this.width && h === this.height) return
    this.width = w
    this.height = h
    this.renderer.setSize(w, h, false)
    this.camera.aspect = w / h
    this.camera.updateProjectionMatrix()
    this.request()
  }

  readonly request = () => {
    if (this.raf) return
    this.raf = requestAnimationFrame(this.frame)
  }

  private readonly onStart = () => {
    this.controls.autoRotate = false
    this.cb.onInteract()
  }

  private readonly onContextLost = (e: Event) => {
    e.preventDefault()
    this.cb.onLost()
  }

  /* ---------------- veri */

  setData(data: Scene3DData, colorOf: (n: Node3D) => string, linkStyle: 'line' | 'tube', extra: SceneLinkExtra[]) {
    const prev = this.data ? { index: this.index, cur: this.cur } : null
    const animate = !this.reduced && prev !== null
    this.data = data
    this.colorOf = colorOf
    this.linkStyle = linkStyle
    this.index = new Map(data.nodes.map((n, i) => [n.id, i]))
    const cur = new Float32Array(data.nodes.length * 4)
    data.nodes.forEach((n, i) => {
      const j = prev?.index.get(n.id)
      // Aynı kimlikli düğüm eski yerinden kayar, yeni düğüm sıfır boyuttan büyür.
      if (animate && j !== undefined) cur.set(prev!.cur.subarray(j * 4, j * 4 + 4), i * 4)
      else cur.set([n.x, n.y, n.z, animate ? 0 : n.r], i * 4)
    })
    this.cur = cur
    this.links = data.links
      .map((l) => ({ a: this.index.get(l.source), b: this.index.get(l.target), w: l.weight ?? 1, s: l.source, t: l.target }))
      .filter((l): l is { a: number; b: number; w: number; s: string; t: string } => l.a !== undefined && l.b !== undefined)
    this.maxW = this.links.reduce((m, l) => Math.max(m, l.w), 1)
    this.extra = extra
      .map((l) => ({ a: this.index.get(l.source), b: this.index.get(l.target) }))
      .filter((l): l is { a: number; b: number } => l.a !== undefined && l.b !== undefined)

    this.clearObjects()
    const count = data.nodes.length
    const seg = count > 600 ? 10 : 18
    this.nodes = new InstancedMesh(new SphereGeometry(1, seg, Math.round(seg * 0.7)), new MeshLambertMaterial(), Math.max(1, count))
    this.nodes.count = count
    this.scene.add(this.nodes)

    if (linkStyle === 'tube') {
      if (this.links.length) {
        this.tubes = new InstancedMesh(new CylinderGeometry(1, 1, 1, 8, 1, true), new MeshLambertMaterial({ transparent: true, opacity: 0.7 }), this.links.length)
        this.scene.add(this.tubes)
      }
    } else {
      const g = new BufferGeometry()
      g.setAttribute('position', new BufferAttribute(new Float32Array(this.links.length * 6), 3))
      g.setAttribute('color', new BufferAttribute(new Float32Array(this.links.length * 6), 3))
      this.lines = new LineSegments(g, new LineBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.75 }))
      this.scene.add(this.lines)
    }
    if (this.extra.length) {
      const g = new BufferGeometry()
      g.setAttribute('position', new BufferAttribute(new Float32Array(this.extra.length * 6), 3))
      this.extraLines = new LineSegments(g, new LineDashedMaterial({ color: this.colors.resolve('hsl(var(--chart-4))'), dashSize: 10, gapSize: 7 }))
      this.scene.add(this.extraLines)
    }
    if (data.rings?.length) {
      const SEG = 96
      const pts = new Float32Array(data.rings.length * SEG * 6)
      let k = 0
      for (const r of data.rings) {
        for (let s = 0; s < SEG; s++) {
          const a0 = (s / SEG) * Math.PI * 2
          const a1 = ((s + 1) / SEG) * Math.PI * 2
          pts.set([r.x + r.r * Math.sin(a0), r.y, r.z - r.r * Math.cos(a0), r.x + r.r * Math.sin(a1), r.y, r.z - r.r * Math.cos(a1)], k)
          k += 6
        }
      }
      const g = new BufferGeometry()
      g.setAttribute('position', new BufferAttribute(pts, 3))
      this.rings = new LineSegments(g, new LineBasicMaterial({ color: this.colors.resolve('hsl(var(--border))'), transparent: true, opacity: 0.7 }))
      this.scene.add(this.rings)
    }
    this.applyColors()
    this.buildHalos()
    this.settled = false
    this.writePositions()

    // Yerleşim türü/odak değişince sığdır; zaman kaydırıcısındaki küçük değişimde kamera yerinde kalır.
    const shape = `${data.nodes[0]?.id ?? ''}|${Math.round(data.radius / 60)}`
    if (shape !== this.shape) {
      const first = this.shape === ''
      this.shape = shape
      this.fit(first)
    }
    this.request()
  }
  private shape = ''

  setColorOf(colorOf: (n: Node3D) => string) {
    this.colorOf = colorOf
    this.applyColors()
    this.request()
  }

  /** Tema değişti: önbellekteki renkler yeniden hesaplanır. */
  refreshTheme() {
    this.colors.dispose()
    this.colors = colorResolver(this.host)
    if (this.data) this.setData(this.data, this.colorOf, this.linkStyle, this.extra.map((e) => ({ source: this.data!.nodes[e.a].id, target: this.data!.nodes[e.b].id })))
  }

  private applyColors() {
    const d = this.data
    if (!d || !this.nodes) return
    d.nodes.forEach((n, i) => this.nodes!.setColorAt(i, this.colors.resolve(this.colorOf(n))))
    if (this.nodes.instanceColor) this.nodes.instanceColor.needsUpdate = true
    this.applyLinkColors()
  }

  private applyLinkColors() {
    const base = this.colors.resolve('hsl(var(--muted-foreground) / 0.55)')
    const on = this.colors.resolve('hsl(var(--primary))')
    const { selected, highlight } = this.style
    const colorFor = (l: { s: string; t: string }) =>
      (selected && (l.s === selected || l.t === selected)) || (highlight.has(l.s) && highlight.has(l.t)) ? on : base
    if (this.tubes) {
      this.links.forEach((l, i) => this.tubes!.setColorAt(i, colorFor(l)))
      if (this.tubes.instanceColor) this.tubes.instanceColor.needsUpdate = true
    }
    if (this.lines) {
      const attr = this.lines.geometry.getAttribute('color') as BufferAttribute
      this.links.forEach((l, i) => {
        const c = colorFor(l)
        attr.setXYZ(i * 2, c.r, c.g, c.b)
        attr.setXYZ(i * 2 + 1, c.r, c.g, c.b)
      })
      attr.needsUpdate = true
    }
  }

  setStyle(selected: string | null, highlight: ReadonlySet<string>, pulse: readonly string[], pulseChanged: boolean) {
    this.style = {
      selected,
      highlight,
      pulse: pulse.filter((id) => this.index.has(id)).slice(0, 300),
      pulseStart: pulseChanged ? performance.now() : this.style.pulseStart,
    }
    this.applyLinkColors()
    this.buildHalos()
    this.request()
  }

  private haloIds: string[] = []

  private buildHalos() {
    this.disposeObject(this.halos)
    this.disposeObject(this.pulses)
    this.halos = this.pulses = null
    const { selected, highlight, pulse } = this.style
    const ids: string[] = []
    if (selected && this.index.has(selected)) ids.push(selected)
    for (const id of highlight) if (id !== selected && this.index.has(id) && ids.length < 200) ids.push(id)
    this.haloIds = ids
    if (ids.length) {
      this.halos = new InstancedMesh(
        new SphereGeometry(1, 18, 12),
        new MeshBasicMaterial({ color: this.colors.resolve('hsl(var(--primary))'), transparent: true, opacity: 0.22, depthWrite: false }),
        ids.length,
      )
      this.scene.add(this.halos)
    }
    if (pulse.length) {
      this.pulses = new InstancedMesh(
        new SphereGeometry(1, 16, 10),
        new MeshBasicMaterial({ color: 0xf59e0b, transparent: true, opacity: 0.5, depthWrite: false }),
        pulse.length,
      )
      this.scene.add(this.pulses)
    }
    this.writeHalos()
  }

  /* ---------------- kamera */

  private moveTo(pos: Vector3, target: Vector3) {
    if (this.reduced) {
      this.camera.position.copy(pos)
      this.controls.target.copy(target)
      this.controls.update()
      this.request()
      return
    }
    this.anim = { p0: this.camera.position.clone(), p1: pos, t0: this.controls.target.clone(), t1: target, start: performance.now(), dur: 650 }
    this.request()
  }

  fit(instant = false) {
    const d = this.data
    if (!d) return
    const c = new Vector3(d.center.x, d.center.y, d.center.z)
    const fov = (this.camera.fov * Math.PI) / 180
    const half = Math.min(fov / 2, Math.atan(Math.tan(fov / 2) * this.camera.aspect))
    const dist = (d.radius / Math.sin(half)) * 1.02
    const dir = this.fitted ? this.camera.position.clone().sub(this.controls.target).normalize() : new Vector3(0.45, 0.55, 0.7).normalize()
    this.camera.near = Math.max(0.5, dist / 2000)
    this.camera.far = dist + d.radius * 6
    this.camera.updateProjectionMatrix()
    this.controls.maxDistance = dist * 4
    const pos = c.clone().add(dir.multiplyScalar(dist))
    if (instant || !this.fitted) {
      this.camera.position.copy(pos)
      this.controls.target.copy(c)
      this.controls.update()
      this.request()
    } else this.moveTo(pos, c)
    this.fitted = true
  }

  centerOn(id: string) {
    const i = this.index.get(id)
    const d = this.data
    if (i === undefined || !d) return
    const p = new Vector3(d.nodes[i].x, d.nodes[i].y, d.nodes[i].z)
    const dir = this.camera.position.clone().sub(this.controls.target).normalize()
    const dist = Math.max(d.nodes[i].r * 14, d.radius * 0.45)
    this.moveTo(p.clone().add(dir.multiplyScalar(dist)), p)
  }

  dolly(factor: number) {
    const offset = this.camera.position.clone().sub(this.controls.target).multiplyScalar(factor)
    this.camera.position.copy(this.controls.target).add(offset)
    this.controls.update()
    this.request()
  }

  rotate(dTheta: number, dPhi: number) {
    const offset = this.camera.position.clone().sub(this.controls.target)
    const s = new Spherical().setFromVector3(offset)
    s.theta += dTheta
    s.phi = Math.min(Math.PI - 0.05, Math.max(0.05, s.phi + dPhi))
    offset.setFromSpherical(s)
    this.camera.position.copy(this.controls.target).add(offset)
    this.controls.update()
    this.request()
  }

  setAutoRotate(on: boolean) {
    this.controls.autoRotate = on && !this.reduced
    this.request()
  }

  /* ---------------- işaretçi */

  private pick(clientX: number, clientY: number): string | null {
    const d = this.data
    if (!d || !this.nodes) return null
    const r = this.renderer.domElement.getBoundingClientRect()
    this.pointer.set(((clientX - r.left) / r.width) * 2 - 1, -((clientY - r.top) / r.height) * 2 + 1)
    this.raycaster.setFromCamera(this.pointer, this.camera)
    const hit = this.raycaster.intersectObject(this.nodes, false)[0]
    return hit?.instanceId !== undefined ? (d.nodes[hit.instanceId]?.id ?? null) : null
  }

  private readonly onDown = (e: PointerEvent) => {
    this.down = { x: e.clientX, y: e.clientY }
  }
  private readonly onUp = (e: PointerEvent) => {
    const d = this.down
    this.down = null
    if (!d || Math.hypot(e.clientX - d.x, e.clientY - d.y) > 5) return
    const id = this.pick(e.clientX, e.clientY)
    if (id) this.cb.onPick(id)
  }
  private hoverRaf = 0
  private readonly onMove = (e: PointerEvent) => {
    if (this.down || this.hoverRaf) return
    const { clientX, clientY } = e
    this.hoverRaf = requestAnimationFrame(() => {
      this.hoverRaf = 0
      const id = this.pick(clientX, clientY)
      const r = this.renderer.domElement.getBoundingClientRect()
      this.cb.onHover(id ? { id, x: clientX - r.left, y: clientY - r.top } : null)
    })
  }
  private readonly onLeave = () => this.cb.onHover(null)

  /* ---------------- kare */

  private writePositions() {
    const cur = this.cur
    const m = this.nodes
    if (m) {
      for (let i = 0; i < m.count; i++) {
        const k = i * 4
        const r = Math.max(0.0001, cur[k + 3])
        tmpObj.position.set(cur[k], cur[k + 1], cur[k + 2])
        tmpObj.scale.set(r, r, r)
        tmpObj.quaternion.identity()
        tmpObj.updateMatrix()
        m.setMatrixAt(i, tmpObj.matrix)
      }
      m.instanceMatrix.needsUpdate = true
      m.computeBoundingSphere()
    }
    const seg = (a: number, b: number) => [cur[a * 4], cur[a * 4 + 1], cur[a * 4 + 2], cur[b * 4], cur[b * 4 + 1], cur[b * 4 + 2]]
    if (this.lines) {
      const attr = this.lines.geometry.getAttribute('position') as BufferAttribute
      this.links.forEach((l, i) => (attr.array as Float32Array).set(seg(l.a, l.b), i * 6))
      attr.needsUpdate = true
      this.lines.geometry.computeBoundingSphere()
    }
    if (this.tubes) {
      this.links.forEach((l, i) => {
        const ax = cur[l.a * 4], ay = cur[l.a * 4 + 1], az = cur[l.a * 4 + 2]
        const bx = cur[l.b * 4], by = cur[l.b * 4 + 1], bz = cur[l.b * 4 + 2]
        tmpV.set(bx - ax, by - ay, bz - az)
        const len = Math.max(0.001, tmpV.length())
        tmpQ.setFromUnitVectors(UP, tmpV.normalize())
        const w = 0.5 + 3 * (l.w / this.maxW)
        tmpMat.compose(new Vector3((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2), tmpQ, new Vector3(w, len, w))
        this.tubes!.setMatrixAt(i, tmpMat)
      })
      this.tubes.instanceMatrix.needsUpdate = true
      this.tubes.computeBoundingSphere()
    }
    if (this.extraLines) {
      const attr = this.extraLines.geometry.getAttribute('position') as BufferAttribute
      this.extra.forEach((l, i) => (attr.array as Float32Array).set(seg(l.a, l.b), i * 6))
      attr.needsUpdate = true
      this.extraLines.geometry.computeBoundingSphere()
      this.extraLines.computeLineDistances()
    }
  }

  /** Vurgu halkaları; true dönerse animasyon sürüyor. */
  private writeHalos(): boolean {
    const cur = this.cur
    let busy = false
    if (this.halos) {
      this.haloIds.forEach((id, i) => {
        const k = (this.index.get(id) ?? 0) * 4
        const r = cur[k + 3] * (id === this.style.selected ? 1.55 : 1.3) + 2
        tmpObj.position.set(cur[k], cur[k + 1], cur[k + 2])
        tmpObj.scale.set(r, r, r)
        tmpObj.updateMatrix()
        this.halos!.setMatrixAt(i, tmpObj.matrix)
      })
      this.halos.instanceMatrix.needsUpdate = true
      this.halos.computeBoundingSphere()
    }
    if (this.pulses) {
      const age = (performance.now() - this.style.pulseStart) / 1600
      const mat = this.pulses.material as MeshBasicMaterial
      // Azaltılmış harekette yanıp sönme/büyüme yok: sabit vurgu kısa süre kalır.
      mat.opacity = age >= 1 ? 0 : this.reduced ? 0.35 : 0.5 * (1 - age)
      this.pulses.visible = age < 1
      this.style.pulse.forEach((id, i) => {
        const k = (this.index.get(id) ?? 0) * 4
        const r = cur[k + 3] * (this.reduced ? 1.6 : 1.4 + age * 0.8) + 3
        tmpObj.position.set(cur[k], cur[k + 1], cur[k + 2])
        tmpObj.scale.set(r, r, r)
        tmpObj.updateMatrix()
        this.pulses!.setMatrixAt(i, tmpObj.matrix)
      })
      this.pulses.instanceMatrix.needsUpdate = true
      this.pulses.computeBoundingSphere()
      busy = age < 1
    }
    return busy
  }

  private readonly frame = () => {
    this.raf = 0
    const d = this.data
    let busy = false

    const a = this.anim
    if (a) {
      const t = Math.min(1, (performance.now() - a.start) / a.dur)
      const e = t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) ** 2 / 2
      this.camera.position.lerpVectors(a.p0, a.p1, e)
      this.controls.target.lerpVectors(a.t0, a.t1, e)
      if (t >= 1) this.anim = null
      busy = true
    }
    // OrbitControls.update: sönümleme/otomatik dönme sürüyorsa true.
    if (this.controls.update()) busy = true
    if (this.controls.autoRotate) busy = true

    if (d && !this.settled) {
      let moving = false
      const cur = this.cur
      d.nodes.forEach((n, i) => {
        const k = i * 4
        const dx = n.x - cur[k]
        const dy = n.y - cur[k + 1]
        const dz = n.z - cur[k + 2]
        const dr = n.r - cur[k + 3]
        if (Math.abs(dx) + Math.abs(dy) + Math.abs(dz) + Math.abs(dr) > 0.05) {
          cur[k] += dx * 0.14
          cur[k + 1] += dy * 0.14
          cur[k + 2] += dz * 0.14
          cur[k + 3] += dr * 0.14
          moving = true
        } else cur.set([n.x, n.y, n.z, n.r], k)
      })
      this.writePositions()
      if (!moving) this.settled = true
      busy = true
    }
    if (this.writeHalos()) busy = true

    this.renderer.render(this.scene, this.camera)

    // HTML etiketleri düğümün ekran konumuna; kamera arkasındakiler gizli.
    for (const [id, el] of this.labelEls) {
      const i = this.index.get(id)
      if (i === undefined) {
        el.style.visibility = 'hidden'
        continue
      }
      const k = i * 4
      tmpV.set(this.cur[k], this.cur[k + 1] + this.cur[k + 3] + 4, this.cur[k + 2]).project(this.camera)
      if (tmpV.z > 1 || tmpV.z < -1 || Math.abs(tmpV.x) > 1.1 || Math.abs(tmpV.y) > 1.1) {
        el.style.visibility = 'hidden'
        continue
      }
      el.style.visibility = 'visible'
      el.style.transform = `translate(-50%, -100%) translate(${((tmpV.x + 1) / 2) * this.width}px, ${((1 - tmpV.y) / 2) * this.height}px)`
    }

    if (busy) this.request()
  }
}

/* ------------------------------------------------------------------ React kabı */

export default function Scene3D(props: Scene3DProps) {
  const host = useRef<HTMLDivElement>(null)
  const engine = useRef<Engine | null>(null)
  const labelEls = useRef(new Map<string, HTMLElement>())
  const [hover, setHover] = useState<{ id: string; x: number; y: number } | null>(null)
  const [lost, setLost] = useState(false)
  const [themeSeq, setThemeSeq] = useState(0)
  const pickRef = useRef(props.onPick)
  pickRef.current = props.onPick
  if (lost) throw new Error('WebGL bağlamı kayboldu')

  // Motor bir kez kurulur, sökülürken tüm kaynaklar bırakılır.
  useEffect(() => {
    const el = host.current
    if (!el) return
    const e = new Engine(
      el,
      {
        onPick: (id) => pickRef.current(id),
        onHover: (h) => setHover((prev) => (prev?.id === h?.id ? prev : h)),
        onLost: () => setLost(true),
        onInteract: () => {},
      },
      prefersReducedMotion(),
      labelEls.current,
    )
    engine.current = e
    const mo = new MutationObserver(() => setThemeSeq((n) => n + 1))
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
    return () => {
      mo.disconnect()
      e.dispose()
      engine.current = null
    }
  }, [])

  const extraKey = (props.extraLinks ?? []).map((l) => `${l.source}>${l.target}`).join(',')
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const extra = useMemo(() => props.extraLinks ?? [], [extraKey])
  useEffect(() => {
    engine.current?.setData(props.data, props.colorOf, props.linkStyle ?? 'line', extra)
    // Renk işlevi ayrı effect'te; veri değişmeden renk değişince yeniden kurulmaz.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.data, props.linkStyle, extra])
  useEffect(() => engine.current?.setColorOf(props.colorOf), [props.colorOf])
  useEffect(() => {
    if (themeSeq) engine.current?.refreshTheme()
  }, [themeSeq])

  const lastPulse = useRef(props.pulseSeq)
  const pulseKey = [...(props.pulse ?? [])].join(',')
  useEffect(() => {
    const changed = lastPulse.current !== props.pulseSeq
    lastPulse.current = props.pulseSeq
    engine.current?.setStyle(props.selected, props.highlight, [...(props.pulse ?? [])], changed)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.selected, props.highlight, pulseKey, props.pulseSeq, props.data])

  useEffect(() => engine.current?.setAutoRotate(Boolean(props.autoRotate)), [props.autoRotate])
  useEffect(() => {
    if (props.fitRequest) engine.current?.fit()
  }, [props.fitRequest])
  useEffect(() => {
    if (props.centerRequest) engine.current?.centerOn(props.centerRequest.id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.centerRequest?.seq])

  const lastZoom = useRef(props.zoom ?? 1)
  useEffect(() => {
    const z = props.zoom ?? 1
    const prev = lastZoom.current
    lastZoom.current = z
    if (Math.abs(z - prev) > 0.001) engine.current?.dolly(prev / z)
  }, [props.zoom])

  // Etiket öğeleri her çizimde yeniden kaydedilir; yeni etiket ilk karede konumlansın.
  useEffect(() => engine.current?.request(), [props.labelIds])

  const byId = useMemo(() => new Map(props.data.nodes.map((n) => [n.id, n])), [props.data])
  const hovered = hover ? byId.get(hover.id) : undefined
  const hoverLabel = hovered ? props.labelOf(hovered) : null

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const en = engine.current
    if (!en || e.target !== e.currentTarget) return
    const step = 0.18
    const map: Record<string, () => void> = {
      ArrowLeft: () => en.rotate(-step, 0),
      ArrowRight: () => en.rotate(step, 0),
      ArrowUp: () => en.rotate(0, -step),
      ArrowDown: () => en.rotate(0, step),
      '+': () => en.dolly(0.85),
      '=': () => en.dolly(0.85),
      '-': () => en.dolly(1.18),
      '0': () => en.fit(),
    }
    const fn = map[e.key]
    if (!fn) return
    e.preventDefault()
    en.setAutoRotate(false)
    fn()
  }

  return (
    <div
      ref={host}
      className={cn('relative touch-none overflow-hidden outline-none select-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset', props.className)}
      style={{ cursor: hover ? 'pointer' : 'grab' }}
      tabIndex={0}
      role="application"
      aria-roledescription={'3B'}
      aria-label={props.ariaLabel}
      onKeyDown={onKeyDown}
    >
      {/* Etiketler (HTML): okunur yazı, yazı tipi dosyası gerekmez. Erişilebilir karşılık yanındaki listede. */}
      <div className="pointer-events-none absolute inset-0" aria-hidden>
        {props.labelIds.map((id) => {
          const n = byId.get(id)
          if (!n) return null
          const sel = id === props.selected
          return (
            <div
              key={id}
              ref={(el) => {
                if (el) labelEls.current.set(id, el)
                else labelEls.current.delete(id)
              }}
              className={cn(
                'absolute top-0 left-0 max-w-44 truncate rounded px-1.5 py-0.5 text-[11px] leading-tight whitespace-nowrap shadow-sm',
                sel
                  ? 'bg-primary font-semibold text-primary-foreground'
                  : props.highlight.has(id)
                    ? 'bg-card font-semibold text-foreground ring-1 ring-primary/60'
                    : 'bg-card/85 text-foreground',
              )}
              style={{ visibility: 'hidden' }}
            >
              {props.labelOf(n).title}
            </div>
          )
        })}
        {hover && hoverLabel && (
          <div
            className="absolute z-10 max-w-64 rounded-md border border-border bg-popover px-2 py-1 text-[12px] text-popover-foreground shadow-md"
            style={{ left: hover.x + 14, top: hover.y + 14 }}
          >
            <p className="font-medium">{hoverLabel.title}</p>
            {hoverLabel.sub && <p className="text-muted-foreground">{hoverLabel.sub}</p>}
          </div>
        )}
      </div>
    </div>
  )
}
