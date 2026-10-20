import { Link } from 'react-router-dom'
import { motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { ArrowRight, Crown, Vote } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { BorderBeam } from '@/components/fx/border-beam'
import { Spotlight } from '@/components/fx/spotlight'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { periodLabel, unitLabel, useEomMe, useEomWinners, type EomWinnerUnit } from '@/api/eom'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { Panel } from '@/components/ui/panel'

/**
 * Kartta gösterilecek kazananlar: kişinin kendi sonuç birimi (ana) ve iki aşamalıda kendi alt biriminin
 * kazananı (ikincil). Kişi hiçbir birimde değilse (ör. İK) ilk sonuç birimleri.
 */
export function pickWinners(units: EomWinnerUnit[]): { main: EomWinnerUnit | null; side: EomWinnerUnit[] } {
  const finals = units.filter((u) => u.isFinal)
  const main = finals.find((u) => u.isMine) ?? null
  if (main) {
    const sub = units.find((u) => !u.isFinal && u.isMine && u.winner.id !== main.winner.id)
    return { main, side: sub ? [sub] : [] }
  }
  return { main: finals[0] ?? null, side: finals.slice(1, 4) }
}

/** Halkalı fotoğraf: altın-yeşil degrade halka ve hafif parıltı. */
function Halo({ unit, size }: { unit: EomWinnerUnit; size: number }) {
  const reduced = useReducedMotion()
  return (
    <span className="relative inline-grid shrink-0 place-items-center">
      <motion.span aria-hidden="true" className="absolute -inset-1.5 rounded-full bg-[conic-gradient(from_0deg,#f5c542,hsl(var(--primary)),#f5c542)] opacity-80 blur-[1px]"
        animate={reduced ? undefined : { rotate: 360 }} transition={{ duration: 12, repeat: Infinity, ease: 'linear' }} />
      <span className="relative rounded-full bg-card p-1">
        <EmployeeAvatar id={unit.winner.id} name={unit.winner.name} version={unit.winner.photoVersion} size={size} />
      </span>
      <span className="absolute -top-3 left-1/2 -translate-x-1/2 rounded-full bg-amber-400 p-1 text-amber-950 shadow-lg"><Crown className="size-3.5" /></span>
    </span>
  )
}

/**
 * "Ayın çalışanı" kartı (ana sayfa ve oylama sayfası). Bir sonraki sonuç açıklanana kadar kazananın
 * fotoğrafını gösterir; oylama açıksa "Oyunu kullan" düğmesi çıkar. Modül kapalıysa hiçbir şey çizmez.
 */
export function WinnerSpotlight({ className, compact = false }: { className?: string; compact?: boolean }) {
  const w = useEomWinners()
  const me = useEomMe()
  const canAct = (me.data?.units ?? []).some((u) => u.action)
  if (!w.data?.enabled) return null
  const { main, side } = pickWinners(w.data.units ?? [])
  if (!main && !canAct) return null

  return (
    <Panel className={cn('relative h-full gap-0 overflow-hidden py-0', className)}>
      <BorderBeam size={200} duration={16} />
      <Spotlight size={360} />
      <div aria-hidden="true" className="pointer-events-none absolute -top-16 -right-10 size-48 rounded-full bg-amber-400/10 blur-3xl" />
      <div className={cn('relative flex h-full flex-col gap-4', compact ? 'p-5' : 'p-6 sm:p-7')}>
        <div className="flex items-center justify-between gap-2 text-[12.5px]">
          <span className="flex items-center gap-1.5 font-medium text-amber-500 dark:text-amber-300"><Crown className="size-4" />{tx('Ayın çalışanı')}</span>
          {w.data.period && <span className="text-muted-foreground">{periodLabel(w.data.period.key)}</span>}
        </div>

        {main ? (
          <div className={cn('flex items-center gap-5', compact ? 'flex-col text-center sm:flex-row sm:text-left' : 'flex-col text-center sm:flex-row sm:text-left')}>
            <Halo unit={main} size={compact ? 84 : 112} />
            <div className="min-w-0">
              <p className={cn('font-semibold tracking-tight', compact ? 'text-[19px]' : 'text-[24px]')}>{main.winner.name}</p>
              <p className="text-[13px] text-muted-foreground">{main.winner.position ?? '—'}</p>
              <p className="mt-1 text-[12.5px] text-muted-foreground">{unitLabel(main)}</p>
            </div>
          </div>
        ) : (
          <p className="text-[13.5px] text-muted-foreground">{tx('İlk ayın çalışanı oylaması sürüyor.')}</p>
        )}

        {side.length > 0 && (
          <ul className="flex flex-wrap gap-2">
            {side.map((u) => (
              <li key={u.id} className="flex items-center gap-2 rounded-full border border-border bg-card/60 py-1 pr-3 pl-1">
                <EmployeeAvatar id={u.winner.id} name={u.winner.name} version={u.winner.photoVersion} size={28} />
                <span className="text-[12px]"><span className="font-medium">{u.winner.name}</span> <span className="text-muted-foreground">· {unitLabel(u)}</span></span>
              </li>
            ))}
          </ul>
        )}

        <div className="mt-auto flex flex-wrap gap-2">
          {canAct && (
            <Button size="sm" asChild>
              <Link to="/panel/ayin-calisani"><Vote className="size-4" />{me.data?.period?.status === 'Nominating' ? tx('Aday göster') : tx('Oyunu kullan')}</Link>
            </Button>
          )}
          {compact && main && (
            <Button size="sm" variant="ghost" asChild>
              <Link to="/panel/ayin-calisani">{tx('Tümü')}<ArrowRight className="size-4" /></Link>
            </Button>
          )}
        </div>
      </div>
    </Panel>
  )
}
