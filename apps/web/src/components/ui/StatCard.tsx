/**
 * Genel bakış metrik kartı.
 *
 * Kaynak: 21st.dev "Stat Card" (felipemenezes098, id 26138) — sabit demo
 * içeriği prop'lara açıldı, trend rozetinin sabit emerald rengi tasarım
 * token'larına çevrildi (yoksa koyu temada rozet kopuk görünüyordu).
 *
 * İskelet varyantı 21st.dev "Stat Cards Skeleton" (id 18999) uyarlaması.
 */

import { TrendingDown, TrendingUp, Minus } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { Sparkline } from '@/components/ui/sparkline'
import { GlowingEffect } from '@/components/fx/glowing-effect'
import { Spotlight } from '@/components/fx/spotlight'
import { BorderBeam } from '@/components/fx/border-beam'
import { CountUp } from '@/motion/primitives'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

export type TrendDirection = 'up' | 'down' | 'flat'

/** Yükselişin iyi mi kötü mü olduğu metriğe göre değişir (izin bakiyesi vs. gecikmiş onay). */
export type TrendSense = 'positive' | 'negative' | 'neutral'

const TREND_ICON: Record<TrendDirection, React.ElementType> = {
  up: TrendingUp,
  down: TrendingDown,
  flat: Minus,
}

function toneClass(direction: TrendDirection, sense: TrendSense): string {
  if (sense === 'neutral' || direction === 'flat') return 'text-muted-foreground'
  const good = sense === 'positive' ? direction === 'up' : direction === 'down'
  return good ? 'text-[hsl(var(--success))]' : 'text-destructive'
}

export interface StatCardProps {
  label: string
  /** Hazır biçimlenmiş değer. Sayılabilir bir metrikse `count` tercih edin. */
  value?: string | number
  /**
   * Sayısal metrik: kart görününce 0'dan bu değere sayar. Ham sayı verilir,
   * biçimlendirme `format` ile — böylece ara kareler de tr-TR biçiminde olur.
   */
  count?: number
  format?: (value: number) => string
  icon?: React.ElementType
  /** Örn. "+%20,1" — biçimlendirme çağıran tarafta. */
  trend?: string
  trendDirection?: TrendDirection
  /** Yükseliş iyi mi? Varsayılan: iyi. */
  trendSense?: TrendSense
  /** Rozetin yanındaki açıklama, ör. "geçen aya göre". */
  compareLabel?: string
  /** Gerçek bir zaman serisi varsa kartın altında küçük trend grafiği çizilir. */
  series?: number[]
  seriesLabels?: string[]
  /** Dikkat isteyen metrik (ör. süresi geçen onay > 0): kenar ve ikon uyarı tonunda. */
  attention?: boolean
  className?: string
}

export function StatCard({
  label,
  value,
  count,
  format,
  icon: Icon,
  trend,
  trendDirection = 'flat',
  trendSense = 'positive',
  compareLabel,
  series,
  seriesLabels,
  attention = false,
  className,
}: StatCardProps) {
  const TrendIcon = TREND_ICON[trendDirection]

  return (
    <div
      className={cn(
        'surface group relative isolate flex w-full flex-col overflow-hidden rounded-2xl p-5',
        'transition-[border-color,transform] duration-300 hover:-translate-y-0.5 hover:border-primary/30',
        attention && 'border-[hsl(var(--warning))]/35',
        className,
      )}
    >
      <GlowingEffect />
      <Spotlight />
      {attention && <BorderBeam colorFrom="hsl(var(--warning))" colorTo="hsl(20 95% 60%)" duration={8} />}
      {/* Üst kenarda ince zümrüt ışık; üzerine gelince belirginleşir. */}
      <span
        aria-hidden="true"
        className={cn(
          'pointer-events-none absolute inset-x-6 top-0 h-px bg-gradient-to-r from-transparent to-transparent opacity-60 transition-opacity duration-300 group-hover:opacity-100',
          attention ? 'via-[hsl(var(--warning))]' : 'via-primary',
        )}
      />
      <span
        aria-hidden="true"
        className="pointer-events-none absolute -top-16 -right-16 -z-10 size-40 rounded-full bg-foreground/[0.04] opacity-0 blur-3xl transition-opacity duration-500 group-hover:opacity-100"
      />

      <div className="flex items-start justify-between gap-3">
        <p className="text-[13px] font-medium text-muted-foreground">{label}</p>
        {Icon && (
          <span
            className={cn(
              'flex size-9 shrink-0 items-center justify-center rounded-xl ring-1 transition-transform duration-500 group-hover:scale-110 group-hover:rotate-[-8deg]',
              attention
                ? 'bg-[hsl(var(--warning))]/10 text-[hsl(var(--warning))] ring-[hsl(var(--warning))]/25'
                : 'bg-muted text-foreground/75 ring-border group-hover:text-primary',
            )}
          >
            <Icon className="size-4" strokeWidth={1.75} />
          </span>
        )}
      </div>

      <p className="tabular mt-2 text-[34px] leading-none font-semibold tracking-[-0.04em]">
        {count !== undefined ? (
          <CountUp
            to={count}
            duration={1.1}
            // Ara karelerde kesirli değer basılmasın ("11,467" gibi); tam sayı metrikler yuvarlanır.
            format={(v) => (format ?? String)(Number.isInteger(count) ? Math.round(v) : v)}
          />
        ) : (
          value
        )}
      </p>

      {trend && (
        <div className="mt-3 flex flex-wrap items-center gap-2 text-[12.5px] text-muted-foreground">
          <Badge
            variant="secondary"
            className={cn('tabular gap-1 bg-muted/70', toneClass(trendDirection, trendSense))}
          >
            <TrendIcon className="size-3" />
            {trend}
          </Badge>
          {compareLabel}
        </div>
      )}

      {series && series.length > 1 && (
        <Sparkline data={series} labels={seriesLabels} height={40} className="mt-4 -mb-1" />
      )}
    </div>
  )
}

/** Dashboard yüklenirken — önceki sürümdeki "boş ekran" şikayetinin karşılığı. */
export function StatCardsSkeleton({ count = 4 }: { count?: number }) {
  return (
    <div
      aria-busy="true"
      className="grid w-full grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4"
    >
      <span className="sr-only">{tx('Yükleniyor')}</span>
      {Array.from({ length: count }).map((_, i) => (
        <div key={i} className="surface flex flex-col gap-3 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <Skeleton className="h-3 w-20" />
            <Skeleton className="size-7 rounded-md" />
          </div>
          <Skeleton className="h-7 w-24" />
          <Skeleton className="h-3 w-16" />
        </div>
      ))}
    </div>
  )
}
