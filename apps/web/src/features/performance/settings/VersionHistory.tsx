/**
 * Sürüm geçmişi — ayar üzerine yazılmaz, her kayıt yeni sürümdür.
 * Her sürümün yanında bir öncekine göre neyin değiştiği yazılır; eski bir
 * sürümün değerleri forma yüklenip yeni sürüm olarak kaydedilebilir.
 */

import { useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { ArrowRight, ChevronDown, History, Upload } from 'lucide-react'
import type { ScoringConfig } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { Chip, errorText } from '../components/controls'
import { FIELDS, FIELD_KEYS, diff, toInput } from './fields'
import { tx } from '@/lib/i18n'

export function VersionHistory({
  versions,
  isPending,
  error,
  currentVersion,
  onLoad,
}: {
  versions: ScoringConfig[] | undefined
  isPending: boolean
  error: unknown
  currentVersion: number | null
  /** Verilmezse (salt okunur görünüm) "forma yükle" düğmesi gösterilmez. */
  onLoad?: (v: ScoringConfig) => void
}) {
  const [open, setOpen] = useState<number | null>(null)
  const list = [...(versions ?? [])].sort((a, b) => b.version - a.version)

  return (
    <div>
      <h2 className="flex items-center gap-2 text-[14px] font-semibold">
        <History className="size-4 text-muted-foreground" aria-hidden />
        {tx('Sürüm geçmişi')}
      </h2>
      <p className="mt-0.5 text-[12px] text-muted-foreground">{tx('Kapanmış dönemler, kapandıkları sürümle puanlanmış olarak kalır.')}</p>

      {isPending && (
        <div className="mt-4 flex flex-col gap-3">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} className="h-14 w-full rounded-lg" />
          ))}
        </div>
      )}
      {!isPending && Boolean(error) && <p className="mt-4 text-[12px] text-destructive">{errorText(error, tx('Sürüm geçmişi alınamadı.'))}</p>}
      {!isPending && !error && list.length === 0 && <p className="mt-4 text-[12px] text-muted-foreground">{tx('Henüz kayıtlı sürüm yok.')}</p>}

      <ol className="relative mt-4">
        <span aria-hidden className="absolute top-2 bottom-2 left-[13px] w-px bg-border" />
        {list.map((v, i) => {
          const prev = list[i + 1]
          const changes = prev ? diff(toInput(prev), toInput(v)) : []
          const isCurrent = v.version === currentVersion
          const expanded = open === v.version
          return (
            <motion.li
              key={v.version}
              layout
              initial={{ opacity: 0, x: -8 }}
              animate={{ opacity: 1, x: 0 }}
              transition={{ duration: 0.45, ease: EASE, delay: 0.05 * i }}
              className="relative pb-4 pl-9 last:pb-0"
            >
              <span
                className={cn(
                  'absolute top-0.5 left-0 flex size-7 items-center justify-center rounded-full border-2 text-[11px] font-bold',
                  isCurrent ? 'border-primary bg-primary text-primary-foreground' : 'border-border bg-card text-muted-foreground',
                )}
              >
                {v.version}
                {isCurrent && <span aria-hidden className="hr-ring absolute inset-0 rounded-full border-2 border-primary" />}
              </span>
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="text-[13px] font-semibold">{tx('Sürüm {0}', [v.version])}</span>
                {isCurrent && <Chip tone="success">{tx('Yürürlükte')}</Chip>}
              </div>
              <p className="text-[11px] text-muted-foreground">
                {formatDateTime(v.createdAt ?? null)}
                {v.createdByName ? ` · ${v.createdByName}` : ''}
              </p>

              {prev ? (
                changes.length ? (
                  <ul className="mt-1.5 flex flex-col gap-0.5">
                    {changes.slice(0, expanded ? undefined : 3).map((c) => (
                      <li key={c.key} className="flex flex-wrap items-center gap-1 text-[11px]">
                        <span className="text-muted-foreground">{c.label}</span>
                        <span className="tabular">{c.from}</span>
                        <ArrowRight className="size-3 text-muted-foreground" aria-hidden />
                        <span className="tabular font-semibold">{c.to}</span>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p className="mt-1 text-[11px] text-muted-foreground">{tx('Değer değişikliği yok.')}</p>
                )
              ) : (
                <p className="mt-1 text-[11px] text-muted-foreground">{tx('İlk sürüm.')}</p>
              )}

              <div className="mt-1.5 flex flex-wrap items-center gap-1">
                <button
                  type="button"
                  onClick={() => setOpen(expanded ? null : v.version)}
                  className="inline-flex items-center gap-0.5 text-[11px] font-medium text-primary hover:underline"
                  aria-expanded={expanded}
                >
                  {expanded ? tx('Daha az') : changes.length > 3 ? tx('+{0} değişiklik · tüm değerler', [changes.length - 3]) : tx('Tüm değerler')}
                  <ChevronDown className={cn('size-3 transition-transform', expanded && 'rotate-180')} aria-hidden />
                </button>
                {!isCurrent && onLoad && (
                  <Button size="xs" variant="ghost" onClick={() => onLoad(v)} className="h-5 px-1.5 text-[11px]">
                    <Upload aria-hidden />
                    {tx('Forma yükle')}
                  </Button>
                )}
              </div>

              <AnimatePresence initial={false}>
                {expanded && (
                  <motion.dl
                    initial={{ opacity: 0, height: 0 }}
                    animate={{ opacity: 1, height: 'auto' }}
                    exit={{ opacity: 0, height: 0 }}
                    className="mt-2 grid grid-cols-[1fr_auto] gap-x-3 gap-y-0.5 overflow-hidden rounded-md bg-muted/40 px-2.5 py-2 text-[11px]"
                  >
                    {FIELD_KEYS.map((k) => (
                      <div key={k} className="contents">
                        <dt className="text-muted-foreground">{FIELDS[k].label}</dt>
                        <dd className="tabular text-right font-medium">{FIELDS[k].format(v[k])}</dd>
                      </div>
                    ))}
                  </motion.dl>
                )}
              </AnimatePresence>
            </motion.li>
          )
        })}
      </ol>
    </div>
  )
}
