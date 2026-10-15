/**
 * Arayüz dili (Türkçe / İngilizce).
 *
 * Kaynak metinler kodda Türkçe yazılır ve `tx('…')` ile sarılır; Türkçe metnin
 * kendisi anahtardır. İngilizce seçiliyse `src/locales/en.json` sözlüğünden
 * karşılığı döner, yoksa Türkçe metin gösterilir (eksik çeviri ekranı bozmaz).
 *
 * Dil uygulama açılmadan önce belirlenir (main.tsx sözlüğü yükleyip sonra
 * uygulamayı içe aktarır); bu yüzden modül düzeyindeki sabitler (menü, durum
 * etiketleri) de doğru dilde oluşur. Dil değişince sayfa yeniden yüklenir.
 *
 * Yer tutucular: `tx('{0} kayıt', [n])`. İngilizce karşılıkta tekil/çoğul
 * "|" ile ayrılabilir: "{0} record|{0} records" (ilk parametre 1 ise tekil).
 */

export type Lang = 'tr' | 'en'

const STORAGE_KEY = 'hr360.lang'

function detect(): Lang {
  try {
    const fromUrl = new URLSearchParams(window.location.search).get('lang')
    if (fromUrl === 'en' || fromUrl === 'tr') {
      localStorage.setItem(STORAGE_KEY, fromUrl)
      return fromUrl
    }
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored === 'en' || stored === 'tr') return stored
  } catch {
    /* gizli sekme vb. */
  }
  return 'tr'
}

export let lang: Lang = typeof window === 'undefined' ? 'tr' : detect()

/** Intl/toLocale* çağrıları için. */
export const appLocale = lang === 'en' ? 'en-GB' : 'tr-TR'

let dict: Record<string, string> = {}

/** main.tsx tarafından, uygulama modülleri yüklenmeden önce çağrılır. */
export async function loadLocale(): Promise<void> {
  if (lang === 'en') dict = (await import('@/locales/en.json')).default as Record<string, string>
  if (typeof document !== 'undefined') document.documentElement.lang = lang
}

/** Testler için. */
export function setDictionary(next: Record<string, string>, nextLang: Lang = lang): void {
  dict = next
  lang = nextLang
  patterns = null
}

function fill(text: string, params?: ReadonlyArray<unknown>): string {
  if (!params || params.length === 0) return text
  return text.replace(/\{(\d+)\}/g, (_m, i: string) => {
    const v = params[Number(i)]
    return v === undefined || v === null ? '' : String(v)
  })
}

/** Yüzde biçimi: Türkçede "%40", İngilizcede "40%". */
export function pct(value: unknown): string {
  return lang === 'en' ? `${String(value)}%` : `%${String(value)}`
}

function plural(text: string, params?: ReadonlyArray<unknown>): string {
  if (!text.includes('|')) return text
  const [one, many] = text.split('|')
  return Number(params?.[0]) === 1 ? one : many
}

/** Türkçe kaynak metni seçili dile çevirir. */
export function tx(source: string, params?: ReadonlyArray<unknown>): string {
  let text = source
  if (lang === 'en') {
    const hit = dict[source] ?? dict[source.trim()]
    if (hit !== undefined) text = plural(hit, params)
  }
  return fill(text, params)
}

type Pattern = { re: RegExp; order: number[]; out: string; literal: number }
let patterns: Pattern[] | null = null

/** Sunucu iletilerindeki "{0}" yerleri için düzenli ifadeler (ilk kullanımda derlenir). */
function serverPatterns(): Pattern[] {
  if (patterns) return patterns
  patterns = []
  for (const [k, out] of Object.entries(dict)) {
    if (!k.startsWith('@server:') || !/\{\d+\}/.test(k)) continue
    const src = k.slice(8)
    const multiline = src.includes('\n')
    const order: number[] = []
    const body = src
      .split(/(\{\d+\})/)
      .map((part) => {
        const m = part.match(/^\{(\d+)\}$/)
        if (m) {
          order.push(Number(m[1]))
          // Yer tutucu satır sonu aşmaz (kalıp çok satırlıysa aşabilir): "{0} İnsan Kaynakları" gibi
          // kısa bir kalıp, çok satırlı bir mektubun tamamını yakalayıp çevirmesin.
          return multiline ? '([\\s\\S]+?)' : '([^\\n]+?)'
        }
        return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
      })
      .join('')
    // En az bir sabit sözcük içermeyen kalıplar ("{0}") her şeyi yakalar; atlanır.
    if (!/\p{L}{2,}/u.test(src.replace(/\{\d+\}/g, ''))) continue
    patterns.push({ re: new RegExp('^' + body + '$'), order, out, literal: src.replace(/\{\d+\}/g, '').length })
  }
  // Daha çok sabit metin içeren (daha belirgin) kalıplar önce denenir; aksi halde
  // kısa bir kalıp, içinde başka bir iletiyi taşıyan uzun iletiyi yanlış yakalar.
  patterns.sort((a, b) => b.literal - a.literal)
  return patterns
}

