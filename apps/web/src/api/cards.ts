import { apiFetch } from './client'
import { tx } from '@/lib/i18n'

/** Grup kartı (engagement-service /api/cards). Kart teslim edilene kadar alıcıdan gizlidir. */
const B = '/api/engagement/cards'

export type CardOccasion = 'Birthday' | 'Farewell' | 'Welcome' | 'Anniversary' | 'Congrats' | 'GetWell' | 'Other'
export type CardColor = 'yellow' | 'pink' | 'blue' | 'green' | 'violet' | 'orange'

export interface GroupCard {
  id: string
  recipientEmployeeId: string
  recipientName: string
  occasion: CardOccasion
  title: string
  deliverAt: string
  deliveredAt: string | null
  createdByName: string | null
  createdAt: string
  signers: number
  signedByMe: boolean
  forMe: boolean
  canManage: boolean
}
export interface CardSignature { id: string; employeeId: string; name: string; message: string; color: CardColor; createdAt: string; mine: boolean }

export const CARD_OCCASIONS: CardOccasion[] = ['Birthday', 'Farewell', 'Welcome', 'Anniversary', 'Congrats', 'GetWell', 'Other']
export const CARD_COLORS: CardColor[] = ['yellow', 'pink', 'blue', 'green', 'violet', 'orange']

export const cardOccasionLabels = (): Record<CardOccasion, string> => ({
  Birthday: tx('Doğum günü'), Farewell: tx('Veda'), Welcome: tx('Hoş geldin'), Anniversary: tx('İş yıl dönümü'),
  Congrats: tx('Tebrik'), GetWell: tx('Geçmiş olsun'), Other: tx('Diğer'),
})

export const cardsApi = {
  list: (signal?: AbortSignal) => apiFetch<GroupCard[]>(B, { signal }),
  get: (id: string, signal?: AbortSignal) => apiFetch<{ card: GroupCard; signatures: CardSignature[] }>(`${B}/${id}`, { signal }),
  create: (b: { recipientEmployeeId: string; occasion: CardOccasion; title: string; deliverAt: string; notifyTeam: boolean }) => apiFetch<GroupCard>(B, { method: 'POST', body: b }),
  sign: (id: string, message: string, color: CardColor) => apiFetch<{ id: string }>(`${B}/${id}/sign`, { method: 'PUT', body: { message, color } }),
  unsign: (id: string) => apiFetch<void>(`${B}/${id}/sign`, { method: 'DELETE' }),
  deliver: (id: string) => apiFetch<{ id: string }>(`${B}/${id}/deliver`, { method: 'POST' }),
  remove: (id: string) => apiFetch<void>(`${B}/${id}`, { method: 'DELETE' }),
}
