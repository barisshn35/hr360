import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * Tasarım sistemi kuralı: ilkeller (components/ui/primitives/: dialog, alert-dialog, sheet, card,
 * badge, table, komut paleti motoru) yalnızca ortak bileşenlerin (components/ui/*) içinde kullanılır.
 * Ekranlar sarmalayıcıları kullanır: Modal, Drawer, useConfirm, Panel, StatusBadge, DataTable,
 * CommandPalette. Böylece pencere, kart ve rozet her ekranda aynı görünür ve davranır.
 */
const SRC = join(__dirname, '..', '..')
const UI = join(SRC, 'components', 'ui')
// Komut paleti motorunun tek sarmalayıcısı menü ağacıyla birlikte layout/ altında durur.
const WRAPPERS = new Set([join(SRC, 'components', 'layout', 'CommandPalette.tsx')])
const PRIMITIVE = /from ['"](@\/components\/ui\/primitives\/[^'"]+|\.{1,2}\/(?:[^'"]*\/)?primitives\/[^'"]+)['"]/

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n)
    return statSync(p).isDirectory() ? files(p) : /\.(ts|tsx)$/.test(n) ? [p] : []
  })
}

describe('tasarım sistemi › ilkeller', () => {
  it('ekranlar ilkelleri doğrudan içe aktarmaz', () => {
    const offenders = files(SRC)
      .filter((f) => !f.startsWith(UI + '/') && !WRAPPERS.has(f) && !/\.test\.tsx?$/.test(f))
      .filter((f) => PRIMITIVE.test(readFileSync(f, 'utf8')))
      .map((f) => relative(SRC, f))
    expect(offenders).toEqual([])
  })

  it('bileşen dosya adları kebab-case', () => {
    const bad = files(UI).map((f) => relative(UI, f)).filter((f) => !/^([a-z0-9]+(-[a-z0-9]+)*\/)*[a-z0-9]+(-[a-z0-9]+)*(\.test)?\.tsx?$/.test(f))
    expect(bad).toEqual([])
  })
})
