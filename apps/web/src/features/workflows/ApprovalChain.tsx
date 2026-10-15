import { motion, useReducedMotion } from 'motion/react'
import { Lock } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { StepDecisionBadge } from '@/components/ui/ModuleBadges'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ApprovalStep } from '@/api/types'
import { tx } from '@/lib/i18n'

/**
 * Ürünün merkezi.
 *
 * İş kuralı görselleştirilir: order'ı küçük olan adım karara bağlanmadan
 * sonraki adım açılmaz. Kilitli adım soluk ve eylemsizdir, sırası gelen
 * adım tek vurgudur — kullanıcı API'nin 400'üne hiç çarpmaz.
 */

export function activeStepId(steps: ApprovalStep[]): string | null {
  return (
    [...steps].sort((a, b) => a.order - b.order).find((s) => s.decision === 'Pending')?.id ?? null
  )
}

type NodeState = 'approved' | 'rejected' | 'delegated' | 'live' | 'locked'

function stateOf(step: ApprovalStep, isCurrent: boolean): NodeState {
  if (step.decision === 'Approved') return 'approved'
  if (step.decision === 'Rejected') return 'rejected'
  if (step.decision === 'Delegated') return 'delegated'
  return isCurrent ? 'live' : 'locked'
}

const NODE: Record<NodeState, string> = {
  approved: 'border-[hsl(var(--success))] bg-[hsl(var(--success))] text-background',
  rejected: 'border-destructive bg-destructive text-destructive-foreground',
  delegated: 'border-border bg-muted text-muted-foreground',
  live: 'border-primary bg-primary text-primary-foreground',
  locked: 'border-border bg-background text-muted-foreground',
}

export function ApprovalChain({
  steps,
  nameOf,
  canDecide,
  canDelegate,
  onDecide,
  onDelegate,
  busyStepId,
}: {
  steps: ApprovalStep[]
  nameOf: (id: string | null | undefined) => string
  canDecide: boolean
  canDelegate: boolean
  onDecide: (step: ApprovalStep, approve: boolean) => void
  onDelegate: (step: ApprovalStep) => void
  busyStepId?: string | null
}) {
  const reduced = useReducedMotion()
  const ordered = [...steps].sort((a, b) => a.order - b.order)
  const currentId = activeStepId(ordered)

  return (
    <ol className="relative px-4 py-3">
      {ordered.map((step, i) => {
        const isCurrent = step.id === currentId
        const state = stateOf(step, isCurrent)
        const isLocked = state === 'locked'
        const isLast = i === ordered.length - 1
        const busy = busyStepId === step.id

        return (
          <motion.li key={step.id} layout={!reduced} className="relative flex gap-4 pb-2">
            {/* Zincir: düğüm + onu sonrakine bağlayan çizgi */}
            <div className="relative flex w-7 shrink-0 flex-col items-center">
              <motion.span
                layout={!reduced}
                className={cn(
                  'tabular relative z-10 flex size-7 items-center justify-center rounded-full border-2',
                  'text-[12px] font-bold transition-colors duration-300',
                  NODE[state],
                )}
              >
                {isLocked ? <Lock aria-hidden="true" className="size-3" /> : step.order}
              </motion.span>

              {/* Sırası gelen adım tek canlı işarettir. */}
              {isCurrent && !reduced && (
                <motion.span
                  aria-hidden="true"
                  className="absolute top-0 size-7 rounded-full border-2 border-primary"
                  animate={{ scale: [1, 1.75], opacity: [0.55, 0] }}
                  transition={{ duration: 2.2, repeat: Infinity, ease: 'easeOut' }}
                />
              )}

              {!isLast && (
                <span
                  aria-hidden="true"
                  className={cn(
                    'w-px flex-1 transition-colors duration-300',
                    state === 'approved' || state === 'delegated'
                      ? 'bg-border'
                      : state === 'rejected'
                        ? 'bg-destructive/40'
                        : 'bg-border/60',
                  )}
                />
              )}
            </div>

            <div
              className={cn(
                'min-w-0 flex-1 pb-6 transition-opacity duration-300',
                isLocked && 'opacity-55',
              )}
            >
              <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                <p className="text-[14px] font-semibold">{nameOf(step.approverEmployeeId)}</p>
                {isLocked ? (
                  <span className="text-[11px] text-muted-foreground">{tx('Sırası gelmedi')}</span>
                ) : (
                  <StepDecisionBadge decision={step.decision} />
                )}
              </div>

              {step.delegatedToEmployeeId && (
                <p className="mt-1 text-[13px] text-muted-foreground">
                  {tx('{0} kişisine devredildi', [nameOf(step.delegatedToEmployeeId)])}</p>
              )}

              {step.comment && (
                <p className="mt-1.5 border-l-2 border-border pl-3 text-[13px] leading-relaxed break-words text-muted-foreground">
                  {step.comment}
                </p>
              )}

              {step.decidedAt && (
                <p className="tabular mt-1.5 text-[11px] text-muted-foreground">
                  {formatDateTime(step.decidedAt)}
                </p>
              )}

              {isLocked && (
                <p className="mt-1.5 text-[12px] text-muted-foreground">
                  {tx('{0}. adım karara bağlanınca açılır', [step.order - 1])}</p>
              )}

              {isCurrent && (canDecide || canDelegate) && (
                <motion.div
                  layout={!reduced}
                  initial={reduced ? false : { opacity: 0, y: -4 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ duration: 0.24, ease: 'easeOut', delay: 0.05 }}
                  className="mt-3 flex flex-wrap gap-2"
                >
                  {canDecide && (
                    <>
                      <Button
                        size="sm"
                        className="cursor-pointer"
                        disabled={busy}
                        onClick={() => onDecide(step, true)}
                      >
                        {tx('Onayla')}
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        className="cursor-pointer"
                        disabled={busy}
                        onClick={() => onDecide(step, false)}
                      >
                        {tx('Reddet')}
                      </Button>
                    </>
                  )}
                  {canDelegate && (
                    <Button
                      size="sm"
                      variant="ghost"
                      className="cursor-pointer"
                      disabled={busy}
                      onClick={() => onDelegate(step)}
                    >
                      {tx('Devret')}
                    </Button>
                  )}
                </motion.div>
              )}
            </div>
          </motion.li>
        )
      })}
    </ol>
  )
}
