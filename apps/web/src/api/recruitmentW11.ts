/**
 * Dalga 11 (işe alım): çalışan önerisi (73), aday durum bağlantısı (74), Google for Jobs (75),
 * puan kartı tutarlılığı (77) ve huni analizi (78) uçları.
 */
import { apiFetch, qs } from './client'
import type { EmploymentType } from './recruitment'

const BASE = '/api/recruitment'

export interface ProgramSettings {
  referralEnabled: boolean
  referralRewardAmount: number | null
  referralRewardCurrency: string
  referralProbationDays: number
  referralRewardNote: string | null
  publishSalaryInJobPostings: boolean
}

export type ReferralCoarseStatus = 'InReview' | 'Hired' | 'Closed'
export type RewardStatus = 'None' | 'Waiting' | 'Eligible' | 'Approved' | 'Paid' | 'Forfeited' | 'NotEligible'
export type Relationship = 'FormerColleague' | 'Friend' | 'Network' | 'Other'

export interface ReferralInput {
  jobPostingId: string
  firstName: string
  lastName: string
  email: string
  phone?: string
  relationship?: Relationship
  note?: string
  candidateConsent: boolean
}

export interface MyReferral {
  id: string
  jobPostingId: string
  posting: string | null
  candidateName: string | null
  status: ReferralCoarseStatus
  statusLabel: string
  rewardStatus: RewardStatus
  rewardLabel: string
  rewardEligibleAt: string | null
  createdAt: string
}

export interface AdminReferral extends MyReferral {
  applicationId: string | null
  applicationStatus: string | null
  referrerEmployeeId: string
  referrer: string | null
  relationship: Relationship | null
  note: string | null
  candidateConsentConfirmed: boolean
  consentConfirmedAt: string
  noticeSentAt: string | null
  hiredAt: string | null
  rewardAmount: number | null
  rewardCurrency: string | null
  rewardDecidedAt: string | null
  rewardNote: string | null
}

export interface ReferralSummary {
  total: number
  inReview: number
  hired: number
  hireRate: number | null
  awaitingApproval: number
  waiting: number
  paidTotal: number
  referrers: number
}

export type RewardAction = 'approve' | 'pay' | 'forfeit' | 'not-eligible'

export interface StatusLinkRow {
  id: string
  createdAt: string
  expiresAt: string
  revokedAt: string | null
  revokedBy: 'Hr' | 'Candidate' | 'Reissued' | null
  lastViewedAt: string | null
  viewCount: number
  active: boolean
}

export interface PublicStatus {
  company: string
  posting: string | null
  status: 'Received' | 'InReview' | 'Offer' | 'Positive' | 'Negative' | 'Withdrawn' | 'Closed'
  statusLabel: string
  nextStep: string
  appliedAt: string
  updatedAt: string
  expiresAt: string
}

export interface PublicJobDetail {
  company: string
  expired: boolean
  jsonLd: Record<string, unknown> | null
  job: {
    id: string
    title: string
    description: string | null
    employmentType: EmploymentType
    department: string | null
    publishedAt: string | null
    validThrough: string | null
    location: string | null
    region: string | null
    country: string
    remoteAllowed: boolean
    salary: { min: number | null; max: number | null; currency: string; period: string } | null
  }
}

export interface CareerDetails {
  location?: string | null
  region?: string | null
  country?: string | null
  remoteAllowed: boolean
  validThrough?: string | null
  salaryMin?: number | null
  salaryMax?: number | null
  salaryCurrency?: string | null
  salaryPeriod?: string | null
}

export interface CriterionStat { key: string; label: string; count: number; mean: number | null; std: number | null; spread: number | null; highDisagreement: boolean }
export interface Consistency { criteria: CriterionStat[]; recommendationSplit: boolean; highDisagreement: boolean; raters: number }

export interface DurationStat { count: number; suppressed: boolean; medianDays: number | null; averageDays: number | null; p75Days: number | null }
export interface FunnelReport {
  from: string
  to: string
  applications: number
  stages: Array<{ stage: string; reached: number; conversionFromPrevious: number | null; timeInStage: DurationStat }>
  timeToHire: DurationStat
  sources: Array<{ source: string; applications: number | null; hired: number | null; hireRate: number | null; suppressed: boolean }>
  offers: { sent: number; accepted: number | null; declined: number | null; expired: number | null; acceptanceRate: number | null; suppressed: boolean }
  minGroup: number
}

