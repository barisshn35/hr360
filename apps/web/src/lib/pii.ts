/**
 * Serbest metinde kişisel / özel nitelikli veri uyarısı (KVKK veri en aza indirme).
 *
 * ml-inference `pii.py` ile aynı kurallar, tamamen istemcide: metin tarayıcıdan çıkmaz (anonim
 * etik bildirim formunda da güvenle kullanılır). Anahtar sözcükler `piiRules.json`'dadır; bu
 * dosya `apps/ml-inference/pii_rules.json`'ın kopyasıdır (pii.test.ts eşitliği denetler).
 * Yalnızca uyarır: kayıt engellenmez, otomatik karar verilmez.
 */
import rules from './piiRules.json'

export type PiiCategory =
  | 'tckn' | 'iban' | 'phone' | 'email'
  | 'health' | 'criminal' | 'religion' | 'ethnicity' | 'politics' | 'union' | 'sexual' | 'biometric'

export interface PiiFinding {
  category: PiiCategory
  start: number
  end: number
  special: boolean
}

interface RuleCategory {
  key: string
  label: string
  special: boolean
  kind: string
  stems?: string[]
  words?: string[]
}

export const piiRules = rules as { version: number; exclusions?: string[]; categories: RuleCategory[] }

/** Kategori adları (Türkçe kaynak; ekranda tx() ile çevrilir). */
export const PII_LABELS: Record<PiiCategory, string> = {
  tckn: 'TCKN',
  iban: 'IBAN',
  phone: 'Telefon',
  email: 'E-posta',
  health: 'Sağlık',
  criminal: 'Ceza mahkûmiyeti / güvenlik tedbiri',
  religion: 'Din / mezhep / inanç',
  ethnicity: 'Irk / etnik köken',
  politics: 'Siyasi düşünce',
  union: 'Sendika / dernek üyeliği',
  sexual: 'Cinsel hayat',
  biometric: 'Biyometrik / genetik veri',
}

const FOLD: Record<string, string> = {
  İ: 'i', I: 'i', ı: 'i', Ş: 's', ş: 's', Ğ: 'g', ğ: 'g', Ü: 'u', ü: 'u',
  Ö: 'o', ö: 'o', Ç: 'c', ç: 'c', Â: 'a', â: 'a', Î: 'i', î: 'i', Û: 'u', û: 'u',
}

/** Harf harf küçük harf + ASCII'ye indirgeme; uzunluk korunur (konumlar özgün metinle aynı). */
export function fold(text: string): string {
  let out = ''
  for (const ch of text) {
    const m = FOLD[ch]
    if (m !== undefined) out += m
    else {
      const low = ch.toLowerCase()
      out += low.length === ch.length ? low : ch
    }
  }
  return out
}

/** T.C. kimlik no: 11 hane, ilki 0 değil; 10. ve 11. hane sağlama kuralları. */
export function tcknValid(s: string): boolean {
  if (!/^[1-9]\d{10}$/.test(s)) return false
  const d = [...s].map(Number)
  const odd = d[0] + d[2] + d[4] + d[6] + d[8]
  const even = d[1] + d[3] + d[5] + d[7]
  const sum10 = d.slice(0, 10).reduce((a, b) => a + b, 0)
  return (((odd * 7 - even) % 10) + 10) % 10 === d[9] && sum10 % 10 === d[10]
}

/** ISO 13616 mod-97 (boşluklar yok sayılır); TR IBAN'ı 26 karakter olmalı. */
export function ibanValid(raw: string): boolean {
  const s = raw.replace(/\s+/g, '').toUpperCase()
  if (!/^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$/.test(s)) return false
  if (s.startsWith('TR') && s.length !== 26) return false
  let rem = 0
  for (const ch of s.slice(4) + s.slice(0, 4)) {
    const digits = /[A-Z]/.test(ch) ? String(ch.charCodeAt(0) - 55) : ch
    for (const c of digits) rem = (rem * 10 + Number(c)) % 97
  }
  return rem === 1
}