/**
 * Sunucudan gelen iletiyi çevirir (servisler Türkçe ileti döner). Önce birebir
 * karşılık, sonra "{0}" yer tutuculu kalıplar denenir; bulunamazsa ileti aynen kalır.
 */
export function txServer(message: string | null | undefined, depth = 0): string {
  if (message == null) return ''
  if (lang !== 'en' || !message) return message
  const exact = dict['@server:' + message] ?? dict[message] ?? dict['@server:' + message.trim()]
  if (exact !== undefined) return plural(exact, [])
  // Kalıplar yalnızca Türkçe harf ya da tanıdık bir sözcük içerebilecek iletilerde denenir.
  for (const p of serverPatterns()) {
    const m = p.re.exec(message)
    if (!m) continue
    const params: string[] = []
    // Yakalanan değerler de sunucu iletisi olabilir (ör. olay özetindeki talep konusu). Türkçe ondalık
    // sayı ("0,5") İngilizcede noktayla yazılır ("0.5-day ... request").
    p.order.forEach((idx, i) => {
      const raw = m[i + 1]
      params[idx] = /^\d+,\d+$/.test(raw) ? raw.replace(',', '.') : depth < 2 ? txServer(raw, depth + 1) : raw
    })
    return fill(plural(p.out, params), params)
  }
  return message
}

const TR_CHARS = /[çğıöşüÇĞİÖŞÜ]/

/**
 * Hiçbir zaman çevrilmeyen alanlar: imzalanan/imzalanacak belge gövdeleri (teklif mektubu, e-imza belgesi),
 * tutanak ve savunma metinleri, aday beyanı. Bunlar hukuki metindir; arayüz dili ne olursa olsun
 * sunucunun ürettiği (ve özeti alınan) metin birebir gösterilmelidir.
 */
export const NON_TRANSLATABLE_KEYS: ReadonlySet<string> = new Set([
  'letterText', 'html', 'invitationText', 'defenceText', 'minutesText', 'resumeText', 'coverNote',
  'documentText', 'signedText', 'privacyNotice',
])

/**
 * API yanıtındaki sunucu üretimi metinleri (bildirim konusu, ekip sağlığı uyarısı,
 * olay özeti, rozet adı…) İngilizce arayüzde çevirir. Yalnızca sözlükte birebir ya
 * da kalıp karşılığı olan dizeler değişir; ad, departman gibi veriler olduğu gibi kalır.
 */
export function translateServerData<T>(data: T): T {
  if (lang !== 'en') return data
  const walk = (v: unknown): unknown => {
    if (typeof v === 'string') {
      if (v.length < 2 || v.length > 600) return v
      const exact = dict['@server:' + v]
      if (exact !== undefined) return plural(exact, [])
      // Çok satırlı metinler (mektup, tutanak, belge gövdesi) yalnızca birebir karşılıkla çevrilir.
      if (v.includes('\n')) return v
      return TR_CHARS.test(v) ? txServer(v) : v
    }
    if (Array.isArray(v)) return v.map(walk)
    if (v && typeof v === 'object') {
      const out: Record<string, unknown> = {}
      for (const [k, x] of Object.entries(v)) out[k] = NON_TRANSLATABLE_KEYS.has(k) ? x : walk(x)
      return out
    }
    return v
  }
  return walk(data) as T
}

export function setLanguage(next: Lang): void {
  try {
    localStorage.setItem(STORAGE_KEY, next)
  } catch {
    /* yok say */
  }
  const url = new URL(window.location.href)
  url.searchParams.delete('lang')
  window.location.replace(url.toString())
}
