// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen } from '@testing-library/react'
import { prefersReducedMotion, useReduceMotionSetting, useReducedMotion } from './motion'

function Probe() {
  const reduced = useReducedMotion()
  const [setting, set] = useReduceMotionSetting()
  return <button onClick={() => set(!setting)}>{`${reduced}/${setting}`}</button>
}

function mockSystem(reduce: boolean) {
  window.matchMedia = vi.fn().mockReturnValue({ matches: reduce, addEventListener: vi.fn(), removeEventListener: vi.fn() })
}

describe('lib/motion › Animasyonları azalt', () => {
  afterEach(() => { cleanup(); window.localStorage.clear(); document.documentElement.removeAttribute('data-motion') })

  it('ayar açılınca <html data-motion="reduce"> ve kalıcı; kapanınca kalkar', () => {
    mockSystem(false)
    render(<Probe />)
    const b = screen.getByRole('button')
    expect(b.textContent).toBe('false/false')
    act(() => b.click())
    expect(b.textContent).toBe('true/true')
    expect(document.documentElement.getAttribute('data-motion')).toBe('reduce')
    expect(window.localStorage.getItem('hr360.reduceMotion')).toBe('1')
    expect(prefersReducedMotion()).toBe(true)
    act(() => b.click())
    expect(b.textContent).toBe('false/false')
    expect(document.documentElement.hasAttribute('data-motion')).toBe(false)
    expect(window.localStorage.getItem('hr360.reduceMotion')).toBeNull()
  })

  it('sistem ayarı açıksa kullanıcı tercihi olmadan da azaltılır', () => {
    mockSystem(true)
    render(<Probe />)
    expect(screen.getByRole('button').textContent).toBe('true/false')
    expect(prefersReducedMotion()).toBe(true)
    expect(document.documentElement.hasAttribute('data-motion')).toBe(false)
  })
})
