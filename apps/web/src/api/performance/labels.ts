/**
 * Performans modülünün TEK çeviri ve sunum sözlüğü.
 *
 * Backend enum'ları İngilizce yollar; ekranda ham enum görünmemeli. Her enum
 * için etiket, gerekiyorsa kısa açıklama ve renk tonu burada. Bilinmeyen bir
 * değer gelirse `labelOf` ham değeri değil "Diğer"i döndürür ve konsola
 * uyarı düşer — yeni bir enum eklendiğini fark etmek için.
 */

import type { StatusTone } from '@/components/ui/StatusBadge'
import type {
  AnalyticsPeriod,
  CyclePeriod,
  CycleStatus,
  FeedbackReason,
  FeedbackSentiment,
  GoalStatus,
  MetricCategory,
  MetricScale,
  MetricTemplate,
  RecommendationAction,
  ReviewType,
  ScaleRange,
  ScoringConfigInput,
  Thresholds,
} from './types'
import { lang, tx, appLocale } from '@/lib/i18n'

/* ---------------------------------- Kategoriler -------------------------------- */

export const CATEGORIES: MetricCategory[] = ['Technical', 'Behavioral', 'Leadership', 'Delivery', 'Custom']

export const categoryLabels: Record<MetricCategory, string> = {
  Technical: tx('Teknik'),
  Behavioral: tx('Davranışsal'),
  Leadership: tx('Liderlik'),
  Delivery: tx('Teslimat'),
  Custom: tx('Özel'),
}

export const categoryHints: Record<MetricCategory, string> = {
  Technical: tx('Uzmanlık, iş kalitesi, teknik derinlik'),
  Behavioral: tx('İletişim, iş birliği, sorumluluk'),
  Leadership: tx('Yön verme, gelişim sağlama, karar'),
  Delivery: tx('Zamanında teslim, öngörülebilirlik, sonuç'),
  Custom: tx('Şirkete özgü ölçütler'),
}

/** Grafik ve rozetlerde kategori başına sabit renk — `--chart-1..5`. */
export const categoryColor: Record<MetricCategory, string> = {
  Technical: 'hsl(var(--chart-1))',
  Behavioral: 'hsl(var(--chart-2))',
  Leadership: 'hsl(var(--chart-5))',
  Delivery: 'hsl(var(--chart-3))',
  Custom: 'hsl(var(--chart-4))',
}

/** Kategori → puanlama ayarındaki düz alan adı. */
export const categoryWeightKey: Record<MetricCategory, keyof ScoringConfigInput> = {
  Technical: 'technicalWeight',
  Behavioral: 'behavioralWeight',
  Leadership: 'leadershipWeight',
  Delivery: 'deliveryWeight',
  Custom: 'customWeight',
}

/* ------------------------------------ Ölçekler --------------------------------- */

export const SCALES: MetricScale[] = ['OneToFive', 'OneToTen', 'Percentage']

export const scaleLabels: Record<MetricScale, string> = {
  OneToFive: '1–5',
  OneToTen: '1–10',
  Percentage: tx('Yüzde'),
}

export const scaleHints: Record<MetricScale, string> = {
  OneToFive: tx('Beş kademeli; yıldızla girilir.'),
  OneToTen: tx('On kademeli; daha ince ayrım isteyen metrikler için.'),
  Percentage: tx('0–100 arası; ölçülebilir oranlar için (ör. test kapsamı).'),
}

/** Backend `range` göndermezse kullanılan varsayılan sınırlar. */
export const scaleRange: Record<MetricScale, ScaleRange> = {
  OneToFive: { min: 1, max: 5 },
  OneToTen: { min: 1, max: 10 },
  Percentage: { min: 0, max: 100 },
}

/** Ölçekteki değeri 0–100'e çevirir — önizleme ve karşılaştırma için. */
export function normalizeToHundred(range: ScaleRange, value: number): number {
  if (range.max === range.min) return 0
  return ((value - range.min) / (range.max - range.min)) * 100
}

/* ------------------------------------ Şablonlar -------------------------------- */

