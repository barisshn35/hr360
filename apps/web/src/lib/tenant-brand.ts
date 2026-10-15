/**
 * Tenant'ın kendi ana rengini çalışma zamanında uygular.
 * shadcn/Tailwind CSS değişkenleri HSL bileşenleri olarak tutulur
 * ("160 86% 30%"), o yüzden hex önce HSL'e çevrilir. --primary-foreground
 * (metin rengi) parlaklığa (lightness) göre otomatik seçilir - tenant bunu
 * ayarlamaz, koyu bir renk seçilse de metin her zaman okunur kalır.
 */
function hexToHsl(hex: string): { h: number; s: number; l: number } {
  const r = parseInt(hex.slice(1, 3), 16) / 255
  const g = parseInt(hex.slice(3, 5), 16) / 255
  const b = parseInt(hex.slice(5, 7), 16) / 255

  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  const delta = max - min

  let h = 0
  let s = 0
  if (delta !== 0) {
    s = delta / (1 - Math.abs(2 * l - 1))
    switch (max) {
      case r:
        h = ((g - b) / delta) % 6
        break
      case g:
        h = (b - r) / delta + 2
        break
      default:
        h = (r - g) / delta + 4
    }
    h *= 60
    if (h < 0) h += 360
  }

  return { h: Math.round(h), s: Math.round(s * 100), l: Math.round(l * 100) }
}

const HEX_PATTERN = /^#[0-9a-fA-F]{6}$/

const VARS = [
  '--tenant-primary',
  '--tenant-primary-foreground',
  '--tenant-primary-dark',
  '--tenant-primary-dark-foreground',
] as const

function hslToRgb(h: number, s: number, l: number): [number, number, number] {
  const S = s / 100
  const L = l / 100
  const k = (n: number) => (n + h / 30) % 12
  const a = S * Math.min(L, 1 - L)
  const f = (n: number) => L - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)))
  return [f(0), f(8), f(4)]
}

/** WCAG göreli parlaklık (0-1). */
function luminance([r, g, b]: [number, number, number]) {
  const c = (v: number) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)
  return 0.2126 * c(r) + 0.7152 * c(g) + 0.0722 * c(b)
}

function contrast(a: number, b: number) {
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
}

// Zeminler: index.css --background (açık 99%, koyu 240 8% 3.5%). Metin, birincil rengin
// %10'luk tonlu zemini (bg-primary/10) üzerinde de durabildiği için hedefte küçük pay var.
const LIGHT_BG = luminance(hslToRgb(0, 0, 99))
const DARK_BG = luminance(hslToRgb(240, 8, 3.5))
const TARGET = 4.8
const WHITE = 1
const NEAR_BLACK = luminance(hslToRgb(160, 50, 4))

/**
 * Rengin tonunu ve doygunluğunu koruyup açıklığını, zemine karşı WCAG AA (4,5:1) metin
 * kontrastı sağlanana kadar kaydırır: koyu temada açar, açık temada koyulaştırır. Şirket
 * rengini tanır kalır ama "text-primary" metinleri her iki temada okunur.
 */
function readableLightness(h: number, s: number, l: number, bg: number, direction: 1 | -1) {
  let x = l
  while (x >= 0 && x <= 100 && contrast(luminance(hslToRgb(h, s, x)), bg) < TARGET) x += direction
  return Math.max(0, Math.min(100, x))
}

/** Birincil renkli düğme üzerindeki metin: beyaz ya da koyu, hangisi daha kontrastlıysa. */
function foregroundFor(h: number, s: number, l: number) {
  const lum = luminance(hslToRgb(h, s, l))
  return contrast(lum, WHITE) >= contrast(lum, NEAR_BLACK) ? '0 0% 100%' : '160 50% 4%'
}

/**
 * `primaryColorHex` verilmişse şirketin rengini uygular; null/undefined ya da
 * geçersiz formatsa önceki ayarı temizler (varsayılan zümrüde döner).
 *
 * index.css --primary'yi bu değişkenlerden okur. Açık ve koyu tema için ayrı
 * değer yazılır: koyu temada çok koyu bir marka rengi (ör. lacivert) obsidyen
 * zeminde kaybolacağı için parlaklık kontrast hedefine kadar açılır (açık temada
 * çok açık renkler koyulaştırılır); ton (hue) ve doygunluk aynı kalır.
 */
/** Marka renginden açık/koyu tema CSS değişkenlerini (HSL bileşenleri) üretir; DOM'a dokunmaz. */
export function brandPalette(primaryColorHex: string): Record<(typeof VARS)[number], string> {
  const { h, s, l } = hexToHsl(primaryColorHex)
  const lightL = readableLightness(h, s, l, LIGHT_BG, -1)
  const darkL = readableLightness(h, s, Math.max(l, 45), DARK_BG, 1)
  return {
    '--tenant-primary': `${h} ${s}% ${lightL}%`,
    '--tenant-primary-foreground': foregroundFor(h, s, lightL),
    '--tenant-primary-dark': `${h} ${s}% ${darkL}%`,
    '--tenant-primary-dark-foreground': foregroundFor(h, s, darkL),
  }
}

export function applyTenantBrandColor(primaryColorHex: string | null | undefined) {
  const root = document.documentElement

  if (!primaryColorHex || !HEX_PATTERN.test(primaryColorHex)) {
    for (const v of VARS) root.style.removeProperty(v)
    return
  }

  for (const [name, value] of Object.entries(brandPalette(primaryColorHex))) root.style.setProperty(name, value)
}
