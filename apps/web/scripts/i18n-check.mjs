#!/usr/bin/env node
// Arayüz çevirilerini denetler.
//
//   node scripts/i18n-check.mjs            Eksik/fazla anahtarları raporlar (eksik varsa çıkış 1)
//   node scripts/i18n-check.mjs --write    Eksik anahtarları en.json'a boş değerle ekler, fazlaları siler
//   node scripts/i18n-check.mjs --missing  Eksik anahtarları JSON olarak basar (çevirmen için)
//
// Kodda tx('Türkçe metin') ve tx('… {0} …', [x]) çağrılarının ilk argümanı anahtardır.
import fs from 'node:fs'
import path from 'node:path'
// TypeScript 7 (yerel derleyici) JS API'si sunmuyor; ayrıştırma için 6.x kullanılır.
import ts from 'typescript-api'

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..')
const SRC = path.join(ROOT, 'src')
const EN = path.join(SRC, 'locales', 'en.json')

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name)
    if (e.isDirectory()) {
      if (e.name === 'mocks' || e.name === 'locales') continue
      walk(p, out)
    } else if (/\.(ts|tsx)$/.test(e.name) && !/\.test\./.test(e.name) && !e.name.endsWith('.d.ts')) out.push(p)
  }
  return out
}

const keys = new Map()
const dynamic = []
for (const file of walk(SRC)) {
  const text = fs.readFileSync(file, 'utf8')
  if (!text.includes('tx(')) continue
  const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, file.endsWith('x') ? ts.ScriptKind.TSX : ts.ScriptKind.TS)
  const visit = (n) => {
    if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && n.expression.text === 'tx') {
      const a = n.arguments[0]
      const line = sf.getLineAndCharacterOfPosition(n.getStart()).line + 1
      const ref = `${path.relative(ROOT, file)}:${line}`
      if (a && (ts.isStringLiteral(a) || ts.isNoSubstitutionTemplateLiteral(a))) {
        if (!keys.has(a.text)) keys.set(a.text, ref)
      } else dynamic.push(ref)
    }
    ts.forEachChild(n, visit)
  }
  visit(sf)
}

const en = JSON.parse(fs.readFileSync(EN, 'utf8'))
const missing = [...keys.keys()].filter((k) => !(k in en) || en[k] === '')
const extra = Object.keys(en).filter((k) => !keys.has(k) && !k.startsWith('@server:'))

if (process.argv.includes('--missing')) {
  process.stdout.write(JSON.stringify(Object.fromEntries(missing.map((k) => [k, keys.get(k)])), null, 1) + '\n')
  process.exit(0)
}
if (process.argv.includes('--write')) {
  const next = {}
  for (const k of [...keys.keys()].sort((a, b) => a.localeCompare(b, 'tr'))) next[k] = en[k] ?? ''
  // Sunucu iletileri (txServer) koddaki tx() çağrılarında yoktur; korunur.
  for (const [k, v] of Object.entries(en)) if (!(k in next) && k.startsWith('@server:')) next[k] = v
  fs.writeFileSync(EN, JSON.stringify(next, null, 1) + '\n')
}

// Yer tutucu tutarlılığı: {0}, {1}… ve {{Alan}} çeviride de olmalı.
const bad = []
for (const [k, v] of Object.entries(en)) {
  if (!v || !keys.has(k)) continue
  const ph = (s) => (s.match(/\{\d+\}|\{\{\w+\}\}/g) ?? []).sort().join(',')
  const vv = v.includes('|') ? v.split('|')[1] : v
  if (ph(k) !== ph(vv)) bad.push(`${keys.get(k)}  "${k}"  →  "${v}"`)
}

console.log(`Anahtar: ${keys.size}, çevrilmiş: ${keys.size - missing.length}, eksik: ${missing.length}, kullanılmayan: ${extra.length}, yer tutucu uyumsuz: ${bad.length}`)
if (dynamic.length) console.log(`Değişkenle çağrılan tx (sözlükte olmayabilir): ${dynamic.length}`)
for (const b of bad.slice(0, 20)) console.log('  yer tutucu:', b)
for (const m of missing.slice(0, 20)) console.log('  eksik:', JSON.stringify(m), keys.get(m))
process.exit(missing.length || bad.length ? 1 : 0)