export const TEMPLATES: MetricTemplate[] = ['genel', 'yazilim', 'satis']

export const templateInfo: Record<MetricTemplate, { title: string; detail: string; metrics: string[] }> = {
  genel: {
    title: tx('Genel'),
    detail: tx('Her rol için dengeli bir başlangıç: davranış, teslimat ve iş kalitesi.'),
    metrics: [tx('İş kalitesi'), tx('Zamanında teslim'), tx('İletişim'), tx('İş birliği'), tx('Sorumluluk alma')],
  },
  yazilim: {
    title: tx('Yazılım'),
    detail: tx('Mühendislik ekipleri için: kod kalitesi, teknik derinlik, teslimat.'),
    metrics: [tx('Kod kalitesi'), tx('Teknik derinlik'), tx('Test kapsamı'), tx('Teslimat öngörülebilirliği'), tx('Kod inceleme katkısı')],
  },
  satis: {
    title: tx('Satış'),
    detail: tx('Satış ekipleri için: hedef gerçekleşmesi, müşteri ilişkisi, süreç disiplini.'),
    metrics: [tx('Hedef gerçekleşme oranı'), tx('Müşteri memnuniyeti'), tx('Fırsat dönüşümü'), tx('CRM disiplini'), tx('Ekip katkısı')],
  },
}

/* ------------------------------- Değerlendirme türü ----------------------------- */

export const REVIEW_TYPES: ReviewType[] = ['Self', 'Manager', 'TeamLead', 'Peer', 'Upward']

export const reviewTypeLabels: Record<ReviewType, string> = {
  Self: tx('Öz değerlendirme'),
  Manager: tx('Yönetici'),
  TeamLead: tx('Takım lideri'),
  Peer: tx('Ekip arkadaşı'),
  Upward: tx('Yukarı yönlü'),
}

export const reviewTypeHints: Record<ReviewType, string> = {
  Self: tx('Çalışanın kendisini değerlendirmesi'),
  Manager: tx('Bağlı olduğu yöneticinin değerlendirmesi'),
  TeamLead: tx('Ekip liderinin değerlendirmesi'),
  Peer: tx('Aynı ekipten bir çalışma arkadaşının değerlendirmesi'),
  Upward: tx('Yöneticinin, ekibindekiler tarafından değerlendirilmesi'),
}

/** Değerlendirme türü → puanlama ayarındaki katsayı alanı. */
export const reviewWeightKey: Record<ReviewType, keyof ScoringConfigInput> = {
  Self: 'selfReviewWeight',
  Manager: 'managerReviewWeight',
  TeamLead: 'teamLeadReviewWeight',
  Peer: 'peerReviewWeight',
  Upward: 'upwardReviewWeight',
}

export const DEFAULT_REVIEW_WEIGHTS: Record<ReviewType, number> = {
  Self: 0.5,
  Manager: 2.0,
  TeamLead: 1.5,
  Peer: 1.0,
  Upward: 1.0,
}

/** Düz ayar alanlarından eşik özeti. */
export function thresholdsOf(c: ScoringConfigInput): Thresholds {
  return {
    critical: c.criticalThreshold,
    improvement: c.improvementThreshold,
    recognition: c.recognitionThreshold,
    promotion: c.promotionThreshold,
  }
}

/* ------------------------------------- Dönemler --------------------------------- */

export const CYCLE_PERIODS: CyclePeriod[] = ['Q1', 'Q2', 'Q3', 'Q4', 'H1', 'H2', 'Annual']

export const cyclePeriodLabels: Record<CyclePeriod, string> = {
  Q1: tx('1. çeyrek'),
  Q2: tx('2. çeyrek'),
  Q3: tx('3. çeyrek'),
  Q4: tx('4. çeyrek'),
  H1: tx('1. yarıyıl'),
  H2: tx('2. yarıyıl'),
  Annual: tx('Yıllık'),
}

export const cycleStatusLabels: Record<CycleStatus, string> = {
  Planned: tx('Taslak'),
  Open: tx('Açık'),
  InReview: tx('İncelemede'),
  Closed: tx('Kapandı'),
}

