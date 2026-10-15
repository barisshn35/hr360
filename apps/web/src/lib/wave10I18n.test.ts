import { afterAll, describe, expect, it } from 'vitest'
import en from '@/locales/en.json'
import { setDictionary, txServer } from './i18n'

/** Dalga 10: ML gerekçeleri (aday uygunluğu, "Sana uygun", eğitim eşikleri) İngilizce arayüzde kalıpla çevrilir. */
describe('dalga 10 sunucu gerekçeleri', () => {
  afterAll(() => setDictionary({}, 'tr'))

  it('aday uygunluk gerekçeleri ve eğitim eşikleri', () => {
    setDictionary(en as Record<string, string>, 'en')
    expect(txServer('Zorunlu becerilerden 2/3 eşleşti: C#, Docker. Eksik: Kubernetes')).toBe('2/3 required skills matched: C#, Docker. Missing: Kubernetes')
    expect(txServer('Zorunlu becerilerden 0/3 eşleşti. Eksik: C#, Docker')).toBe('0/3 required skills matched. Missing: C#, Docker')
    expect(txServer('Nitelikler: 1/2 karşılanıyor')).toBe('Qualifications: 1/2 met')
    expect(txServer('Deneyim yılı metinde bulunamadı (beklenen en az 5) — elle kontrol edin')).toContain('check manually')
    expect(txServer('Eğitim anlık görüntüsünde en az 200 çalışan gerekir (şu an 4).')).toBe('The training snapshot needs at least 200 employees (currently 4).')
    expect(txServer('2 boş mentorluk yeri')).toBe('2 free mentoring slots')
    expect(txServer('Yetkinlik açığını kapatır: Kubernetes yönetimi (1 → 3, beklenen 4)')).toBe('Closes a competency gap: Kubernetes yönetimi (1 → 3, beklenen 4)')
  })
})
