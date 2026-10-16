import { apiFetch, apiFetchBlob, qs } from './client'
import { tx } from '@/lib/i18n'

/** Etkinlikler ve gönüllülük (engagement-service /api/events). */
const B = '/api/engagement/events'

export type EventKind = 'Social' | 'Training' | 'Volunteer' | 'Sports' | 'Other'
export type RsvpStatus = 'Going' | 'Waitlist'

export interface CompanyEvent {
  id: string
  title: string
  description: string | null
  kind: EventKind
  startsAt: string
  endsAt: string
  location: string | null
  onlineUrl: string | null
  capacity: number | null
  rsvpDeadline: string | null
  volunteerHours: number | null
  status: 'Published' | 'Cancelled'
  createdByName: string | null
  going: number
  waitlist: number
  attendees: { employeeId: string; name: string }[]
  myStatus: RsvpStatus | null
  myPosition: number | null
}

export interface EventInput {
  title: string
  description: string | null
  kind: EventKind
  startsAt: string
  endsAt: string
  location: string | null
  onlineUrl: string | null
  capacity: number | null
  rsvpDeadline: string | null
  volunteerHours: number | null
  announce?: boolean
}

export interface Attendee { id: string; employeeId: string; name: string; status: RsvpStatus; attended: boolean; createdAt: string }

export interface VolunteeringSummary {
  year: number
  totalHours: number
  volunteers: number
  events: number
  mine: number
  top: { employeeId: string; name: string; hours: number; events: number }[]
}

export const eventKindLabels = (): Record<EventKind, string> => ({
  Social: tx('Sosyal'), Training: tx('Eğitim'), Volunteer: tx('Gönüllülük'), Sports: tx('Spor'), Other: tx('Diğer'),
})

export const eventsApi = {
  list: (past: boolean, signal?: AbortSignal) => apiFetch<CompanyEvent[]>(`${B}${qs({ past: past || undefined })}`, { signal }),
  create: (b: EventInput) => apiFetch<CompanyEvent>(B, { method: 'POST', body: b }),
  update: (id: string, b: EventInput) => apiFetch<CompanyEvent>(`${B}/${id}`, { method: 'PUT', body: b }),
  cancel: (id: string) => apiFetch<{ ok: boolean }>(`${B}/${id}/cancel`, { method: 'POST' }),
  rsvp: (id: string, going: boolean) => apiFetch<{ status: RsvpStatus | null }>(`${B}/${id}/rsvp`, { method: 'POST', body: { going } }),
  attendees: (id: string, signal?: AbortSignal) => apiFetch<Attendee[]>(`${B}/${id}/attendees`, { signal }),
  attendance: (id: string, employeeIds: string[]) => apiFetch<{ attended: number }>(`${B}/${id}/attendance`, { method: 'PUT', body: { employeeIds } }),
  volunteering: (year?: number, signal?: AbortSignal) => apiFetch<VolunteeringSummary>(`${B}/volunteering${qs({ year })}`, { signal }),
  ics: (id: string) => apiFetchBlob(`${B}/${id}/ics`),
}
