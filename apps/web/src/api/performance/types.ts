/**
 * Performans modülü — veri sözleşmeleri.
 *
 * Kaynak: backend devir notu ve canlıdan alınmış örnek yanıtlar (metrik,
 * puan dökümü, çalışan önerisi). Bunlar birebir alındı. Örneği verilmeyen
 * yanıtlarda (readiness, compare, vs-team, geçmiş sürümler…) belirsiz alanlar
 * `adapters.ts` içinde toleranslı okunur; ekranlar her zaman buradaki
 * normalize edilmiş şekli görür. Canlıda bir alan adı farklı çıkarsa düzeltme
 * yalnızca adaptöre düşer.
 *
 * Kurallar:
 *  - Puanlar 0–100 aralığında `number`; puan hiç yoksa `null` (0 değil — 0
 *    gerçek bir puandır, "puanlanmadı" ile karışmamalı).
 *  - Enum'lar backend'den İngilizce gelir; ekranda asla ham gösterilmez
 *    (bkz. `labels.ts`). `actionLabel`, `periodLabel`, `trendLabel` zaten
 *    Türkçe gelir ve olduğu gibi kullanılır.
 */

/* ---------------------------------- Enum'lar --------------------------------- */

export type MetricCategory = 'Technical' | 'Behavioral' | 'Leadership' | 'Delivery' | 'Custom'
export type MetricScale = 'OneToFive' | 'OneToTen' | 'Percentage'
export type MetricTemplate = 'genel' | 'yazilim' | 'satis'

export type ReviewType = 'Self' | 'Manager' | 'TeamLead' | 'Peer' | 'Upward'

export type AnalyticsPeriod = 'week' | 'month' | 'quarter' | 'halfYear' | 'year' | 'all'

export type CyclePeriod = 'Q1' | 'Q2' | 'Q3' | 'Q4' | 'H1' | 'H2' | 'Annual'
export type CycleStatus = 'Planned' | 'Open' | 'InReview' | 'Closed'
export type GoalStatus = 'Draft' | 'Active' | 'Achieved' | 'Missed' | 'Cancelled'

export type FeedbackReason =
  | 'Recognition'
  | 'GoalProgress'
  | 'Improvement'
  | 'Coaching'
  | 'Incident'
  | 'PeerObservation'
  | 'ReviewSummary'
  | 'Other'

export type FeedbackSentiment = 'Positive' | 'Neutral' | 'Constructive'

/**
 * Öneri türleri. Liste backend'den "dikkat gerektiren önce" sıralı gelir ve
 * arayüz bu sırayı BOZMAZ. Tanımadığımız bir değer gelirse `string` kabul
 * edilir; ekranda backend'in Türkçe `actionLabel`'ı gösterilir.
 */
export type RecommendationAction =
  | 'Urgent'
  | 'Improvement'
  | 'PromotionCandidate'
  | 'Watch'
  | 'Recognition'
  | 'NoAction'

/* ---------------------------------- Ortak ------------------------------------ */

export interface ScaleRange {
  min: number
  max: number
}

/** Kişi referansı — performans uçları bunu `{ employeeId, name }` olarak döndürür. */
export interface PersonRef {
  employeeId: string
  name: string
}

/* ---------------------------------- Metrikler --------------------------------- */

export interface Metric {
  id: string
  /** Addan otomatik türetilir, sonradan değiştirilemez. */
  code: string
  name: string
  description: string | null
  category: MetricCategory
  scale: MetricScale
  /** Oransal ağırlık. Yüzde DEĞİL: kategori içindeki toplama göre anlam kazanır. */
  weight: number
  departmentId: string | null
  isRequired: boolean
  /** `false` → arşivlenmiş. Geçmiş değerlendirmelerdeki puanları korunur. */
  isActive: boolean
  sortOrder: number
  /** Girdi sınırları — ölçekten türetilir ama backend'den hazır gelir. */
  range: ScaleRange
  /** Biliniyorsa ölçek kilidi önceden gösterilir; bilinmiyorsa backend 400 ile söyler. */
  usageCount?: number
}

export interface MetricInput {
  name: string
  description?: string | null
  category: MetricCategory
  scale: MetricScale
  weight: number
  departmentId?: string | null
  isRequired: boolean
  sortOrder?: number
}

export interface MetricFilters {
  departmentId?: string
  category?: MetricCategory
  includeArchived?: boolean
}

/* ------------------------------- Puanlama ayarı ------------------------------- */

/**
 * Backend düz alanlarla çalışır. PUT kısmi güncelleme kabul ETMEZ — mevcut
 * ayar GET ile alınıp üzerinde değişiklik yapılarak tamamı gönderilir.
 */
