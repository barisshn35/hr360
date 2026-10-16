import { useQuery } from '@tanstack/react-query'
import { apiFetch } from './client'
import { tx } from '@/lib/i18n'

/** Kahve eşleştirme (engagement-service /api/coffee). İsteğe bağlı; varsayılan kapalı. */
const B = '/api/engagement/coffee'

export type CoffeeCadence = 'Weekly' | 'Biweekly' | 'Monthly'
export interface CoffeeSettings { enabled: boolean; cadence: CoffeeCadence; dayOfWeek: number; crossDepartment: boolean }
export interface CoffeeMatch {
  id: string
  met: boolean
  createdAt: string
  period: string
  with: { employeeId: string; name: string; department: string | null; position: string | null }[]
}
export interface CoffeeMe { settings: CoffeeSettings; joined: boolean; participants: number; matches: CoffeeMatch[] }
export interface CoffeeAdmin extends CoffeeSettings {
  participants: number
  stats: { groups: number; met: number; rounds: number; lastRoundAt: string | null }
}

export const cadenceLabels = (): Record<CoffeeCadence, string> => ({ Weekly: tx('Her hafta'), Biweekly: tx('İki haftada bir'), Monthly: tx('Ayda bir') })
export const weekdayLabels = (): string[] => [tx('Pazartesi'), tx('Salı'), tx('Çarşamba'), tx('Perşembe'), tx('Cuma'), tx('Cumartesi'), tx('Pazar')]

export const coffeeApi = {
  me: (signal?: AbortSignal) => apiFetch<CoffeeMe>(`${B}/me`, { signal }),
  join: (joined: boolean) => apiFetch<{ joined: boolean }>(`${B}/me`, { method: 'PUT', body: { joined } }),
  met: (id: string, met: boolean) => apiFetch<{ met: boolean }>(`${B}/matches/${id}/met`, { method: 'POST', body: { met } }),
  settings: (signal?: AbortSignal) => apiFetch<CoffeeAdmin>(`${B}/settings`, { signal }),
  saveSettings: (b: CoffeeSettings) => apiFetch<CoffeeAdmin>(`${B}/settings`, { method: 'PUT', body: b }),
  run: () => apiFetch<{ groups: number }>(`${B}/run`, { method: 'POST' }),
}

/** Kahve eşleştirme açık mı (menü için). */
export function useCoffeeEnabled(enabled = true) {
  return useQuery({ queryKey: ['coffee', 'me'], queryFn: ({ signal }) => coffeeApi.me(signal), enabled, staleTime: 5 * 60_000, retry: false, select: (d) => d.settings.enabled })
}
