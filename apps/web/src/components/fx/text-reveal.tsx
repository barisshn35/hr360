/**
 * Kelime kelime bulanıklıktan netleşen metin.
 * Kaynak: 21st.dev "Text Generate Effect" (cnippet-dev, id 18600) ve
 * "Blur In Text" (animbits, id 19228) desenleri; motion/react ile yeniden yazıldı.
 */
import { motion, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

export function TextReveal({
  text,
  className,
  wordClassName,
  delay = 0,
  step = 0.06,
  as = 'span',
}: {
  text: string
  className?: string
  wordClassName?: string
  delay?: number
  step?: number
  as?: 'span' | 'h1' | 'h2' | 'p'
}) {
  const reduced = useReducedMotion()
  const Tag = motion[as] as typeof motion.span
  const words = text.split(' ')
  if (reduced) {
    const Static = as
    return <Static className={className}>{text}</Static>
  }
  return (
    <Tag className={className}>
      {/* Ekran okuyucu tek parça metni okur; kelime kelime animasyon gizlidir. */}
      <span className="sr-only">{text}</span>
      {words.map((w, i) => (
        <motion.span
          key={`${w}-${i}`}
          aria-hidden="true"
          className={cn('inline-block whitespace-pre', wordClassName)}
          initial={{ opacity: 0, filter: 'blur(10px)', y: 8 }}
          animate={{ opacity: 1, filter: 'blur(0px)', y: 0 }}
          transition={{ duration: 0.55, delay: delay + i * step, ease: [0.16, 1, 0.3, 1] }}
        >
          {w}
          {i < words.length - 1 ? ' ' : ''}
        </motion.span>
      ))}
    </Tag>
  )
}
