import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

/** Fikir kutusu (engagement-service /api/ideas). Anonim fikirde yazar hiçbir yanıtta dönmez. */
const B = '/api/engagement/ideas'

export type IdeaStatus = 'New' | 'UnderReview' | 'Planned' | 'Done' | 'Declined'

export interface Idea {
  id: string
  title: string
  body: string
  category: string
  status: IdeaStatus
  response: string | null
  respondedByName: string | null
  respondedAt: string | null
  createdAt: string
  anonymous: boolean
  authorName: string | null
  authorEmployeeId: string | null
  mine: boolean
  votes: number
  votedByMe: boolean
  commentCount: number
}

export interface IdeaComment { id: string; authorName: string; authorEmployeeId: string | null; body: string; createdAt: string; mine: boolean }

export const ideaStatusLabels = (): Record<IdeaStatus, string> => ({
  New: tx('Yeni'), UnderReview: tx('İnceleniyor'), Planned: tx('Planlandı'), Done: tx('Uygulandı'), Declined: tx('Uygun görülmedi'),
})

export const ideasApi = {
  meta: (signal?: AbortSignal) => apiFetch<{ statuses: IdeaStatus[]; categories: string[] }>(`${B}/meta`, { signal }),
  list: (p: { status?: string; sort?: 'top' | 'new' }, signal?: AbortSignal) => apiFetch<Idea[]>(`${B}${qs(p)}`, { signal }),
  get: (id: string, signal?: AbortSignal) => apiFetch<{ idea: Idea; comments: IdeaComment[] }>(`${B}/${id}`, { signal }),
  create: (b: { title: string; body: string; category: string; anonymous: boolean }) => apiFetch<Idea>(B, { method: 'POST', body: b }),
  vote: (id: string) => apiFetch<{ votes: number; votedByMe: boolean }>(`${B}/${id}/vote`, { method: 'POST' }),
  comment: (id: string, body: string) => apiFetch<IdeaComment>(`${B}/${id}/comments`, { method: 'POST', body: { body } }),
  deleteComment: (id: string) => apiFetch<void>(`${B}/comments/${id}`, { method: 'DELETE' }),
  setStatus: (id: string, status: IdeaStatus, response: string | null) => apiFetch<Idea>(`${B}/${id}/status`, { method: 'PUT', body: { status, response } }),
  remove: (id: string) => apiFetch<void>(`${B}/${id}`, { method: 'DELETE' }),
}
