import { useEffect, useState } from 'react'

/** Değer `delay` ms değişmeden kalınca güncellenir (sunucu tarafı aramada her tuşta istek atılmasın). */
export function useDebouncedValue<T>(value: T, delay = 300): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const id = window.setTimeout(() => setDebounced(value), delay)
    return () => window.clearTimeout(id)
  }, [value, delay])
  return debounced
}
