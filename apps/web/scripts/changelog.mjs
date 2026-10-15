#!/usr/bin/env node
// "Yenilikler" paneli verisini docs/CHANGELOG-tr.md'den üretir (dalga 12).
//
//   node scripts/changelog.mjs           src/features/whats-new/changelog.gen.ts dosyasını yazar
//   node scripts/changelog.mjs --check   dosya güncel değilse çıkış 1 (CI / birim testi)
//
// Metinler tx() içinde üretilir: i18n-check anahtarları görür, İngilizcesi en.json'a yazılır.
// Web imajı yalnızca apps/web ile derlendiği için üretilen dosya depoya eklenir.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
export const MD = path.resolve(ROOT, '..', '..', 'docs', 'CHANGELOG-tr.md')
export const OUT = path.join(ROOT, 'src', 'features', 'whats-new', 'changelog.gen.ts')

/** Markdown → [{ id, date, title, items }] (en yeni başta, dosyadaki sırayla). */
export function parseChangelog(md) {
  const entries = []
  let cur = null
  let fence = false
  for (const raw of md.split(/\r?\n/)) {
    const line = raw.trimEnd()
    if (line.startsWith('```')) { fence = !fence; continue }
    if (fence) continue
    const h = /^##\s+(\d+)\s+·\s+(\d{4}-\d{2}-\d{2})\s+·\s+(.+)$/.exec(line)
    if (h) {
      cur = { id: h[1], date: h[2], title: h[3].trim(), items: [] }
      entries.push(cur)
      continue
    }
    if (line.startsWith('## ')) throw new Error(`Biçimsiz başlık: ${line}`)
    const b = /^-\s+(.+)$/.exec(line)
    if (b && cur) cur.items.push(b[1].trim())
  }
  const ids = new Set()
  for (const e of entries) {
    if (ids.has(e.id)) throw new Error(`Yinelenen dalga: ${e.id}`)
    ids.add(e.id)
    if (!e.items.length) throw new Error(`Maddesiz dalga: ${e.id}`)
  }
  return entries
}

const lit = (s) => `'${s.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`

export function render(entries) {
  const out = [
    '// OTOMATİK ÜRETİLDİ — elle değiştirmeyin. Kaynak: docs/CHANGELOG-tr.md',
    '// Yeniden üretmek için: cd apps/web && node scripts/changelog.mjs',
    "import { tx } from '@/lib/i18n'",
    '',
    'export interface ChangelogEntry { id: string; date: string; title: string; items: string[] }',
    '',
    '/** En yeni dalga başta. Metinler arayüz dilinde döner. */',
    'export const changelog = (): ChangelogEntry[] => [',
  ]
  for (const e of entries) {
    out.push(`  { id: ${lit(e.id)}, date: ${lit(e.date)}, title: tx(${lit(e.title)}), items: [`)
    for (const i of e.items) out.push(`    tx(${lit(i)}),`)
    out.push('  ] },')
  }
  out.push(']', '')
  return out.join('\n')
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const next = render(parseChangelog(fs.readFileSync(MD, 'utf8')))
  if (process.argv.includes('--check')) {
    const cur = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : ''
    if (cur !== next) {
      console.error('changelog.gen.ts güncel değil: node scripts/changelog.mjs')
      process.exit(1)
    }
    console.log('changelog.gen.ts güncel')
  } else {
    fs.mkdirSync(path.dirname(OUT), { recursive: true })
    fs.writeFileSync(OUT, next)
    console.log(`Yazıldı: ${path.relative(ROOT, OUT)}`)
  }
}
