/**
 * Dalga 5c eğitim uçları: yetkinlik matrisi ve öneri (Y19), eğitim içeriği / sınav / SCORM 1.2 /
 * sertifika (Y20). Sınav uçları doğru cevap DÖNMEZ; puanlama sunucudadır.
 */
import { apiFetch, apiUploadFile, qs } from './client'
import { getValidToken } from '@/auth/keycloak'
import { env } from '@/lib/env'
import { lang, tx } from '@/lib/i18n'
import { ApiError, platformTenantHeaders } from './client'

const BASE = '/api/learning'

/* ------------------------------------------------------------------ Y19 */

export interface Competency {
  id: string
  name: string
  description: string | null
  category: string | null
  isActive: boolean
}

export interface RoleProfileEntry {
  id: string
  competencyId: string
  positionTitle: string | null
  departmentId: string | null
  requiredLevel: number
}

export type AssessmentSource = 'Self' | 'Manager' | 'Hr'

export interface GapItem {
  competencyId: string
  competency: string
  category: string | null
  required: number
  current: number | null
  gap: number
  basis: 'Position' | 'Department'
  assessedAt: string | null
  source: AssessmentSource | null
  assessedBy: string | null
}

export interface GapView {
  employeeId: string
  name: string
  positionTitle: string | null
  department: string | null
  totalGap: number
  items: GapItem[]
  extra: { competencyId: string; competency: string; current: number; assessedAt: string; source: AssessmentSource }[]
}

export interface TeamHeatmap {
  departments: { id: string; name: string }[]
  competencies: { id: string; name: string; category: string | null }[]
  rows: {
    employeeId: string
    name: string
    positionTitle: string | null
    department: string | null
    totalGap: number
    cells: { competencyId: string; required: number; current: number | null; gap: number }[]
  }[]
}

export interface Recommendations {
  notice: string
  employeeId: string
  items: {
    courseId: string
    title: string
    category: string
    durationHours: number
    isMandatory: boolean
    maxGap: number
    totalClosed: number
    enrollmentStatus: string | null
    closes: { competencyId: string; competency: string; gap: number; from: number; to: number }[]
  }[]
}

export interface CourseTag {
  id: string
  courseId: string
  competencyId: string
  targetLevel: number
}

/* ------------------------------------------------------------------ Y20 */

export type ModuleKind = 'Video' | 'Text' | 'Quiz' | 'Scorm'

export interface CourseModule {
  id: string
  courseId: string
  position: number
  title: string
  kind: ModuleKind
  videoUrl: string | null
  textBody: string | null
  passMarkPercent: number | null
  maxAttempts: number | null
  scormPackageId: string | null
  questionCount: number
}

export interface ModuleInput {
  title: string
  kind: ModuleKind
  position?: number
  videoUrl?: string
  textBody?: string
  passMarkPercent?: number
  maxAttempts?: number | null
  scormPackageId?: string | null
}

export interface QuizOption {
  id: string
  text: string
}

/** Öğrenen görünümü: doğru cevap alanı yok. */
export interface LearnerQuiz {
  moduleId: string
  title: string
  passMarkPercent: number
  maxAttempts: number | null
  attemptsUsed: number
  attemptsLeft: number | null
  passed: boolean
  attempts: { attemptNo: number; scorePercent: number; passed: boolean; submittedAt: string }[]
  questions: { id: string; text: string; kind: 'Single' | 'Multiple'; options: QuizOption[] }[]
}

export interface QuizQuestionInput {
  text: string
  kind: 'Single' | 'Multiple'
  options: QuizOption[]
  correct: string[]
}

export interface AttemptResult {
  attemptNo: number
  scorePercent: number
  passed: boolean
  correctCount: number
  total: number
  passMarkPercent: number
  attemptsLeft: number | null
  perQuestion: { questionId: string; correct: boolean }[]
  enrollmentStatus: string
  certificateId: string | null
}

export type ModuleStatus = 'NotStarted' | 'InProgress' | 'Completed' | 'Failed'

export interface CourseProgress {
  enrollmentId: string
  employeeId: string
  status: string
  score: number | null
  enrolledAt: string
  completedAt: string | null
  completedModules: number
  totalModules: number
  modules: {
    moduleId: string
    title: string
    kind: ModuleKind
    status: ModuleStatus
    score: number | null
    completedAt: string | null
    attempts: number
    lessonStatus: string | null
  }[]
  certificate: { id: string; verificationCode: string; issuedOn: string; expiresOn: string | null } | null
}

