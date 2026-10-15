import { describe, expect, it, vi } from 'vitest'
import { FormTokenHolder, remainingWait } from './formGuard'

describe('herkese açık form jetonu', () => {
  it('kalan bekleme süresi', () => {
    expect(remainingWait(1000, 3, 1000)).toBe(3300)
    expect(remainingWait(1000, 3, 10_000)).toBe(0)
  })

  it('çok hızlı gönderimde bekler, jetonu bir kez verir ve yenisini hazırlar', async () => {
    let n = 0
    let now = 0
    const fetcher = vi.fn(async () => ({ token: `t${++n}`, minSeconds: 3 }))
    const sleeps: number[] = []
    const h = new FormTokenHolder(fetcher, async (ms) => { sleeps.push(ms); now += ms }, () => now)
    h.prepare()
    now = 1000
    expect(await h.take()).toBe('t1')
    expect(sleeps).toEqual([3300]) // jetonun alındığı andan itibaren 3 sn + pay
    now += 10_000
    expect(await h.take()).toBe('t2')
    expect(fetcher).toHaveBeenCalledTimes(3)
  })

  it('jeton alınamazsa bir kez yeniden dener, yine olmazsa boş döner', async () => {
    const fetcher = vi.fn(async () => { throw new Error('ağ') })
    const h = new FormTokenHolder(fetcher, async () => {}, () => 0)
    expect(await h.take()).toBeUndefined()
    expect(fetcher).toHaveBeenCalledTimes(2)
  })
})
