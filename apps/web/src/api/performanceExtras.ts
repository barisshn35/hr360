/** G12 uçları: 9-kutu (potansiyel, kalibrasyon) ve dönem şablonları. */
import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'
import type { CyclePeriod, ReviewCycle } from './performance'

const BASE = '/api/performance'

export interface NineBoxEmployee {
  employeeId: string
  name: string
  department: string | null
  positionTitle: string | null
  score: number | null
  isProvisional: boolean
  performanceBand: number | null
  potential: number | null
  potentialNote: string | null
  potentialRatedBy: string | null
  potentialPublished: boolean
  computedCell: number | null
  cell: number | null
  override: { performanceBand: number; potentialBand: number; reason: string; overriddenBy: string; createdAt: string } | null
}

export interface NineBoxGrid {
  notice: string
  cycle: { id: string; name: string; status: string }
  thresholds: { low: number; high: number }
  canCalibrate: boolean
  cells: { cell: number; label: string; performanceBand: number; potentialBand: number; employees: NineBoxEmployee[] }[]
  unplaced: NineBoxEmployee[]
}

export interface CycleConfig {
  scale: { min: number; max: number; labels?: string[] | null }
  sections: { title: string; weight: number; questions: { text: string }[] }[]
  goalWeightPercent?: number | null
}

export interface CycleTemplate {
  id: string
  name: string
  description: string | null
  period: CyclePeriod
  durationDays: number
  config: CycleConfig | null
  createdBy: string | null
  createdAt: string
}

export const performanceExtrasApi = {
  nineBox: (cycleId: string, signal?: AbortSignal) => apiFetch<NineBoxGrid>(`${BASE}/nine-box${qs({ cycleId })}`, { signal }),
  setPotential: (body: { cycleId: string; employeeId: string; rating: number; note?: string }) =>
    apiFetch<{ rating: number }>(`${BASE}/nine-box/potential`, { method: 'PUT', body }),
  publishPotential: (body: { cycleId: string; employeeId: string; published: boolean }) =>
    apiFetch<{ publishedToEmployee: boolean }>(`${BASE}/nine-box/potential/publish`, { method: 'POST', body }),
  myPotential: (cycleId: string, signal?: AbortSignal) =>
    apiFetch<{ published: boolean; rating?: number; note?: string | null }>(`${BASE}/nine-box/my-potential${qs({ cycleId })}`, { signal }),
  override: (body: { cycleId: string; employeeId: string; performanceBand: number; potentialBand: number; reason: string }) =>
    apiFetch<{ cell: number; label: string }>(`${BASE}/nine-box/override`, { method: 'POST', body }),

  templates: (signal?: AbortSignal) => apiFetch<CycleTemplate[]>(`${BASE}/review-cycles/templates`, { signal }),
  createTemplate: (body: { name: string; description?: string; period: CyclePeriod; durationDays: number; config: CycleConfig }) =>
    apiFetch<CycleTemplate>(`${BASE}/review-cycles/templates`, { method: 'POST', body }),
  deleteTemplate: (id: string) => apiFetch<void>(`${BASE}/review-cycles/templates/${id}`, { method: 'DELETE' }),
  saveAsTemplate: (cycleId: string, body: { name: string; description?: string }) =>
    apiFetch<CycleTemplate>(`${BASE}/review-cycles/${cycleId}/save-as-template`, { method: 'POST', body }),
  createFromTemplate: (body: { templateId: string; name?: string; year: number; period?: CyclePeriod; startDate: string; endDate?: string }) =>
    apiFetch<ReviewCycle>(`${BASE}/review-cycles/from-template`, { method: 'POST', body }),
}

export const potentialLabels: Record<number, string> = { 1: tx('Düşük'), 2: tx('Orta'), 3: tx('Yüksek') }
