/**
 * 3B görünümlerin ön koşulları — ana pakette kalan küçük, three.js'siz yardımcılar.
 *
 *  - WebGL 2 var mı: three.js (r163+) yalnızca WebGL 2 ile çalışır. Bağlam açılamazsa three
 *    konsola hata yazar; bu yüzden 3B parçası yüklenmeden ÖNCE burada sessizce denenir.
 *  - Kullanıcı tercihi "3B'yi kapat": tarayıcıda hatırlanır (localStorage; gizli pencerede/
 *    engellenmişse sessizce yok sayılır).
 *  - Azaltılmış hareket: otomatik dönme ve geçiş animasyonları kapanır.
 */

import { useCallback, useSyncExternalStore } from 'react'

const PREF_KEY = 'hr360.org3d.off'

let webgl2: boolean | null = null

/** Tarayıcı WebGL 2 bağlamı açabiliyor mu (sonuç oturum boyunca önbellekte). */
export function webglAvailable(): boolean {
  if (webgl2 !== null) return webgl2
  try {
    if (typeof document === 'undefined') return (webgl2 = false)
    const canvas = document.createElement('canvas')
    const gl = canvas.getContext('webgl2', { failIfMajorPerformanceCaveat: false }) as WebGL2RenderingContext | null
    webgl2 = Boolean(gl)
    // Deneme bağlamını hemen bırak (tarayıcının bağlam sınırı dolmasın).
    gl?.getExtension('WEBGL_lose_context')?.loseContext()
  } catch {
    webgl2 = false
  }
  return webgl2
}

/** Testler için önbelleği sıfırlar. */
export function resetWebglCache() {
  webgl2 = null
}

const listeners = new Set<() => void>()

export function read3dDisabled(): boolean {
  try {
    return window.localStorage.getItem(PREF_KEY) === '1'
  } catch {
    return false
  }
}

export function write3dDisabled(off: boolean) {
  try {
    if (off) window.localStorage.setItem(PREF_KEY, '1')
    else window.localStorage.removeItem(PREF_KEY)
  } catch {
    // Depolama kapalı: tercih yalnızca bu sayfa açıkken (bellekte) geçerli olur.
  }
  memoryPref = off
  listeners.forEach((l) => l())
}

let memoryPref: boolean | null = null

const subscribe = (l: () => void) => {
  listeners.add(l)
  return () => listeners.delete(l)
}
const snapshot = () => memoryPref ?? read3dDisabled()

export function prefersReducedMotion(): boolean {
  try {
    return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false
  } catch {
    return false
  }
}

export interface ThreeDSupport {
  /** Tarayıcıda WebGL 2 var. */
  available: boolean
  /** Kullanıcı 3B'yi kapattı. */
  disabled: boolean
  /** 3B çizilebilir (var ve kapatılmamış). */
  enabled: boolean
  setDisabled: (off: boolean) => void
  reducedMotion: boolean
}

export function useThreeDSupport(): ThreeDSupport {
  const disabled = useSyncExternalStore(subscribe, snapshot, () => false)
  const available = webglAvailable()
  const setDisabled = useCallback((off: boolean) => write3dDisabled(off), [])
  return { available, disabled, enabled: available && !disabled, setDisabled, reducedMotion: prefersReducedMotion() }
}