export interface ScoringConfigInput {
  /** Hedef ayağının payı. `metricWeightPercent` ile toplamı 100 olmalı (aksi 400). */
  goalWeightPercent: number
  metricWeightPercent: number

  /** Oransal. 0 verilen kategori hesaba hiç girmez. */
  technicalWeight: number
  behavioralWeight: number
  leadershipWeight: number
  deliveryWeight: number
  customWeight: number

  /** Değerlendirici katsayıları. Varsayılan 0.5 / 2.0 / 1.5 / 1.0 / 1.0. */
  selfReviewWeight: number
  managerReviewWeight: number
  teamLeadReviewWeight: number
  peerReviewWeight: number
  upwardReviewWeight: number

  minReviewsForValidScore: number
  allowSelfOnlyScore: boolean

  /** critical < improvement < recognition < promotion olmalı (aksi 400). */
  criticalThreshold: number
  improvementThreshold: number
  recognitionThreshold: number
  promotionThreshold: number
  /** Terfi önerisi için eşiğin kaç dönem üst üste aşılması gerektiği. */
  promotionConsecutivePeriods: number
}

export interface ScoringConfig extends ScoringConfigInput {
  id?: string
  /** Her kaydetme yeni sürüm üretir; üzerine yazılmaz. */
  version: number
  createdAt?: string | null
  createdByName?: string | null
  /** Geçmiş listesinde yürürlükteki sürümü işaretlemek için. */
  isCurrent?: boolean
}

/** Öneri uçlarının döndürdüğü eşik özeti. */
export interface Thresholds {
  critical: number
  improvement: number
  recognition: number
  promotion: number
}

/* ------------------------------------ Ekipler ---------------------------------- */

export interface TeamMember {
  /** Üyelik kaydının kimliği (çalışan kimliği değil). */
  id: string
  employeeId: string
  /** Organizasyon servisi adı bilmeyebilir; arayüz çalışan listesinden çözer. */
  employeeName?: string | null
  roleInTeam: string | null
  joinedOn: string
  /** Doluysa eski üye. Kayıt silinmez, yalnızca ayrılma tarihi yazılır. */
  leftOn: string | null
  isLead: boolean
}

export interface Team {
  id: string
  name: string
  description: string | null
  departmentId: string
  /** Opsiyonel. Atanınca kişi otomatik olarak ekibe üye olur. */
  leadEmployeeId: string | null
  isActive: boolean
  memberCount: number
  members?: TeamMember[]
}

export interface CreateTeamInput {
  name: string
  description?: string | null
  departmentId: string
}

/** Backend: UpdateTeamRequest(Name?, Description?, IsActive?) — departman sonradan değişmez. */
export interface UpdateTeamInput {
  name?: string
  description?: string | null
  isActive?: boolean
}

export interface TeamFilters {
  departmentId?: string
  leadEmployeeId?: string
  includeInactive?: boolean
  /** Liste yanıtına üyeleri de koyar; verilmezse `members: null` gelir. */
  includeMembers?: boolean
}

export interface AddMemberInput {
  employeeId: string
  roleInTeam?: string
  joinedOn?: string
}

/* ------------------------------------ Dönemler --------------------------------- */

export interface ReviewCycle {
  id: string
  name: string
  year: number
  period: CyclePeriod
  status: CycleStatus
  startDate: string
  endDate: string
  /** Kapanışta sabitlenen nihai puan sayısı — yalnızca kapanmış dönemde. */
  finalizedEmployeeCount?: number | null
  closedAt?: string | null
}

export interface CreateCycleInput {
  name: string
  year: number
  period: CyclePeriod
  startDate: string
  endDate: string
}

export interface CycleFilters {
  year?: number
  status?: CycleStatus
}

export interface ReadinessEmployee {
  employeeId: string
  name?: string | null
  /** `true` → kapanışta puanı geçici kalacak. */
  isProvisional: boolean
  /** Neden geçici kalacağı — backend dolu gönderir. */
  reason: string | null
  reviewCount?: number
  pendingReviews?: number
}

export interface CycleReadiness {
  cycleId: string
  readyCount: number
  provisionalCount: number
  pendingReviewTotal: number
  employees: ReadinessEmployee[]
}

export interface CycleStatusResult extends ReviewCycle {
  finalizedEmployeeCount?: number | null
}

/* ------------------------------------ Hedefler --------------------------------- */

export interface Goal {
  id: string
  cycleId: string
  employeeId: string
  title: string
  description: string | null
  /** Oransal — çalışanın dönemdeki hedef toplamına göre anlam kazanır. */
  weight: number
  targetValue: number | null
  currentValue: number | null
  unit: string | null
  status: GoalStatus
  /** İsteğe bağlı son tarih (yyyy-MM-dd); dönem başlangıcı ile bitişi arasında. */
  dueDate: string | null
}

