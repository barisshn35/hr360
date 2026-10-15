import type { PredictionReason } from '@/api/types'
import type { ModelFreshness, ReliabilityBin, ThresholdRow } from '@/api/mlModel'
import { appLocale, tx } from '@/lib/i18n'

/**
 * Devir riski modelinin kalite ekranı yardımcıları (ML dalgası 1): güvenilirlik eğrisi
 * koordinatları, eşik tablosundan satır seçimi, sade dilde "neden?" cümleleri, güncellik.
 */

/** Güvenilirlik eğrisi noktaları (SVG koordinatı; 0–1 alanı `size` piksele ölçeklenir).
 * 5'ten az kayıtlı (gizlenen) kovalar çizilmez. */
export function reliabilityPoints(bins: ReliabilityBin[], size: number): Array<{ x: number; y: number; bin: ReliabilityBin }> {
  return bins
    .filter((b) => b.mean_predicted !== null && b.observed_rate !== null)
    .map((b) => ({
      x: Math.round((b.mean_predicted as number) * size * 10) / 10,
      y: Math.round((1 - (b.observed_rate as number)) * size * 10) / 10,
      bin: b,
    }))
}

export function polyline(points: Array<{ x: number; y: number }>): string {
  return points.map((p) => `${p.x},${p.y}`).join(' ')
}

/** Eşik tablosunda verilen eşiğe en yakın satır (tablo 0,05 adımlıdır; kayıtlı eşik ara değer olabilir). */
export function nearestRow(rows: ThresholdRow[], threshold: number): ThresholdRow | undefined {
  let best: ThresholdRow | undefined
  for (const r of rows) {
    if (!best || Math.abs(r.threshold - threshold) < Math.abs(best.threshold - threshold)) best = r
  }
  return best
}

/** Sunucu kodu → çeviri anahtarı. Bilinmeyen kodda sunucunun Türkçe metni kullanılır. */
export function reasonText(r: PredictionReason): string {
  const p = r.params ?? []
  const n = (i: number, digits = 1) =>
    typeof p[i] === 'number' ? new Intl.NumberFormat(appLocale, { maximumFractionDigits: digits }).format(p[i] as number) : String(p[i] ?? '')
  const base = (() => {
    switch (r.code) {
      case 'tenure_short': return tx('Kıdemi kısa ({0} yıl)', [n(0)])
      case 'tenure': return tx('Kıdem {0} yıl', [n(0)])
      case 'pay_below': return tx('Ücreti bant ortasının altında (%{0})', [n(0, 0)])
      case 'pay_above': return tx('Ücreti bant ortasının üstünde (%{0})', [n(0, 0)])
      case 'pay_mid': return tx('Ücreti bant ortasına yakın (%{0})', [n(0, 0)])
      case 'rating': return tx('Son performans puanı {0}/5', [n(0)])
      case 'no_promotion': return tx('Son {0} aydır terfi ya da unvan değişikliği yok', [n(0, 0)])
      case 'recent_promotion': return tx('Son terfi {0} ay önce', [n(0, 0)])
      case 'overtime': return tx('Aylık ortalama {0} saat fazla mesai', [n(0, 0)])
      case 'training': return tx('Son 12 ayda {0} saat eğitim', [n(0, 0)])
      default: return null
    }
  })()
  if (base === null) return r.text
  return `${base} (${r.direction === 'up' ? '+' : '−'})`
}

export type FreshnessState = 'fresh' | 'expiring' | 'stale' | 'unknown'

/** Güncellik durumu: süresi geçmiş, son 30 gün ya da güncel. */
export function freshnessState(f: ModelFreshness | undefined | null): FreshnessState {
  if (!f || f.stale === null || f.stale === undefined) return 'unknown'
  if (f.stale) return 'stale'
  return (f.days_left ?? Infinity) <= 30 ? 'expiring' : 'fresh'
}

/** Adillik denetimi CSV şablonunun başlığı. */
export const FAIRNESS_CSV_HEADER =
  'employee_id,tenure_years,compa_ratio,last_rating,months_since_promotion,overtime_hours_month,training_hours_year,label'
