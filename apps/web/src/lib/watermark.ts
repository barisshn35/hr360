import { apiFetch } from '@/api/client'

/**
 * Güvenlik dalgası 2B — filigran ve iz kodu.
 *
 * Yazdırılan/indirilen kişisel veri içeren çıktılara (bordro pusulası, belge, sertifika, tablo dışa
 * aktarımı) indiren kişiyi gösteren "<ad soyad> · <tarih saat> · <iz kodu>" yazılır. Kod sunucuda
 * denetim kaydına bağlanır (governance /data-protection/trace); sızan bir çıktıdaki kodla İK
 * Veri koruma › İz kodu ekranından kimin, ne zaman indirdiğini bulur.
 *
 * İz kodu alınamazsa (ağ hatası, kiracısız platform oturumu) çıktı yine üretilir; filigran eklenmez.
 */
export interface TraceInfo {
  code: string
  name: string
  at: string
  text: string
}

export async function requestTrace(kind: string, opts: { subject?: string; format?: string; rows?: number } = {}): Promise<TraceInfo | null> {
  try {
    return await apiFetch<TraceInfo>('/api/governance/data-protection/trace', { method: 'POST', body: { kind, ...opts }, noQueue: true })
  } catch {
    return null
  }
}

export { WATERMARK_CSS, csvWatermarkLine, isWatermarkLine, watermarkHtml, withCsvWatermark } from './watermarkText'