export interface CreateGoalInput {
  cycleId: string
  employeeId: string
  title: string
  description?: string
  weight: number
  targetValue?: number
  unit?: string
  /** yyyy-MM-dd; düzenlemede null gönderilirse son tarih temizlenir. */
  dueDate?: string | null
}

/** Hedef düzenleme — dönem ve çalışan değişmez; kapalı dönemde 409. */
export type UpdateGoalInput = Omit<CreateGoalInput, 'cycleId' | 'employeeId'>

export interface GoalProgressInput {
  currentValue?: number
  status?: GoalStatus
}

export interface GoalFilters {
  employeeId?: string
  cycleId?: string
}

/* -------------------------------- Değerlendirme -------------------------------- */

export interface MetricScoreInput {
  metricId: string
  /** Metriğin kendi ölçeğinde — sınırlar `metric.range`'den. */
  value: number
  comment?: string
}

export interface Review {
  id: string
  cycleId: string
  employeeId: string
  reviewerEmployeeId: string | null
  type: ReviewType
  /** Gönderildiyse dolu. Gönderilmiş değerlendirme kilitlidir. */
  submittedAt: string | null
  isSubmitted: boolean
  strengths: string | null
  improvements: string | null
  comments: string | null
  /** `GET /reviews/{id}` doldurur; listede boş gelebilir. */
  scores: MetricScoreInput[]
  createdAt?: string | null
}

export interface CreateReviewInput {
  cycleId: string
  employeeId: string
  reviewerEmployeeId: string
  type: ReviewType
}

export interface SubmitReviewInput {
  scores: MetricScoreInput[]
  strengths?: string
  improvements?: string
  comments?: string
}

export interface ReviewFilters {
  employeeId?: string
  cycleId?: string
}

/* -------------------------------- Puan dökümü --------------------------------- */

export interface MetricBreakdown {
  metricId: string
  code: string
  name: string
  /** 0–100'e normalize edilmiş, değerlendirici katsayılarıyla ağırlıklı ortalama. */
  normalizedScore: number | null
  /** Kategori içindeki oransal ağırlık. */
  weight: number
  reviewCount: number
}

export interface CategoryBreakdown {
  category: MetricCategory
  score: number | null
  /** Puanlama ayarındaki oransal kategori ağırlığı. */
  weight: number
  metrics: MetricBreakdown[]
}

export interface GoalBreakdown {
  goalId: string
  title: string
  weight: number
  /** Gerçekleşme yüzdesi (0–100, aşım kırpılmış). Sunucu alanı: GoalBreakdown.Achievement → "achievement". */
  achievement: number | null
  status?: GoalStatus | null
}

export interface ScoreResult {
  score: number | null
  /** Hedef yoksa backend 0 döndürür — "boş ayak" `breakdown.goals.length` ile anlaşılır. */
  goalScore: number | null
  metricScore: number | null
  isProvisional: boolean
  provisionalReason: string | null
  reviewCount: number
  configVersion: number
  /** Bu puanı hesaplayan ayar sürümündeki paylar (0–100). Eski backend göndermeyebilir. */
  goalWeightPercent: number | null
  metricWeightPercent: number | null
  breakdown: {
    categories: CategoryBreakdown[]
    goals: GoalBreakdown[]
  }
}

/* ------------------------------ Sürekli izleme ------------------------------- */

export interface SeriesPoint {
  /** Grafiğin x ekseni: "Hf 32", "Eyl", "Q3 2026" gibi hazır etiket. */
  bucket: string
  score: number | null
  /** Az değerlendirmeye dayanıyor — grafikte içi boş nokta / kesik çizgi. */
  isProvisional: boolean
  reviewCount?: number
}

export interface EmployeeAnalytics {
  employeeId: string
  period: AnalyticsPeriod
  periodLabel: string
  series: SeriesPoint[]
  current: number | null
  change: number | null
  trend: string | null
  trendLabel: string | null
}

export interface VsTeamPoint {
  bucket: string
  employee: number | null
  team: number | null
  isProvisional: boolean
}

export interface VsTeamAnalytics {
  employeeId: string
  teamId: string
  teamName: string | null
  periodLabel: string
  series: VsTeamPoint[]
}

export interface DistributionBucket {
  /** "60–70" gibi hazır etiket. */
  label: string
  from: number
  to: number
  count: number
}

export interface MemberScore {
  employeeId: string
  name: string | null
  score: number
  isProvisional: boolean
  rank?: number | null
}

export interface TeamComparison {
  average: number | null
  median: number | null
  spread: number | null
  /** Doluysa MUTLAKA gösterilir: sıralama tek başına anlamlı olmayabilir. */
  spreadNote: string | null
}

