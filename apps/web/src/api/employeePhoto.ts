import { useQuery, useQueryClient } from '@tanstack/react-query'
import { apiFetch, apiFetchBlob, apiUploadFile } from './client'
import { directoryKey, useDirectory } from './directory'
import { qk } from './queries'

/**
 * Çalışan fotoğrafı. Fotoğraf herkese açık bir adresten verilmez (kişisel veri): yetkili istekle alınıp
 * nesne adresine çevrilir. Adres sürümlüdür (photoVersion): fotoğraf değişmedikçe yeniden indirilmez.
 */
const path = (id: string) => `/api/employee/employees/${id}/photo`

export const photoApi = {
  upload: (id: string, file: File) => apiUploadFile<{ photoVersion: number }>(path(id), file, 'file', 'PUT'),
  remove: (id: string) => apiFetch<void>(path(id), { method: 'DELETE' }),
}

/** Fotoğraf nesne adresi; fotoğraf yoksa ya da sürüm bilinmiyorsa null. */
export function usePhotoUrl(id: string | null | undefined, version: number | null | undefined) {
  const q = useQuery({
    queryKey: ['employee', 'photo', id, version],
    queryFn: async ({ signal }) => {
      const blob = await apiFetchBlob(`${path(id!)}?v=${version}`, signal)
      return blob ? URL.createObjectURL(blob) : null
    },
    enabled: !!id && !!version,
    staleTime: Infinity,
    gcTime: 60 * 60_000,
    retry: false,
  })
  return version ? (q.data ?? null) : null
}

/** Dizinden (her rol okuyabilir, önbellekte) sürümü bulup fotoğrafı getirir. */
export function useEmployeePhoto(id: string | null | undefined) {
  const dir = useDirectory(!!id)
  const version = id ? dir.data?.find((e) => e.id === id)?.photoVersion : null
  return usePhotoUrl(id, version)
}

/** Yükleme/kaldırma sonrası: dizin, çalışan kaydı ve profil sürümü tazelenir. */
export function useInvalidatePhoto() {
  const qc = useQueryClient()
  return () => {
    void qc.invalidateQueries({ queryKey: directoryKey })
    void qc.invalidateQueries({ queryKey: qk.employees })
    void qc.invalidateQueries({ queryKey: ['profile', 'me'] })
  }
}
