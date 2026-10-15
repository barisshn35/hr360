import type { ReactNode } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { RotateCcw } from 'lucide-react'
import { Panel } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { EASE } from '@/motion/primitives'
import { Chip } from '../components/controls'
import { tx } from '@/lib/i18n'

/** Ayar bölümü: harfli rozet, başlık, açıklama; değiştiyse "değişti" işareti ve geri alma. */
export function SettingsSection({
  letter,
  title,
  description,
  changed,
  onReset,
  index,
  id,
  children,
}: {
  letter: string
  title: string
  description: ReactNode
  changed?: boolean
  onReset?: () => void
  index: number
  id: string
  children: ReactNode
}) {
  return (
    <motion.section
      id={id}
      aria-labelledby={`${id}-title`}
      initial={{ opacity: 0, y: 18 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.6, ease: EASE, delay: 0.08 * index }}
      className="scroll-mt-20"
    >
      <Panel className="relative">
        <AnimatePresence>
          {changed && (
            <motion.span
              aria-hidden
              initial={{ scaleY: 0 }}
              animate={{ scaleY: 1 }}
              exit={{ scaleY: 0 }}
              className="absolute inset-y-0 left-0 w-1 origin-top bg-primary"
            />
          )}
        </AnimatePresence>
        <div className="flex flex-wrap items-start justify-between gap-3 border-b border-border px-5 py-4">
          <div className="flex min-w-0 gap-3">
            <span className="flex size-7 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-[13px] font-bold text-primary">
              {letter}
            </span>
            <div className="min-w-0">
              <h2 id={`${id}-title`} className="text-[15px] font-semibold">
                {title}
              </h2>
              <div className="mt-0.5 max-w-2xl text-[13px] leading-relaxed text-muted-foreground">{description}</div>
            </div>
          </div>
          <AnimatePresence>
            {changed && (
              <motion.div initial={{ opacity: 0, x: 8 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: 8 }} className="flex items-center gap-1.5">
                <Chip tone="primary">{tx('Değişti')}</Chip>
                {onReset && (
                  <Button size="xs" variant="ghost" onClick={onReset}>
                    <RotateCcw aria-hidden />
                    {tx('Geri al')}
                  </Button>
                )}
              </motion.div>
            )}
          </AnimatePresence>
        </div>
        <div className="px-5 py-5">{children}</div>
      </Panel>
    </motion.section>
  )
}
