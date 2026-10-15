/**
 * Arka planda kayan meteorlar.
 * Kaynak: 21st.dev "Meteors" (aceternity, id 1407). Uyarlama: rastgele
 * konumlar ilk çizimde sabitlenir (her render'da zıplamasın), renk zümrüt.
 */
import { useMemo } from 'react'
import { cn } from '@/lib/utils'

export function Meteors({ number = 16, className }: { number?: number; className?: string }) {
  const meteors = useMemo(
    () =>
      Array.from({ length: number }, () => ({
        left: `${Math.floor(Math.random() * 110 - 5)}%`,
        delay: `${(Math.random() * 6).toFixed(2)}s`,
        duration: `${Math.floor(Math.random() * 6 + 4)}s`,
      })),
    [number],
  )
  return (
    <div aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden">
      {meteors.map((m, i) => (
        <span
          key={i}
          className={cn(
            'animate-meteor absolute top-0 size-0.5 rotate-[215deg] rounded-full bg-foreground/70 shadow-[0_0_0_1px_hsl(var(--foreground)/0.1)]',
            "before:absolute before:top-1/2 before:h-px before:w-[60px] before:-translate-y-1/2 before:bg-gradient-to-r before:from-foreground/50 before:to-transparent before:content-['']",
            className,
          )}
          style={{ left: m.left, animationDelay: m.delay, animationDuration: m.duration }}
        />
      ))}
    </div>
  )
}