export interface ScormPackage {
  id: string
  title: string
  entryPoint: string
  fileCount: number
  totalBytes: number
  uploadedBy: string | null
  createdAt: string
}

export interface ScormRuntimeState {
  lessonStatus: string
  scoreRaw: number | null
  suspendData: string
  lessonLocation: string
  entry: 'ab-initio' | 'resume'
  updatedAt: string | null
}

export interface ScormLaunch {
  packageId: string
  launchUrl: string
  enrollmentStatus: string
  runtime: ScormRuntimeState
  studentId: string
  studentName: string
}

export interface CertificateView {
  id: string
  name: string
  issuer: string | null
  issuedOn: string
  expiresOn: string | null
  verificationCode: string | null
  credentialId: string | null
  employeeName: string
  courseTitle: string | null
  durationHours: number | null
  score: number | null
  company: string | null
}

/** apiUploadFile ek alan göndermediği için başlıklı yükleme burada. */
async function uploadScorm(file: File, title?: string): Promise<ScormPackage> {
  if (!title) return apiUploadFile<ScormPackage>(`${BASE}/scorm/packages`, file)
  const token = await getValidToken()
  if (!token) throw new ApiError(401, tx('Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.'))
  const fd = new FormData()
  fd.append('file', file)
  fd.append('title', title)
  const res = await fetch(`${env.apiBase}${BASE}/scorm/packages`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'X-HR360-Lang': lang, ...platformTenantHeaders() },
    body: fd,
  })
  const text = await res.text()
  let data: unknown
  try {
    data = text ? JSON.parse(text) : undefined
  } catch {
    data = undefined
  }
  if (!res.ok) throw new ApiError(res.status, (data as { message?: string } | undefined)?.message ?? tx('Yükleme başarısız'), data)
  return data as ScormPackage
}

