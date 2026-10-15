import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  timeshiftApi,
  type AddShiftTeamMemberInput,
  type CreateShiftPatternInput,
  type CreateShiftTeamInput,
  type TeamRoster,
} from './timeshift'

/**
 * Vardiya motoru sorgu anahtarları ve hook'ları.
 *
 * Hepsi `['timeshift', 'engine', …]` altında: bir üye başka ekibe geçince
 * backend eski üyeliği kendisi kapattığı için üyelik değişikliğinde tüm
 * ekipler ve tüm takvimler birlikte tazelenir.
 */
export const qke = {
  root: ['timeshift', 'engine'] as const,
  patterns: ['timeshift', 'engine', 'patterns'] as const,
  teamsRoot: ['timeshift', 'engine', 'teams'] as const,
  teams: (departmentId?: string) => ['timeshift', 'engine', 'teams', departmentId ?? 'all'] as const,
  rosterRoot: ['timeshift', 'engine', 'roster'] as const,
  roster: (teamId: string, from: string, to: string) =>
    ['timeshift', 'engine', 'roster', teamId, from, to] as const,
}

export function useShiftPatterns(enabled = true) {
  return useQuery({
    queryKey: qke.patterns,
    queryFn: ({ signal }) => timeshiftApi.listPatterns(signal),
    enabled,
  })
}

export function useShiftTeams(departmentId?: string) {
  return useQuery({
    queryKey: qke.teams(departmentId),
    queryFn: ({ signal }) => timeshiftApi.listTeams(departmentId, signal),
  })
}

/**
 * Bir ya da birden çok ekibin takvimi. "Tüm ekipler" görünümünde 7/24 kapsama
 * birlikte hesaplanır; ay/hafta değişirken önceki veri ekranda kalır.
 */
export function useTeamRosters(teamIds: string[], from: string, to: string) {
  return useQueries({
    queries: teamIds.map((teamId) => ({
      queryKey: qke.roster(teamId, from, to),
      queryFn: ({ signal }: { signal: AbortSignal }) => timeshiftApi.teamRoster(teamId, from, to, signal),
      placeholderData: (previous: TeamRoster | undefined) => previous,
    })),
  })
}

/**
 * Bir yılın takvimi, ekip başına 12 aylık parça hâlinde.
 *
 * Tek bir 365 günlük istek yerine ay ay çekilir: backend'in izin verdiği en
 * uzun aralık belli değil, aylık parçalar ay görünümüyle aynı önbellek
 * anahtarını paylaşır ve biri hata verirse yalnızca o ay yeniden denenir.
 * Sonuç sırası: ekip0 ocak…aralık, ekip1 ocak…aralık, …
 */
export function useTeamYearRosters(teamIds: string[], year: number) {
  const months = Array.from({ length: 12 }, (_, m) => {
    const from = `${year}-${String(m + 1).padStart(2, '0')}-01`
    const to = new Date(Date.UTC(year, m + 1, 0)).toISOString().slice(0, 10)
    return { from, to }
  })
  return useQueries({
    queries: teamIds.flatMap((teamId) =>
      months.map(({ from, to }) => ({
        queryKey: qke.roster(teamId, from, to),
        queryFn: ({ signal }: { signal: AbortSignal }) => timeshiftApi.teamRoster(teamId, from, to, signal),
        staleTime: 5 * 60_000,
      })),
    ),
  })
}

/* ------------------------------- Mutasyonlar -------------------------------- */

export function useCreateShiftPattern() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateShiftPatternInput) => timeshiftApi.createPattern(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: qke.patterns }),
  })
}

export function useDeleteShiftPattern() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => timeshiftApi.deletePattern(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: qke.patterns }),
  })
}

export function useCreateShiftTeam() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateShiftTeamInput) => timeshiftApi.createTeam(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: qke.teamsRoot }),
  })
}

export function useAddShiftTeamMember() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ teamId, input }: { teamId: string; input: AddShiftTeamMemberInput }) =>
      timeshiftApi.addTeamMember(teamId, input),
    onSuccess: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: qke.teamsRoot }),
        qc.invalidateQueries({ queryKey: qke.rosterRoot }),
      ]),
  })
}

export function useRemoveShiftTeamMember() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ teamId, memberId }: { teamId: string; memberId: string }) =>
      timeshiftApi.removeTeamMember(teamId, memberId),
    onSuccess: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: qke.teamsRoot }),
        qc.invalidateQueries({ queryKey: qke.rosterRoot }),
      ]),
  })
}
