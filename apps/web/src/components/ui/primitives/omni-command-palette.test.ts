import { describe, expect, it } from 'vitest'
import { foldSearch, fuzzyScore } from './omni-command-palette'

describe('komut paleti arama', () => {
  it('katlama uzunluğu korur (vurgu konumları doğru kalır)', () => {
    for (const s of ['İzin', 'İşe alım', 'ÇĞÖŞÜ ığ', 'Yetenek dizini']) expect(foldSearch(s)).toHaveLength(s.length)
    expect(foldSearch('İşe Alım')).toBe('ise alim')
  })

  it('"izin" yazınca İzin, Yetenek dizini\'nden önce gelir', () => {
    const izin = fuzzyScore('izin', 'İzin')
    const dizin = fuzzyScore('izin', 'Yetenek dizini')
    expect(izin.score).toBeGreaterThan(dizin.score)
    expect(izin.indices).toEqual([0, 1, 2, 3])
  })

  it('"işe alım" ve aksansız "ise alim" eşleşir', () => {
    expect(fuzzyScore('işe alım', 'İşe alım').indices).toEqual([0, 1, 2, 3, 4, 5, 6, 7])
    expect(fuzzyScore('ise alim', 'İşe alım').score).toBeGreaterThanOrEqual(1000)
  })

  it('kelime başı eşleşmesi kelime ortasının önüne geçer', () => {
    expect(fuzzyScore('alım', 'İşe alım').indices).toEqual([4, 5, 6, 7])
    expect(fuzzyScore('izin', 'İzin takvimi').score).toBeGreaterThan(fuzzyScore('izin', 'Yetenek dizini').score)
  })
})
