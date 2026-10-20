import type { ReactNode } from 'react'
import { Inbox, LoaderCircle, TriangleAlert } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from './button'
import { Skeleton } from './skeleton'
import { OrbitingCircles } from '@/components/fx/orbiting-circles'
import { tx, txServer } from '@/lib/i18n'

/**
 * Her listenin üç hâli tek yerden gelir: yükleniyor / boş / hata.
 * Ekranlar bunları kendi başına kurgulamaz — aksi hâlde 20 ekranda 20 farklı
 * boş durum metni oluşuyor.
 */

export function CenteredSpinner({ label = tx('Yükleniyor') }: { label?: string }) {
  return (
    <div
      aria-busy="true"
      className="flex min-h-40 items-center justify-center gap-2 py-16 text-sm text-muted-foreground"
    >
      <span aria-hidden="true" className="relative flex size-8 items-center justify-center">
        <span className="size-2 rounded-full bg-primary shadow-[0_0_10px_hsl(var(--primary))]" />
        <OrbitingCircles radius={13} duration={1.6} path={false} className="size-1.5 bg-primary" />
        <OrbitingCircles radius={13} duration={1.6} angle={180} path={false} className="size-1 bg-primary/50" />
      </span>
      {label}
    </div>
  )
}

/** Tam sayfa (oturum doğrulanırken) — sayfa yerinden oynamasın diye ortalanır. */
export function FullPageSpinner({ label = tx('Yükleniyor') }: { label?: string }) {
  return (
    <div
      aria-busy="true"
      className="flex min-h-dvh flex-col items-center justify-center gap-4 bg-background"
    >
      <span className="relative flex size-12 items-center justify-center">
        <span className="absolute inset-0 animate-ping rounded-full bg-primary/15" />
        <span className="relative flex size-10 items-center justify-center rounded-xl bg-primary/10 ring-1 ring-primary/25">
          <LoaderCircle aria-hidden="true" className="size-5 animate-spin text-primary" />
        </span>
      </span>
      <p className="text-sm text-muted-foreground">{label}</p>
    </div>
  )
}

/** Tablo iskeleti: sütun sayısı verilen düzeni korur, içerik gelince zıplamaz. */
export function RowsSkeleton({ rows = 6, columns = 4 }: { rows?: number; columns?: number }) {
  return (
    <div aria-busy="true" aria-live="polite" className="divide-y divide-border">
      <span className="sr-only">{tx('Yükleniyor')}</span>
      {Array.from({ length: rows }).map((_, r) => (
        <div key={r} className="flex items-center gap-4 px-4 py-3.5">
          {Array.from({ length: columns }).map((_, c) => (
            <Skeleton
              key={c}
              className={cn('h-4 rounded-sm', c === 0 ? 'w-[28%]' : c === columns - 1 ? 'w-20' : 'flex-1')}
            />
          ))}
        </div>
      ))}
    </div>
  )
}

/**
 * Boş ekran bir davettir: ne olduğunu söyler ve bir sonraki adımı verir.
 *
 * Görsel: merkezdeki ikonun etrafında dönen küçük ikonlar (21st.dev "Orbiting
 * Circles", Magic UI) ve kesik çizgili çerçeve (21st.dev "Empty State",
 * serafimcloud). Yan ikonlar verilmezse yörüngede zümrüt noktalar döner.
 */
export function EmptyState({
  title,
  detail,
  action,
  icon: Icon = Inbox,
  icons,
  className,
}: {
  title: string
  detail?: string
  action?: ReactNode
  icon?: React.ElementType
  /** Yörüngedeki iki ikon ve ortadaki ana ikon: [sol, orta, sağ]. */
  icons?: [React.ElementType, React.ElementType, React.ElementType]
  className?: string
}) {
  const [Left, Center, Right] = icons ?? [null, Icon, null]
  const satellite = 'size-8 rounded-xl border border-border bg-card text-muted-foreground shadow-lg'
  return (
    <div className={cn('px-4 py-10', className)}>
      <div className="relative mx-auto flex max-w-md flex-col items-center overflow-hidden rounded-3xl border border-dashed border-border px-6 pt-4 pb-9 text-center">
        <div aria-hidden="true" className="hr-dots absolute inset-0 opacity-60" />
        <div className="relative flex size-44 items-center justify-center">
          <span className="absolute size-16 rounded-full bg-primary/10 blur-2xl" />
          <span className="relative grid size-14 place-items-center rounded-2xl border border-border bg-card text-primary shadow-lg">
            <Center aria-hidden="true" className="size-6" strokeWidth={1.5} />
          </span>
          <OrbitingCircles radius={54} duration={18} className={satellite}>
            {Left ? <Left className="size-4" strokeWidth={1.5} /> : <span className="size-1.5 rounded-full bg-primary" />}
          </OrbitingCircles>
          <OrbitingCircles radius={54} duration={18} angle={180} path={false} className={satellite}>
            {Right ? <Right className="size-4" strokeWidth={1.5} /> : <span className="size-1.5 rounded-full bg-foreground/40" />}
          </OrbitingCircles>
          <OrbitingCircles radius={80} duration={26} reverse className="size-1.5 bg-foreground/40" />
        </div>
        <p className="relative text-[15.5px] font-semibold">{title}</p>
        {detail && (
          <p className="relative mt-1.5 max-w-sm text-[13px] leading-relaxed text-muted-foreground">{detail}</p>
        )}
        {action && <div className="relative mt-5">{action}</div>}
      </div>
    </div>
  )
}

/** Hata ne olduğunu ve nasıl düzeltileceğini söyler; özür dilemez. */
export function ErrorState({
  title = tx('Veriler alınamadı'),
  message,
  onRetry,
}: {
  title?: string
  message?: string
  onRetry?: () => void
}) {
  return (
    <div role="alert" className="px-4 py-14">
      <div className="mx-auto flex max-w-md gap-3 rounded-xl border border-destructive/30 bg-destructive/[0.06] p-4 shadow-[inset_0_1px_0_0_hsl(var(--edge-light))]">
        <TriangleAlert aria-hidden="true" className="mt-0.5 size-4.5 shrink-0 text-destructive" />
        <div className="min-w-0">
          <p className="text-[15px] font-semibold">{title}</p>
          {message && (
            <p className="mt-1 text-[13px] leading-relaxed break-words text-muted-foreground">
              {txServer(message)}
            </p>
          )}
          {onRetry && (
            <Button size="sm" variant="outline" className="mt-4" onClick={onRetry}>
              {tx('Yeniden dene')}
            </Button>
          )}
        </div>
      </div>
    </div>
  )
}

/** Sayfa başlığı altındaki kısa bilgi şeridi (ör. Kafka otomasyonu notu). */
export function InfoNote({ children }: { children: ReactNode }) {
  return (
    <p className="rounded-xl border border-border bg-card/50 px-3.5 py-2.5 text-[13px] leading-relaxed text-muted-foreground backdrop-blur">
      {children}
    </p>
  )
}
