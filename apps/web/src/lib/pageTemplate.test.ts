import { describe, expect, it } from 'vitest'
import { pageTemplate } from './pageTemplate'

describe('pageTemplate', () => {
  it('kimlik ve sayıları maskeler, sorguyu atar', () => {
    expect(pageTemplate('/panel/calisanlar/131ec96a-465a-45ae-87f0-58ed52bb9332')).toBe('/panel/calisanlar/:id')
    expect(pageTemplate('/panel/izin?durum=Approved')).toBe('/panel/izin')
    expect(pageTemplate('/panel/ayin-calisani/sonuc/2b4518b3-3a8b-40ee-bf49-9cdd9e15872a/')).toBe('/panel/ayin-calisani/sonuc/:id')
    expect(pageTemplate('/panel/ozel/Ayşe')).toBe('/panel/ozel/:id')
    expect(pageTemplate('/panel/performans/360')).toBe('/panel/performans/360')
    expect(pageTemplate('/panel/kayit/20261016')).toBe('/panel/kayit/:id')
    expect(pageTemplate('/giris')).toBeNull()
  })
})
