import { Component, useState, type ErrorInfo, type ReactNode } from 'react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { ChevronDown, Home, RotateCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { CopyButton } from '@/components/ui/copy-button'
import { ErrorScene } from '@/components/errors/ErrorScene'
import { UnpluggedArt } from '@/components/errors/art'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'

interface State {
  error: Error | null
  id: string | null
  at: string | null
}

/** Destek için kısa hata kimliği: tarayıcı konsolundaki kayıtla eşleşir. */
function newErrorId(): string {
  const bytes = new Uint8Array(4)
  crypto.getRandomValues(bytes)
  return 'HR-' + Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('').toUpperCase()
}

/** Yeni sürüm yayınlandıktan sonra eski sekmede tembel modül inemeyince oluşan hata. */
function isStaleBundle(error: Error): boolean {
  return /dynamically imported module|Importing a module script failed|ChunkLoadError|Loading chunk/i.test(error.message)
}

/** Render sırasındaki beklenmeyen hatada beyaz ekran yerine çıkış yolu sunar. */
export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null, id: null, at: null }

  static getDerivedStateFromError(error: Error): State {
    return { error, id: newErrorId(), at: new Date().toISOString() }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`HR360 arayüz hatası [${this.state.id ?? '?'}]:`, error, info.componentStack)
  }

  render() {
    const { error, id, at } = this.state
    if (!error || !id || !at) return this.props.children
    return <CrashScreen error={error} id={id} at={at} />
  }
}

function CrashScreen({ error, id, at }: { error: Error; id: string; at: string }) {
  const [open, setOpen] = useState(false)
  const reduced = useReducedMotion()
  const stale = isStaleBundle(error)
  const report = [
    `${tx('Hata kimliği')}: ${id}`,
    `${tx('Zaman')}: ${at}`,
    `${tx('Adres')}: ${window.location.pathname}`,
    `${tx('Ayrıntı')}: ${error.name}: ${error.message}`,
  ].join('\n')

  return (
    <ErrorScene
      fullPage
      testId="crash"
      tone="destructive"
      art={<UnpluggedArt />}
      title={stale ? tx('Yeni bir sürüm yayınlandı') : tx('Bir şeyler ters gitti')}
      detail={
        stale
          ? tx('Bu sekme eski sürümü kullanıyor. Sayfayı yenilediğinizde güncel sürüm açılır; girdiğiniz veriler sunucuda kayıtlıysa kaybolmaz.')
          : tx('Bu ekran beklenmedik bir hatayla durdu. Sayfayı yenilemek çoğu zaman yeterlidir; sorun sürerse aşağıdaki hata kimliğini yöneticinize iletin.')
      }
      actions={
        <>
          <Button onClick={() => window.location.reload()}>
            <RotateCw className="size-4" />
            {tx('Sayfayı yenile')}
          </Button>
          <Button variant="outline" asChild>
            <a href="/panel">
              <Home className="size-4" />
              {tx('Genel bakışa dön')}
            </a>
          </Button>
        </>
      }
    >
      <div className="mx-auto w-full max-w-md rounded-xl border border-border bg-card/60 text-left backdrop-blur">
        <div className="flex items-center justify-between gap-3 px-3.5 py-2.5">
          <div className="min-w-0">
            <p className="text-[11px] font-medium tracking-wide text-muted-foreground uppercase">{tx('Hata kimliği')}</p>
            <p className="font-mono text-[14px] font-semibold" data-testid="crash-id">{id}</p>
          </div>
          <CopyButton value={report} label={tx('Kopyala')} />
        </div>
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
          className="flex w-full cursor-pointer items-center justify-between border-t border-border px-3.5 py-2.5 text-[13px] text-muted-foreground transition-colors hover:text-foreground"
        >
          {tx('Teknik ayrıntılar')}
          <ChevronDown className={cn('size-4 transition-transform duration-200', open && 'rotate-180')} />
        </button>
        <AnimatePresence initial={false}>
          {open && (
            <motion.div
              initial={reduced ? false : { height: 0, opacity: 0 }}
              animate={{ height: 'auto', opacity: 1 }}
              exit={reduced ? undefined : { height: 0, opacity: 0 }}
              transition={{ duration: 0.25 }}
              className="overflow-hidden"
            >
              <pre className="max-h-48 overflow-auto border-t border-border px-3.5 py-3 font-mono text-[11.5px] leading-relaxed break-words whitespace-pre-wrap text-muted-foreground">
                {report}
              </pre>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    </ErrorScene>
  )
}
