import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { AnimatePresence, motion } from 'motion/react'
import { CircleAlert, CircleCheck, Info, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

type Tone = 'ok' | 'stop' | 'info'

interface Item {
  id: number
  tone: Tone
  message: string
}

interface Api {
  ok: (message: string) => void
  stop: (message: string) => void
  info: (message: string) => void
}

const ToastContext = createContext<Api | null>(null)

const tones: Record<Tone, { box: string; icon: React.ElementType }> = {
  ok: { box: 'border-[hsl(var(--success))]/30 bg-[hsl(var(--success))]/10 text-foreground', icon: CircleCheck },
  stop: { box: 'border-destructive/30 bg-destructive/10 text-foreground', icon: CircleAlert },
  info: { box: 'border-border bg-card text-foreground', icon: Info },
}

let nextId = 0

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Item[]>([])

  const remove = useCallback((id: number) => {
    setItems((prev) => prev.filter((t) => t.id !== id))
  }, [])

  const push = useCallback(
    (tone: Tone, message: string) => {
      const id = nextId++
      setItems((prev) => [...prev, { id, tone, message }])
      // Hata mesajları daha uzun durur: kullanıcının okuyup düzeltmesi gerekir.
      window.setTimeout(() => remove(id), tone === 'stop' ? 8000 : 4500)
    },
    [remove],
  )

  const api = useMemo<Api>(
    () => ({
      ok: (m: string) => push('ok', m),
      stop: (m: string) => push('stop', m),
      info: (m: string) => push('info', m),
    }),
    [push],
  )

  return (
    <ToastContext.Provider value={api}>
      {children}
      {createPortal(
        <div
          role="status"
          aria-live="polite"
          aria-relevant="additions"
          className="pointer-events-none fixed inset-x-3 bottom-3 z-[200] flex flex-col items-center gap-2 sm:inset-x-auto sm:right-4 sm:bottom-4 sm:items-end"
        >
          <AnimatePresence initial={false}>
            {items.map((t) => {
              const tone = tones[t.tone]
              const Icon = tone.icon
              return (
                <motion.div
                  key={t.id}
                  // Hata mesajı ekran okuyucuda hemen (assertive) duyurulur.
                  role={t.tone === 'stop' ? 'alert' : undefined}
                  layout
                  initial={{ opacity: 0, y: 8, scale: 0.98 }}
                  animate={{ opacity: 1, y: 0, scale: 1 }}
                  exit={{ opacity: 0, y: 4, scale: 0.98 }}
                  transition={{ duration: 0.18, ease: 'easeOut' }}
                  className={cn(
                    'pointer-events-auto flex w-full max-w-sm items-start gap-2.5 rounded-lg border px-3.5 py-3 text-[13px] leading-relaxed shadow-lg',
                    tone.box,
                  )}
                >
                  <Icon aria-hidden="true" className="mt-px size-4 shrink-0" strokeWidth={1.75} />
                  <span className="min-w-0 flex-1 break-words">{t.message}</span>
                  <button
                    type="button"
                    aria-label={tx('Kapat')}
                    onClick={() => remove(t.id)}
                    className="-mr-1 shrink-0 rounded p-0.5 text-muted-foreground transition-colors hover:text-foreground"
                  >
                    <X className="size-3.5" />
                  </button>
                </motion.div>
              )
            })}
          </AnimatePresence>
        </div>,
        document.body,
      )}
    </ToastContext.Provider>
  )
}

export function useToast(): Api {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error(tx('useToast yalnızca <ToastProvider> içinde kullanılabilir.'))
  return ctx
}