export interface TeamAnalytics {
  teamId: string
  teamName: string | null
  periodLabel: string
  series: SeriesPoint[]
  distribution: DistributionBucket[]
  members: MemberScore[]
  /** Puanı hiç olmayan üyeler — ayrı bölümde, gözden kaçmasın. */
  unscored: PersonRef[]
  comparison: TeamComparison
}

/* ------------------------------- Dönem sonuçları ------------------------------ */

export interface CycleScoreEntry {
  cycleId: string
  cycleName: string
  period: CyclePeriod | null
  score: number | null
  isProvisional: boolean
  /** Dönem kapandıysa `true` — puan artık değişmez. */
  isFinal: boolean
  actionLabel: string | null
}

export interface EmployeeCycleHistory {
  employeeId: string
  year: number | null
  cycles: CycleScoreEntry[]
}

export interface CycleAnalytics {
  cycleId: string
  cycleName: string
  teamId: string | null
  distribution: DistributionBucket[]
  members: MemberScore[]
  unscored: PersonRef[]
  comparison: TeamComparison
}

export interface CycleMovement {
  employeeId: string
  name: string | null
  from: number
  to: number
  delta: number
}

export interface CycleCompare {
  cycles: { cycleId: string; cycleName: string }[]
  rows: {
    employeeId: string
    name: string | null
    /** Anahtar: cycleId. Puan yoksa null. */
    scores: Record<string, number | null>
  }[]
  averages: Record<string, number | null>
  improved: CycleMovement[]
  declined: CycleMovement[]
}

/* --------------------------------- Geri bildirim -------------------------------- */

export interface Feedback {
  id: string
  /** Geri bildirim anonim DEĞİL — çalışan kimin yazdığını görür. */
  from: PersonRef
  to: PersonRef
  reason: FeedbackReason
  reasonDetail: string | null
  sentiment: FeedbackSentiment
  body: string
  metricId: string | null
  metricName: string | null
  visibleToEmployee: boolean
  isRead: boolean
  readAt: string | null
  createdAt: string
}

export interface FeedbackInput {
  toEmployeeId: string
  reason: FeedbackReason
  /** `sentiment = Constructive` ise zorunlu; backend 400 döner. */
  reasonDetail?: string
  sentiment: FeedbackSentiment
  body: string
  metricId?: string
  visibleToEmployee: boolean
}

export interface FeedbackFilters {
  asManager?: boolean
  reason?: FeedbackReason
  since?: string
}

export interface FeedbackSummary {
  total: number
  unread: number
  bySentiment: Partial<Record<FeedbackSentiment, number>>
  byReason: Partial<Record<FeedbackReason, number>>
}

/* ----------------------------------- Benim ----------------------------------- */

/** `/analytics/me` raporu ve gelen geri bildirimleri birlikte döndürür. */
export interface MyAnalytics extends EmployeeAnalytics {
  feedback: Feedback[]
  unreadCount: number
}

/* ------------------------------------ Öneriler ---------------------------------- */

export interface RecommendationFactor {
  code: string
  /** "Mevcut puan", "Süreklilik" gibi Türkçe etiket. */
  label: string
  /** İşaretli etki: pozitif öneriyi destekler, negatif zayıflatır. */
  contribution: number
  /** "84.88 puan, takdir eşiğinin (75) üzerinde." gibi açıklama. */
  explanation: string
}

export interface MlSignal {
  code: string
  label: string
  /** "2 dönem sonra ~91" gibi hazır metin. */
  value: string
  confidenceLabel: string | null
}

export interface MlLayer {
  /** "Kararı kural motoru verir; bu sinyaller yalnızca ek bilgidir." */
  note: string
  /** Doluysa ML çalışmadı — sebebi. Sessizce boş bırakılmaz. */
  skipReason: string | null
  signals: MlSignal[]
}

export interface Recommendation {
  employeeId: string
  employeeName: string | null
  action: RecommendationAction | string
  /** Backend'den Türkçe gelir: "Terfi adayı". */
  actionLabel: string
  /** 0–1. */
  confidence: number
  summary: string
  currentScore: number | null
  /** Sıralı gelir; sıra bozulmaz. */
  factors: RecommendationFactor[]
  cautions: string[]
  thresholds: Thresholds | null
  configVersion: number | null
  mlLayer: MlLayer | null
}

export interface RecommendationList {
  /** Önerileri üreten ayar sürümü — üstte gösterilir. */
  configVersion: number | null
  thresholds: Thresholds | null
  promotionConsecutivePeriods: number | null
  /** Sıralı gelir (Acil → Gelişim → Terfi → İzle → Takdir); yeniden sıralanmaz. */
  items: Recommendation[]
  /** Liste düzeyinde ML durumu (örn. ekip küçük olduğu için atlandı). */
  mlLayer: Pick<MlLayer, 'note' | 'skipReason'> | null
}
