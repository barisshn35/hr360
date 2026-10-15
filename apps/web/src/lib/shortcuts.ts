import { useEffect, useState } from 'react'
import { tx } from '@/lib/i18n'

/**
 * Klavye kısayolları (G23) açık/kapalı tercihi. Cihaza özgüdür (localStorage);
 * ekran okuyucu kullananlar tek tuşlu kısayolları Profilim › Erişilebilirlik'ten kapatabilir
 * (WCAG 2.1.4 Karakter tuşu kısayolları).
 */
const KEY = 'hr360.shortcuts'
const EVENT = 'hr360:shortcuts'

export function shortcutsEnabled(): boolean {
  try {
    return window.localStorage.getItem(KEY) !== 'off'
  } catch {
    return true
  }
}

export function setShortcutsEnabled(on: boolean): void {
  try {
    window.localStorage.setItem(KEY, on ? 'on' : 'off')
  } catch {
    /* yok say */
  }
  window.dispatchEvent(new Event(EVENT))
}

export function useShortcutsEnabled(): [boolean, (on: boolean) => void] {
  const [on, setOn] = useState(shortcutsEnabled)
  useEffect(() => {
    const sync = () => setOn(shortcutsEnabled())
    window.addEventListener(EVENT, sync)
    window.addEventListener('storage', sync)
    return () => {
      window.removeEventListener(EVENT, sync)
      window.removeEventListener('storage', sync)
    }
  }, [])
  return [on, setShortcutsEnabled]
}

/** "g" ardından basılan harf → hedef sayfa. */
export const GOTO_SHORTCUTS: Array<{ key: string; path: string; label: string }> = [
  { key: 'd', path: '/panel', label: tx('Genel bakış') },
  { key: 'o', path: '/panel/onaylar', label: tx('Onay kutusu') },
  { key: 'i', path: '/panel/izin', label: tx('İzin') },
  { key: 'm', path: '/panel/masraf', label: tx('Masraf') },
  { key: 'p', path: '/panel/profil', label: tx('Profilim') },
  { key: 'b', path: '/panel/bildirimler', label: tx('Bildirimler') },
]

/** Yazı yazılan bir alanda mı (kısayollar devre dışı)? */
export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable) return true
  const tag = target.tagName
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true
  if (tag === 'INPUT') {
    const type = (target as HTMLInputElement).type
    return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file'].includes(type)
  }
  return target.closest('[role="combobox"], [role="textbox"], [role="listbox"]') !== null
}
