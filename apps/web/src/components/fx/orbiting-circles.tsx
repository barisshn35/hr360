/**
 * Merkez etrafında dönen öğeler.
 * Kaynak: 21st.dev "Orbiting Circles" (dillionverma / Magic UI, id 1411).
 * Uyarlama: yörünge çizgisi token renginde, başlangıç açısı (`angle`) eklendi.
 */
import { cn } from '@/lib/utils'

export interface OrbitingCirclesProps {
  className?: string
  children?: React.ReactNode
  reverse?: boolean
  duration?: number
  /** Derece cinsinden başlangıç açısı. */
  angle?: number
  radius?: number
  path?: boolean
}

export function OrbitingCircles({
  className,
  children,
  reverse,
  duration = 20,
  angle = 0,
  radius = 50,
  path = true,
}: OrbitingCirclesProps) {
  return (
    <>
      {path && (
        <svg aria-hidden="true" className="pointer-events-none absolute inset-0 size-full">
          <circle
            className="stroke-foreground/10"
            strokeDasharray="3 5"
            cx="50%"
            cy="50%"
            r={radius}
            fill="none"
          />
        </svg>
      )}
      <div
        style={{ '--duration': duration, '--radius': radius, '--angle': angle } as React.CSSProperties}
        className={cn(
          'animate-orbit absolute flex transform-gpu items-center justify-center rounded-full',
          reverse && '[animation-direction:reverse]',
          className,
        )}
      >
        {children}
      </div>
    </>
  )
}
