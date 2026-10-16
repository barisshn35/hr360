import { useQuery } from '@tanstack/react-query'
import { apiFetch, qs } from './client'

/** Takdir puanları ve ödül kataloğu (engagement-service /api/recognition). İsteğe bağlı; varsayılan kapalı. */
const B = '/api/engagement/recognition'

export interface RecognitionSettings { enabled: boolean; monthlyAllowance: number; maxPerKudos: number }
export interface PointsMe extends RecognitionSettings {
  balance: number
  givenThisMonth: number
  remaining: number
  history: { id: string; delta: number; reason: string; label: string; createdAt: string }[]
}
export interface Reward { id: string; title: string; description: string | null; cost: number; stock: number | null; active: boolean }
export type RedemptionStatus = 'Pending' | 'Fulfilled' | 'Rejected'
export interface Redemption {
  id: string
  rewardTitle: string
  employeeId?: string
  name?: string
  cost: number
  status: RedemptionStatus
  note: string | null
  createdAt: string
  decidedAt: string | null
}

export const recognitionApi = {
  settings: (signal?: AbortSignal) => apiFetch<RecognitionSettings>(`${B}/settings`, { signal }),
  saveSettings: (b: RecognitionSettings) => apiFetch<RecognitionSettings>(`${B}/settings`, { method: 'PUT', body: b }),
  me: (signal?: AbortSignal) => apiFetch<PointsMe>(`${B}/me`, { signal }),
  rewards: (all = false, signal?: AbortSignal) => apiFetch<Reward[]>(`${B}/rewards${qs({ all: all || undefined })}`, { signal }),
  createReward: (b: Omit<Reward, 'id'>) => apiFetch<Reward>(`${B}/rewards`, { method: 'POST', body: b }),
  updateReward: (id: string, b: Omit<Reward, 'id'>) => apiFetch<Reward>(`${B}/rewards/${id}`, { method: 'PUT', body: b }),
  redeem: (id: string) => apiFetch<{ id: string; status: RedemptionStatus; balance: number }>(`${B}/rewards/${id}/redeem`, { method: 'POST' }),
  myRedemptions: (signal?: AbortSignal) => apiFetch<Redemption[]>(`${B}/redemptions/me`, { signal }),
  redemptions: (status?: RedemptionStatus, signal?: AbortSignal) => apiFetch<Redemption[]>(`${B}/redemptions${qs({ status })}`, { signal }),
  decide: (id: string, fulfilled: boolean, note: string | null) => apiFetch<{ id: string; status: RedemptionStatus }>(`${B}/redemptions/${id}/decide`, { method: 'POST', body: { fulfilled, note } }),
  grant: (employeeId: string, points: number, note: string) => apiFetch<{ balance: number }>(`${B}/grant`, { method: 'POST', body: { employeeId, points, note } }),
}

/** Takdir puanları açık mı (menü ve takdir penceresi için). */
export function useRecognitionSettings(enabled = true) {
  return useQuery({ queryKey: ['recognition', 'settings'], queryFn: ({ signal }) => recognitionApi.settings(signal), enabled, staleTime: 5 * 60_000, retry: false })
}