const pub = (tenant: string) => `${BASE}/public/${encodeURIComponent(tenant)}`

export const recruitmentW11Api = {
  settings: (signal?: AbortSignal) => apiFetch<ProgramSettings>(`${BASE}/referrals/settings`, { signal }),
  saveSettings: (body: ProgramSettings) => apiFetch<ProgramSettings>(`${BASE}/referrals/settings`, { method: 'PUT', body }),
  refer: (body: ReferralInput) =>
    apiFetch<{ id: string; status: ReferralCoarseStatus; statusLabel: string; linkedToExisting: boolean }>(`${BASE}/referrals`, { method: 'POST', body }),
  myReferrals: (signal?: AbortSignal) => apiFetch<{ settings: ProgramSettings; items: MyReferral[] }>(`${BASE}/referrals/mine`, { signal }),
  referrals: (rewardStatus?: RewardStatus, signal?: AbortSignal) =>
    apiFetch<{ summary: ReferralSummary; items: AdminReferral[] }>(`${BASE}/referrals${qs({ rewardStatus })}`, { signal }),
  decideReward: (id: string, body: { action: RewardAction; amount?: number; note?: string }) =>
    apiFetch<{ id: string; rewardStatus: RewardStatus }>(`${BASE}/referrals/${id}/reward`, { method: 'POST', body }),
  evaluateRewards: () => apiFetch<{ changed: number }>(`${BASE}/referrals/evaluate`, { method: 'POST' }),

  statusLinks: (applicationId: string, signal?: AbortSignal) =>
    apiFetch<StatusLinkRow[]>(`${BASE}/applications/${applicationId}/status-links`, { signal }),
  issueStatusLink: (applicationId: string, body: { days?: number; sendEmail: boolean }) =>
    apiFetch<{ link: StatusLinkRow; path: string; emailed: boolean }>(`${BASE}/applications/${applicationId}/status-links`, { method: 'POST', body }),
  revokeStatusLinks: (applicationId: string) =>
    apiFetch<{ revoked: number }>(`${BASE}/applications/${applicationId}/status-links/revoke`, { method: 'POST' }),

  saveCareerDetails: (postingId: string, body: CareerDetails) =>
    apiFetch<CareerDetails>(`${BASE}/job-postings/${postingId}/career-details`, { method: 'PUT', body }),

  funnel: (f: { from?: string; to?: string; jobPostingId?: string }, signal?: AbortSignal) =>
    apiFetch<FunnelReport>(`${BASE}/recruitment-analytics/funnel${qs(f)}`, { signal }),
}

/** Oturumsuz uçlar. */
export const careerW11Api = {
  job: (tenant: string, id: string, signal?: AbortSignal) =>
    apiFetch<PublicJobDetail>(`${pub(tenant)}/jobs/${id}`, { signal, anonymous: true }),
  status: (tenant: string, token: string, signal?: AbortSignal) =>
    apiFetch<PublicStatus>(`${pub(tenant)}/status/${encodeURIComponent(token)}`, { signal, anonymous: true }),
  revokeStatus: (tenant: string, token: string) =>
    apiFetch<{ revoked: boolean }>(`${pub(tenant)}/status/${encodeURIComponent(token)}/revoke`, { method: 'POST', anonymous: true, noQueue: true }),
}

/** schema.org JobPosting verisini <script type="application/ld+json"> olarak yazar; temizleyici döner. */
export function injectJsonLd(data: Record<string, unknown> | null | undefined, doc: Document = document): () => void {
  if (!data) return () => {}
  const el = doc.createElement('script')
  el.type = 'application/ld+json'
  el.dataset.hr360 = 'job-posting'
  // "</script>" kaçışı: JSON içindeki "<" karakteri < olur (HTML ayrıştırıcısı bloğu erken kapatmasın).
  el.textContent = JSON.stringify(data).replace(/</g, '\\u003c')
  doc.head.appendChild(el)
  return () => el.remove()
}
