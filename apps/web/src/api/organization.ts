import { apiFetch } from './client'
import type {
  Company,
  CreateCompanyInput,
  CreateDepartmentInput,
  CreateDepartmentLinkInput,
  Department,
  DepartmentLink,
} from './types'

const BASE = '/api/organization'

export const organizationApi = {
  listCompanies: (signal?: AbortSignal) => apiFetch<Company[]>(`${BASE}/companies`, { signal }),

  getCompany: (id: string, signal?: AbortSignal) =>
    apiFetch<Company>(`${BASE}/companies/${id}`, { signal }),

  createCompany: (input: CreateCompanyInput) =>
    apiFetch<Company>(`${BASE}/companies`, { method: 'POST', body: input }),

  listDepartments: (companyId?: string, signal?: AbortSignal) =>
    apiFetch<Department[]>(
      companyId ? `${BASE}/departments?companyId=${encodeURIComponent(companyId)}` : `${BASE}/departments`,
      { signal },
    ),

  createDepartment: (input: CreateDepartmentInput) =>
    apiFetch<Department>(`${BASE}/departments`, { method: 'POST', body: input }),

  /** PUT tam kayit: `headEmployeeId` her zaman gonderilmeli (null = basi kaldir). */
  updateDepartment: (id: string, name: string, headEmployeeId: string | null) =>
    apiFetch<Department>(`${BASE}/departments/${id}`, {
      method: 'PUT',
      body: { name, headEmployeeId },
    }),

  /**
   * Departmani siler. Icinde ekip/alt departman/atanmis calisan varsa
   * backend 409 doner - mesaj kullaniciya oldugu gibi gosterilmeli,
   * "once su kayitlarin tasinmasi gerekiyor" seklinde acik bir uyari.
   */
  deleteDepartment: (id: string) =>
    apiFetch<void>(`${BASE}/departments/${id}`, { method: 'DELETE' }),

  /** Matris bağları (noktalı çizgi raporlama); `companyId` ile en az bir ucu o şirkette olanlar. */
  listDepartmentLinks: (companyId?: string, signal?: AbortSignal) =>
    apiFetch<DepartmentLink[]>(
      companyId ? `${BASE}/department-links?companyId=${encodeURIComponent(companyId)}` : `${BASE}/department-links`,
      { signal },
    ),

  /** Yalnızca İK/kiracı yöneticisi. Aynı bağ varsa 409, geçersizse 400 (Türkçe ileti). */
  createDepartmentLink: (input: CreateDepartmentLinkInput) =>
    apiFetch<DepartmentLink>(`${BASE}/department-links`, { method: 'POST', body: input }),

  deleteDepartmentLink: (id: string) => apiFetch<void>(`${BASE}/department-links/${id}`, { method: 'DELETE' }),
}
