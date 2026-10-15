/**
 * Şemayı dışa aktarma: yerleşim çıktısından (orgLayouts) bağımsız bir SVG belgesi
 * üretir; PNG için tuvale çizer, PDF için tarayıcının yazdırma penceresini açar.
 * Dış servis ya da PDF kütüphanesi yok; her şey tarayıcıda.
 *
 * Tema renkleri CSS değişkenidir (`hsl(var(--chart-2))`) ve dosyanın dışında
 * çözülemez: dışa aktarmadan önce gerçek renge çevrilir. CSP: görsel `blob:` adresiyle
 * yüklenir (img-src 'self' data: blob:), satır içi betik yok.
 */

import type { OrgLayout } from './orgLayouts'
import { arcPath, linkPath, matrixPath, NODE_H, NODE_W, ROOT_ID } from './orgLayouts'

export interface ExportInput {
  layout: OrgLayout
  title: string
  /** Düğüm rengi (CSS; var() içerebilir). */
  colorOf: (id: string) => string
  /** Düğümün ikinci satırı (ör. "12 kişi"). */
  subtitleOf: (id: string) => string
  matrix: Array<{ from: string; to: string; kind: 'Functional' | 'Project' }>
  /** Köke giden vurgulu yol. */
  path: ReadonlySet<string>
}

const esc = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

const clip = (s: string, n: number) => (s.length > n ? `${s.slice(0, n - 1)}…` : s)

/** CSS renk ifadesini (var() dahil) hesaplanmış `rgb(...)` değerine çevirir. */
export function makeColorResolver(): { resolve: (css: string) => string; dispose: () => void } {
  const probe = document.createElement('span')
  probe.style.display = 'none'
  document.body.appendChild(probe)
  const cache = new Map<string, string>()
  return {
    resolve: (css) => {
      const hit = cache.get(css)
      if (hit) return hit
      probe.style.color = ''
      probe.style.color = css
      const out = getComputedStyle(probe).color || css
      cache.set(css, out)
      return out
    },
    dispose: () => probe.remove(),
  }
}

/** Yerleşimi tek başına açılabilen bir SVG belgesine çevirir. */
export function layoutToSvg(input: ExportInput): string {
  const { layout, title } = input
  const { resolve, dispose } = makeColorResolver()
  try {
    const fg = resolve('hsl(var(--foreground))')
    const muted = resolve('hsl(var(--muted-foreground))')
    const card = resolve('hsl(var(--card))')
    const border = resolve('hsl(var(--border))')
    const primary = resolve('hsl(var(--primary))')
    const fn = resolve('hsl(var(--chart-2))')
    const proj = resolve('hsl(var(--chart-5))')

    const pad = 40
    const head = 44
    const { x0, y0, x1, y1 } = layout.bounds
    // Başlık sığsın: dar ağaçta (ör. odakta iki düğüm) belge başlık kadar genişler.
    const w = Math.ceil(Math.max(x1 - x0 + pad * 2, title.length * 10 + 48))
    const h = Math.ceil(y1 - y0 + pad * 2 + head)
    const out: string[] = []
    out.push(
      `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="${x0 - pad} ${y0 - pad - head} ${w} ${h}" font-family="Geist, Inter, Arial, sans-serif">`,
      `<rect x="${x0 - pad}" y="${y0 - pad - head}" width="${w}" height="${h}" fill="${card}"/>`,
      `<text x="${x0 - pad + 20}" y="${y0 - pad - head + 30}" font-size="18" font-weight="600" fill="${fg}">${esc(title)}</text>`,
      `<defs>${[['m-fn', fn], ['m-proj', proj]].map(([id, c]) => `<marker id="${id}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0,0L10,5L0,10z" fill="${c}"/></marker>`).join('')}</defs>`,
    )

    const kind = layout.kind
    const nodeLink = kind === 'vertical' || kind === 'horizontal' || kind === 'radial'
    if (nodeLink || kind === 'list') {
      for (const l of layout.links) {
        const s = layout.byId.get(l.source)!
        const t = layout.byId.get(l.target)!
        const on = input.path.has(l.source) && input.path.has(l.target)
        const d =
          kind === 'list'
            ? `M${s.box.x + 8},${s.box.y + s.box.h}V${t.y}H${t.box.x}`
            : linkPath(kind, s, t)
        out.push(`<path d="${d}" fill="none" stroke="${on ? primary : border}" stroke-width="${on ? 2.5 : 1.5}"/>`)
      }
    }

    for (const n of layout.nodes) {
      const color = resolve(n.id === ROOT_ID ? 'hsl(var(--primary))' : input.colorOf(n.id))
      const on = input.path.has(n.id)
      const sub = esc(input.subtitleOf(n.id))
      const name = esc(clip(n.name, 26))
      if (kind === 'sunburst' && n.arc) {
        const { a0, a1, r0, r1 } = n.arc
        out.push(`<path d="${arcPath(a0, a1, r0, r1)}" fill="${color}" fill-opacity="${n.depth === 0 ? 0.15 : 0.85}" stroke="${on ? primary : card}" stroke-width="${on ? 3 : 1}"/>`)
        const span = (a1 - a0) * ((r0 + r1) / 2)
        if (n.depth === 0 || span > 46) {
          out.push(`<text x="${n.x}" y="${n.y + 4}" text-anchor="middle" font-size="11" fill="${fg}">${esc(clip(n.name, Math.max(4, Math.floor(span / 7))))}</text>`)
        }
      } else if (kind === 'treemap') {
        const b = n.box
        out.push(`<rect x="${b.x}" y="${b.y}" width="${b.w}" height="${b.h}" rx="4" fill="${color}" fill-opacity="${n.depth === 0 ? 0.08 : 0.28}" stroke="${on ? primary : color}" stroke-width="${on ? 3 : 1}"/>`)
        if (b.w > 44) out.push(`<text x="${b.x + 5}" y="${b.y + 14}" font-size="11" font-weight="600" fill="${fg}">${esc(clip(n.name, Math.floor(b.w / 7)))}</text>`)
      } else if (kind === 'radial') {
        out.push(`<circle cx="${n.x}" cy="${n.y}" r="${n.id === ROOT_ID ? 16 : 9}" fill="${color}" stroke="${on ? primary : card}" stroke-width="${on ? 3 : 2}"/>`)
        out.push(`<text x="${n.x}" y="${n.y + 24}" text-anchor="middle" font-size="11" fill="${fg}">${name}</text>`)
      } else if (kind === 'list') {
        const b = n.box
        out.push(`<rect x="${b.x}" y="${b.y}" width="${b.w}" height="${b.h}" rx="6" fill="${card}" stroke="${on ? primary : border}"/>`)
        out.push(`<rect x="${b.x}" y="${b.y}" width="4" height="${b.h}" rx="2" fill="${color}"/>`)
        out.push(`<text x="${b.x + 12}" y="${b.y + b.h / 2 + 4}" font-size="12" font-weight="600" fill="${fg}">${name}</text>`)
        out.push(`<text x="${b.x + b.w - 8}" y="${b.y + b.h / 2 + 4}" text-anchor="end" font-size="11" fill="${muted}">${sub}</text>`)
      } else {
        const x = n.x - NODE_W / 2
        const y = n.y - NODE_H / 2
        out.push(`<rect x="${x}" y="${y}" width="${NODE_W}" height="${NODE_H}" rx="10" fill="${card}" stroke="${on ? primary : border}" stroke-width="${on ? 2.5 : 1}"/>`)
        out.push(`<rect x="${x}" y="${y + 6}" width="4" height="${NODE_H - 12}" rx="2" fill="${color}"/>`)
        out.push(`<text x="${x + 14}" y="${y + 23}" font-size="13" font-weight="600" fill="${fg}">${name}</text>`)
        out.push(`<text x="${x + 14}" y="${y + 41}" font-size="11" fill="${muted}">${sub}</text>`)
      }
    }

    for (const m of input.matrix) {
      const d = matrixPath(layout, m.from, m.to)
      if (!d) continue
      out.push(`<path d="${d}" fill="none" stroke="${m.kind === 'Project' ? proj : fn}" stroke-width="2" stroke-dasharray="6 5" marker-end="url(#${m.kind === 'Project' ? 'm-proj' : 'm-fn'})"/>`)
    }
    out.push('</svg>')
    return out.join('')
  } finally {
    dispose()
  }
}

