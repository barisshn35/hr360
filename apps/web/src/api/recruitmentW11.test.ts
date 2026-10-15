import { describe, expect, it } from 'vitest'
import { injectJsonLd } from './recruitmentW11'

/** Vitest node ortamında DOM yok: injectJsonLd'nin kullandığı kadarını taklit eden belge. */
function fakeDoc() {
  const children: Array<{ type: string; textContent: string; dataset: Record<string, string>; remove: () => void }> = []
  const doc = {
    head: { appendChild: (el: (typeof children)[number]) => { children.push(el); return el } },
    createElement: () => {
      const el = { type: '', textContent: '', dataset: {} as Record<string, string>, remove: () => { children.splice(children.indexOf(el), 1) } }
      return el
    },
  }
  return { doc: doc as unknown as Document, children }
}

describe('injectJsonLd', () => {
  it('ld+json veri bloğu ekler, "<" kaçışlıdır ve temizleyici kaldırır', () => {
    const { doc, children } = fakeDoc()
    const cleanup = injectJsonLd({ '@type': 'JobPosting', description: '<p>x</p></script><script>alert(1)</script>' }, doc)
    expect(children).toHaveLength(1)
    expect(children[0].type).toBe('application/ld+json')
    expect(children[0].textContent).not.toContain('</script>')
    expect(JSON.parse(children[0].textContent).description).toContain('</script>')
    cleanup()
    expect(children).toHaveLength(0)
  })

  it('veri yoksa bir şey eklemez', () => {
    const { doc, children } = fakeDoc()
    injectJsonLd(null, doc)()
    expect(children).toHaveLength(0)
  })
})
