/**
 * Panoya kopyalama düğmesi — 21st.dev "Copy Button" (ddoemonn) uyarlaması: simge
 * geçişi, tik çizimi (pathLength), etiket çapraz geçişi, pano API'si yoksa eski
 * yönteme düşme ve `prefers-reduced-motion` desteği. Renkler proje tokenlarından.
 */
import { useCallback, useEffect, useRef, useState } from 'react'
import { motion, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const EASE = [0.23, 1, 0.32, 1] as const
const PRESS = { type: 'spring', stiffness: 520, damping: 34, mass: 0.45 } as const
const CROSSFADE = { type: 'spring', stiffness: 260, damping: 34, mass: 0.8 } as const
const DRAW = { duration: 0.26, ease: EASE } as const
const INSTANT = { duration: 0 } as const

export type CopyStatus = 'idle' | 'copied' | 'error'

function writeFallback(text: string): boolean {
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.top = '0'
  area.style.opacity = '0'
  document.body.appendChild(area)
  area.select()
  let ok = false
  try {
    ok = document.execCommand('copy')
  } catch {
    ok = false
  }
  document.body.removeChild(area)
  return ok
}

export function useCopyToClipboard(timeout = 2000) {
  const [status, setStatus] = useState<CopyStatus>('idle')
  const [ticket, setTicket] = useState(0)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
    }
  }, [])

  const copy = useCallback(async (text: string) => {
    if (!text) return false
    let ok = false
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text)
        ok = true
      } else ok = writeFallback(text)
    } catch {
      try {
        ok = writeFallback(text)
      } catch {
        ok = false
      }
    }
    if (!mounted.current) return ok
    setStatus(ok ? 'copied' : 'error')
    setTicket((t) => t + 1)
    return ok
  }, [])

  useEffect(() => {
    if (ticket === 0 || status === 'idle') return
    const id = setTimeout(() => setStatus('idle'), timeout)
    return () => clearTimeout(id)
  }, [ticket, status, timeout])

  return { copy, status }
}

export function CopyButton({
  value,
  label = tx('Kopyala'),
  copiedLabel = tx('Kopyalandı'),
  errorLabel = tx('Kopyalanamadı'),
  onCopied,
  disabled = false,
  className,
}: {
  value: string
  label?: string
  copiedLabel?: string
  errorLabel?: string
  onCopied?: () => void
  disabled?: boolean
  className?: string
}) {
  const { copy, status } = useCopyToClipboard()
  const reduced = useReducedMotion()
  const fade = reduced ? INSTANT : CROSSFADE
  const draw = reduced ? INSTANT : DRAW
  const labels: Array<[CopyStatus, string]> = [
    ['idle', label],
    ['copied', copiedLabel],
    ['error', errorLabel],
  ]

  return (
    <motion.button
      type="button"
      disabled={disabled}
      aria-label={label}
      onClick={() => {
        void copy(value).then((ok) => ok && onCopied?.())
      }}
      whileTap={disabled || reduced ? undefined : { y: 1 }}
      transition={PRESS}
      className={cn(
        'inline-flex h-9 cursor-pointer select-none items-center gap-2 rounded-md border border-border bg-card px-3 text-[13px] font-medium text-foreground shadow-xs outline-none transition-colors hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-50',
        status === 'copied' && 'border-[hsl(var(--success))]/40 text-[hsl(var(--success))]',
        status === 'error' && 'border-destructive/40 text-destructive',
        className,
      )}
    >
      <span className="grid size-[14px] shrink-0" aria-hidden="true">
        <motion.svg viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"
          className="col-start-1 row-start-1 size-[14px]" initial={false}
          animate={{ opacity: status === 'idle' ? 1 : 0, scale: status === 'idle' ? 1 : 0.92 }} transition={fade}>
          <path d="M9.6 5.1V3.7A1.7 1.7 0 0 0 7.9 2H3.7A1.7 1.7 0 0 0 2 3.7v4.2a1.7 1.7 0 0 0 1.7 1.7h1.4" />
          <rect x="5.1" y="5.1" width="6.9" height="6.9" rx="1.7" />
        </motion.svg>
        <motion.svg viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"
          className="col-start-1 row-start-1 size-[14px]" initial={false}
          animate={{ opacity: status === 'copied' ? 1 : 0, scale: status === 'copied' ? 1 : 0.92 }} transition={fade}>
          <motion.path d="M2.9 7.4 5.6 10.1 11.1 4" initial={false} animate={{ pathLength: status === 'copied' ? 1 : 0 }} transition={draw} />
        </motion.svg>
        <motion.svg viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round"
          className="col-start-1 row-start-1 size-[14px]" initial={false}
          animate={{ opacity: status === 'error' ? 1 : 0, scale: status === 'error' ? 1 : 0.92 }} transition={fade}>
          <path d="M3.6 3.6 10.4 10.4" />
          <path d="M10.4 3.6 3.6 10.4" />
        </motion.svg>
      </span>
      <span aria-hidden="true" className="relative grid">
        {labels.map(([key, text]) => (
          <motion.span key={key} initial={false}
            animate={key === status ? { opacity: 1, y: 0, filter: 'blur(0px)' } : { opacity: 0, y: 3, filter: 'blur(3px)' }}
            transition={fade} className="col-start-1 row-start-1 whitespace-nowrap">
            {text}
          </motion.span>
        ))}
      </span>
      <span role="status" aria-live="polite" className="sr-only">
        {status === 'copied' ? copiedLabel : status === 'error' ? errorLabel : ''}
      </span>
    </motion.button>
  )
}
