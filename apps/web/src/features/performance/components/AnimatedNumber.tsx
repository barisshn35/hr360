/**
 * Değeri değiştiğinde eski değerden yenisine akan sayı.
 * `CountUp` görünüme girişte bir kez oynar; bu ise her değişimde oynar
 * (kaydırıcı sürüklenirken yüzdeler akar).
 */

import { useEffect, useRef, useState } from 'react'
import { animate, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

export function AnimatedNumber({
  value,
  format = (v) => String(Math.round(v)),
  duration = 0.45,
  className,
}: {
  value: number
  format?: (v: number) => string
  duration?: number
  className?: string
}) {
  const reduced = useReducedMotion()
  const [text, setText] = useState(() => format(value))
  const from = useRef(value)

  useEffect(() => {
    if (reduced) {
      setText(format(value))
      from.current = value
      return
    }
    const controls = animate(from.current, value, {
      duration,
      ease: [0.16, 1, 0.3, 1],
      onUpdate: (v) => {
        from.current = v
        setText(format(v))
      },
    })
    return () => controls.stop()
    // `format` her render'da yeni olabilir; yalnızca değer değişiminde oyna.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, reduced, duration])

  return <span className={cn('tabular', className)}>{text}</span>
}
