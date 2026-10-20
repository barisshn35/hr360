import type { HTMLAttributes, ReactNode } from 'react'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'

/**
 * Sınırlanmış içerik bölgesi. shadcn `Card`'ının üzerine başlık/not/eylem
 * bandı ve gövde ekler — 20 ekranda aynı üç parça tekrar tekrar kurulmasın.
 */
export function Panel({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <Card
      className={cn(
        'gap-0 overflow-hidden py-0 animate-in fade-in slide-in-from-bottom-3 duration-500 fill-mode-both',
        className,
      )}
      {...props}
    />
  )
}

export function PanelHead({
  title,
  note,
  action,
  className,
}: {
  title: ReactNode
  note?: ReactNode
  action?: ReactNode
  className?: string
}) {
  return (
    <div
      className={cn(
        'flex flex-wrap items-center justify-between gap-x-4 gap-y-1.5 border-b border-border px-5 py-3.5',
        className,
      )}
    >
      <div className="min-w-0">
        <h2 className="text-[14.5px] font-semibold tracking-tight">{title}</h2>
        {note && <p className="mt-0.5 text-[13px] text-muted-foreground">{note}</p>}
      </div>
      {action && <div className="shrink-0">{action}</div>}
    </div>
  )
}

export function PanelBody({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('p-5', className)} {...props} />
}

/**
 * Etiketli veri satırı (tanım listesi öğesi). Etiket sakin, değer okunur;
 * hiyerarşi büyük harfle değil renk ve boyutla kurulur.
 */
export function DataField({
  label,
  children,
  className,
}: {
  label: string
  children: ReactNode
  className?: string
}) {
  return (
    <div className={className}>
      <dt className="text-[12px] text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-[13px]">{children}</dd>
    </div>
  )
}