const TCKN = /(?<!\d)[1-9]\d{10}(?!\d)/g
const IBAN_TR = /(?<![0-9A-Za-z])TR\d{2}(?: ?[0-9A-Z]){22}(?![0-9A-Za-z])/gi
const IBAN_ANY = /(?<![0-9A-Za-z])[A-Z]{2}\d{2}[A-Z0-9]{11,30}(?![0-9A-Za-z])/g
const EMAIL = /(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g
const PHONE_MOBILE = /(?<![\d+])(?:\+?90[\s.-]?|0[\s.-]?)?\(?5\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}(?!\d)/g
const PHONE_LAND = /(?<![\d+])(?:\+?90[\s.-]?|0[\s.-]?)\(?[2-4]\d{2}\)?[\s.-]?\d{3}[\s.-]?\d{2}[\s.-]?\d{2}(?!\d)/g
const WORD = /[\p{L}\p{N}]+/gu

type Entry = { stems: string[]; exact: boolean }
let compiled: Array<{ key: PiiCategory; entries: Entry[] }> | null = null

function keywordRules() {
  compiled ??= piiRules.categories
    .filter((c) => c.kind === 'keyword')
    .map((c) => ({
      key: c.key as PiiCategory,
      entries: [
        ...(c.stems ?? []).map((s) => ({ stems: fold(s).split(/\s+/), exact: false })),
        ...(c.words ?? []).map((s) => ({ stems: fold(s).split(/\s+/), exact: true })),
      ],
    }))
  return compiled
}

let exclusions: string[][] | null = null
function exclusionRules() {
  exclusions ??= (piiRules.exclusions ?? []).map((e) => fold(e).split(/\s+/))
  return exclusions
}

const SPECIAL = new Set(piiRules.categories.filter((c) => c.special).map((c) => c.key))

/** Bulguları konum sırasıyla döner; çakışan bulgularda önce denetlenen kategori kazanır. */
export function scanPii(text: string): PiiFinding[] {
  if (!text) return []
  const found: PiiFinding[] = []
  const add = (category: PiiCategory, start: number, end: number) => {
    if (found.some((f) => start < f.end && f.start < end)) return
    found.push({ category, start, end, special: SPECIAL.has(category) })
  }
  for (const m of text.matchAll(TCKN)) if (tcknValid(m[0])) add('tckn', m.index, m.index + m[0].length)
  for (const rx of [IBAN_TR, IBAN_ANY]) for (const m of text.matchAll(rx)) if (ibanValid(m[0])) add('iban', m.index, m.index + m[0].length)
  for (const m of text.matchAll(EMAIL)) add('email', m.index, m.index + m[0].length)
  for (const rx of [PHONE_MOBILE, PHONE_LAND]) for (const m of text.matchAll(rx)) add('phone', m.index, m.index + m[0].length)

  // UTF-16 konumları: fold() her karakteri tek kod birimine eşler (Türkçe harfler BMP'de).
  const folded = fold(text)
  const words = [...folded.matchAll(WORD)].map((m) => ({ w: m[0], start: m.index, end: m.index + m[0].length }))
  const kw = keywordRules()
  // İstisna kalıpları (ör. "dini bayram"): kapsadıkları sözcükler anahtar sözcük denetimine girmez.
  const excluded = new Set<number>()
  for (const stems of exclusionRules()) {
    for (let i = 0; i + stems.length <= words.length; i++) {
      if (stems.every((st, k) => words[i + k].w.startsWith(st))) for (let k = 0; k < stems.length; k++) excluded.add(i + k)
    }
  }
  for (let i = 0; i < words.length; i++) {
    if (excluded.has(i)) continue
    for (const { key, entries } of kw) {
      let hit = 0
      for (const { stems, exact } of entries) {
        const n = stems.length
        if (i + n > words.length) continue
        let ok = true
        for (let k = 0; k < n && ok; k++) ok = exact ? words[i + k].w === stems[k] : words[i + k].w.startsWith(stems[k])
        if (ok && n > hit) hit = n
      }
      if (hit) {
        add(key, words[i].start, words[i + hit - 1].end)
        break
      }
    }
  }
  return found.sort((a, b) => a.start - b.start)
}

/** Bulunan kategoriler (tekrarsız, ilk görülme sırasıyla). */
export function piiCategories(findings: PiiFinding[]): PiiCategory[] {
  return [...new Set(findings.map((f) => f.category))]
}
