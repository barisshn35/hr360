#!/usr/bin/env node
/**
 * Tema renk kontrastı denetimi (G23, WCAG 2.1 AA 1.4.3 / 1.4.11).
 *
 * src/styles/index.css içindeki :root (açık) ve .dark (koyu) değişkenlerini okur, metin/zemin
 * çiftlerinin kontrast oranını hesaplar. Gövde metni ≥ 4,5:1; büyük metin ve arayüz bileşeni ≥ 3:1.
 * Yarı saydam rozet zeminleri (ör. bg-success/12) kart zemini üzerine karıştırılarak hesaplanır.
 *
 *   node scripts/contrast-check.mjs              # güncel dosya
 *   node scripts/contrast-check.mjs eski.css     # başka bir dosya (ör. önceki sürüm)
 * Eşiğin altında çift varsa çıkış kodu 1.
 */
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
const file = process.argv[2] ?? path.join(here, '..', 'src', 'styles', 'index.css')
const css = readFileSync(file, 'utf8')

function block(selector) {
  const re = new RegExp(`(^|\\n)${selector.replace('.', '\\.')}\\s*\\{([\\s\\S]*?)\\n\\}`)
  const m = css.match(re)
  if (!m) throw new Error(`${selector} bloğu bulunamadı`)
  const vars = {}
  for (const [, k, v] of m[2].matchAll(/--([\w-]+):\s*([^;]+);/g)) {
    // var(--tenant-primary, 160 86% 26%) → varsayılan değer (kiracı rengi yokken)
    const fb = v.match(/var\(--[\w-]+,\s*([^)]+)\)/)
    vars[k] = (fb ? fb[1] : v).trim()
  }
  return vars
}

function hslToRgb(hsl) {
  const m = hsl.match(/([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/)
  if (!m) return null
  const h = +m[1] / 360, s = +m[2] / 100, l = +m[3] / 100
  const f = (n) => {
    const k = (n + h * 12) % 12
    return l - s * Math.min(l, 1 - l) * Math.max(-1, Math.min(k - 3, 9 - k, 1))
  }
  return [f(0), f(8), f(4)].map((x) => x * 255)
}

const lum = (rgb) => {
  const [r, g, b] = rgb.map((v) => {
    const c = v / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}
const ratio = (a, b) => {
  const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p)
  return (x + 0.05) / (y + 0.05)
}
const mix = (fg, bg, alpha) => fg.map((v, i) => v * alpha + bg[i] * (1 - alpha))

// [ad, metin, zemin, (zemin = metin rengi %alpha'sı kart üzerinde), eşik]
const PAIRS = [
  ['Gövde metni / zemin', 'foreground', 'background', null, 4.5],
  ['Soluk metin / zemin', 'muted-foreground', 'background', null, 4.5],
  ['Soluk metin / kart', 'muted-foreground', 'card', null, 4.5],
  ['Soluk metin / muted (nötr rozet)', 'muted-foreground', 'muted', null, 4.5],
  ['Soluk metin / accent (üzerine gelme)', 'muted-foreground', 'accent', null, 4.5],
  ['Birincil (bağlantı) / zemin', 'primary', 'background', null, 4.5],
  ['Birincil (bağlantı) / kart', 'primary', 'card', null, 4.5],
  ['Birincil düğme yazısı', 'primary-foreground', 'primary', null, 4.5],
  // Düğme iki temada da tam `bg-destructive` kullanır (bkz. components/ui/button.tsx).
  ['Tehlike düğme yazısı', 'destructive-foreground', 'destructive', null, 4.5],
  ['Tehlike metni / kart', 'destructive', 'card', null, 4.5],
  ['Başarı metni / kart', 'success', 'card', null, 4.5],
  ['Uyarı metni / kart', 'warning', 'card', null, 4.5],
  ['Bilgi metni / kart', 'info', 'card', null, 4.5],
  ['Rozet: başarı (bg %12)', 'success', 'card', 0.12, 4.5],
  ['Rozet: uyarı (bg %12)', 'warning', 'card', 0.12, 4.5],
  ['Rozet: tehlike (bg %10)', 'destructive', 'card', 0.1, 4.5],
  ['Rozet: bilgi/birincil (bg %10)', 'primary', 'card', 0.1, 4.5],
  ['Odak halkası / zemin (1.4.11)', 'ring', 'background', null, 3],
]

let failures = 0
for (const [theme, sel] of [['Açık tema', ':root'], ['Koyu tema', '.dark']]) {
  const light = block(':root')
  const v = sel === ':root' ? light : { ...light, ...block(sel) }
  console.log(`\n${theme} (${path.basename(file)})`)
  for (const [name, fgKey, bgKey, alpha, min, opts] of PAIRS) {
    const fg = hslToRgb(v[fgKey] ?? '')
    let bg = hslToRgb(v[bgKey] ?? '')
    const special = opts?.[sel === ':root' ? 'light' : 'dark']
    if (special && bg) bg = mix(bg, hslToRgb(v[special.over]), special.bgAlpha)
    if (!fg || !bg) {
      console.log(`  ?    ${name}: değişken çözülemedi (${fgKey}=${v[fgKey]}, ${bgKey}=${v[bgKey]})`)
      continue
    }
    if (alpha != null) bg = mix(fg, bg, alpha)
    const r = ratio(fg, bg)
    const ok = r >= min
    if (!ok) failures++
    console.log(`  ${ok ? 'OK ' : 'ALT'}  ${r.toFixed(2).padStart(5)}:1  (≥ ${min})  ${name}`)
  }
}
console.log(failures ? `\n${failures} çift eşiğin altında.` : '\nTüm çiftler eşiği sağlıyor.')
process.exit(failures ? 1 : 0)
