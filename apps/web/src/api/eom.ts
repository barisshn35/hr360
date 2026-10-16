import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiFetch } from './client'
import { tx, appLocale } from '@/lib/i18n'

/**
 * "Ayın çalışanı" oylaması (engagement-service /api/eom). İsteğe bağlı modül: İK ayarından açılır.
 * Oylar kişinin rolüne göre katsayılı sayılır; görünürlük kuralı dönem açılırken dondurulur.
 */
const B = '/api/engagement/eom'

export type EomScope = 'TopLevel' | 'EachDepartment' | 'TwoStage'
export type EomProcess = 'Direct' | 'Nominate'
export type EomVisibility = 'WinnerOnly' | 'RankingPublic' | 'HrSeesVotes' | 'RevealAfter'
export type EomRevealTo = 'Department' | 'HrAndHead'
export type EomPeriodStatus = 'Scheduled' | 'Nominating' | 'Voting' | 'Stage2' | 'Tie' | 'Closed' | 'Cancelled'
export type EomUnitStatus = 'Pending' | 'Open' | 'Closed' | 'Tie' | 'NoVotes'

export interface EomPerson {
  id: string
  name: string
  position: string | null
  department: string | null
  photoVersion: number | null
}

export interface EomPeriod {
  id: string
  key: string
  status: EomPeriodStatus
  nominationStartsAt: string | null
  votingStartsAt: string
  stage1EndsAt: string
  stage2EndsAt: string | null
  announcedAt: string | null
  scope: EomScope
  process: EomProcess
  visibility: EomVisibility
  revealTo: EomRevealTo
  allowComment: boolean
  finalistCount: number
}

export interface EomMyUnit {
  id: string
  stage: 1 | 2
  name: string
  departmentId: string
  direct: boolean
  isFinal: boolean
  status: EomUnitStatus
  action: 'vote' | 'nominate' | null
  memberCount: number
  candidates: EomPerson[]
  myChoice: string | null
  myComment: string | null
}

export interface EomMe {
  enabled: boolean
  isHr: boolean
  period?: EomPeriod | null
  next?: { key: string; startsAt: string } | null
  units?: EomMyUnit[]
  myWeight?: number | null
}

export interface EomWinnerUnit {
  id: string
  stage: 1 | 2
  name: string
  departmentId: string
  direct: boolean
  isFinal: boolean
  isMine: boolean
  winner: EomPerson
}

export interface EomWinners {
  enabled: boolean
  period?: EomPeriod | null
  units?: EomWinnerUnit[]
}

export interface EomResultUnit {
  id: string
  stage: 1 | 2
  name: string
  direct: boolean
  isFinal: boolean
  status: EomUnitStatus
  winner: EomPerson | null
  tied: EomPerson[] | null
  participation: { voted: number; members: number; nominated: number } | null
  ranking: { person: EomPerson; score: number; votes: number }[] | null
  votes: { voter: EomPerson; nominee: EomPerson; weight: number; comment: string | null }[] | null
  comments: { nominee: EomPerson | null; text: string }[] | null
}

export interface EomLeaderRow {
  person: EomPerson
  rank: number
  wins: number
  subWins: number
  lastWonKey: string
}

export interface EomLeaderboard {
  enabled: boolean
  periods?: number
  company?: EomLeaderRow[]
  departments?: { departmentId: string; name: string; isMine: boolean; periods: number; rows: EomLeaderRow[] }[]
}

export interface EomSettings {
  enabled: boolean
  cadence: 'Monthly' | 'Quarterly'
  scope: EomScope
  process: EomProcess
  visibility: EomVisibility
  revealTo: EomRevealTo
  startDay: number
  openHour: number
  nominationDays: number
  finalistCount: number
  votingDays: number
  stage2Days: number
  reminderDaysBefore: number
  cooldownPeriods: number
  weightEmployee: number
  weightManager: number
  weightHr: number
  allowComment: boolean
  updatedAt?: string
  updatedByName?: string | null
}

export interface EomSettingsView {
  settings: EomSettings
  active: EomPeriod | null
  next: { key: string; startsAt: string; votingStartsAt: string; endsAt: string }
}

