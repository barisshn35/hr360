import type { ReactNode } from 'react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

/**
 * Modal sarmalayıcı.
 *
 * fe14'te odak tuzağı, Esc ve gövde kilidi elle yazılmıştı; artık Radix
 * Dialog bunların hepsini sağlıyor. Burada yalnızca HR360'ın başlık/not/
 * gövde/alt bant düzeni ve mobilde tam yükseklik davranışı var.
 */
export function Modal({
  open,
  onClose,
  title,
  note,
  children,
  footer,
  size = 'md',
}: {
  open: boolean
  onClose: () => void
  title: string
  note?: string
  children: ReactNode
  footer?: ReactNode
  size?: 'md' | 'lg' | 'xl'
}) {
  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent
        className={cn(
          'max-h-[calc(100dvh-2rem)] gap-0 overflow-hidden p-0',
          size === 'xl' ? 'sm:max-w-4xl' : size === 'lg' ? 'sm:max-w-2xl' : 'sm:max-w-lg',
        )}
      >
        <DialogHeader className="border-b border-border bg-gradient-to-b from-primary/[0.05] to-transparent px-5 py-4 text-left">
          <DialogTitle className="text-[16px]">{title}</DialogTitle>
          {note ? (
            <DialogDescription className="text-[13px] leading-relaxed">{note}</DialogDescription>
          ) : (
            <DialogDescription className="sr-only">{title}</DialogDescription>
          )}
        </DialogHeader>

        {children != null && <div className="max-h-[70dvh] overflow-y-auto px-5 py-5">{children}</div>}

        {footer && (
          <DialogFooter className="gap-2 border-t border-border bg-muted/30 px-5 py-4">{footer}</DialogFooter>
        )}
      </DialogContent>
    </Dialog>
  )
}

export interface SummaryItem {
  fieldId: string
  message: string
}

/**
 * Başarısız gönderimden sonra formun başına yerleşir ve odağı üzerine alır.
 * Satır içi alan hatalarının yerini almaz; onları tamamlar.
 */
export function ErrorSummary({ items }: { items: SummaryItem[] }) {
  if (items.length === 0) return null

  return (
    <div
      role="alert"
      tabIndex={-1}
      aria-labelledby="error-summary-title"
      className="rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3"
    >
      <h3 id="error-summary-title" className="text-[13px] font-semibold text-destructive">
        {items.length === 1 ? tx('Bir alanı düzeltin') : tx('{0} alanı düzeltin', [items.length])}
      </h3>
      <ul className="mt-1.5 space-y-1">
        {items.map((item) => (
          <li key={item.fieldId}>
            <a
              href={`#${item.fieldId}`}
              className="cursor-pointer text-[13px] text-destructive underline underline-offset-2 hover:opacity-75"
              onClick={(e) => {
                e.preventDefault()
                document.getElementById(item.fieldId)?.focus()
              }}
            >
              {item.message}
            </a>
          </li>
        ))}
      </ul>
    </div>
  )
}
