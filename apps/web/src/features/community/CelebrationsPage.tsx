import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion, useReducedMotion } from 'motion/react'
import { Cake, PartyPopper, Sparkles, Trophy } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { Tabs } from '@/components/ui/Tabs'
import { engagementApi, type Celebration } from '@/api/engagement'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Initials, TenantGate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const KIND: Record<Celebration['kind'], { label: string; icon: React.ElementType; color: string }> = {
  birthday: { label: tx('Doğum günü'), icon: Cake, color: 'text-pink-400 bg-pink-400/10' },
  anniversary: { label: tx('İş yıl dönümü'), icon: Trophy, color: 'text-amber-400 bg-amber-400/10' },
  newcomer: { label: tx('Aramıza katıldı'), icon: Sparkles, color: 'text-primary bg-primary/10' },
}

function when(c: Celebration) {
  if (c.kind === 'newcomer') return c.inDays === 0 ? tx('bugün başladı') : tx('{0} gün önce başladı', [-c.inDays])
  if (c.inDays === 0) return tx('bugün 🎉')
  if (c.inDays === 1) return tx('yarın')
  return tx('{0} gün sonra', [c.inDays])
}

/** Konfeti: hafif, CSS/motion parçacıkları — kütüphane yok. */
export function Confetti({ burst }: { burst: number }) {
  const reduced = useReducedMotion()
  const pieces = useMemo(
    () => Array.from({ length: 70 }, (_, i) => ({
      id: `${burst}-${i}`,
      x: (Math.random() - 0.5) * 900,
      y: -(200 + Math.random() * 420),
      rot: Math.random() * 720 - 360,
      color: ['#10b981', '#f59e0b', '#ec4899', '#38bdf8', '#a78bfa', '#f43f5e'][i % 6],
      size: 5 + Math.random() * 7,
      delay: Math.random() * 0.15,
    })),
    [burst],
  )
  if (reduced || burst === 0) return null
  return (
    <div aria-hidden="true" className="pointer-events-none fixed inset-x-0 bottom-0 z-[60] flex justify-center">
      {pieces.map((p) => (
        <motion.span
          key={p.id}
          initial={{ x: 0, y: 0, opacity: 1, rotate: 0 }}
          animate={{ x: p.x, y: [0, p.y, p.y + 520], opacity: [1, 1, 0], rotate: p.rot }}
          transition={{ duration: 2.4, delay: p.delay, ease: [0.2, 0.7, 0.4, 1] }}
          className="absolute bottom-0 rounded-[2px]"
          style={{ width: p.size, height: p.size * 0.45, background: p.color }}
        />
      ))}
    </div>
  )
}

export function CelebrationList({ days = 30, compact = false, kind }: { days?: number; compact?: boolean; kind?: Celebration['kind'] }) {
  const q = useQuery({ queryKey: ['celebrations', days], queryFn: ({ signal }) => engagementApi.celebrations(days, signal) })
  const [burst, setBurst] = useState(0)
  const send = useAction((c: Celebration) => engagementApi.sendKudos({
    toEmployeeId: c.employeeId, badge: 'thanks',
    message: c.kind === 'birthday' ? tx('Doğum günün kutlu olsun! 🎂') : c.kind === 'anniversary' ? tx('{0}. yılın kutlu olsun, iyi ki buradasın! 🏆', [c.years]) : tx('Aramıza hoş geldin! 👋'),
  }), { success: tx('Tebrik gönderildi'), invalidate: [['kudos']], onDone: () => setBurst((b) => b + 1) })

  if (q.isPending) return <RowsSkeleton rows={3} columns={2} />
  if (q.isError) return <ErrorState message={(q.error as Error).message} onRetry={() => q.refetch()} />
  const items = q.data.filter((c) => !kind || c.kind === kind)
  if (items.length === 0)
    return compact ? <p className="px-2 py-3 text-[13px] text-muted-foreground">{tx('Önümüzdeki günlerde kutlama yok.')}</p> : (
      <EmptyState icon={PartyPopper} title={tx('Yaklaşan kutlama yok')} detail={tx('Doğum günleri, çalışanların profilinde paylaşmayı seçtiği tarihlerden gelir.')} />
    )
  return (
    <>
      <Confetti burst={burst} />
      <ul className={cn('grid gap-2.5', !compact && 'sm:grid-cols-2 xl:grid-cols-3')}>
        {items.map((c, i) => {
          const K = KIND[c.kind]
          const today = c.inDays === 0 && c.kind !== 'newcomer'
          return (
            <motion.li
              key={`${c.kind}-${c.employeeId}`}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: Math.min(i * 0.05, 0.5) }}
              className={cn('flex items-center gap-3 rounded-2xl border border-border p-3', today ? 'bg-gradient-to-r from-primary/15 to-transparent shadow-[0_0_0_1px_hsl(var(--primary)/0.4)]' : 'bg-card/40')}
            >
              <div className="relative">
                <Initials name={c.name} size={compact ? 32 : 42} />
                <span className={cn('absolute -right-1 -bottom-1 grid size-5 place-items-center rounded-full ring-2 ring-background', K.color)}>
                  <K.icon className="size-3" />
                </span>
              </div>
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13.5px] font-medium">{c.name}</p>
                <p className="truncate text-[12px] text-muted-foreground">
                  {K.label}
                  {c.years ? tx(' · {0}. yıl', [c.years]) : ''} · {when(c)}
                  {!compact && c.department ? ` · ${c.department}` : ''}
                </p>
              </div>
              {!compact && (
                <Button size="sm" variant="outline" onClick={() => send.mutate(c)} disabled={send.isPending}>
                  {tx('Tebrik et')}
                </Button>
              )}
              {compact && <span className="text-[11.5px] text-muted-foreground">{formatDate(c.date)}</span>}
            </motion.li>
          )
        })}
      </ul>
    </>
  )
}

export function CelebrationsPage() {
  const [tab, setTab] = useState<'all' | Celebration['kind']>('all')
  return (
    <TenantGate>
      <PageHeader title={tx('Kutlamalar')} description={tx('Doğum günleri, iş yıl dönümleri ve aramıza yeni katılanlar. Bir tıkla tebrik gönderin.')} />
      <div className="mb-5">
        <Tabs
          label={tx('Kutlama türü')}
          value={tab}
          onChange={setTab}
          tabs={[
            { key: 'all', label: tx('Tümü') },
            { key: 'birthday', label: tx('Doğum günleri') },
            { key: 'anniversary', label: tx('Yıl dönümleri') },
            { key: 'newcomer', label: tx('Yeni katılanlar') },
          ]}
        />
      </div>
      <Panel>
        <PanelHead title={tx('Önümüzdeki 60 gün')} note={tx('Doğum günü yalnızca kişi profilinde paylaşmayı seçtiyse görünür.')} />
        <PanelBody>
          <CelebrationList days={60} kind={tab === 'all' ? undefined : tab} />
        </PanelBody>
      </Panel>
    </TenantGate>
  )
}
