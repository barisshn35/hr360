import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { Mail, MailCheck, MailWarning, QrCode as QrIcon, ShieldCheck, TimerReset, TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { CopyButton } from '@/components/ui/copy-button'
import { Modal } from '@/components/ui/modal'
import { QrCode } from '@/components/ui/qr-code'
import { Callout } from '@/components/ui/callout'
import { EASE } from '@/motion/primitives'
import type { PasswordLinkView } from '@/api/password-links'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { remainingText } from './passwordHelpers'

/** Kalan süre çubuğu: üretildiği andan bitişe doğru akar (dakikada bir güncellenir). */
export function CountdownBar({ createdAt, expiresAt }: { createdAt: number; expiresAt: string }) {
  const reduced = useReducedMotion()
  const end = new Date(expiresAt).getTime()
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 30_000)
    return () => clearInterval(id)
  }, [])
  const total = Math.max(1, end - createdAt)
  const left = Math.max(0, end - now)
  const ratio = left / total
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between text-[12px] text-muted-foreground">
        <span className="inline-flex items-center gap-1.5">
          <TimerReset className="size-3.5" aria-hidden="true" />
          {tx('Kalan süre')}
        </span>
        <span className="font-medium tabular-nums text-foreground">{remainingText(left)}</span>
      </div>
      <div
        className="h-1.5 overflow-hidden rounded-full bg-muted"
        role="progressbar"
        aria-label={tx('Linkin kalan süresi')}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(ratio * 100)}
      >
        <motion.div
          className={cn('h-full origin-left rounded-full', ratio > 0.25 ? 'bg-primary' : 'bg-[hsl(var(--warning))]')}
          initial={reduced ? false : { scaleX: 1 }}
          animate={{ scaleX: ratio }}
          transition={{ duration: reduced ? 0 : 1.1, ease: EASE }}
        />
      </div>
      <p className="text-[11.5px] text-muted-foreground">{tx('Son kullanma: {0}', [formatDateTime(expiresAt)])}</p>
    </div>
  )
}

/**
 * Üretilen parola linki: bir kez gösterilir. Kopyala (tik animasyonu), QR (büyüyerek açılır),
 * kalan süre çubuğu ve e-posta durumu. Eski yöntemde (Keycloak e-postası) link yoktur.
 */
