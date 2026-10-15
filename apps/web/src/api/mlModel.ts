import { apiFetch } from './client'

/**
 * Devir riski modelinin şeffaflık uçları (G5): model kartı, küresel özellik önemi ve veri kayması.
 * Gateway /ml/model/* yollarını ml-inference'a iletir; uçlar Keycloak jetonu ve İK rolü ister.
 * Kişi bazlı tahmin (/ml/predict, /ml/explain) gateway'de kapalıdır - governance üzerinden yapılır.
 */
const BASE = '/ml/model'

export interface ModelFeature {
  name: string
  label: string
  unit: string
  description: string
  min: number
  max: number
}

export interface ModelMetrics {
  auc: number
  accuracy: number
  n: number
  positive_rate: number
}

export interface ModelCard {
  model: string
  loaded: boolean
  serving: boolean
  blocked_reason: string | null
  version: string | null
  trained_at: string | null
  training_source: 'synthetic' | 'provided' | null
  training_rows: number | null
  data_window: { start: string; end: string } | null
  data_window_note: string | null
  /** Dalga 10: kiracı izniyle toplanan veriyle eğitildiyse kaynağı (kiracı adı değil özeti). */
  provenance?: { kind: 'tenant-consented'; tenant_ref: string; consent_at: string; validation: 'time-based'; train_snapshot: string; eval_snapshot: string } | null
  evaluation_source: string | null
  evaluation_rows: number | null
  metrics: ModelMetrics | null
  algorithm: Record<string, string | number>
  features: ModelFeature[]
  excluded_attributes: Array<{ key: string; label: string }>
  excluded_check: { passed: boolean; violations: Array<{ feature: string; attribute: string }> }
  promotion: { promote: boolean; recommended?: boolean; reason: string; auc_delta: number | null; compared_with_version: string | null } | null
  /** Kalibrasyon özeti (eski sürümlerde null: ham olasılık). */
  calibration: CalibrationSummary | null
  /** Model kartının güncelliği: eğitimden itibaren `validity_days` gün geçerli. */
  freshness?: ModelFreshness
  /** Yeni aday yayına insan onayıyla mı girer (champion/challenger). */
  approval_required?: boolean
  drift_thresholds: { moderate: number; significant: number }
  promotion_tolerance: number
  intended_use: string
  limitations: string[]
  fairness: { note: string; residual_risk: string }
  kvkk: string
}

export interface ModelFreshness {
  validity_days: number
  trained_on?: string
  expires_on: string | null
  age_days: number | null
  days_left?: number
  stale: boolean | null
  data_range: { start: string; end: string } | null
}

export type CalibrationMethod = 'none' | 'sigmoid' | 'isotonic'

export interface CalibrationSummary {
  method: CalibrationMethod
  brier: number
  brier_raw: number
  ece: number
  ece_raw: number
  recommended_threshold: number
  default_threshold: number
}

export interface ReliabilityBin {
  lo: number
  hi: number
  count: number
  mean_predicted: number | null
  observed_rate: number | null
  suppressed: boolean
}

export interface ThresholdRow {
  threshold: number
  flag_rate: number
  precision: number | null
  recall: number | null
  false_positive_rate: number | null
  f1: number | null
}

export interface CalibrationReport extends Partial<CalibrationSummary> {
  available: boolean
  version: string | null
  note?: string
  selection?: Record<string, number>
  calibration_rows?: number
  evaluation_rows?: number
  reliability?: ReliabilityBin[]
  reliability_raw?: ReliabilityBin[]
  thresholds?: ThresholdRow[]
}

export interface ModelVersionRow {
  version: string
  status: 'champion' | 'challenger' | 'rejected' | 'retired'
  was_champion: boolean
  trained_at: string | null
  source: string | null
  compared_with_version: string | null
  comparison_current: boolean
  recommended: boolean
  reason: string | null
  serving: boolean
  metrics: { auc?: number; accuracy?: number; brier?: number; ece?: number; positive_rate?: number }
}

export interface ModelVersions {
  champion: string | null
  approval_required: boolean
  versions: ModelVersionRow[]
}

export interface ModelSettings {
  model: string
  riskThreshold: number
  isDefault: boolean
  updatedBy: string | null
  updatedAt: string | null
}

export interface FairnessGroupRow {
  group: string
  n: number
  flag_rate: number | null
  tpr?: number | null
  fpr?: number | null
  disparity_ratio: number | null
  tpr_ratio: number | null
  four_fifths_warning: boolean
}

export interface FairnessReport {
  rows: number
  threshold: number
  flag_rate: number | null
  labels_used: boolean
  min_group: number
  four_fifths: number
  model_version: string | null
  computed_at: string
  note: string
  attributes: Array<{ attribute: string; label: string; groups: FairnessGroupRow[]; hidden_groups: number; min_ratio: number | null; comparable: boolean }>
  absent_attributes: Array<{ attribute: string; label: string }>
  warnings: Array<{ attribute: string; group: string; disparity_ratio: number }>
}

