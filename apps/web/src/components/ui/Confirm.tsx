import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react'
import { CircleAlert, CircleHelp } from 'lucide-react'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
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

  const destructive = opts?.destructive !== false
  const Icon = destructive ? CircleAlert : CircleHelp

  return (
    <Ctx.Provider value={confirm}>
      {children}
      <AlertDialog open={open} onOpenChange={(next) => !next && done(false)}>
        <AlertDialogContent>
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
              <AlertDialogTitle>{opts?.title ?? ''}</AlertDialogTitle>
              {opts?.note ? <AlertDialogDescription>{opts.note}</AlertDialogDescription> : <AlertDialogDescription className="sr-only">{opts?.title ?? ''}</AlertDialogDescription>}
            </AlertDialogHeader>
          </div>
          <AlertDialogFooter>
            <AlertDialogCancel onClick={() => done(false)}>{tx('Vazgeç')}</AlertDialogCancel>
            <AlertDialogAction variant={destructive ? 'destructive' : 'default'} onClick={() => done(true)}>
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
