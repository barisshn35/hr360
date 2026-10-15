import { motion } from 'motion/react'
import { Ban, CalendarClock, Flame, Pencil, Target, Trash2, TrendingUp } from 'lucide-react'
import { formatShareOf, goalStatusLabels, goalStatusTone, shareOf, type Goal } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { formatGoalValue, progressOf } from './goalMath'
import { tx } from '@/lib/i18n'
import { formatDate } from '@/lib/format'

/** Yerel bugünün yyyy-MM-dd hâli (toISOString UTC'ye kaydırır). */
function todayLocal(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

export function GoalCard({
  goal,
  totalWeight,
  index,
  canEdit,
  onUpdate,
  onEdit,
  onDelete,
}: {
  goal: Goal
  totalWeight: number
  index: number
  canEdit: boolean
  onUpdate: () => void
  onEdit?: () => void
  onDelete?: () => void
}) {
  const p = progressOf(goal)
  const share = goal.status === 'Cancelled' ? 0 : shareOf(goal.weight, totalWeight)
  const over = p.source === 'numeric' && (p.raw ?? 0) > 100
  // Gecikme: son tarih geçmiş ve hedef hâlâ açık (taslak/devam ediyor).
  const overdue = !!goal.dueDate && goal.dueDate < todayLocal() && (goal.status === 'Active' || goal.status === 'Draft')
  const barColor =
    goal.status === 'Missed' ? 'hsl(var(--destructive))' : (p.pct ?? 0) >= 100 ? 'hsl(var(--success))' : (p.pct ?? 0) >= 60 ? 'hsl(var(--primary))' : 'hsl(var(--warning))'

  return (
    <motion.article
      layout
      initial={{ opacity: 0, y: 14 }}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0, scale: 0.97 }}
      transition={{ duration: 0.5, ease: EASE, delay: Math.min(index, 8) * 0.06 }}
      className={cn('group rounded-xl border bg-card p-4 transition-shadow hover:shadow-md', goal.status === 'Cancelled' ? 'border-dashed border-border opacity-70' : 'border-border')}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 gap-3">
          <span className={cn('mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-lg', p.source === 'numeric' ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground')}>
            {goal.status === 'Cancelled' ? <Ban className="size-4" aria-hidden /> : p.source === 'numeric' ? <TrendingUp className="size-4" aria-hidden /> : <Target className="size-4" aria-hidden />}
          </span>
          <div className="min-w-0">
            <h3 className={cn('text-[14px] font-semibold', goal.status === 'Cancelled' && 'line-through decoration-muted-foreground/60')}>{goal.title}</h3>
            {goal.description && <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground">{goal.description}</p>}
          </div>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          <StatusBadge tone={goalStatusTone[goal.status]}>{goalStatusLabels[goal.status]}</StatusBadge>
          {overdue && <StatusBadge tone="danger">{tx('Gecikti')}</StatusBadge>}
        </div>
      </div>
      {goal.dueDate && (
        <p className={cn('mt-2 inline-flex items-center gap-1 text-[12px]', overdue ? 'font-medium text-destructive' : 'text-muted-foreground')}>
          <CalendarClock className="size-3.5" aria-hidden />
          {tx('Son tarih: {0}', [formatDate(goal.dueDate)])}
        </p>
      )}

      {p.pct !== null && (
        <div className="mt-4">
          <div className="mb-1.5 flex items-baseline justify-between gap-2 text-[12px]">
            <span className="text-muted-foreground">
              {p.source === 'numeric' ? (
                <>
                  <span className="tabular font-medium text-foreground">{formatGoalValue(goal.currentValue ?? 0, goal.unit)}</span> / {formatGoalValue(goal.targetValue, goal.unit)}
                </>
              ) : (
                tx('Sayısal hedef yok — ilerleme durumdan')
              )}
            </span>
            <span className="tabular text-[15px] font-semibold" style={{ color: barColor }}>
              %{Math.round(p.pct)}
            </span>
          </div>
          <div className="relative h-2.5 overflow-hidden rounded-full bg-muted">
            <motion.div
              className="h-full rounded-full"
              style={{ background: barColor }}
              initial={{ width: 0 }}
              animate={{ width: `${p.pct}%` }}
              transition={{ duration: 1, ease: EASE, delay: 0.15 + index * 0.05 }}
            />
            {over && <span aria-hidden className="hr-sheen absolute inset-0" />}
          </div>
          {over && (
            <p className="mt-1.5 inline-flex items-center gap-1 rounded-md bg-[hsl(var(--success))]/10 px-1.5 py-0.5 text-[11px] font-medium text-[hsl(var(--success))]">
              <Flame className="size-3" aria-hidden />{tx('Hedef aşıldı: %{0} gerçekleşti — puana %100 olarak yansır.', [Math.round(p.raw ?? 0)])}</p>
          )}
          {p.source === 'status' && (
            <p className="mt-1.5 text-[11px] text-muted-foreground">
              {tx('{0} = %{1} sayılır (Gerçekleşti %100 · Devam ediyor %50 · Gerçekleşmedi %0).', [goalStatusLabels[goal.status], Math.round(p.pct)])}</p>
          )}
        </div>
      )}
      {goal.status === 'Cancelled' && <p className="mt-3 text-[12px] text-muted-foreground">{tx('İptal edilen hedef hedef ayağının hesabına girmez.')}</p>}

      <div className="mt-4 flex items-center justify-between gap-3 border-t border-border pt-3">
        <p className="text-[12px] text-muted-foreground">
          {tx('Ağırlık')}{' '}<span className="tabular font-semibold text-foreground">{goal.weight}</span>
          {goal.status !== 'Cancelled' && (
            <>
              {' '}{tx('· hedeflerin', [])}{' '}<span className="tabular font-semibold text-foreground">{formatShareOf(share)}</span>
            </>
          )}
        </p>
        {canEdit && (
          <div className="flex shrink-0 items-center gap-1">
            {onEdit && (
              <Button size="icon" variant="ghost" className="size-8" onClick={onEdit} aria-label={tx('Düzenle')} title={tx('Düzenle')}>
                <Pencil className="size-4" aria-hidden />
              </Button>
            )}
            {onDelete && (
              <Button size="icon" variant="ghost" className="size-8 text-destructive hover:text-destructive" onClick={onDelete} aria-label={tx('Sil')} title={tx('Sil')}>
                <Trash2 className="size-4" aria-hidden />
              </Button>
            )}
            <Button size="sm" variant="outline" onClick={onUpdate}>
              {tx('İlerlemeyi güncelle')}
            </Button>
          </div>
        )}
      </div>
    </motion.article>
  )
}