export interface FairnessResult {
  available: boolean
  id?: string
  createdAt?: string
  createdBy?: string | null
  skipped?: number
  report?: FairnessReport
}

export interface FeatureImportance {
  model: string
  version: string
  method: 'mean_abs_shap' | 'impurity'
  sample_rows: number
  features: Array<{ feature: string; label: string; value: number; share: number }>
}

export type DriftLevel = 'stable' | 'moderate' | 'significant'

export interface DriftReport {
  available: boolean
  rows?: number
  min_rows?: number
  source?: 'batch' | 'recent-predictions'
  model_version?: string
  computed_at?: string
  max_psi?: number
  status?: DriftLevel
  significant_features?: string[]
  recommendation?: string
  /** Tahmin (skor) dağılımının eğitim referansına göre PSI'ı. */
  prediction_psi?: number
  prediction_level?: DriftLevel
  features?: Array<{ feature: string; label: string; psi: number; level: DriftLevel; reference_mean: number | null; batch_mean: number | null }>
}

export interface RetrainResult {
  candidate_version: string
  promoted: boolean
  /** Aday karşılaştırmayı geçti, yayına alma onay bekliyor (challenger). */
  awaiting_approval?: boolean
  decision: { promote: boolean; reason: string; auc_delta: number | null }
  candidate: ModelMetrics
  current: ModelMetrics | null
  serving_version: string
  /** Deneme eğitimi miydi (yayımlanmaz)? */
  dry_run?: boolean
  /** Çağıran yalnızca deneme eğitimi yapabilir (platform yöneticisi değil). */
  dry_run_only?: boolean
  notice?: string
}

export const mlModelApi = {
  card: (signal?: AbortSignal) => apiFetch<ModelCard>(`${BASE}/card`, { signal }),
  importance: (signal?: AbortSignal) => apiFetch<FeatureImportance>(`${BASE}/importance`, { signal }),
  /** Son tahmin girdilerinin toplu histogramından (kiracı başına) PSI. */
  recentDrift: (signal?: AbortSignal) => apiFetch<DriftReport>(`${BASE}/drift/recent`, { signal }),
  /** Son kaydedilen kayma ölçümü (toplu satır gönderimi ya da son tahminler). */
  lastDrift: (signal?: AbortSignal) => apiFetch<DriftReport>(`${BASE}/drift/last`, { signal }),
  /**
   * Sentetik veriyle yeni tohumla yeniden eğitim (İK). Aday, mevcut modelden kötü değilse yayımlanır;
   * yayımlama yalnızca platform yöneticisindedir — şirket İK'sı için sunucu her zaman deneme eğitimi yapar.
   */
  retrainSynthetic: (dryRun: boolean) =>
    // governance üzerinden: eğitim özeti denetim kaydına yazılır.
    apiFetch<RetrainResult>('/api/governance/model/retrain', { method: 'POST', body: { dryRun } }),
  /** Kalibrasyon raporu: Brier, ECE, güvenilirlik eğrisi, eşik tablosu. */
  calibration: (signal?: AbortSignal) => apiFetch<CalibrationReport>(`${BASE}/calibration`, { signal }),
  /** Champion (yayında), aday (onay bekliyor) ve eski sürümler. */
  versions: (signal?: AbortSignal) => apiFetch<ModelVersions>(`${BASE}/versions`, { signal }),
  /** Şirketin inceleme eşiği (governance; değişiklik denetim kaydına yazılır). */
  settings: (signal?: AbortSignal) => apiFetch<ModelSettings>('/api/governance/model/settings', { signal }),
  saveThreshold: (riskThreshold: number) =>
    apiFetch<ModelSettings>('/api/governance/model/settings', { method: 'PUT', body: { riskThreshold } }),
  /** Onay bekleyen adayı yayına alır / eski bir sürüme döner (platform yöneticisi; denetim kaydına yazılır). */
  promote: (version: string, note?: string) =>
    apiFetch<{ serving_version: string; previous_version: string | null }>(`/api/governance/model/promote/${version}`, { method: 'POST', body: { note } }),
  rollback: (version: string, note?: string) =>
    apiFetch<{ serving_version: string; previous_version: string | null }>(`/api/governance/model/rollback/${version}`, { method: 'POST', body: { note } }),
  /** Adillik denetimi: CSV (employee_id + 6 özellik + isteğe bağlı label). */
  runFairness: (csv: string) => apiFetch<FairnessResult>('/api/governance/model/fairness', { method: 'POST', body: { csv } }),
  latestFairness: (signal?: AbortSignal) => apiFetch<FairnessResult>('/api/governance/model/fairness/latest', { signal }),
}