export const cycleStatusTone: Record<CycleStatus, StatusTone> = {
  Planned: 'neutral',
  Open: 'success',
  InReview: 'info',
  Closed: 'info',
}

/**
 * Ozet sayaclar ("Acik/Taslak/Kapanan") ve zaman cizelgesi icin 3'lu
 * gruplama. Backend'in InReview durumu ayri bir sekme yerine "Acik"
 * grubuna dahil edilir - dönem henuz KAPANMAMIS olmasi ortak paydalari.
 */
export function cycleStatusGroup(status: CycleStatus): 'planned' | 'open' | 'closed' {
  if (status === 'Planned') return 'planned'
  if (status === 'Closed') return 'closed'
  return 'open' // Open | InReview
}

/* ------------------------------------- Hedefler --------------------------------- */

export const GOAL_STATUSES: GoalStatus[] = ['Draft', 'Active', 'Achieved', 'Missed', 'Cancelled']

export const goalStatusLabels: Record<GoalStatus, string> = {
  Draft: tx('Taslak'),
  Active: tx('Devam ediyor'),
  Achieved: tx('Gerçekleşti'),
  Missed: tx('Gerçekleşmedi'),
  Cancelled: tx('İptal edildi'),
}

export const goalStatusTone: Record<GoalStatus, StatusTone> = {
  Draft: 'neutral',
  Active: 'info',
  Achieved: 'success',
  Missed: 'danger',
  Cancelled: 'neutral',
}

/* ------------------------------------- Dönem ----------------------------------- */

export const PERIODS: AnalyticsPeriod[] = ['week', 'month', 'quarter', 'halfYear', 'year', 'all']

export const periodLabels: Record<AnalyticsPeriod, string> = {
  week: tx('Hafta'),
  month: tx('Ay'),
  quarter: tx('Çeyrek'),
  halfYear: tx('Yarıyıl'),
  year: tx('Yıl'),
  all: tx('Tümü'),
}

/* --------------------------------- Geri bildirim -------------------------------- */

export const REASONS: FeedbackReason[] = [
  'Recognition',
  'GoalProgress',
  'Improvement',
  'Coaching',
  'Incident',
  'PeerObservation',
  'ReviewSummary',
  'Other',
]

export const reasonLabels: Record<FeedbackReason, string> = {
  Recognition: tx('Takdir'),
  GoalProgress: tx('Hedef ilerlemesi'),
  Improvement: tx('Gelişim alanı'),
  Coaching: tx('Koçluk'),
  Incident: tx('Olay bildirimi'),
  PeerObservation: tx('Ekip arkadaşı gözlemi'),
  ReviewSummary: tx('Değerlendirme özeti'),
  Other: tx('Diğer'),
}

export const SENTIMENTS: FeedbackSentiment[] = ['Positive', 'Neutral', 'Constructive']

export const sentimentLabels: Record<FeedbackSentiment, string> = {
  Positive: tx('Olumlu'),
  Neutral: tx('Nötr'),
  Constructive: tx('Yapıcı eleştiri'),
}

export const sentimentTone: Record<FeedbackSentiment, StatusTone> = {
  Positive: 'success',
  Neutral: 'neutral',
  Constructive: 'warning',
}

/* ------------------------------------ Öneriler ---------------------------------- */

export const actionTone: Record<RecommendationAction, StatusTone> = {
  Urgent: 'danger',
  Improvement: 'warning',
  PromotionCandidate: 'info',
  Watch: 'warning',
  Recognition: 'success',
  NoAction: 'neutral',
}

export const actionLabelsFallback: Record<RecommendationAction, string> = {
  Urgent: tx('Acil aksiyon'),
  Improvement: tx('Gelişim planı'),
  PromotionCandidate: tx('Terfi adayı'),
  Watch: tx('İzlenmeli'),
  Recognition: tx('Takdir'),
  NoAction: tx('Aksiyon gerekmiyor'),
}

export function toneOfAction(action: string): StatusTone {
  return (actionTone as Record<string, StatusTone>)[action] ?? 'neutral'
}

