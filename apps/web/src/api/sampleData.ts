import { apiFetch } from './client'

/** Örnek veri (tenant-service). Şirket yöneticisi kendi şirketi için; platform yöneticisi şirket kısa adıyla. */
export interface SampleDataStatus { hasSample: boolean; realEmployees: number; sampleEmployees: number; canFill: boolean }

const mine = '/api/tenant/my-tenant/sample-data'
const bySlug = (slug: string) => `/api/tenant/tenants/by-slug/${encodeURIComponent(slug)}/sample-data`

export const sampleDataApi = {
  status: (slug?: string, signal?: AbortSignal) => apiFetch<SampleDataStatus>(slug ? bySlug(slug) : mine, { signal }),
  fill: (slug?: string) => apiFetch<SampleDataStatus>(slug ? bySlug(slug) : mine, { method: 'POST' }),
  remove: (slug?: string) => apiFetch<SampleDataStatus>(slug ? bySlug(slug) : mine, { method: 'DELETE' }),
}