export const learningContentApi = {
  /* yetkinlik */
  competencies: (signal?: AbortSignal) => apiFetch<Competency[]>(`${BASE}/competencies`, { signal }),
  createCompetency: (body: { name: string; description?: string; category?: string }) =>
    apiFetch<Competency>(`${BASE}/competencies`, { method: 'POST', body }),
  updateCompetency: (id: string, body: { name: string; description?: string | null; category?: string | null; isActive?: boolean }) =>
    apiFetch<Competency>(`${BASE}/competencies/${id}`, { method: 'PUT', body }),
  deleteCompetency: (id: string) => apiFetch<void>(`${BASE}/competencies/${id}`, { method: 'DELETE' }),
  roleProfiles: (signal?: AbortSignal) => apiFetch<RoleProfileEntry[]>(`${BASE}/competencies/role-profiles`, { signal }),
  upsertRoleProfile: (body: { competencyId: string; positionTitle?: string; departmentId?: string; requiredLevel: number }) =>
    apiFetch<RoleProfileEntry>(`${BASE}/competencies/role-profiles`, { method: 'PUT', body }),
  deleteRoleProfile: (id: string) => apiFetch<void>(`${BASE}/competencies/role-profiles/${id}`, { method: 'DELETE' }),
  orgOptions: (signal?: AbortSignal) =>
    apiFetch<{ departments: { id: string; name: string }[]; positions: string[] }>(`${BASE}/competencies/org-options`, { signal }),
  assess: (body: { employeeId: string; competencyId: string; level: number; note?: string }) =>
    apiFetch<{ id: string; source: AssessmentSource }>(`${BASE}/competencies/assessments`, { method: 'POST', body }),
  gaps: (employee: string, signal?: AbortSignal) => apiFetch<GapView>(`${BASE}/competencies/gaps/${employee}`, { signal }),
  team: (departmentId?: string, signal?: AbortSignal) =>
    apiFetch<TeamHeatmap>(`${BASE}/competencies/team${qs({ departmentId })}`, { signal }),
  recommendations: (employee: string, signal?: AbortSignal) =>
    apiFetch<Recommendations>(`${BASE}/competencies/recommendations/${employee}`, { signal }),
  courseTags: (courseId: string, signal?: AbortSignal) => apiFetch<CourseTag[]>(`${BASE}/courses/${courseId}/competencies`, { signal }),
  saveCourseTags: (courseId: string, tags: { competencyId: string; targetLevel: number }[]) =>
    apiFetch<CourseTag[]>(`${BASE}/courses/${courseId}/competencies`, { method: 'PUT', body: tags }),

  /* içerik */
  modules: (courseId: string, signal?: AbortSignal) => apiFetch<CourseModule[]>(`${BASE}/courses/${courseId}/modules`, { signal }),
  createModule: (courseId: string, body: ModuleInput) =>
    apiFetch<CourseModule>(`${BASE}/courses/${courseId}/modules`, { method: 'POST', body }),
  updateModule: (courseId: string, id: string, body: ModuleInput) =>
    apiFetch<CourseModule>(`${BASE}/courses/${courseId}/modules/${id}`, { method: 'PUT', body }),
  deleteModule: (courseId: string, id: string) => apiFetch<void>(`${BASE}/courses/${courseId}/modules/${id}`, { method: 'DELETE' }),
  quiz: (courseId: string, moduleId: string, signal?: AbortSignal) =>
    apiFetch<LearnerQuiz>(`${BASE}/courses/${courseId}/modules/${moduleId}/quiz`, { signal }),
  answerKey: (courseId: string, moduleId: string, signal?: AbortSignal) =>
    apiFetch<(QuizQuestionInput & { id: string })[]>(`${BASE}/courses/${courseId}/modules/${moduleId}/quiz/answer-key`, { signal }),
  saveQuiz: (courseId: string, moduleId: string, questions: QuizQuestionInput[]) =>
    apiFetch<{ questionCount: number }>(`${BASE}/courses/${courseId}/modules/${moduleId}/quiz`, { method: 'PUT', body: { questions } }),
  attempt: (courseId: string, moduleId: string, answers: Record<string, string[]>) =>
    apiFetch<AttemptResult>(`${BASE}/courses/${courseId}/modules/${moduleId}/quiz/attempts`, { method: 'POST', body: { answers } }),
  completeModule: (courseId: string, moduleId: string) =>
    apiFetch<{ status: string; enrollmentStatus: string; certificateId: string | null }>(
      `${BASE}/courses/${courseId}/modules/${moduleId}/complete`,
      { method: 'POST' },
    ),
  progress: (courseId: string, employeeId?: string, signal?: AbortSignal) =>
    apiFetch<{ enrolled: boolean; progress?: CourseProgress }>(`${BASE}/courses/${courseId}/progress${qs({ employeeId })}`, { signal }),
  results: (courseId: string, signal?: AbortSignal) =>
    apiFetch<{ name: string; progress: CourseProgress }[]>(`${BASE}/courses/${courseId}/results`, { signal }),
  courseSettings: (courseId: string, certificateValidityMonths: number | null) =>
    apiFetch<{ certificateValidityMonths: number | null }>(`${BASE}/courses/${courseId}/settings`, {
      method: 'PUT',
      body: { certificateValidityMonths },
    }),

  /* SCORM */
  scormPackages: (signal?: AbortSignal) => apiFetch<ScormPackage[]>(`${BASE}/scorm/packages`, { signal }),
  uploadScorm,
  deleteScorm: (id: string) => apiFetch<void>(`${BASE}/scorm/packages/${id}`, { method: 'DELETE' }),
  launch: (courseId: string, moduleId: string) =>
    apiFetch<ScormLaunch>(`${BASE}/scorm/launch`, { method: 'POST', body: { courseId, moduleId } }),
  saveRuntime: (courseId: string, moduleId: string, values: Record<string, string>, finish: boolean) =>
    apiFetch<{ runtime: ScormRuntimeState; enrollmentStatus: string; certificateId: string | null }>(`${BASE}/scorm/runtime`, {
      method: 'PUT',
      body: { courseId, moduleId, values, finish },
      noQueue: true,
    }),

  /* sertifika */
  certificate: (id: string, signal?: AbortSignal) => apiFetch<CertificateView>(`${BASE}/certifications/${id}/certificate`, { signal }),
  verify: (code: string, signal?: AbortSignal) =>
    apiFetch<{ valid: boolean; name: string; issuedOn: string; expiresOn: string | null; holder: string }>(
      `${BASE}/certifications/verify/${encodeURIComponent(code)}`,
      { signal },
    ),
}

export const competencyLevelLabels: Record<number, string> = {
  1: tx('1 · Başlangıç'),
  2: tx('2 · Gelişen'),
  3: tx('3 · Yetkin'),
  4: tx('4 · İleri'),
  5: tx('5 · Uzman'),
}

export const moduleKindLabels: Record<ModuleKind, string> = {
  Video: tx('Video'),
  Text: tx('Metin'),
  Quiz: tx('Sınav'),
  Scorm: tx('SCORM paketi'),
}
