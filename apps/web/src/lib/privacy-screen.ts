import { useEffect, useState } from 'react'

/**
 * Ekran paylaşım modu: açıkken `.sensitive-scope` içindeki sayısal değerler
 * (ücret, hak ediş) ve `.sensitive` öğeler bulanıklaşır; üzerine gelince
 * görünür. Toplantıda ekran paylaşırken maaşların kazara görünmesini önler.
 */
const KEY = 'hr360.privacyScreen'

function apply(on: boolean) {
  document.documentElement.dataset.privacy = on ? 'on' : 'off'
}

try { apply(localStorage.getItem(KEY) === 'on') } catch { /* depolama kapalı */ }

export function usePrivacyScreen() {
  const [on, setOn] = useState(() => document.documentElement.dataset.privacy === 'on')
  useEffect(() => {
    apply(on)
    try { localStorage.setItem(KEY, on ? 'on' : 'off') } catch { /* yok say */ }
  }, [on])
  return [on, setOn] as const
}
