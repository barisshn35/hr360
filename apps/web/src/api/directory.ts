import { useQuery } from '@tanstack/react-query'
import { apiFetch } from './client'

/**
 * Çalışan dizini — her kimlik doğrulanmış kullanıcı çağırabilir.
 *
 * Tam çalışan listesi (`/api/employee/employees`) yönetici yetkisi ister;
 * dizin yalnızca ad döndürür (e-posta, ücret, görevlendirme yok). Ekip,
 * geri bildirim, öneri gibi ekranlar kimlikten adı buradan çözer.
 * Dizin nadiren değişir: uygulama açılışında bir kez çekilir ve uzun süre
 * önbellekte kalır.
 */

export interface DirectoryEntry {
  id: string
  firstName: string
  lastName: string
  fullName: string
}

export const directoryApi = {
  list: (signal?: AbortSignal) => apiFetch<DirectoryEntry[]>('/api/employee/employees/directory', { signal }),
}

export const directoryKey = ['employee', 'directory'] as const

export function useDirectory(enabled = true) {
  return useQuery({
    queryKey: directoryKey,
    queryFn: ({ signal }) => directoryApi.list(signal),
    enabled,
    staleTime: 10 * 60_000,
    gcTime: 60 * 60_000,
  })
}