function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 2000)
}

export const svgBlob = (svg: string) => new Blob([svg], { type: 'image/svg+xml;charset=utf-8' })

export function downloadSvg(svg: string, fileBase: string) {
  download(svgBlob(svg), `${fileBase}.svg`)
}

function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.onload = () => resolve(img)
    img.onerror = () => reject(new Error('image'))
    img.src = url
  })
}

/** PNG: tuval boyutu tarayıcı sınırını (≈16k piksel) aşmasın diye ölçek düşürülür. */
export async function downloadPng(svg: string, fileBase: string): Promise<void> {
  const url = URL.createObjectURL(svgBlob(svg))
  try {
    const img = await loadImage(url)
    const w = img.naturalWidth || 1200
    const h = img.naturalHeight || 800
    const scale = Math.max(0.25, Math.min(2, 16000 / Math.max(w, h), Math.sqrt(120_000_000 / (w * h))))
    const canvas = document.createElement('canvas')
    canvas.width = Math.round(w * scale)
    canvas.height = Math.round(h * scale)
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('canvas')
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height)
    const blob = await new Promise<Blob | null>((r) => canvas.toBlob(r, 'image/png'))
    if (!blob) throw new Error('png')
    download(blob, `${fileBase}.png`)
  } finally {
    URL.revokeObjectURL(url)
  }
}

/**
 * PDF: SVG'yi yalnızca yazdırmada görünen bir katmana koyup tarayıcının yazdırma
 * penceresini açar ("PDF olarak kaydet"). Uygulama kabuğu yazdırma CSS'iyle gizlenir
 * (orgchart.css → body.org-printing).
 */
export async function printSvg(svg: string): Promise<void> {
  const url = URL.createObjectURL(svgBlob(svg))
  const root = document.createElement('div')
  root.className = 'org-print-root'
  const img = document.createElement('img')
  img.alt = ''
  root.appendChild(img)
  document.body.appendChild(root)
  try {
    img.src = url
    await img.decode().catch(() => undefined)
    document.body.classList.add('org-printing')
    await new Promise<void>((resolve) => {
      const done = () => {
        window.removeEventListener('afterprint', done)
        resolve()
      }
      window.addEventListener('afterprint', done)
      window.print()
      // Bazı tarayıcılar afterprint göndermez; print() eşzamanlıysa hemen döner.
      window.setTimeout(done, 1500)
    })
  } finally {
    document.body.classList.remove('org-printing')
    root.remove()
    URL.revokeObjectURL(url)
  }
}

/** Dosya adı için güvenli parça. */
export const fileSlug = (s: string) =>
  s
    .toLocaleLowerCase('tr-TR')
    .replace(/ı/g, 'i')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 48) || 'organizasyon'
