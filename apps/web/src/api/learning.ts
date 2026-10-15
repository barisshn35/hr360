import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

const BASE = '/api/learning'

/* ------------------------------------------------------------------ tipler */

export type CourseCategory =
  | 'Technical'
  | 'Compliance'
  | 'Leadership'
  | 'Soft'
  | 'Safety'
  | 'Other'

export type EnrollmentStatus = 'Enrolled' | 'InProgress' | 'Completed' | 'Failed' | 'Dropped'

export const courseCategoryLabels: Record<CourseCategory, string> = {
  Technical: tx('Teknik'),
  Compliance: tx('Mevzuat'),
  Leadership: tx('Liderlik'),
  Soft: tx('Kişisel gelişim'),
  Safety: tx('İş güvenliği'),
  Other: tx('Diğer'),
}

export const enrollmentStatusLabels: Record<EnrollmentStatus, string> = {
  Enrolled: tx('Kayıtlı'),
  InProgress: tx('Sürüyor'),
  Completed: tx('Tamamlandı'),
  Failed: tx('Başarısız'),
  Dropped: tx('Bırakıldı'),
}

export interface Enrollment {
  id: string
  employeeId: string
  status: EnrollmentStatus
  enrolledAt: string
  completedAt: string | null
  /** Dalga 11: atamanın son tarihi (yyyy-MM-dd). */
  dueOn?: string | null
  passed: boolean | null
  score: number | null
}

export interface Course {
  id: string
  title: string
  description: string | null
  provider: string | null
  durationHours: number
  category: CourseCategory
  isMandatory: boolean
  /** Sertifika geçerlilik süresi (ay); null = süresiz. */
  certificateValidityMonths?: number | null
  enrollments?: Enrollment[]
}

export interface Certification {
  id: string
  employeeId: string
  name: string
  issuer: string | null
  credentialId: string | null
  issuedOn: string
  expiresOn: string | null
  /** /expiring uçlarında dolar. */
  daysRemaining?: number
  expired?: boolean
}

export interface ComplianceRow {
  courseId: string
  courseTitle: string
  requiredCount: number
  completedCount: number
  compliancePercent: number
}

export interface CreateCourseInput {
  title: string
  description?: string
  provider?: string
  durationHours: number
  category: CourseCategory
  isMandatory: boolean
  certificateValidityMonths?: number | null
}

export interface CreateCertificationInput {
  employeeId: string
  name: string
  issuer?: string
  credentialId?: string
  issuedOn: string
  expiresOn?: string
}

/* ------------------------------------------------------------------ servis */

export const learningApi = {
  listCourses: (
    filters: { category?: CourseCategory; mandatoryOnly?: boolean } = {},
    signal?: AbortSignal,
  ) => apiFetch<Course[]>(`${BASE}/courses${qs(filters)}`, { signal }),

  getCourse: (id: string, signal?: AbortSignal) =>
    apiFetch<Course>(`${BASE}/courses/${id}`, { signal }),

  createCourse: (input: CreateCourseInput) =>
    apiFetch<Course>(`${BASE}/courses`, { method: 'POST', body: input }),

  updateCourse: (id: string, input: CreateCourseInput) =>
    apiFetch<Course>(`${BASE}/courses/${id}`, { method: 'PUT', body: input }),

  /** Kaydı olan eğitimde 409 (code: has_enrollments) döner; o durumda arşivlenir. */
  deleteCourse: (id: string) =>
    apiFetch<void>(`${BASE}/courses/${id}`, { method: 'DELETE' }),

  archiveCourse: (id: string) =>
    apiFetch<Course>(`${BASE}/courses/${id}/archive`, { method: 'POST' }),

  /** dueOn (yyyy-MM-dd): dalga 11 — son tarih, yalnızca yönetici+ için dikkate alınır. */
  enroll: (courseId: string, employeeId: string, dueOn?: string) =>
    apiFetch<Enrollment>(`${BASE}/courses/${courseId}/enroll`, {
      method: 'POST',
      body: { employeeId, dueOn: dueOn || undefined },
    }),

  completeEnrollment: (courseId: string, enrollmentId: string, passed: boolean, score?: number) =>
    apiFetch<Enrollment>(`${BASE}/courses/${courseId}/enrollments/${enrollmentId}/complete`, {
      method: 'POST',
      body: { passed, score },
    }),

  compliance: (signal?: AbortSignal) =>
    apiFetch<ComplianceRow[]>(`${BASE}/courses/compliance`, { signal }),

  listCertifications: (employeeId?: string, signal?: AbortSignal) =>
    apiFetch<Certification[]>(`${BASE}/certifications${qs({ employeeId })}`, { signal }),

  createCertification: (input: CreateCertificationInput) =>
    apiFetch<Certification>(`${BASE}/certifications`, { method: 'POST', body: input }),

  expiringCertifications: (withinDays = 90, signal?: AbortSignal) =>
    apiFetch<Certification[]>(`${BASE}/certifications/expiring${qs({ withinDays })}`, { signal }),
}
