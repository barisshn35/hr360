import { useCallback, useSyncExternalStore } from 'react'

/**
 * "Animasyonları azalt" (Profil › Erişilebilirlik). Açıkken, işletim sisteminin "hareketi azalt" ayarı açıkmış
 * gibi davranılır: CSS kuralları <html data-motion="reduce"> ile, motion/react bileşenleri App'teki
 * MotionConfig ile, elle yazılmış animasyonlar {@link useReducedMotion} / {@link prefersReducedMotion} ile.
 * Kapalıyken yalnızca sistem ayarı geçerlidir. Tercih bu tarayıcıda saklanır (tema gibi).
 */
const KEY = 'hr360.reduceMotion'
const QUERY = '(prefers-reduced-motion: reduce)'
const listeners = new Set<() => void>()

function readPref(): boolean {
  try {
    return window.localStorage.getItem(KEY) === '1'
  } catch {
    return false
  }
}

let pref = typeof window === 'undefined' ? false : readPref()

function apply() {
  if (typeof document === 'undefined') return
  if (pref) document.documentElement.setAttribute('data-motion', 'reduce')
  else document.documentElement.removeAttribute('data-motion')
}

// İlk boyamadan önce uygula: açılış animasyonları da tercihe uysun.
apply()

const media = () => (typeof window === 'undefined' ? undefined : window.matchMedia?.(QUERY))

/** Sistem ayarı ya da kullanıcı tercihi animasyonları azaltıyor mu (anlık değer; olay dinleyicilerinde, döngülerde). */
export function prefersReducedMotion(): boolean {
  return pref || (media()?.matches ?? false)
}

function subscribe(cb: () => void) {
  listeners.add(cb)
  const mq = media()
  mq?.addEventListener?.('change', cb)
  return () => {
    listeners.delete(cb)
    mq?.removeEventListener?.('change', cb)
  }
}

/** motion/react'in useReducedMotion'ı yerine: sistem ayarı + kullanıcı tercihi. */
export function useReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, prefersReducedMotion, () => false)
}

/** Kullanıcının kendi tercihi (sistem ayarı hariç) ve değiştirici. */
export function useReduceMotionSetting(): [boolean, (on: boolean) => void] {
  const value = useSyncExternalStore(subscribe, () => pref, () => false)
  const set = useCallback((on: boolean) => {
    pref = on
    try {
      if (on) window.localStorage.setItem(KEY, '1')
      else window.localStorage.removeItem(KEY)
    } catch {
      /* depolama kapalı: oturum boyunca geçerli */
    }
    apply()
    listeners.forEach((l) => l())
  }, [])
  return [value, set]
}

/** Sistem ayarı açık mı (Ayarlar'da açıklama için). */
export function systemReducesMotion(): boolean {
  return media()?.matches ?? false
}
