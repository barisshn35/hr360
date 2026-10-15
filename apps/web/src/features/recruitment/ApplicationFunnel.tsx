import { motion, useReducedMotion } from 'motion/react'
import {
  APPLICATION_FUNNEL,
  applicationStatusLabels,
  type Application,
  type ApplicationStatus,
} from '@/api/types'
import { formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, pct } from '@/lib/i18n'

/**
 * Başvuru hunisi — bu modülün en değerli ekranı.
 *
 * Aşamalar gerçekten sıralı olduğu için sıra numarası meşru: aday
 * Applied → Screening → Interview → Offer → Hired yolunu izler.
 * Çubuk genişliği en kalabalık aşamaya göre ölçeklenir; her aşamada bir
 * önceki aşamaya göre geçiş oranı da yazılır, çünkü hunide asıl bilgi
 * düşüş oranıdır.
 */
export function ApplicationFunnel({ applications }: { applications: Application[] }) {
  const reduced = useReducedMotion()

  const counts = APPLICATION_FUNNEL.map((stage) => ({
    stage,
    count: applications.filter((a) => a.status === stage).length,
  }))

  // Hunide bir aşamada duran aday, sonraki aşamalara henüz ulaşmamıştır.
  // "Bu aşamaya kadar gelen" sayısı = bu ve sonraki aşamalardaki toplam.
  const reached = counts.map((_, i) => counts.slice(i).reduce((sum, c) => sum + c.count, 0))
  const top = reached[0] || 1

  const rejected = applications.filter(
    (a) => a.status === 'Rejected' || a.status === 'Withdrawn',
  ).length

  return (
    <div className="space-y-3">
      <ol className="space-y-2.5">
        {counts.map((row, i) => {
          const total = reached[i]
          const percentOfTop = Math.round((total / top) * 100)
          const conversion = i === 0 ? null : Math.round((total / (reached[i - 1] || 1)) * 100)
          const isLast = i === counts.length - 1

          return (
            <li key={row.stage} className="flex items-center gap-3">
              <span
                aria-hidden="true"
                className={cn(
                  'tabular flex size-6 shrink-0 items-center justify-center rounded-full border text-[11px] font-bold',
                  isLast && total > 0
                    ? 'border-[hsl(var(--success))] bg-[hsl(var(--success))] text-background'
                    : 'border-border bg-card text-muted-foreground',
                )}
              >
                {i + 1}
              </span>

              <span className="w-24 shrink-0 text-[13px] sm:w-28">
                {applicationStatusLabels[row.stage as ApplicationStatus]}
              </span>

              <span className="relative h-6 min-w-0 flex-1 overflow-hidden rounded-md bg-muted">
                <motion.span
                  className={cn(
                    'block h-full rounded-md',
                    isLast ? 'bg-[hsl(var(--success))]' : 'bg-primary',
                  )}
                  initial={reduced ? false : { width: 0 }}
                  animate={{ width: `${percentOfTop}%` }}
                  transition={{ duration: 0.5, ease: 'easeOut', delay: reduced ? 0 : i * 0.07 }}
                />
              </span>

              <span className="tabular w-10 shrink-0 text-right text-[14px] font-semibold">
                {formatNumber(total)}
              </span>

              <span className="tabular w-14 shrink-0 text-right text-[11px] text-muted-foreground">
                {conversion === null ? tx('başvuru') : pct(conversion)}
              </span>
            </li>
          )
        })}
      </ol>

      {rejected > 0 && (
        <p className="border-t border-border pt-2.5 text-[12px] text-muted-foreground">{tx('Ayrıca', [])}{' '}
          <span className="tabular font-semibold text-foreground">{formatNumber(rejected)}</span>{' '}{tx('başvuru elendi ya da geri çekildi.', [])}</p>
      )}
    </div>
  )
}
