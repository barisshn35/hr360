import { apiFetch, qs } from './client'

/* ============================== sohbet botu (Dalga 5e) ==============================
 * BG20 bot yönetimi (komut aç/kapat, kullanım sayıları, son hatalar), B12 nabız planı,
 * BG13 sohbetten gelen hassas işlemin doğrulanması, B11 yıldönümü kutlaması izni.
 * ==================================================================================== */

const BASE = '/api/governance'

export interface ChatFeatureDef { key: string; label: string }
export interface ChatUsageRow { feature: string; outcome: string; count: number }
export interface ChatStats {
  days: number
  total: number
  linkedUsers: number
  byFeature: ChatUsageRow[]
  daily: { day: string; count: number }[]
  errors: { at: string; feature: string; message: string }[]
  lastError: string | null
}
export interface ChatPulse {
  id: string
  question: string
  sendAt: string
  closesAt: string | null
  status: 'Scheduled' | 'Sent' | 'Closed'
  sentCount: number
  createdAt: string
  responses: number
  hidden: boolean
  threshold: number
  distribution: number[] | null
  average: number | null
}
export interface StepUpPreview { kind: string; label: string; summary: string | null; expiresAt: string }
export interface StepUpOtp { id: string; summary: string; otp: string; expiresAt: string }

export const chatbotApi = {
  features: (signal?: AbortSignal) => apiFetch<ChatFeatureDef[]>(`${BASE}/chat-admin/features`, { signal }),
  setDisabled: (appId: string, disabled: string[]) =>
    apiFetch<{ disabled: string[] }>(`${BASE}/chat-admin/apps/${appId}/features`, { method: 'PUT', body: { disabled } }),
  stats: (appId: string, days = 30, signal?: AbortSignal) => apiFetch<ChatStats>(`${BASE}/chat-admin/apps/${appId}/stats${qs({ days })}`, { signal }),
  runJobs: () => apiFetch<Record<string, number>>(`${BASE}/chat-admin/jobs/run`, { method: 'POST' }),
  pulses: (signal?: AbortSignal) => apiFetch<ChatPulse[]>(`${BASE}/chat-admin/pulses`, { signal }),
  createPulse: (body: { question: string; sendAt?: string | null; closesAt?: string | null }) =>
    apiFetch<{ id: string }>(`${BASE}/chat-admin/pulses`, { method: 'POST', body }),
  closePulse: (id: string) => apiFetch<void>(`${BASE}/chat-admin/pulses/${id}/close`, { method: 'POST' }),
  stepUpPreview: (code: string, signal?: AbortSignal) => apiFetch<StepUpPreview>(`${BASE}/chat/stepup/${encodeURIComponent(code)}`, { signal }),
  stepUpConfirm: (code: string) => apiFetch<{ confirmed: boolean }>(`${BASE}/chat/stepup`, { method: 'POST', body: { code } }),
  stepUpOtps: (signal?: AbortSignal) => apiFetch<StepUpOtp[]>(`${BASE}/chat/stepup`, { signal }),
  optIns: (signal?: AbortSignal) => apiFetch<{ showAnniversary: boolean; showBirthday: boolean; linked: boolean }>(`${BASE}/chat/optins/me`, { signal }),
  setAnniversary: (showAnniversary: boolean) => apiFetch<{ showAnniversary: boolean }>(`${BASE}/chat/optins/me`, { method: 'PUT', body: { showAnniversary } }),
}
