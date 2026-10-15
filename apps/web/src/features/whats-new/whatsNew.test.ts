import fs from 'node:fs'
import { describe, expect, it } from 'vitest'
// @ts-expect-error — düz JS betiği (tip bildirimi yok)
import { MD, OUT, parseChangelog, render } from '../../../scripts/changelog.mjs'
import { latestId, unreadCount } from './whatsNew'

describe('Yenilikler (dalga 12)', () => {
  it('okunmamış sayısı', () => {
    expect(unreadCount(['12', '11', '10'], null)).toBe(1)
    expect(unreadCount(['12', '11', '10'], '10')).toBe(2)
    expect(unreadCount(['12', '11'], '12')).toBe(0)
    expect(unreadCount([], null)).toBe(0)
    expect(latestId(['9', '12', '10'])).toBe('12')
  })

  it('docs/CHANGELOG-tr.md ile üretilen dosya aynı (node scripts/changelog.mjs)', () => {
    const entries = parseChangelog(fs.readFileSync(MD, 'utf8'))
    expect(entries.length).toBeGreaterThanOrEqual(12)
    // En yeni dalga başta: kimlikler azalan sırada (yeni dalga eklenince test değişmesin).
    expect(Number(entries[0].id)).toBe(Math.max(...entries.map((e: { id: string }) => Number(e.id))))
    expect(fs.readFileSync(OUT, 'utf8')).toBe(render(entries))
  })

  it('biçimsiz başlığı reddeder', () => {
    expect(() => parseChangelog('## 3 - tarih yok\n- madde')).toThrow()
    expect(() => parseChangelog('## 1 · 2026-01-01 · A\n- x\n## 1 · 2026-01-02 · B\n- y')).toThrow()
  })
})
