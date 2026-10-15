/**
 * Analiz parçaları: yayılım notu, puansız üyeler, sıralı üye listesi,
 * yükselen/düşenler, küçük istatistik kutusu.
 *
 * Yayılım notu (spreadNote) doluysa HER ZAMAN gösterilir: ekip içi farklar
 * küçükse sıralama tek başına anlamlı değildir; gizlemek arayüzü yanıltıcı
 * yapar.
 */

import type { ReactNode } from 'react'
import { motion } from 'motion/react'
import { ArrowDownRight, ArrowUpRight, Hourglass, Scale, UserRoundX } from 'lucide-react'
import { formatScore, type CycleMovement, type MemberScore, type PersonRef, type Thresholds } from '@/api/performance'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { AnimatedNumber } from './AnimatedNumber'
import { PersonAvatar } from './people'
import { scoreColor } from './score'
import { tx } from '@/lib/i18n'

export function StatTile({ label, value, hint, format = (v: number) => formatScore(v) }: { label: string; value: number | null; hint?: ReactNode; format?: (v: number) => string }) {
  return (
    <div className="rounded-xl border border-border bg-card p-3.5">
      <p className="text-[11px] text-muted-foreground">{label}</p>
      <p className="mt-0.5 text-[22px] leading-tight font-semibold">{value === null ? '—' : <AnimatedNumber value={value} format={format} duration={0.9} />}</p>
      {hint && <p className="mt-0.5 text-[11px] text-muted-foreground">{hint}</p>}
    </div>
  )
}

export function SpreadNote({ note }: { note: string | null }) {
  if (!note) return null
  return (
    <motion.div
      role="note"
      initial={{ opacity: 0, y: -6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.4, ease: EASE }}
      className="flex items-start gap-3 rounded-xl border border-primary/30 bg-primary/5 px-4 py-3"
    >
      <Scale className="mt-0.5 size-4.5 shrink-0 text-primary" aria-hidden />
      <div>
        <p className="text-[13px] font-semibold">{tx('Sıralamayı tek başına okumayın')}</p>
        <p className="mt-0.5 text-[13px] leading-relaxed text-foreground/80">{note}</p>
      </div>
    </motion.div>
  )
}

export function UnscoredList({ people, nameOf }: { people: PersonRef[]; nameOf: (id: string, fallback?: string | null) => string }) {
  return (
    <div className="rounded-xl border border-dashed border-border p-4">
      <p className="flex items-center gap-2 text-[13px] font-semibold">
        <UserRoundX className="size-4 text-muted-foreground" aria-hidden />
        {tx('Puanı olmayan üyeler')}
        <span className="tabular rounded-md bg-muted px-1.5 text-[11px] font-medium text-muted-foreground">{people.length}</span>
      </p>
      {people.length === 0 ? (
        <p className="mt-1.5 text-[12px] text-muted-foreground">{tx('Herkesin en az bir puanı var.')}</p>
      ) : (
        <>
          <p className="mt-0.5 text-[12px] text-muted-foreground">{tx('Henüz değerlendirme gönderilmedi; ortalamaya ve sıralamaya dahil değiller.')}</p>
          <ul className="mt-3 flex flex-wrap gap-2">
            {people.map((p) => (
              <li key={p.employeeId} className="inline-flex items-center gap-2 rounded-full border border-border bg-background py-1 pr-3 pl-1 text-[12px]">
                <PersonAvatar id={p.employeeId} name={nameOf(p.employeeId, p.name)} size="xs" />
                {nameOf(p.employeeId, p.name)}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  )
}

export function RankedMembers({
  members,
  thresholds,
  nameOf,
  selectedId,
  onSelect,
}: {
  members: MemberScore[]
  thresholds: Thresholds | null
  nameOf: (id: string, fallback?: string | null) => string
  selectedId?: string | null
  onSelect?: (id: string) => void
}) {
  return (
    <ol className="flex flex-col gap-0.5">
      {members.map((m, i) => {
        const color = scoreColor(m.score, thresholds)
        const name = nameOf(m.employeeId, m.name)
        return (
          <motion.li key={m.employeeId} initial={{ opacity: 0, x: -8 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: Math.min(i, 14) * 0.03, duration: 0.35 }}>
            <button
              type="button"
              onClick={() => onSelect?.(m.employeeId)}
              disabled={!onSelect}
              className={cn(
                'flex w-full items-center gap-3 rounded-lg px-2 py-1.5 text-left transition-colors',
                onSelect && 'hover:bg-muted/60',
                selectedId === m.employeeId && 'bg-primary/10 ring-1 ring-primary/25',
              )}
            >
              <span className="tabular w-5 shrink-0 text-right text-[11px] text-muted-foreground">{m.rank ?? i + 1}</span>
              <PersonAvatar id={m.employeeId} name={name} size="xs" />
              <span className="min-w-0 flex-1 truncate text-[13px]">{name}</span>
              <span className="relative hidden h-1.5 w-28 overflow-hidden rounded-full bg-muted sm:block">
                <motion.span className="absolute inset-y-0 left-0 rounded-full" style={{ background: color, opacity: m.isProvisional ? 0.55 : 1 }} initial={{ width: 0 }} animate={{ width: `${m.score}%` }} transition={{ duration: 0.8, ease: EASE, delay: 0.1 + i * 0.02 }} />
              </span>
              <span className="tabular flex w-20 shrink-0 items-center justify-end gap-1 text-[13px] font-semibold" style={{ color }}>
                {m.isProvisional && <Hourglass className="size-3 text-[hsl(var(--warning))]" aria-label={tx('geçici')} />}
                {formatScore(m.score)}
              </span>
            </button>
          </motion.li>
        )
      })}
    </ol>
  )
}

export function MovementList({ title, items, kind, nameOf }: { title: string; items: CycleMovement[]; kind: 'up' | 'down'; nameOf: (id: string, fallback?: string | null) => string }) {
  const Icon = kind === 'up' ? ArrowUpRight : ArrowDownRight
  const tone = kind === 'up' ? 'hsl(var(--success))' : 'hsl(var(--destructive))'
  return (
    <div className="rounded-xl border p-4" style={{ borderColor: `color-mix(in oklab, ${tone} 35%, transparent)`, background: `color-mix(in oklab, ${tone} 5%, transparent)` }}>
      <p className="flex items-center gap-2 text-[13px] font-semibold" style={{ color: tone }}>
        <Icon className="size-4" aria-hidden />
        {title}
        <span className="tabular text-[11px] font-medium">{items.length}</span>
      </p>
      {items.length === 0 ? (
        <p className="mt-2 text-[12px] text-muted-foreground">{kind === 'up' ? tx('Belirgin yükselen yok.') : tx('Belirgin düşen yok.')}</p>
      ) : (
        <ul className="mt-2 flex flex-col gap-1">
          {items.map((m, i) => (
            <motion.li key={m.employeeId} initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.04 }} className="flex items-center gap-2 text-[13px]">
              <PersonAvatar id={m.employeeId} name={nameOf(m.employeeId, m.name)} size="xs" />
              <span className="min-w-0 flex-1 truncate">{nameOf(m.employeeId, m.name)}</span>
              <span className="tabular text-[12px] text-muted-foreground">
                {formatScore(m.from)} → {formatScore(m.to)}
              </span>
              <span className="tabular w-16 text-right text-[12px] font-semibold" style={{ color: tone }}>
                {m.delta > 0 ? '+' : '−'}
                {formatScore(Math.abs(m.delta))}
              </span>
            </motion.li>
          ))}
        </ul>
      )}
    </div>
  )
}
