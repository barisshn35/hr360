/**
 * Dalga 12: kullanıcı başına arayüz tercihleri (kayıtlı liste görünümleri, ana panel düzeni,
 * "Yenilikler" okundu bilgisi).
 *
 * Sunucuda saklanır (notification-service `ui-prefs`): kişi hangi cihazdan girerse girsin aynı
 * düzeni görür. Sunucu yoksa ya da kiracı seçilmemişse (409) tarayıcıda tutulur; depolama kapalıysa
 * (gizli pencere) yalnızca bu oturum için bellekte kalır. KVKK: yalnızca kişinin kendi arayüz ayarı.
 */
import { useCallback, useMemo, useSyncExternalStore } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { apiFetch } from '@/api/client'

const BASE = '/api/notification/notifications/ui-prefs'
const LOCAL_PREFIX = 'hr360.pref.'
export const uiPrefsKey = ['ui-prefs'] as const

type PrefMap = Record<string, unknown>

export const uiPrefsApi = {
  all: (signal?: AbortSignal) => apiFetch<PrefMap>(BASE, { signal }),
  put: (key: string, value: unknown) => apiFetch<unknown>(`${BASE}/${encodeURIComponent(key)}`, { method: 'PUT', body: value }),
  remove: (key: string) => apiFetch<void>(`${BASE}/${encodeURIComponent(key)}`, { method: 'DELETE' }),
}

function readLocal(key: string): unknown {
  try {
    const raw = localStorage.getItem(LOCAL_PREFIX + key)
    return raw == null ? undefined : (JSON.parse(raw) as unknown)
  } catch {
    return undefined
  }
}

function writeLocal(key: string, value: unknown) {
  try {
    if (value == null) localStorage.removeItem(LOCAL_PREFIX + key)
    else localStorage.setItem(LOCAL_PREFIX + key, JSON.stringify(value))
  } catch {
    /* depolama kapalı: bellekteki önbellek yeterli */
  }
}

/**
 * Bir tercihi okur/yazar. `fallback` kayıt yokken döner.
 * Yazım iyimserdir: önbellek hemen güncellenir, sunucu hatası düzeni geri almaz (tarayıcıda kalır).
 */
export function useUiPref<T>(key: string, fallback: T): { value: T; set: (next: T | null) => Promise<void>; ready: boolean; serverBacked: boolean } {
  const qc = useQueryClient()
  const q = useQuery({
    queryKey: uiPrefsKey,
    // 204 (kiracı seçilmemiş) → null: tercihler bu tarayıcıda tutulur.
    queryFn: async ({ signal }) => (await uiPrefsApi.all(signal)) ?? null,
    staleTime: Infinity,
    gcTime: Infinity,
    retry: false,
  })
  const serverBacked = q.isSuccess && q.data !== null
  // Sunucu yanıt verdiyse doğru kaynak odur (aynı tarayıcıdaki başka kullanıcının yerel kopyası karışmasın).
  const stored = serverBacked ? q.data?.[key] : readLocal(key)
  const value = (stored ?? fallback) as T

  const set = useCallback(async (next: T | null) => {
    writeLocal(key, next)
    if (!serverBacked) {
      // Yerel mod (kiracı yok / sunucu yanıt vermiyor): okuyan bileşenler yeniden çizilsin.
      bump()
      return
    }
    qc.setQueryData<PrefMap | null>(uiPrefsKey, (prev) => {
      const copy = { ...(prev ?? {}) }
      if (next == null) delete copy[key]
      else copy[key] = next
      return copy
    })
    try {
      if (next == null) await uiPrefsApi.remove(key)
      else await uiPrefsApi.put(key, next)
    } catch {
      /* sunucu yazamadı: bu oturumda önbellekteki, sonraki girişte tarayıcıdaki kopya kalır */
    }
  }, [key, qc, serverBacked])

  useLocalVersion()
  return useMemo(() => ({ value, set, ready: !q.isPending, serverBacked }), [value, set, q.isPending, serverBacked])
}

/* Yerel modda yazım sonrası okuyan bileşenler yeniden çizilsin diye küçük bir sürüm sayacı. */
let localVersion = 0
const localListeners = new Set<() => void>()
function bump() {
  localVersion++
  localListeners.forEach((l) => l())
}
function useLocalVersion() {
  return useSyncExternalStore(
    (l) => {
      localListeners.add(l)
      return () => void localListeners.delete(l)
    },
    () => localVersion,
  )
}

/** Oturum kapanırken: tarayıcıdaki tercih kopyaları silinir (paylaşılan cihaz). */
export function clearLocalUiPrefs() {
  try {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const k = localStorage.key(i)
      if (k?.startsWith(LOCAL_PREFIX)) localStorage.removeItem(k)
    }
  } catch {
    /* depolama kapalı */
  }
}
