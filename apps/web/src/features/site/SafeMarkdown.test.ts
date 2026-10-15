import { describe, expect, it } from 'vitest'
import { isValidElement, type ReactElement } from 'react'
import { inline, parseBlocks, slugify } from './SafeMarkdown'
import { cookieRows, defaultFaq, defaultPrivacy, defaultTerms } from './defaults'

describe('SafeMarkdown', () => {
  it('blokları ayırır: başlık, liste, tablo, paragraf', () => {
    const b = parseBlocks('# Başlık\n\nParagraf satır 1\nsatır 2\n\n- bir\n- iki\n\n1. a\n2. b\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n---')
    expect(b.map((x) => x.kind)).toEqual(['h', 'p', 'ul', 'ol', 'table', 'hr'])
    expect(b[1]).toEqual({ kind: 'p', text: 'Paragraf satır 1 satır 2' })
    expect(b[4]).toEqual({ kind: 'table', head: ['A', 'B'], rows: [['1', '2']] })
  })

  it('javascript: ve // bağlantıları bağlantıya çevrilmez, http/mailto/site içi çevrilir', () => {
    const links = (src: string) => inline(src).filter((n): n is ReactElement<{ href: string }> => isValidElement(n) && n.type === 'a')
    expect(links('[x](javascript:alert(1))')).toHaveLength(0)
    expect(links('[x](//kotu.com)')).toHaveLength(0)
    expect(links('[x](data:text/html,1)')).toHaveLength(0)
    expect(links('[a](https://ornek.com) [b](mailto:a@b.c) [c](/gizlilik#cerezler)').map((l) => l.props.href)).toEqual([
      'https://ornek.com',
      'mailto:a@b.c',
      '/gizlilik#cerezler',
    ])
  })

  it('HTML etiketleri düz metin kalır', () => {
    const out = inline('<script>alert(1)</script>')
    expect(out).toEqual(['<script>alert(1)</script>'])
  })

  it('başlık çapası Türkçe harfleri sadeleştirir', () => {
    expect(slugify('Çerezler')).toBe('cerezler')
    expect(slugify('İlgili kişinin hakları (md. 11)')).toBe('ilgili-kisinin-haklari-md-11')
  })
})

describe('varsayılan site metinleri', () => {
  it('işletmeci bilgileri şablona yerleşir, boşsa genel ifade kullanılır', () => {
    const site = { operatorName: 'Acme A.Ş.', address: null, email: 'kvkk@acme.com', phone: null, kep: null, verbisNo: null, privacyMd: null, termsMd: null, faq: [], searchIndexing: false }
    expect(defaultPrivacy(site, 'tr')).toContain('Acme A.Ş.')
    expect(defaultPrivacy(site, 'tr')).toContain('mailto:kvkk@acme.com')
    expect(defaultPrivacy(undefined, 'tr')).toContain('bu hizmeti işleten kuruluş')
    expect(defaultTerms(undefined, 'en')).toContain('AGPL-3.0')
    expect(defaultPrivacy(undefined, 'tr')).toMatch(/md\. 11/)
  })

  it('SSS ve çerez tablosu her iki dilde dolu', () => {
    for (const l of ['tr', 'en'] as const) {
      expect(defaultFaq(l).length).toBeGreaterThan(5)
      expect(cookieRows(l).some((r) => r.name.includes('KEYCLOAK_SESSION'))).toBe(true)
    }
  })
})
