import type { ReactNode } from 'react'
import { CheckCircle2, CircleAlert, Info, TriangleAlert, type LucideIcon } from 'lucide-react'
import type { StatusTone } from '@/components/ui/status-badge'
import { cn } from '@/lib/utils'

/**
 * Bilgi / uyarı kutusu. Ekranlarda elle yazılan "kenarlıklı renkli kutu"ların tek karşılığı; tonlar
 * durum rozetiyle (StatusBadge) aynı beş renk. Renkler tema tokenlarından gelir (açık/koyu tema).
 *
 *   <Callout tone="warning" title="Bellek az">…</Callout>
 *   <Callout tone="info" icon={CircleAlert} action={<Button>Güncelle</Button>}>…</Callout>
 */
const TONE: Record<StatusTone, { box: string; icon: string; Icon: LucideIcon }> = {
  neutral: { box: 'border-border bg-card/50 text-muted-foreground backdrop-blur', icon: 'text-muted-foreground', Icon: Info },
  info: { box: 'border-primary/30 bg-primary/5', icon: 'text-primary', Icon: Info },
  success: { box: 'border-[hsl(var(--success))]/30 bg-[hsl(var(--success))]/5', icon: 'text-[hsl(var(--success))]', Icon: CheckCircle2 },
  warning: { box: 'border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10', icon: 'text-[hsl(var(--warning))]', Icon: TriangleAlert },
  danger: { box: 'border-destructive/30 bg-destructive/5', icon: 'text-destructive', Icon: CircleAlert },
}

export function Callout({ tone = 'neutral', title, icon, action, className, children }: {
  tone?: StatusTone
  title?: ReactNode
  /** Varsayılan simge yerine; false ise simge yok (nötr ton varsayılanı da simgesizdir). */
  icon?: LucideIcon | false
  /** Sağda düğme vb. */
  action?: ReactNode
  className?: string
  children?: ReactNode
}) {
  const t = TONE[tone]
  const Icon = icon === false || (icon === undefined && tone === 'neutral') ? null : icon ?? t.Icon
  return (
    <div className={cn('flex flex-wrap items-start gap-3 rounded-xl border px-3.5 py-2.5 text-[13px] leading-relaxed', t.box, className)}>
      {Icon && <Icon aria-hidden="true" className={cn('mt-0.5 size-4 shrink-0', t.icon)} />}
      <div className="min-w-0 flex-1">
        {title && <p className="font-medium text-foreground">{title}</p>}
        {children && <div className={cn(title && 'text-[12px] text-muted-foreground')}>{children}</div>}
      </div>
      {action && <div className="flex shrink-0 items-center gap-2">{action}</div>}
    </div>
  )
}
