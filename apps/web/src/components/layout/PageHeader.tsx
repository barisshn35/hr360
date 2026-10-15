import { useEffect, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import { motion, useReducedMotion } from 'motion/react'
import { ChevronRight } from 'lucide-react'
import { TextReveal } from '@/components/fx/text-reveal'
import { locate } from './nav-config'

/**
 * Her modül sayfasının üst bandı.
 *
 * Bölüm yolu (ör. "Kişiler › İşe alım") ışıltılı bir rozet olarak üstte;
 * başlık kelime kelime bulanıklıktan netleşir (21st.dev "Text Generate
 * Effect"), açıklama ve eylemler ardından süzülür. Altında soldan sağa
 * çizilen ince zümrüt hat.
 */
export function PageHeader({
  title,
  description,
  actions,
  eyebrow,
}: {
  title: string
  description?: string
  actions?: ReactNode
  /** Verilmezse menüdeki konumdan üretilir. */
  eyebrow?: string[]
}) {
  const { pathname } = useLocation()
  const reduced = useReducedMotion()
  const here = locate(pathname)
  // Sayfa başlığı (WCAG 2.4.2): her ekranın kendi adı sekme başlığında.
  useEffect(() => {
    document.title = `${title} · HR360`
  }, [title, pathname])
  const trail =
    eyebrow ??
    [here.group?.heading, here.parent?.title, here.item && here.item.title !== title ? here.item.title : undefined].filter(
      (c): c is string => Boolean(c),
    )

  return (
    <div className="relative mb-8">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <div className="min-w-0">
          {trail.length > 0 && (
            <motion.div
              initial={reduced ? false : { opacity: 0, y: -6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.4 }}
              className="mb-3 inline-flex items-center gap-1.5 rounded-full border border-border bg-card/60 px-3 py-1 text-[11.5px] font-medium text-muted-foreground backdrop-blur"
            >
              <span className="relative flex size-1.5">
                <span className="absolute inline-flex size-full animate-ping rounded-full bg-primary opacity-60" />
                <span className="relative inline-flex size-1.5 rounded-full bg-primary" />
              </span>
              {trail.map((c, i) => (
                <span key={c} className="flex items-center gap-1.5">
                  {i > 0 && <ChevronRight className="size-3 opacity-50" />}
                  {c}
                </span>
              ))}
            </motion.div>
          )}
          <TextReveal
            as="h1"
            text={title}
            className="block text-[28px] leading-[1.1] font-semibold tracking-[-0.035em] sm:text-[36px]"
          />
          {description && (
            <motion.p
              initial={reduced ? false : { opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.5, delay: 0.15 }}
              className="mt-2.5 max-w-2xl text-[14px] leading-relaxed text-muted-foreground"
            >
              {description}
            </motion.p>
          )}
        </div>
        {actions && (
          <motion.div
            initial={reduced ? false : { opacity: 0, x: 12 }}
            animate={{ opacity: 1, x: 0 }}
            transition={{ duration: 0.5, delay: 0.2 }}
            className="flex shrink-0 flex-wrap items-center gap-2"
          >
            {actions}
          </motion.div>
        )}
      </div>
      <motion.div
        aria-hidden="true"
        initial={reduced ? false : { scaleX: 0 }}
        animate={{ scaleX: 1 }}
        transition={{ duration: 1, delay: 0.1, ease: [0.16, 1, 0.3, 1] }}
        className="mt-6 h-px origin-left bg-gradient-to-r from-foreground/25 via-border to-transparent"
      />
    </div>
  )
}
