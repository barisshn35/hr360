import type { ReactNode } from 'react'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/primitives/sheet'
import { cn } from '@/lib/utils'

/**
 * Sağdan açılan panel (ayrıntı, yan form). Modal ile aynı düzen: başlık / not / gövde / alt bant;
 * ekranlar Sheet ilkelini doğrudan kullanmaz. Gövde kayar, başlık ve alt bant sabit kalır.
 *
 *   <Drawer open={open} onClose={close} title="Kiracı detayı" note="…" footer={<Button>…</Button>}>…</Drawer>
 */
export function Drawer({ open, onClose, title, note, eyebrow, headerExtra, footer, size = 'md', children }: {
  open: boolean
  onClose: () => void
  title: ReactNode
  note?: ReactNode
  /** Başlığın üstünde küçük vurgulu satır (ör. departman yolu). */
  eyebrow?: ReactNode
  /** Başlık bandında notun altında (ör. "Yönetimde aç" düğmesi). */
  headerExtra?: ReactNode
  footer?: ReactNode
  /** md ≈ 28rem, lg ≈ 32rem, xl ≈ 56rem (geniş form). */
  size?: 'md' | 'lg' | 'xl'
  children?: ReactNode
}) {
  return (
    <Sheet open={open} onOpenChange={(next) => !next && onClose()}>
      <SheetContent side="right"
        className={cn('w-full gap-0 p-0', size === 'xl' ? 'sm:max-w-4xl' : size === 'lg' ? 'sm:max-w-lg' : 'sm:max-w-md')}>
        <SheetHeader className="border-b border-border bg-gradient-to-b from-primary/[0.05] to-transparent p-5 pr-12 text-left">
          {eyebrow && <p className="text-[12px] font-medium text-primary">{eyebrow}</p>}
          <SheetTitle className="flex flex-wrap items-center gap-2 text-[17px]">{title}</SheetTitle>
          {note
            ? <SheetDescription className="text-[13px] leading-relaxed">{note}</SheetDescription>
            : <SheetDescription className="sr-only">{typeof title === 'string' ? title : ''}</SheetDescription>}
          {headerExtra}
        </SheetHeader>
        <div className="min-h-0 flex-1 overflow-y-auto p-5">{children}</div>
        {footer && <div className="flex flex-wrap justify-end gap-2 border-t border-border bg-muted/30 px-5 py-4">{footer}</div>}
      </SheetContent>
    </Sheet>
  )
}
