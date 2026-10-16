import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { installNativeShell } from './nativeShell'

/** Test ortamı DOM'suz (node); kabuğun kullandığı en küçük parçalar taklit edilir. */
class FakeAnchor {
  href = ''
  download = ''
  hasAttribute(n: string) { return n === 'download' && !!this.download }
  click() { /* tarayıcı varsayılanı */ }
}
const g = globalThis as Record<string, unknown>

describe('Android kabuğu indirme köprüsü', () => {
  const realCreate = URL.createObjectURL, realRevoke = URL.revokeObjectURL
  beforeEach(() => {
    g.window = globalThis
    g.HTMLAnchorElement = FakeAnchor
    g.document = { addEventListener: vi.fn() }
  })
  afterEach(() => {
    delete g.HR360Native; delete g.HTMLAnchorElement; delete g.document; delete g.window
    URL.createObjectURL = realCreate; URL.revokeObjectURL = realRevoke
    vi.useRealTimers()
  })

  it('tarayıcıda (köprü yoksa) hiçbir şeyi değiştirmez', () => {
    const before = FakeAnchor.prototype.click
    installNativeShell()
    expect(FakeAnchor.prototype.click).toBe(before)
    expect(URL.createObjectURL).toBe(realCreate)
  })

  it('blob indirmesini, hemen iptal edilse de yerel köprüyle kaydeder', async () => {
    const saveFile = vi.fn(() => true)
    g.HR360Native = { getServer: () => 'https://ik.example.com', resetServer: vi.fn(), saveFile }
    URL.createObjectURL = vi.fn(() => 'blob:https://ik.example.com/1')
    const revoke = vi.fn()
    URL.revokeObjectURL = revoke
    installNativeShell()
    vi.useFakeTimers({ toFake: ['setTimeout'] })
    const url = URL.createObjectURL(new Blob(['merhaba'], { type: 'text/calendar' }))
    const a = new FakeAnchor() as unknown as HTMLAnchorElement
    a.href = url; a.download = 'etkinlik.ics'
    a.click()
    URL.revokeObjectURL(url)
    expect(revoke).not.toHaveBeenCalled()
    await vi.waitFor(() => expect(saveFile).toHaveBeenCalled())
    expect(saveFile).toHaveBeenCalledWith(btoa('merhaba'), 'etkinlik.ics', 'text/calendar')
    vi.advanceTimersByTime(60_000)
    expect(revoke).toHaveBeenCalledWith(url)
  })
})
