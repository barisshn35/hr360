import { apiFetch, qs, requireLinked, type Paged } from './client'
import type { Assignment, CreateAssignmentInput, CreateEmployeeInput, Employee } from './types'

const BASE = '/api/employee'

export interface EmployeePageParams {
  page: number
  pageSize: number
  q?: string
  qDepartmentIds?: string
  status?: number
  sort?: 'name' | 'email' | 'hireDate' | 'status' | 'createdAt'
  dir?: 'asc' | 'desc'
}

export const employeeApi = {
  /** email verilirse tek kaydı döner - "employee" rolü SADECE bu şekilde
   * (kendi e-postasıyla) sorgulayabilir; manager+ email'siz tüm listeyi çeker. */
  list: (signal?: AbortSignal, email?: string) =>
    apiFetch<Employee[]>(`${BASE}/employees${qs({ email })}`, { signal }),

  /** Sunucu tarafı sayfalı liste (yönetici+). `qDepartmentIds`: aramayla adı eşleşen
   * departmanlar (adlar organization-service'te olduğundan istemci çözer). */
  page: (params: EmployeePageParams, signal?: AbortSignal) =>
    apiFetch<Paged<Employee>>(`${BASE}/employees${qs(params)}`, { signal }),

  get: (id: string, signal?: AbortSignal) =>
    apiFetch<Employee>(`${BASE}/employees/${id}`, { signal }),

  /** Oturumdaki kullanıcının kendi çalışan kaydı (KeycloakUserId eşlemesiyle).
   * Kayıt yoksa (ör. platform/tenant-admin hesapları) 404 döner. */
  me: async (signal?: AbortSignal) => requireLinked(await apiFetch<Employee | undefined>(`${BASE}/employees/me?optional=true`, { signal })),

  create: (input: CreateEmployeeInput) =>
    apiFetch<Employee>(`${BASE}/employees`, { method: 'POST', body: input }),

  addAssignment: (employeeId: string, input: CreateAssignmentInput) =>
    apiFetch<Assignment>(`${BASE}/employees/${employeeId}/assignments`, {
      method: 'POST',
      body: input,
    }),
}
