import { apiFetch, apiUploadFile } from './client'

/* ml-inference /ai uçları — yerel, açıklanabilir yöntemler (LLM değil). */
const BASE = '/ml/ai'

export interface CvResult {
  name: string | null
  email: string | null
  phone: string | null
  linkedin: string | null
  location: string | null
  skills: string[]
  languages: string[]
  education: string | null
  universities: string[]
  experience_years: number | null
  experience_basis: string | null
  summary: string
  text_length: number
  warnings: string[]
}
export interface BiasFinding { phrase: string; category: string; severity: 'high' | 'low'; reason: string; suggestion: string; start: number; end: number }
export interface BiasResult { score: number; findings: BiasFinding[]; verdict: string }
export interface JobDraftInput {
  title: string
  department?: string
  level: 'junior' | 'mid' | 'senior' | 'lead'
  skills: string[]
  responsibilities: string[]
  location?: string
  work_model: 'office' | 'hybrid' | 'remote'
  employment_type: 'full' | 'part' | 'contract' | 'intern'
  benefits: string[]
  company?: string
  tone: 'formal' | 'friendly'
}
export interface JobDraft { title: string; text: string; bias: BiasResult }
export interface PerfSummary {
  headline: string
  paragraph: string
  strengths: string[]
  development: string[]
  themes: Array<{ theme: string; mentions: number }>
  goal_stats: { total: number; completed: number; averageProgress: number | null }
  method: string
}
export interface MatchRow { id: string; name: string; score: number; similarity: number; skill_overlap: string[]; missing_skills: string[]; top_terms: string[] }
export interface ForecastResult {
  points: Array<{ month: string; forecast: number; low: number; high: number }>
  method: string
  trend_per_month: number
  seasonal: boolean
  peak_month: string | null
  note: string
  /** Geri test: son ayları dışarıda bırakarak ölçülen hata (%); veri azsa null. */
  backtest_mape?: number | null
  backtest_months?: number
}
export interface TrainingRec { id: string; title: string; score: number; reason: string; is_mandatory: boolean }

export const aiApi = {
  parseCv: (file: File) => apiUploadFile<CvResult>(`${BASE}/cv/parse`, file),
  parseCvText: (text: string) => apiFetch<CvResult>(`${BASE}/cv/parse-text`, { method: 'POST', body: { text } }),
  biasCheck: (text: string) => apiFetch<BiasResult>(`${BASE}/jobs/bias-check`, { method: 'POST', body: { text } }),
  jobDraft: (body: JobDraftInput) => apiFetch<JobDraft>(`${BASE}/jobs/draft`, { method: 'POST', body }),
  perfSummary: (body: {
    name: string
    score?: number | null
    previous_score?: number | null
    goals: Array<{ title: string; progress?: number | null; weight?: number | null }>
    reviews: Array<{ type?: string | null; strengths?: string | null; improvements?: string | null; comments?: string | null }>
    feedback: string[]
  }) => apiFetch<PerfSummary>(`${BASE}/performance/summary`, { method: 'POST', body }),
  match: (body: { job_title: string; job_text: string; job_skills: string[]; candidates: Array<{ id: string; name: string; text: string; skills?: string[] }> }) =>
    apiFetch<MatchRow[]>(`${BASE}/match/candidates`, { method: 'POST', body }),
  forecastLeave: (history: Array<{ month: string; days: number }>, horizon = 3) =>
    apiFetch<ForecastResult>(`${BASE}/forecast/leave`, { method: 'POST', body: { history, horizon } }),
  recommendTraining: (body: {
    position?: string | null
    skills: string[]
    goals: string[]
    development_areas: string[]
    completed_course_ids: string[]
    courses: Array<{ id: string; title: string; description?: string | null; category?: string | null; is_mandatory: boolean }>
  }) => apiFetch<TrainingRec[]>(`${BASE}/recommend/training`, { method: 'POST', body }),
}