/* ---------------------------------- Yardımcılar -------------------------------- */

const warned = new Set<string>()

/** Bilinmeyen enum için güvenli etiket. Ham İngilizce değeri asla döndürmez. */
export function labelOf<K extends string>(map: Record<K, string>, value: string | null | undefined): string {
  if (!value) return '—'
  const hit = (map as Record<string, string>)[value]
  if (hit) return hit
  if (import.meta.env.DEV && !warned.has(value)) {
    warned.add(value)
    console.warn(`[performans] Çevirisi olmayan enum değeri: "${value}"`)
  }
  return tx('Diğer')
}

const scoreFmt = new Intl.NumberFormat(appLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const oneFmt = new Intl.NumberFormat(appLocale, { minimumFractionDigits: 0, maximumFractionDigits: 2 })

/** 0–100 puan, iki ondalık, tr-TR. Puan yoksa "—". */
export function formatScore(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value) ? scoreFmt.format(value) : '—'
}

/** Ağırlık/katsayı: gereksiz sıfır yok ("2", "2,5", "0,5"). */
export function formatWeight(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value) ? oneFmt.format(value) : '—'
}

/** İşaretli değer: "+1,5" / "−0,8" (gerçek eksi işareti). */
export function formatSigned(value: number, digits = 1): string {
  const f = new Intl.NumberFormat(appLocale, { minimumFractionDigits: digits, maximumFractionDigits: digits })
  const abs = f.format(Math.abs(value))
  return value > 0 ? `+${abs}` : value < 0 ? `−${abs}` : abs
}

/**
 * Oransal ağırlığın grup içindeki payı (0–100). Toplam 0 ise 0.
 * "3 ve 1" yazan kullanıcı "%75 ve %25" görmeli.
 */
export function shareOf(weight: number, total: number): number {
  return total > 0 ? (Math.max(0, weight) / total) * 100 : 0
}

export function formatShare(value: number): string {
  return lang === 'en' ? `${Math.round(value)}%` : `%${Math.round(value)}`
}

/**
 * Yüzdenin iyelik eki: "%31'i", "%42'si", "%40'ı", "%100'ü".
 * Ek, sayının okunuşunun son hecesine göre seçilir.
 */
export function possessiveSuffix(n: number): string {
  // İngilizcede iyelik eki yok.
  if (lang === 'en') return ''
  const v = Math.abs(Math.round(n))
  const units = ["'ı", "'i", "'si", "'ü", "'ü", "'i", "'sı", "'si", "'i", "'u"]
  const tens = ['', "'u", "'si", "'u", "'ı", "'si", "'ı", "'i", "'i", "'ı"]
  if (v === 0) return "'ı"
  if (v % 10 !== 0) return units[v % 10]
  if (v % 100 !== 0) return tens[(v % 100) / 10]
  if (v % 1000 !== 0) return "'ü"
  return "'i"
}

/** "%31'i" — pay cümlelerinde. */
export function formatShareOf(value: number): string {
  return `${formatShare(value)}${possessiveSuffix(value)}`
}

/** Puanın eşiklere göre bandı — rozet tonu ve eşik çubuğu için. */
export type ScoreBand = 'critical' | 'improvement' | 'normal' | 'recognition' | 'promotion'

export function bandOf(score: number, t: Thresholds): ScoreBand {
  if (score < t.critical) return 'critical'
  if (score < t.improvement) return 'improvement'
  if (score >= t.promotion) return 'promotion'
  if (score >= t.recognition) return 'recognition'
  return 'normal'
}

export const bandTone: Record<ScoreBand, StatusTone> = {
  critical: 'danger',
  improvement: 'warning',
  normal: 'neutral',
  recognition: 'success',
  promotion: 'info',
}

export const bandLabels: Record<ScoreBand, string> = {
  critical: tx('Kritik'),
  improvement: tx('Gelişim gerekli'),
  normal: tx('Beklenen aralık'),
  recognition: tx('Takdir düzeyi'),
  promotion: tx('Terfi düzeyi'),
}
