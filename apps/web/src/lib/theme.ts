import { useCallback, useEffect, useState } from 'react'

export type Theme = 'light' | 'dark' | 'system'

const KEY = 'hr360.theme'

/** Ürünün imza görünümü koyu "Obsidyen Zümrüt"; kullanıcı Ayarlar'dan değiştirebilir. */
export const DEFAULT_THEME: Theme = 'dark'

function read(): Theme {
  try {
    const v = window.localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' || v === 'system' ? v : DEFAULT_THEME
  } catch {
    return DEFAULT_THEME
  }
}

function prefersDark() {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false
}

/** index.css koyu temayı `.dark` sınıfına bağlıyor; tek yazan yer burası. */
function apply(theme: Theme) {
  const dark = theme === 'dark' || (theme === 'system' && prefersDark())
  document.documentElement.classList.toggle('dark', dark)
}

// İlk boyamadan önce uygula — açılışta beyaz parlama olmasın.
apply(read())

export function useTheme() {
  const [theme, setThemeState] = useState<Theme>(read)

  useEffect(() => {
    apply(theme)
    if (theme !== 'system') return
    // Sistem tercihi değişirse takip et.
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const onChange = () => apply('system')
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [theme])

  const setTheme = useCallback((next: Theme) => {
    try {
      window.localStorage.setItem(KEY, next)
    } catch {
      /* depolama kapalı — oturum boyunca geçerli olur */
    }
    setThemeState(next)
  }, [])

  const isDark = theme === 'dark' || (theme === 'system' && prefersDark())

  return { theme, setTheme, isDark, toggle: () => setTheme(isDark ? 'light' : 'dark') }
}
