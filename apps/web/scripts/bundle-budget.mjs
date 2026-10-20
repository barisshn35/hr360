// Paket boyutu bütçesi (CI "web" işi, vite build'den sonra): ilk açılışta yüklenen kod ve tek parça
// büyüklüğü sınırı aşarsa çıkış kodu 1. Ölçü gzip'li boyuttur (nginx dosyaları gzip'le gönderir).
//
//   node scripts/bundle-budget.mjs [--summary DOSYA]   (DOSYA: Markdown özet eklenir, ör. $GITHUB_STEP_SUMMARY)
//
// İlk açılış = giriş (index) + uygulama kabuğu (App) ve bunların statik içe aktardığı parçalar; sayfalar
// ve ağır kütüphaneler (Swagger, three, grafikler, İngilizce sözlük) gerektiğinde ayrı yüklenir.
import { appendFileSync, existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { gzipSync } from 'node:zlib'

const BUDGET = {
  entryKb: 100, // index + vendor (giriş ekranı)
  shellKb: 400, // giriş + uygulama kabuğu (ilk ekran)
  chunkKb: 450, // tek parça (gzip); Swagger arayüzü ~370
}

const dir = join(import.meta.dirname, '..', 'dist', 'assets')
if (!existsSync(dir)) { console.error('dist/assets yok: önce vite build'); process.exit(1) }
const files = readdirSync(dir).filter((f) => f.endsWith('.js'))
const gz = new Map(files.map((f) => [f, gzipSync(readFileSync(join(dir, f)), { level: 9 }).length]))

function closure(prefix) {
  const start = files.find((f) => f.startsWith(prefix + '-'))
  if (!start) { console.error(`parça bulunamadı: ${prefix}`); process.exit(1) }
  const seen = new Set()
  const stack = [start]
  while (stack.length) {
    const f = stack.pop()
    if (seen.has(f) || !gz.has(f)) continue
    seen.add(f)
    const src = readFileSync(join(dir, f), 'utf8')
    for (const m of src.matchAll(/(?:from|import)\s*"\.\/([^"]+\.js)"/g)) stack.push(m[1])
  }
  return seen
}

const kb = (set) => Math.round([...set].reduce((s, f) => s + gz.get(f), 0) / 1024)
const entry = closure('index')
const shell = new Set([...entry, ...closure('App')])
const biggest = [...gz].sort((a, b) => b[1] - a[1]).slice(0, 6)
const total = Math.round([...gz.values()].reduce((s, n) => s + n, 0) / 1024)

const rows = [
  ['Giriş (index + vendor)', kb(entry), BUDGET.entryKb],
  ['İlk ekran (giriş + uygulama kabuğu)', kb(shell), BUDGET.shellKb],
  [`En büyük parça (${biggest[0][0].replace(/-[^-]+\.js$/, '')})`, Math.round(biggest[0][1] / 1024), BUDGET.chunkKb],
]
const failed = rows.filter(([, v, max]) => v > max)
const md = [
  '### Arayüz paket boyutu (gzip)', '',
  '| Ölçü | Boyut | Bütçe |', '|---|---|---|',
  ...rows.map(([n, v, max]) => `| ${n} | ${v} KB | ${max} KB${v > max ? ' — **AŞILDI**' : ''} |`),
  `| Tüm JS (${files.length} parça, sayfalar gerektiğinde yüklenir) | ${total} KB | — |`, '',
  'En büyük parçalar: ' + biggest.map(([f, n]) => `${f.replace(/-[^-]+\.js$/, '')} ${Math.round(n / 1024)} KB`).join(', '),
].join('\n')
console.log(md)
const i = process.argv.indexOf('--summary')
if (i > 0 && process.argv[i + 1]) appendFileSync(process.argv[i + 1], md + '\n')
if (failed.length) {
  console.error(`\nPaket bütçesi aşıldı: ${failed.map(([n]) => n).join(', ')}. Yeni ağır kütüphaneyi sayfaya özel (lazy) yükleyin.`)
  process.exit(1)
}