export const eomApi = {
  me: (signal?: AbortSignal) => apiFetch<EomMe>(`${B}/me`, { signal }),
  winners: (signal?: AbortSignal) => apiFetch<EomWinners>(`${B}/winners`, { signal }),
  history: (signal?: AbortSignal) => apiFetch<{ period: EomPeriod; winners: { unitId: string; name: string; winner: EomPerson }[] }[]>(`${B}/history`, { signal }),
  leaderboard: (signal?: AbortSignal) => apiFetch<EomLeaderboard>(`${B}/leaderboard`, { signal }),
  results: (id: string, signal?: AbortSignal) => apiFetch<{ period: EomPeriod; units: EomResultUnit[] }>(`${B}/periods/${id}/results`, { signal }),
  vote: (unitId: string, employeeId: string, comment?: string) => apiFetch<void>(`${B}/vote`, { method: 'POST', body: { unitId, employeeId, comment } }),
  nominate: (unitId: string, employeeId: string) => apiFetch<void>(`${B}/nominate`, { method: 'POST', body: { unitId, employeeId } }),
  settings: (signal?: AbortSignal) => apiFetch<EomSettingsView>(`${B}/settings`, { signal }),
  saveSettings: (s: EomSettings) => apiFetch<EomSettings>(`${B}/settings`, { method: 'PUT', body: s }),
  periods: (signal?: AbortSignal) => apiFetch<{ period: EomPeriod; unitCount: number; tieCount: number }[]>(`${B}/periods`, { signal }),
  startNow: () => apiFetch<{ id: string }>(`${B}/periods/start-now`, { method: 'POST' }),
  skipPhase: (id: string) => apiFetch<void>(`${B}/periods/${id}/skip-phase`, { method: 'POST' }),
  cancel: (id: string) => apiFetch<void>(`${B}/periods/${id}/cancel`, { method: 'POST' }),
  resolve: (unitId: string, employeeId: string) => apiFetch<void>(`${B}/units/${unitId}/resolve`, { method: 'POST', body: { employeeId } }),
}

export const eomKeys = {
  all: ['eom'] as const,
  me: ['eom', 'me'] as const,
  winners: ['eom', 'winners'] as const,
  history: ['eom', 'history'] as const,
  leaderboard: ['eom', 'leaderboard'] as const,
  results: (id: string) => ['eom', 'results', id] as const,
  settings: ['eom', 'settings'] as const,
  periods: ['eom', 'periods'] as const,
}

export const useEomMe = () => useQuery({ queryKey: eomKeys.me, queryFn: ({ signal }) => eomApi.me(signal) })
export const useEomWinners = (enabled = true) =>
  useQuery({ queryKey: eomKeys.winners, queryFn: ({ signal }) => eomApi.winners(signal), enabled, staleTime: 5 * 60_000 })

/** Değişiklikten sonra tüm "ayın çalışanı" sorguları tazelenir. */
export function useEomMutation<A extends unknown[], R>(fn: (...args: A) => Promise<R>) {
  const qc = useQueryClient()
  return useMutation({ mutationFn: (args: A) => fn(...args), onSuccess: () => void qc.invalidateQueries({ queryKey: eomKeys.all }) })
}

/** Dönem adı: "2026-10" → "Ekim 2026", "2026-Q3" → "2026 3. çeyrek", "M-…" → "Özel oylama (16.10.2026)". */
export function periodLabel(key: string): string {
  const q = /^(\d{4})-Q([1-4])$/.exec(key)
  if (q) return tx('{0} {1}. çeyrek', [q[1], q[2]])
  const m = /^(\d{4})-(\d{2})$/.exec(key)
  if (m) {
    const s = new Date(Number(m[1]), Number(m[2]) - 1, 1).toLocaleDateString(appLocale, { month: 'long', year: 'numeric' })
    return s.charAt(0).toLocaleUpperCase(appLocale) + s.slice(1)
  }
  const d = /^M-(\d{4})(\d{2})(\d{2})/.exec(key)
  if (d) return tx('Özel oylama ({0})', [`${d[3]}.${d[2]}.${d[1]}`])
  return key
}

/** Birim adı: iki aşamalıda ana departmana doğrudan bağlılar ayrıca belirtilir. */
export const unitLabel = (u: { name: string; direct?: boolean }) => (u.direct ? tx('{0} (doğrudan bağlı)', [u.name]) : u.name)

/** Oy verirken gösterilen görünürlük kuralı (sunucudaki e-posta metniyle aynı anlam). */
export function visibilityNotice(p: Pick<EomPeriod, 'visibility' | 'revealTo'>): string {
  switch (p.visibility) {
    case 'HrSeesVotes': return tx('Bu oylamada İK, kimin kime oy verdiğini görebilir.')
    case 'RevealAfter':
      return p.revealTo === 'Department'
        ? tx('Oylama sürerken oylar gizlidir; sonuç açıklanınca kimin kime oy verdiği birimdeki herkese açılır.')
        : tx('Oylama sürerken oylar gizlidir; sonuç açıklanınca kimin kime oy verdiğini İK ve departman yöneticisi görebilir.')
    case 'RankingPublic': return tx('Oylar gizlidir: kimin kime oy verdiğini kimse göremez. Sonuçta puan sıralaması birimdeki herkese açılır.')
    default: return tx('Oylar gizlidir: kimin kime oy verdiğini kimse göremez. Yalnızca kazanan açıklanır.')
  }
}