export function PasswordLinkCard({ view, who }: { view: PasswordLinkView; who?: string }) {
  const reduced = useReducedMotion()
  const [showQr, setShowQr] = useState(false)
  const [createdAt] = useState(() => Date.now())

  if (!view.link) {
    const keycloak = view.emailStatus === 'keycloak'
    return (
      <motion.div
        initial={reduced ? false : { opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.45, ease: EASE }}
        className="flex gap-3 rounded-xl border border-border bg-muted/30 p-4"
      >
        <motion.span
          initial={reduced ? false : { scale: 0.6, rotate: -12 }}
          animate={{ scale: 1, rotate: 0 }}
          transition={{ type: 'spring', stiffness: 320, damping: 18 }}
          className="flex size-9 shrink-0 items-center justify-center rounded-full bg-primary/12 text-primary"
        >
          <MailCheck className="size-4.5" aria-hidden="true" />
        </motion.span>
        <div className="space-y-1 text-[13px] leading-relaxed">
          <p className="font-medium">
            {keycloak ? tx('Parola e-postası gönderildi (Keycloak e-postası)') : tx('Parola linki e-postayla gönderildi')}
          </p>
          <p className="text-muted-foreground">
            {keycloak
              ? tx('Bağlantı 24 saat geçerli. Şirket ayarlarındaki yöntem "Keycloak e-postası".')
              : tx('Link {0} tarihine kadar geçerli.', [formatDateTime(view.expiresAt)])}
          </p>
        </div>
      </motion.div>
    )
  }

  return (
    <motion.div
      initial={reduced ? false : { opacity: 0, y: 10, scale: 0.98 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: 0.5, ease: EASE }}
      className="space-y-4"
    >
      {view.emailFallback && (
        <motion.div
          initial={reduced ? false : { opacity: 0, x: -6 }}
          animate={{ opacity: 1, x: 0 }}
          role="alert"
        >
          <Callout tone="warning" icon={MailWarning}>{tx('E-posta gönderilemedi, link aşağıda. Kişiye uygun bir yoldan iletin.')}</Callout>
        </motion.div>
      )}

      <div className="rounded-xl border border-primary/25 bg-primary/[0.04] p-4">
        <div className="mb-2 flex items-center gap-2 text-[12.5px] font-medium text-primary">
          <ShieldCheck className="size-4" aria-hidden="true" />
          {view.purpose === 'reset' ? tx('Parola sıfırlama linki') : tx('Parola belirleme linki')}
          {who ? <span className="truncate text-muted-foreground">· {who}</span> : null}
        </div>
        <p className="break-all rounded-md bg-background/80 px-3 py-2 font-mono text-[12px] leading-relaxed select-all" data-testid="password-link">
          {view.link}
        </p>
        <div className="mt-3 flex flex-wrap gap-2">
          <CopyButton value={view.link} label={tx('Linki kopyala')} />
          <Button type="button" variant="outline" size="sm" className="h-9" onClick={() => setShowQr((v) => !v)} aria-expanded={showQr}>
            <QrIcon className="size-4" aria-hidden="true" />
            {showQr ? tx('QR kodunu gizle') : tx('QR göster')}
          </Button>
        </div>
        <AnimatePresence initial={false}>
          {showQr && (
            <motion.div
              key="qr"
              initial={reduced ? { opacity: 0 } : { opacity: 0, scale: 0.6, height: 0 }}
              animate={reduced ? { opacity: 1 } : { opacity: 1, scale: 1, height: 'auto' }}
              exit={reduced ? { opacity: 0 } : { opacity: 0, scale: 0.6, height: 0 }}
              transition={{ type: 'spring', stiffness: 260, damping: 24 }}
              className="flex origin-top justify-center overflow-hidden pt-4"
            >
              <QrCode value={view.link} label={tx('Parola linkinin QR kodu')} />
            </motion.div>
          )}
        </AnimatePresence>
      </div>

      <CountdownBar createdAt={createdAt} expiresAt={view.expiresAt} />

      <ul className="space-y-1.5 text-[12px] leading-relaxed text-muted-foreground">
        <li className="flex gap-2">
          {view.emailStatus === 'queued'
            ? <><Mail className="mt-0.5 size-3.5 shrink-0 text-primary" aria-hidden="true" />{tx('Aynı link e-postayla da gönderildi.')}</>
            : <><Mail className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />{tx('E-posta gönderimi kapalı; linki kişiye kendiniz iletin (mesaj, QR).')}</>}
        </li>
        <li className="flex gap-2">
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {tx('Link tek kullanımlık ve yalnızca şimdi gösterilir. Kaybolursa Davetler listesinden yeniden üretin.')}
        </li>
      </ul>
    </motion.div>
  )
}

/* ------------------------------- Pencere ------------------------------- */

interface LinkDialogApi {
  /** Üretilen linki pencerede gösterir. `view` yoksa (link üretilemedi) hiçbir şey yapmaz. */
  show: (view: PasswordLinkView | null | undefined, who?: string) => void
}

const LinkDialogContext = createContext<LinkDialogApi | null>(null)

export function PasswordLinkDialogProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<{ view: PasswordLinkView; who?: string } | null>(null)
  const show = useCallback<LinkDialogApi['show']>((view, who) => {
    if (view) setState({ view, who })
  }, [])
  const api = useMemo(() => ({ show }), [show])
  return (
    <LinkDialogContext.Provider value={api}>
      {children}
      <Modal
        open={state !== null}
        onClose={() => setState(null)}
        title={state?.view.purpose === 'reset' ? tx('Parola sıfırlama linki') : tx('Parola belirleme linki')}
        note={state?.view.link ? tx('Bu link yalnızca bir kez gösterilir.') : undefined}
        footer={<Button onClick={() => setState(null)}>{tx('Tamam')}</Button>}
      >
        {state && <PasswordLinkCard view={state.view} who={state.who} />}
      </Modal>
    </LinkDialogContext.Provider>
  )
}

export function usePasswordLinkDialog(): LinkDialogApi {
  const ctx = useContext(LinkDialogContext)
  // Sağlayıcı yoksa (ör. testlerde) sessizce yok sayılır.
  return ctx ?? { show: () => {} }
}
