import { motion } from 'motion/react'
import { useBadges, badgeGroupLabels, badgeInfo, type BadgeGroup } from '@/api/badges'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { RowsSkeleton } from '@/components/ui/States'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

/** Kazanılmış rozetlerin kompakt şeridi (profil başlığı, çalışan sayfası). */
export function BadgeStrip({ employeeId, max = 8 }: { employeeId?: string; max?: number }) {
  const q = useBadges(employeeId)
  const earned = (q.data?.badges ?? []).filter((b) => b.earned)
  if (!earned.length) return null
  return (
    <div className="flex flex-wrap items-center gap-1" aria-label={tx('Rozetler')}>
      {earned.slice(0, max).map((b, i) => {
        const info = badgeInfo(b.id)
        return (
          <motion.span key={b.id} initial={{ opacity: 0, scale: 0.6 }} animate={{ opacity: 1, scale: 1 }} transition={{ delay: i * 0.04 }}
            title={`${info.title}${b.earnedAt ? ` · ${formatDate(b.earnedAt)}` : ''}`}
            className="grid size-7 place-items-center rounded-full bg-amber-400/10 text-[15px] ring-1 ring-amber-400/30">{info.emoji}</motion.span>
        )
      })}
      {earned.length > max && <span className="text-[12px] text-muted-foreground">+{earned.length - max}</span>}
    </div>
  )
}

/** Kişinin kendi rozet koleksiyonu: kazanılanlar tarihli, diğerleri ilerlemeyle. */
export function BadgeCollection() {
  const q = useBadges()
  const groups = badgeGroupLabels()
  if (q.isPending) return <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel>
  const list = q.data?.badges ?? []
  const earned = list.filter((b) => b.earned).length
  return (
    <div className="space-y-5">
      <p className="text-[13px] text-muted-foreground">{tx('{0}/{1} rozet kazandınız. Rozetler profilinizde çalışma arkadaşlarınıza da görünür; ilerlemeniz yalnızca size.', [earned, list.length])}</p>
      {(Object.keys(groups) as BadgeGroup[]).map((g) => {
        const items = list.filter((b) => b.group === g)
        if (!items.length) return null
        return (
          <Panel key={g}>
            <PanelHead title={groups[g]} note={tx('{0}/{1}', [items.filter((b) => b.earned).length, items.length])} />
            <PanelBody className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {items.map((b) => {
                const info = badgeInfo(b.id)
                const pct = b.target ? Math.round(((b.value ?? 0) / b.target) * 100) : 0
                return (
                  <div key={b.id} className={cn('flex items-center gap-3 rounded-2xl border p-3', b.earned ? 'border-amber-400/40 bg-amber-400/5' : 'border-border')}>
                    <span className={cn('grid size-11 shrink-0 place-items-center rounded-full text-[22px]', b.earned ? 'bg-amber-400/15 ring-1 ring-amber-400/40' : 'bg-muted opacity-50 grayscale')}>{info.emoji}</span>
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[13.5px] font-semibold">{info.title}</p>
                      <p className="truncate text-[12px] text-muted-foreground">{b.earned ? (b.earnedAt ? tx('{0} tarihinde kazanıldı', [formatDate(b.earnedAt)]) : tx('Kazanıldı')) : info.how}</p>
                      {!b.earned && b.target != null && (
                        <div className="mt-1.5 flex items-center gap-2">
                          <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted"><div className="h-full rounded-full bg-primary" style={{ width: `${pct}%` }} /></div>
                          <span className="text-[11px] text-muted-foreground tabular-nums">{b.value}/{b.target}</span>
                        </div>
                      )}
                    </div>
                  </div>
                )
              })}
            </PanelBody>
          </Panel>
        )
      })}
    </div>
  )
}
