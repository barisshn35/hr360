import { formatNumber } from './format'
import { tx } from './i18n'

/**
 * Masraf denetimi (ML dalgası 1): ml-inference'ın ürettiği işaretlerin görünümü ve e-Fatura
 * karekodunun forma aktarılması. İşaretler yalnızca onaycıya bilgidir; beyanı reddetmez.
 */

export type FlagSeverity = 'low' | 'medium' | 'high'

export interface AnomalyFlag {
  code: string
  severity: FlagSeverity
  /** Sunucunun Türkçe gerekçesi (kod tanınmazsa gösterilir). */
  reason: string
  details?: Record<string, unknown>
}

/** Kod -> yerelleştirilmiş gerekçe. Sayılar ayrıntıdan doldurulur. */
export function flagText(f: AnomalyFlag): string {
  const d = f.details ?? {}
  const median = typeof d.median === 'number' ? formatNumber(d.median) : '—'
  switch (f.code) {
    case 'DUPLICATE_ETTN':
      return tx('Aynı ETTN\'li fatura daha önce (ya da bu beyanda) masraf olarak girilmiş.')
    case 'DUPLICATE_INVOICE_NO':
      return tx('Aynı tedarikçinin aynı numaralı faturası daha önce girilmiş.')
    case 'POSSIBLE_DUPLICATE':
      return tx('Aynı tutar ve tarihte (±1 gün) aynı tedarikçi/çalışan için başka bir kalem var.')
    case 'DUPLICATE_TEXT':
      return tx('Aynı açıklama ve tutarla daha önce bir kalem girilmiş.')
    case 'AMOUNT_OUTLIER_CATEGORY':
      return tx('Tutar, bu kategorideki olağan tutarların çok üzerinde (medyan {0}).', [median])
    case 'AMOUNT_OUTLIER_EMPLOYEE':
      return tx('Tutar, çalışanın bu kategorideki olağan tutarlarının çok üzerinde (medyan {0}).', [median])
    case 'AMOUNT_OUTLIER_MERCHANT':
      return tx('Tutar, bu tedarikçideki olağan tutarların çok üzerinde (medyan {0}).', [median])
    case 'UNUSUAL_PATTERN':
      return tx('Kalem, şirketin geçmiş masraf örüntüsüne göre olağan dışı (tutar/gün/kategori).')
    default:
      // Sunucu gerekçesi apiFetch'te zaten çevrilir ("@server:" anahtarları).
      return f.reason
  }
}

export const severityTone: Record<FlagSeverity, 'danger' | 'warning' | 'neutral'> = {
  high: 'danger',
  medium: 'warning',
  low: 'neutral',
}

export const severityLabel = (s: FlagSeverity) =>
  s === 'high' ? tx('Yüksek') : s === 'medium' ? tx('Orta') : tx('Düşük')

/** Kalem listesindeki işaretlerin toplamı ve en yüksek önem derecesi. */
export function flagSummary(items: Array<{ anomalyFlags?: AnomalyFlag[] | null }>): { count: number; worst: FlagSeverity | null } {
  const order: FlagSeverity[] = ['low', 'medium', 'high']
  let count = 0
  let worst = -1
  for (const i of items) {
    for (const f of i.anomalyFlags ?? []) {
      count++
      worst = Math.max(worst, order.indexOf(f.severity))
    }
  }
  return { count, worst: worst < 0 ? null : order[worst] }
}

/* ------------------------------------------------------------------ e-Fatura karekodu */

/** expense-service /expense-claims/einvoice yanıtı (ml-inference ayrıştırıcısı). */
export interface EInvoice {
  supplierTaxId: string | null
  supplierTaxIdKind: 'vkn' | 'tckn' | null
  buyerTaxId: string | null
  invoiceNo: string | null
  ettn: string | null
  date: string | null
  scenario: string | null
  type: string | null
  currency: string
  net: number | null
  vat: number | null
  vatBreakdown: Array<{ rate: number; base: number | null; vat: number | null }>
  total: number | null
  payable: number | null
  warnings: string[]
}

/** Forma aktarılacak alanlar (tutar metin olarak, ondalık virgüllü). */
export interface EInvoiceDraft {
  amount?: string
  currency?: string
  expenseDate?: string
  supplierTaxId?: string
  invoiceNo?: string
  ettn?: string
  vatAmount?: number
}

export function einvoiceToDraft(e: EInvoice, currencies: readonly string[]): EInvoiceDraft {
  const out: EInvoiceDraft = {}
  const amount = e.payable ?? e.total
  if (amount != null && amount > 0) out.amount = String(amount).replace('.', ',')
  if (currencies.includes(e.currency)) out.currency = e.currency
  if (e.date && /^\d{4}-\d{2}-\d{2}$/.test(e.date)) out.expenseDate = e.date
  if (e.supplierTaxId) out.supplierTaxId = e.supplierTaxId
  if (e.invoiceNo) out.invoiceNo = e.invoiceNo
  if (e.ettn) out.ettn = e.ettn
  if (e.vat != null) out.vatAmount = e.vat
  return out
}

/** Tarayıcıda karekod çözücü (BarcodeDetector, QR) var mı? Yoksa yapıştırma alanı kullanılır. */
export function qrDetectorAvailable(): boolean {
  return typeof window !== 'undefined' && 'BarcodeDetector' in window
}

interface DetectedBarcode { rawValue: string }
interface BarcodeDetectorLike { detect(source: ImageBitmapSource): Promise<DetectedBarcode[]> }
type BarcodeDetectorCtor = new (opts: { formats: string[] }) => BarcodeDetectorLike

/** Görüntü dosyasındaki ilk QR kodunun metni (tarayıcıda; görüntü sunucuya gönderilmez). */
export async function readQrFromImage(file: Blob): Promise<string | null> {
  const Ctor = (window as unknown as { BarcodeDetector?: BarcodeDetectorCtor }).BarcodeDetector
  if (!Ctor) return null
  const bitmap = await createImageBitmap(file)
  try {
    const codes = await new Ctor({ formats: ['qr_code'] }).detect(bitmap)
    return codes.find((c) => c.rawValue?.trim())?.rawValue ?? null
  } finally {
    bitmap.close?.()
  }
}
