import { useEffect, useState } from 'react'

interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

let deferred: BeforeInstallPromptEvent | null = null
const listeners = new Set<() => void>()
if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault()
    deferred = e as BeforeInstallPromptEvent
    listeners.forEach((l) => l())
  })
  window.addEventListener('appinstalled', () => {
    deferred = null
    listeners.forEach((l) => l())
  })
}

/** Tarayıcı “ana ekrana ekle” sunuyorsa yükleme fonksiyonu döner, yoksa null. */
export function useInstallPrompt(): (() => Promise<void>) | null {
  const [, force] = useState(0)
  useEffect(() => {
    const l = () => force((n) => n + 1)
    listeners.add(l)
    return () => void listeners.delete(l)
  }, [])
  if (!deferred) return null
  return async () => {
    const e = deferred
    if (!e) return
    await e.prompt()
    await e.userChoice
    deferred = null
    listeners.forEach((l) => l())
  }
}

/* ------------------------------------------------------------------ yükleme önerisi (dalga 12) */

const DISMISS_KEY = 'hr360.install.dismissedAt'
/** "Daha sonra" denirse öneri bu kadar gün gösterilmez. */
export const INSTALL_SNOOZE_DAYS = 30

/** iPhone/iPad Safari: "beforeinstallprompt" yoktur; Paylaş › Ana Ekrana Ekle anlatılır. */
export function isIos(ua = typeof navigator === 'undefined' ? '' : navigator.userAgent, touchPoints = typeof navigator === 'undefined' ? 0 : navigator.maxTouchPoints): boolean {
  // iPadOS 13+ masaüstü Safari gibi görünür (Macintosh + dokunmatik).
  return /iPad|iPhone|iPod/.test(ua) || (/Macintosh/.test(ua) && touchPoints > 1)
}

/** Uygulama ana ekrandan (kurulu) mu açıldı? */
export function isStandalone(): boolean {
  if (typeof window === 'undefined') return false
  const nav = navigator as Navigator & { standalone?: boolean }
  return window.matchMedia?.('(display-mode: standalone)').matches === true || nav.standalone === true
}

/** Öneri gösterilsin mi? (saf; birim testli) */
export function shouldOfferInstall(o: { hasPrompt: boolean; ios: boolean; standalone: boolean; dismissedAt: number | null; now: number }): 'prompt' | 'ios' | null {
  if (o.standalone) return null
  if (o.dismissedAt && o.now - o.dismissedAt < INSTALL_SNOOZE_DAYS * 86_400_000) return null
  if (o.hasPrompt) return 'prompt'
  return o.ios ? 'ios' : null
}

export function readInstallDismissed(): number | null {
  try {
    const v = Number(localStorage.getItem(DISMISS_KEY))
    return Number.isFinite(v) && v > 0 ? v : null
  } catch {
    return null
  }
}

export function dismissInstall(now = Date.now()) {
  try {
    localStorage.setItem(DISMISS_KEY, String(now))
  } catch {
    /* depolama kapalı: bu oturumda gizlenir */
  }
}
