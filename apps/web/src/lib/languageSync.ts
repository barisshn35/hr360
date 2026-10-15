import { useEffect } from 'react'
import { apiFetch } from '@/api/client'
import { lang, setLanguage, type Lang } from '@/lib/i18n'

const STORAGE_KEY = 'hr360.lang'

/**
 * Dil tercihini sunucuya yazar: bildirim servisi (e-posta ve bildirim metinleri) ve
 * Keycloak (giriş ekranı, davet ve parola sıfırlama e-postaları). Hata arayüzü
 * engellemez; dil yine tarayıcıda değişir.
 */
export async function saveLanguagePreference(next: Lang): Promise<void> {
  const timeout = <T,>(p: Promise<T>) => Promise.race([p, new Promise((r) => setTimeout(r, 2500))])
  await Promise.allSettled([
    timeout(apiFetch('/api/notification/notifications/preferences/me', { method: 'PUT', body: { language: next } })),
    timeout(apiFetch('/api/tenant/my-tenant/me/locale', { method: 'PUT', body: { language: next } })),
  ])
}

/** Dili değiştirir: önce sunucuya kaydeder, sonra sayfayı yeni dille yükler. */
export async function changeLanguage(next: Lang): Promise<void> {
  await saveLanguagePreference(next)
  setLanguage(next)
}

/**
 * Bu tarayıcıda henüz dil seçilmediyse kullanıcının sunucudaki tercihini uygular
 * (ör. başka bir cihazda İngilizce seçmişse). Seçim zaten varsa bir şey yapmaz.
 */
export function useServerLanguage(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return
    let stored: string | null = null
    try {
      stored = localStorage.getItem(STORAGE_KEY)
    } catch {
      return
    }
    if (stored) return
    let cancelled = false
    apiFetch<{ language: Lang | null }>('/api/notification/notifications/preferences/me')
      .then((r) => {
        if (cancelled) return
        if (r?.language && r.language !== lang) setLanguage(r.language)
        else localStorage.setItem(STORAGE_KEY, lang)
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [enabled])
}
