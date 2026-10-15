import { motion } from 'motion/react'
import { Award, CheckCheck, ClipboardCheck, Eye, EyeOff, GraduationCap, MoreHorizontal, OctagonAlert, Quote, Ruler, Target, TrendingUp } from 'lucide-react'
import { labelOf, reasonLabels, sentimentLabels, sentimentTone, type Feedback, type FeedbackReason, type FeedbackSentiment } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { Chip } from '../components/controls'
import { PersonAvatar } from '../components/people'
import { tx } from '@/lib/i18n'

export const REASON_ICON: Record<FeedbackReason, React.ElementType> = {
  Recognition: Award,
  GoalProgress: Target,
  Improvement: TrendingUp,
  Coaching: GraduationCap,
  Incident: OctagonAlert,
  PeerObservation: Eye,
  ReviewSummary: ClipboardCheck,
  Other: MoreHorizontal,
}

export const SENTIMENT_COLOR: Record<FeedbackSentiment, string> = {
  Positive: 'hsl(var(--success))',
  Neutral: 'hsl(var(--muted-foreground))',
  Constructive: 'hsl(var(--warning))',
}

export function FeedbackCard({
  feedback: f,
  index = 0,
  perspective,
  nameOf,
  onMarkRead,
  marking,
  preview,
}: {
  feedback: Feedback
  index?: number
  /** Kimin gözünden: gelen kutusunda alıcı adı tekrarlanmaz. */
  perspective: 'received' | 'sent' | 'manager'
  nameOf: (id: string, fallback?: string | null) => string
  onMarkRead?: () => void
  marking?: boolean
  preview?: boolean
}) {
  const Icon = REASON_ICON[f.reason] ?? MoreHorizontal
  const color = SENTIMENT_COLOR[f.sentiment] ?? SENTIMENT_COLOR.Neutral
  const from = nameOf(f.from.employeeId, f.from.name)
  const to = nameOf(f.to.employeeId, f.to.name)
  const unread = !f.isRead && perspective === 'received'

  return (
    <motion.article
      layout={!preview}
      initial={preview ? false : { opacity: 0, y: 10 }}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0, scale: 0.98 }}
      transition={{ duration: 0.4, ease: EASE, delay: preview ? 0 : Math.min(index, 10) * 0.04 }}
      className={cn('relative overflow-hidden rounded-xl border bg-card p-4 transition-shadow', unread ? 'border-primary/40 shadow-sm' : 'border-border', !preview && 'hover:shadow-md')}
    >
      <span aria-hidden className="absolute inset-y-0 left-0 w-1" style={{ background: color }} />
      <div className="flex items-start gap-3">
        <PersonAvatar id={perspective === 'sent' ? f.to.employeeId : f.from.employeeId} name={perspective === 'sent' ? to : from} size="md" />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <p className="text-[14px] font-semibold">
              {perspective === 'received' ? from : perspective === 'sent' ? tx('{0} için', [to]) : `${from} → ${to}`}
            </p>
            {unread && (
              <span className="inline-flex items-center gap-1 text-[11px] font-medium text-primary">
                <span className="size-1.5 animate-pulse rounded-full bg-primary" />
                {tx('okunmadı')}
              </span>
            )}
            <span className="ml-auto text-[11px] text-muted-foreground" title={formatDateTime(f.createdAt)}>
              {preview ? tx('şimdi') : formatRelativeToNow(f.createdAt)}
            </span>
          </div>
          <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
            <Chip>
              <Icon className="size-3" aria-hidden />
              {labelOf(reasonLabels, f.reason)}
            </Chip>
            <StatusBadge tone={sentimentTone[f.sentiment] ?? 'neutral'}>{labelOf(sentimentLabels, f.sentiment)}</StatusBadge>
            {f.metricName && (
              <Chip tone="primary">
                <Ruler className="size-3" aria-hidden />
                {f.metricName}
              </Chip>
            )}
            {!f.visibleToEmployee && (
              <Chip tone="warning">
                <EyeOff className="size-3" aria-hidden />
                {tx('Yalnızca yöneticiler görür')}
              </Chip>
            )}
          </div>
          {f.reasonDetail && (
            <p className="mt-3 flex gap-2 rounded-md border-l-2 bg-muted/40 px-3 py-2 text-[12px] leading-relaxed text-foreground/80" style={{ borderColor: color }}>
              <Quote className="mt-0.5 size-3 shrink-0 text-muted-foreground" aria-hidden />
              <span>
                <span className="font-medium text-foreground">{tx('Gerekçe:')}{' '}</span>
                {f.reasonDetail}
              </span>
            </p>
          )}
          <p className="mt-2.5 text-[13px] leading-relaxed whitespace-pre-line">{f.body || <span className="text-muted-foreground">{tx('Metin…')}</span>}</p>
          {(unread && onMarkRead) || (f.isRead && perspective === 'received' && f.readAt) ? (
            <div className="mt-3 flex items-center justify-end gap-2">
              {f.isRead && f.readAt && <span className="text-[11px] text-muted-foreground">{tx('Okundu · {0}', [formatDateTime(f.readAt)])}</span>}
              {unread && onMarkRead && (
                <Button size="sm" variant="outline" onClick={onMarkRead} disabled={marking}>
                  <CheckCheck aria-hidden />
                  {tx('Okundu işaretle')}
                </Button>
              )}
            </div>
          ) : null}
        </div>
      </div>
    </motion.article>
  )
}
