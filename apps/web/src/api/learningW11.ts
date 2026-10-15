/**
 * Dalga 11 eğitim uçları: kariyer yolları (madde 83) ve zorunlu eğitim / İSG eğitimi son tarih
 * takibi (madde 84). Hazırlık oranı ve eğitim önerisi yalnızca bilgi amaçlıdır.
 */
import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

const BASE = '/api/learning'

export interface CareerRequirement { competencyId: string; competency: string; isActive: boolean; requiredLevel: number }
export interface CareerStep { id: string; stepOrder: number; positionTitle: string; description: string | null; minMonths: number | null; requirements: CareerRequirement[] }
export interface CareerPath { id: string; name: string; description: string | null; isActive: boolean; updatedAt: string; steps: CareerStep[] }

export interface CareerPathInput {
  name: string
  description?: string | null
  isActive?: boolean
  steps: Array<{ positionTitle: string; description?: string | null; minMonths?: number | null; requirements: Array<{ competencyId: string; requiredLevel: number }> }>
}

export interface StepGap { competencyId: string; competency: string; required: number; current: number | null; gap: number }
export interface StepCourse {
  courseId: string; title: string; isMandatory: boolean; durationHours: number; enrollmentStatus: string | null
  closes: Array<{ competencyId: string; competency: string; from: number; to: number }>
}
export interface StepProgress {
  id: string; stepOrder: number; positionTitle: string; description: string | null; minMonths: number | null
  readiness: number; totalGap: number; items: StepGap[]; courses: StepCourse[] | null
}
export interface PathProgress {
  pathId: string; name: string; description: string | null
  currentStepId: string | null; currentStep: StepProgress | null; target: StepProgress | null
  steps: Array<{ id: string; stepOrder: number; positionTitle: string; readiness: number }>
}
export interface CareerProgress {
  notice: string; employeeId: string; name: string; positionTitle: string | null
  matched: PathProgress[]; available: Array<{ id: string; name: string }>
}

export type DueKind = 'Training' | 'Certificate' | 'Osh'
export type DueState = 'Overdue' | 'DueSoon' | 'Ok'
export interface DueItem {
  kind: DueKind; sourceId: string; employeeId: string; title: string; dueOn: string; daysLeft: number
  state: DueState; mandatory: boolean; status: string | null; courseId?: string | null
  employee?: string; department?: string | null
}
export interface OverdueView {
  departments: Array<{ id: string; name: string }>
  summary: {
    overdue: number; dueSoon: number
    byKind: Array<{ kind: DueKind; overdue: number; dueSoon: number }>
    byDepartment: Array<{ department: string; overdue: number; dueSoon: number }>
  }
  items: DueItem[]
}

export const dueKindLabels: Record<DueKind, string> = {
  Training: tx('Eğitim'),
  Certificate: tx('Sertifika'),
  Osh: tx('İSG eğitimi'),
}

/** Kalan güne göre rozet: gecikme ve son gün kırmızı, 7 gün ve altı turuncu. */
export function dueBadge(daysLeft: number): { tone: 'danger' | 'warning' | 'info'; label: string } {
  if (daysLeft < 0) return { tone: 'danger', label: tx('{0} gün gecikti', [Math.abs(daysLeft)]) }
  if (daysLeft === 0) return { tone: 'danger', label: tx('Bugün son gün') }
  if (daysLeft <= 7) return { tone: 'warning', label: tx('{0} gün kaldı', [daysLeft]) }
  return { tone: 'info', label: tx('{0} gün kaldı', [daysLeft]) }
}

export const learningW11Api = {
  careerPaths: (includeInactive = false, signal?: AbortSignal) =>
    apiFetch<CareerPath[]>(`${BASE}/career-paths${qs({ includeInactive: includeInactive || undefined })}`, { signal }),
  createCareerPath: (body: CareerPathInput) => apiFetch<CareerPath>(`${BASE}/career-paths`, { method: 'POST', body }),
  updateCareerPath: (id: string, body: CareerPathInput) => apiFetch<CareerPath>(`${BASE}/career-paths/${id}`, { method: 'PUT', body }),
  deleteCareerPath: (id: string) => apiFetch<void>(`${BASE}/career-paths/${id}`, { method: 'DELETE' }),
  careerProgress: (employee: string, sel: { pathId?: string; stepId?: string } = {}, signal?: AbortSignal) =>
    apiFetch<CareerProgress>(`${BASE}/career-paths/progress/${employee}${qs(sel)}`, { signal }),

  myDue: (signal?: AbortSignal) => apiFetch<{ linked: boolean; items: DueItem[] }>(`${BASE}/learning-due/me`, { signal }),
  overdue: (departmentId?: string, withinDays?: number, signal?: AbortSignal) =>
    apiFetch<OverdueView>(`${BASE}/learning-due/overdue${qs({ departmentId, withinDays })}`, { signal }),
  /** "Bir çalışana" seçicisi: İK tüm aktif çalışanlar, yönetici yalnızca başı olduğu departmanlar (kendisi hariç). */
  assignable: (signal?: AbortSignal) =>
    apiFetch<Array<{ id: string; fullName: string; department: string | null }>>(`${BASE}/learning-due/assignable`, { signal }),
  assign: (body: { courseId: string; employeeIds?: string[]; departmentId?: string; dueOn?: string | null }) =>
    apiFetch<{ created: number; updated: number; skipped: number }>(`${BASE}/learning-due/assign`, { method: 'POST', body }),
  /** Çalışanın kendini kaydı (son tarih yok). */
  enrollSelf: (courseId: string, employeeId: string) =>
    apiFetch<unknown>(`${BASE}/courses/${courseId}/enroll`, { method: 'POST', body: { employeeId } }),
}
