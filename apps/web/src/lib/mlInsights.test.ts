import { describe, expect, it } from 'vitest'
import { capacityTone, formatGap, mapeLabel, newSuggestions, payrollFlagText, semanticSourceLabel, sortBySeverity, worstSeverity } from './mlInsights'
import type { AnomalyFlag } from './expenseAudit'

const f = (code: string, severity: AnomalyFlag['severity'] = 'low', details: Record<string, unknown> = {}): AnomalyFlag =>
  ({ code, severity, reason: 'sunucu gerekçesi', details })

describe('bordro / puantaj işaret metinleri', () => {
  it('kendi geçmişi ve eş grubu kodları ölçüte göre yazılır', () => {
    expect(payrollFlagText(f('OVERTIME_SPIKE_OWN', 'low', { value: 60, median: 6 }))).toContain('Fazla mesai saati')
    expect(payrollFlagText(f('OVERTIME_SPIKE_OWN', 'low', { value: 60, median: 6 }))).toContain('60')
    expect(payrollFlagText(f('ADDITIONS_OUTLIER_PEER', 'low', { n: 7, median: 1000 }))).toContain('7')
  })
  it('yasal sınır ve oranlar', () => {
    expect(payrollFlagText(f('OVERTIME_ANNUAL_LIMIT', 'high', { year_to_date: 280 }))).toContain('270')
    expect(payrollFlagText(f('DEDUCTION_RATIO_HIGH', 'medium', { ratio: 0.6 }))).toContain('60')
    expect(payrollFlagText(f('WEEKLY_HOURS_OVER_LIMIT', 'medium', { hours: 52 }))).toContain('45')
  })
  it('bilinmeyen kodda sunucu gerekçesi gösterilir', () => {
    expect(payrollFlagText(f('YENI_KOD'))).toBe('sunucu gerekçesi')
  })
})

describe('önem sıralaması', () => {
  it('en yüksek önem ve sıralama', () => {
    expect(worstSeverity([f('A'), f('B', 'high'), f('C', 'medium')])).toBe('high')
    expect(worstSeverity([])).toBeNull()
    const rows = sortBySeverity([{ id: 1, flags: [f('A')] }, { id: 2, flags: [f('B', 'high')] }, { id: 3, flags: [f('C'), f('D')] }])
    expect(rows.map((r) => r.id)).toEqual([2, 3, 1])
  })
})

describe('kapasite, geri test ve ücret farkı', () => {
  it('kapasite tonu eşikleri', () => {
    expect(capacityTone(25)).toBe('danger')
    expect(capacityTone(10)).toBe('warning')
    expect(capacityTone(4)).toBe('neutral')
  })
  it('MAPE yorumu', () => {
    expect(mapeLabel(null)).toBe('ölçülemedi')
    expect(mapeLabel(12)).toBe('iyi')
    expect(mapeLabel(25)).toBe('orta')
    expect(mapeLabel(50)).toBe('zayıf')
  })
  it('fark biçimi işaretli', () => {
    expect(formatGap(4.2)).toMatch(/^\+%4[,.]2$/)
    expect(formatGap(-3)).toBe('−%3')
    expect(formatGap(0)).toBe('%0')
  })
})

describe('beceri önerisi süzgeci', () => {
  it('var olanı (Türkçe büyük-küçük harf duyarsız) ve tekrarları çıkarır', () => {
    expect(newSuggestions(['İletişim', 'Python', 'python', ' ', 'Kubernetes'], ['iletişim', 'KUBERNETES'])).toEqual(['Python'])
  })
  it('kaynak etiketleri', () => {
    expect(semanticSourceLabel('kb')).toBe('Bilgi bankası')
    expect(semanticSourceLabel('x')).toBe('x')
  })
})
