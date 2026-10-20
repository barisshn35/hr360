import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react'
import { CircleAlert, CircleHelp, LoaderCircle } from 'lucide-react'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/primitives/alert-dialog'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

/**
 * Geri alınamaz ya da etkisi geniş işlemler için ortak onay penceresi.
 *
 *   const confirm = useConfirm()
 *   if (await confirm({ title: tx('Dönem kapatılsın mı?'), note: ..., action: tx('Kapat') })) close.mutate()
 *
 * role="alertdialog": dışarı tıklamak pencereyi kapatmaz, odak "Vazgeç"te başlar (yanlışlıkla
 * Enter'a basmak işlemi yapmaz). Esc = Vazgeç.
 */
export interface ConfirmOptions {
  title: string
  note?: string
  /** Onay düğmesinin metni (varsayılan "Onayla"). */
  action?: string
  /** Kırmızı (yıkıcı) düğme ve uyarı simgesi; varsayılan true. */
  destructive?: boolean
}

type ConfirmFn = (o: ConfirmOptions) => Promise<boolean>

const Ctx = createContext<ConfirmFn | null>(null)

export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [opts, setOpts] = useState<ConfirmOptions | null>(null)
  const [open, setOpen] = useState(false)
  const resolver = useRef<((v: boolean) => void) | null>(null)

  const confirm = useCallback<ConfirmFn>((o) => {
    resolver.current?.(false)
    setOpts(o)
    setOpen(true)
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve
    })
  }, [])

  const done = (v: boolean) => {
    resolver.current?.(v)
    resolver.current = null
    // Metin kapanış animasyonu bitene kadar kalsın diye opts hemen silinmez.
    setOpen(false)
  }

  return (
    <Ctx.Provider value={confirm}>
      {children}
      <AlertDialog open={open} onOpenChange={(next) => !next && done(false)}>
        <AlertDialogContent>
          <ConfirmHead title={opts?.title ?? ''} note={opts?.note} destructive={opts?.destructive !== false} />
          <AlertDialogFooter>
            <AlertDialogCancel onClick={() => done(false)}>{tx('Vazgeç')}</AlertDialogCancel>
            <AlertDialogAction variant={opts?.destructive !== false ? 'destructive' : 'default'} onClick={() => done(true)}>
              {opts?.action ?? tx('Onayla')}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Ctx.Provider>
  )
}

export function useConfirm(): ConfirmFn {
  const fn = useContext(Ctx)
  if (!fn) throw new Error('useConfirm, ConfirmProvider içinde kullanılmalı')
  return fn
}

function ConfirmHead({ title, note, destructive }: { title: string; note?: ReactNode; destructive: boolean }) {
  const Icon = destructive ? CircleAlert : CircleHelp
  return (
    <div className="flex flex-col gap-3 max-sm:items-center sm:flex-row sm:gap-4">
      <div
        className={cn(
          'flex size-10 shrink-0 items-center justify-center rounded-full border',
          destructive ? 'border-destructive/30 bg-destructive/10 text-destructive' : 'border-primary/30 bg-primary/10 text-primary',
        )}
        aria-hidden="true"
      >
        <Icon className="size-[18px]" strokeWidth={2} />
      </div>
      <AlertDialogHeader>
        <AlertDialogTitle>{title}</AlertDialogTitle>
        {note ? <AlertDialogDescription>{note}</AlertDialogDescription> : <AlertDialogDescription className="sr-only">{title}</AlertDialogDescription>}
      </AlertDialogHeader>
    </div>
  )
}

/**
 * Denetimli onay penceresi: useConfirm'ün görünümü, ama ek içerik (önizleme, hata iletisi) taşır ve
 * onay düğmesi pencereyi kendisi kapatmaz — sunucu hatası (ör. 409) pencerede gösterilebilsin.
 * Dışarı tıklamak kapatmaz, odak "Vazgeç"te başlar, Esc = Vazgeç.
 */
export function ConfirmDialog({ open, onClose, title, note, action, onConfirm, pending, disabled, destructive = true, children }: {
  open: boolean
  onClose: () => void
  title: string
  note?: ReactNode
  /** Onay düğmesinin metni. */
  action: string
  onConfirm: () => void
  pending?: boolean
  disabled?: boolean
  destructive?: boolean
  children?: ReactNode
}) {
  return (
    <AlertDialog open={open} onOpenChange={(next) => !next && onClose()}>
      <AlertDialogContent className="sm:max-w-lg">
        <ConfirmHead title={title} note={note} destructive={destructive} />
        {children && <div className="space-y-3 text-[13px]">{children}</div>}
        <AlertDialogFooter>
          <AlertDialogCancel>{tx('Vazgeç')}</AlertDialogCancel>
          <Button variant={destructive ? 'destructive' : 'default'} disabled={pending || disabled} onClick={onConfirm}>
            {pending && <LoaderCircle className="size-4 animate-spin" />}
            {action}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
