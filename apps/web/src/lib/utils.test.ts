import { describe, expect, it } from 'vitest'
import { cn } from './utils'

// tailwind-merge 3 + Tailwind v4: @theme'deki özel gölge/animasyon adları çakışmada doğru birleşmeli.
describe('cn', () => {
  it('son yazılan çakışan sınıfı tutar', () => {
    expect(cn('p-2', 'p-4')).toBe('p-4')
    expect(cn('text-sm text-primary', 'text-muted-foreground')).toBe('text-sm text-muted-foreground')
  })
  it('özel gölgeleri gölge boyutu sayar, rengini korur', () => {
    expect(cn('shadow-sm', 'shadow-card')).toBe('shadow-card')
    expect(cn('shadow-card', 'shadow-black/5')).toBe('shadow-card shadow-black/5')
    expect(cn('shadow-surface', 'shadow-none')).toBe('shadow-none')
  })
  it('özel animasyonları birleştirir', () => {
    expect(cn('animate-shine', 'animate-spin')).toBe('animate-spin')
  })
})
