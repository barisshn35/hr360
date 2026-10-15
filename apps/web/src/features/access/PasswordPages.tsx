/**
 * Herkese açık parola sayfaları (oturum gerekmez):
 *  - /parola-belirle/:token — HR360'ın süreli, tek kullanımlık linkiyle parola belirleme/sıfırlama.
 *    Her link durumunun (geçerli, süresi dolmuş, kullanılmış, iptal, geçersiz) kendi ekranı ve animasyonu var.
 *  - /parola-sifirla — "Parolamı unuttum": e-posta açıksa sıfırlama linki e-postayla gider, değilse
 *    "İK yöneticinize başvurun".
 * Parola yalnızca kimlik sunucusuna (Keycloak) iletilir; HR360 parolayı saklamaz.
 */
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { ArrowLeft, CheckCircle2, Clock3, Eye, EyeOff, KeyRound, Link2Off, LoaderCircle, LockKeyhole, Mail, ShieldBan, UserRound } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { BorderBeam } from '@/components/fx/border-beam'
import { EASE } from '@/motion/primitives'
import { ApiError } from '@/api/client'
import { passwordLinkApi } from '@/api/password-links'
import { usePublicConfig } from '@/api/queries-tenant'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { lang, tx } from '@/lib/i18n'
import { passwordScore, policyText } from './passwordHelpers'

/* --------------------------------- Çerçeve -------------------------------- */

function PublicShell({ children }: { children: ReactNode }) {
  const reduced = useReducedMotion()
  return (
    <main className="relative flex min-h-dvh items-center justify-center overflow-hidden bg-background px-5 py-12 text-foreground">
      <AmbientBackground meteors={false} />
      <motion.div
        initial={reduced ? false : { opacity: 0, y: 18, scale: 0.98 }}
        animate={{ opacity: 1, y: 0, scale: 1 }}
        transition={{ duration: 0.6, ease: EASE }}
        className="relative z-10 w-full max-w-[440px]"
      >
        <div className="mb-6 flex justify-center">
          <span className="flex size-12 items-center justify-center rounded-2xl bg-gradient-to-br from-primary to-[hsl(170_80%_30%)] shadow-[0_0_32px_-6px_hsl(var(--primary))]">
            <img src="/icon-white.svg" alt="" aria-hidden="true" className="size-7" />
          </span>
        </div>
        <Card className="relative gap-0 overflow-hidden p-6 sm:p-7">
          <BorderBeam size={180} duration={10} />
          {children}
        </Card>
      </motion.div>
    </main>
  )
}

/** Duruma özgü animasyonlu simge. */
function StateIcon({ kind }: { kind: 'expired' | 'used' | 'revoked' | 'invalid' | 'done' | 'mail' | 'admin' }) {
  const reduced = useReducedMotion()
  const map = {
    expired: { Icon: Clock3, tone: 'text-[hsl(var(--warning))] bg-[hsl(var(--warning))]/12', anim: { rotate: [0, -18, 14, -8, 0] } },
    used: { Icon: CheckCircle2, tone: 'text-primary bg-primary/12', anim: { scale: [0.6, 1.12, 1] } },
    revoked: { Icon: ShieldBan, tone: 'text-destructive bg-destructive/10', anim: { x: [0, -5, 5, -3, 0] } },
    invalid: { Icon: Link2Off, tone: 'text-muted-foreground bg-muted', anim: { rotate: [0, 8, -8, 0], scale: [0.9, 1] } },
    done: { Icon: CheckCircle2, tone: 'text-[hsl(var(--success))] bg-[hsl(var(--success))]/12', anim: { scale: [0.4, 1.15, 1], rotate: [-20, 0] } },
    mail: { Icon: Mail, tone: 'text-primary bg-primary/12', anim: { y: [8, -3, 0], opacity: [0, 1] } },
    admin: { Icon: UserRound, tone: 'text-primary bg-primary/12', anim: { scale: [0.8, 1.05, 1] } },
  }[kind]
  return (
    <motion.span
      initial={false}
      animate={reduced ? {} : map.anim}
      transition={{ duration: 0.9, ease: EASE }}
      className={cn('mx-auto mb-4 flex size-14 items-center justify-center rounded-full', map.tone)}
      aria-hidden="true"
    >
      {kind === 'done' && !reduced ? (
        <svg viewBox="0 0 24 24" className="size-7" fill="none" stroke="currentColor" strokeWidth={2.2} strokeLinecap="round" strokeLinejoin="round">
          <motion.circle cx="12" cy="12" r="9" initial={{ pathLength: 0 }} animate={{ pathLength: 1 }} transition={{ duration: 0.6, ease: EASE }} />
          <motion.path d="M8 12.5l2.6 2.6L16.2 9.4" initial={{ pathLength: 0 }} animate={{ pathLength: 1 }} transition={{ duration: 0.45, delay: 0.45, ease: EASE }} />
        </svg>
      ) : (
        <map.Icon className="size-7" />
      )}
    </motion.span>
  )
}

function StateScreen({ kind, title, detail, children }: { kind: Parameters<typeof StateIcon>[0]['kind']; title: string; detail: string; children?: ReactNode }) {
  return (
    <div className="text-center" data-testid={`password-state-${kind}`}>
      <StateIcon kind={kind} />
      <h1 className="text-[20px] font-semibold tracking-tight">{title}</h1>
      <p className="mt-2 text-[13.5px] leading-relaxed text-muted-foreground">{detail}</p>
      {children && <div className="mt-6">{children}</div>}
    </div>
  )
}

const goSignIn = (
  <Button asChild className="w-full">
    <Link to="/giris">{tx('Giriş sayfasına git')}</Link>
  </Button>
)

/* --------------------------- Parola belirleme --------------------------- */

export function SetPasswordPage() {
  const { token = '' } = useParams()
  const reduced = useReducedMotion()
  const state = useQuery({
    queryKey: ['password-link', token],
    queryFn: ({ signal }) => passwordLinkApi.linkState(token, signal),
    retry: false,
    staleTime: Infinity,
  })
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [visible, setVisible] = useState(false)
  const [localError, setLocalError] = useState<string | null>(null)
  const navigate = useNavigate()
  // Parola belirlenince teşekkür sayfasına (sonraki adımlar + giriş); geri tuşu forma dönmesin.
  const submit = useMutation({
    mutationFn: () => passwordLinkApi.setPassword(token, password),
    onSuccess: () => navigate('/tesekkurler?neden=parola', { replace: true }),
  })

  const serverStatus = submit.error instanceof ApiError && submit.error.status === 410
    ? ((submit.error.detail as { status?: string } | undefined)?.status ?? 'invalid')
    : null
  const policyError = submit.error instanceof ApiError && submit.error.status === 400
    ? policyText((submit.error.detail as { code?: string } | undefined)?.code)
    : submit.error ? tx('Parola belirlenemedi. Biraz sonra tekrar deneyin.') : null

  function onSubmit(e: FormEvent) {
    e.preventDefault()
    setLocalError(null)
    if (password.length < 8) return setLocalError(tx('Parola en az 8 karakter olmalı.'))
    if (password !== confirm) return setLocalError(tx('Parolalar eşleşmiyor.'))
    submit.mutate()
  }

  let content: ReactNode
  const status = submit.isSuccess ? 'done' : serverStatus ?? state.data?.status
  if (state.isPending) {
    content = (
      <div className="flex flex-col items-center gap-3 py-8 text-[13px] text-muted-foreground" aria-busy="true">
        <LoaderCircle className="size-6 animate-spin text-primary" aria-hidden="true" />
        {tx('Link kontrol ediliyor')}
      </div>
    )
  } else if (state.isError) {
    content = <StateScreen kind="invalid" title={tx('Link kontrol edilemedi')} detail={tx('Bağlantınızı kontrol edip sayfayı yenileyin.')}>{goSignIn}</StateScreen>
  } else if (status === 'done') {
    content = (
      <StateScreen kind="done" title={tx('Parolanız belirlendi')} detail={tx('Artık yeni parolanızla giriş yapabilirsiniz.')}>
        {goSignIn}
      </StateScreen>
    )
  } else if (status === 'expired') {
    content = <StateScreen kind="expired" title={tx('Linkin süresi dolmuş')} detail={tx('Yeni bir link için İK yöneticinize başvurun ya da "Parolamı unuttum" sayfasını kullanın.')}>
      <Button asChild variant="outline" className="w-full"><Link to="/parola-sifirla">{tx('Parolamı unuttum')}</Link></Button>
    </StateScreen>
  } else if (status === 'used') {
    content = <StateScreen kind="used" title={tx('Bu link kullanılmış')} detail={tx('Parola bu linkle zaten belirlendi. Giriş yapabilirsiniz.')}>{goSignIn}</StateScreen>
  } else if (status === 'revoked') {
    content = <StateScreen kind="revoked" title={tx('Bu link iptal edilmiş')} detail={tx('Yerine yeni bir link üretilmiş olabilir. En son gelen linki kullanın ya da İK yöneticinize başvurun.')} />
  } else if (status === 'invalid' || status === 'account_disabled' || !state.data) {
    content = <StateScreen kind="invalid" title={tx('Link geçersiz')} detail={tx('Link hatalı kopyalanmış ya da artık geçerli değil. İK yöneticinizden yeni bir link isteyin.')}>{goSignIn}</StateScreen>
  } else {
    const d = state.data
    const error = localError ?? policyError
    content = (
      <form onSubmit={onSubmit} className="space-y-5" noValidate>
        <div>
          <span className="mb-3 inline-flex size-10 items-center justify-center rounded-xl bg-primary/12 text-primary">
            <KeyRound className="size-5" aria-hidden="true" />
          </span>
          <h1 className="text-[22px] font-semibold tracking-tight">
            {d.purpose === 'reset' ? tx('Yeni parola belirleyin') : tx('Parolanızı belirleyin')}
          </h1>
          <p className="mt-1.5 text-[13.5px] leading-relaxed text-muted-foreground">
            {d.company ? tx('{0} · {1}', [d.company, d.email ?? '']) : d.email}
          </p>
          {d.expiresAt && (
            <p className="mt-1 text-[12px] text-muted-foreground">{tx('Link {0} tarihine kadar geçerli.', [formatDateTime(d.expiresAt)])}</p>
          )}
        </div>

        <div className="space-y-2">
          <Label htmlFor="pw">{tx('Yeni parola')}</Label>
          <div className="relative">
            <Input id="pw" type={visible ? 'text' : 'password'} autoComplete="new-password" value={password}
              onChange={(e) => setPassword(e.target.value)} maxLength={256} required className="pr-10" />
            <button type="button" onClick={() => setVisible((v) => !v)}
              className="absolute inset-y-0 right-0 flex w-10 cursor-pointer items-center justify-center text-muted-foreground hover:text-foreground"
              aria-label={visible ? tx('Parolayı gizle') : tx('Parolayı göster')}>
              {visible ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
            </button>
          </div>
          <StrengthBar value={password} />
        </div>
        <div className="space-y-2">
          <Label htmlFor="pw2">{tx('Parola (tekrar)')}</Label>
          <Input id="pw2" type={visible ? 'text' : 'password'} autoComplete="new-password" value={confirm}
            onChange={(e) => setConfirm(e.target.value)} maxLength={256} required />
        </div>

        <AnimatePresence>
          {error && (
            <motion.p key={error} role="alert"
              initial={reduced ? false : { opacity: 0, x: -8 }} animate={{ opacity: 1, x: [0, -4, 4, 0] }} exit={{ opacity: 0 }}
              transition={{ duration: 0.35 }}
              className="rounded-lg border border-destructive/30 bg-destructive/[0.08] px-3 py-2 text-[12.5px] text-destructive">
              {error}
            </motion.p>
          )}
        </AnimatePresence>

        <Button type="submit" size="lg" className="w-full" disabled={submit.isPending}>
          {submit.isPending ? <LoaderCircle className="size-4 animate-spin" /> : <LockKeyhole className="size-4" />}
          {tx('Parolayı kaydet')}
        </Button>
        <p className="text-center text-[11.5px] leading-relaxed text-muted-foreground">
          {tx('Parolanız yalnızca kimlik sunucusunda saklanır; link tek kullanımlıktır.')}
        </p>
      </form>
    )
  }

  return (
    <PublicShell>
      <AnimatePresence mode="wait" initial={false}>
        <motion.div key={String(status ?? 'loading')}
          initial={reduced ? false : { opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} exit={reduced ? undefined : { opacity: 0, y: -10 }}
          transition={{ duration: 0.35, ease: EASE }}>
          {content}
        </motion.div>
      </AnimatePresence>
    </PublicShell>
  )
}

/** Basit parola gücü göstergesi (uzunluk + karakter çeşitliliği). Kesin kural Keycloak'ın politikasıdır. */
function StrengthBar({ value }: { value: string }) {
  const score = passwordScore(value)
  const labels = [tx('Çok zayıf'), tx('Zayıf'), tx('Orta'), tx('İyi'), tx('Güçlü')]
  const color = score >= 4 ? 'bg-[hsl(var(--success))]' : score >= 3 ? 'bg-primary' : score >= 2 ? 'bg-[hsl(var(--warning))]' : 'bg-destructive'
  return (
    <div className="flex items-center gap-2" aria-live="polite">
      <div className="grid flex-1 grid-cols-4 gap-1">
        {[0, 1, 2, 3].map((i) => (
          <motion.span key={i} className={cn('h-1 rounded-full', i < score ? color : 'bg-muted')}
            initial={false} animate={{ opacity: i < score ? 1 : 0.6, scaleY: i < score ? 1 : 0.7 }} />
        ))}
      </div>
      {value && <span className="w-16 text-right text-[11px] text-muted-foreground">{labels[Math.max(0, score)]}</span>}
    </div>
  )
}

/* --------------------------- Parolamı unuttum --------------------------- */

export function ForgotPasswordPage() {
  const config = usePublicConfig()
  const [params] = useSearchParams()
  const [email, setEmail] = useState(params.get('email') ?? '')
  const send = useMutation({ mutationFn: () => passwordLinkApi.requestReset(email.trim(), lang) })
  const reduced = useReducedMotion()
  const byEmail = config.data?.passwordResetByEmail === true

  let content: ReactNode
  if (config.isPending) {
    content = <div className="flex justify-center py-8"><LoaderCircle className="size-6 animate-spin text-primary" aria-label={tx('Yükleniyor')} /></div>
  } else if (!byEmail) {
    content = (
      <StateScreen kind="admin" title={tx('Parolanızı mı unuttunuz?')}
        detail={tx('Parolanızı sıfırlamak için İK yöneticinize başvurun. Size tek kullanımlık, süreli bir parola linki iletecek.')}>
        <Button asChild variant="outline" className="w-full"><Link to="/giris"><ArrowLeft className="size-4" />{tx('Girişe dön')}</Link></Button>
      </StateScreen>
    )
  } else if (send.isSuccess) {
    content = (
      <StateScreen kind="mail" title={tx('E-postanızı kontrol edin')}
        detail={tx('Bu adrese kayıtlı bir hesap varsa sıfırlama linki gönderildi. E-posta birkaç dakika içinde gelmezse İK yöneticinize başvurun.')}>
        <Button asChild variant="outline" className="w-full"><Link to="/giris"><ArrowLeft className="size-4" />{tx('Girişe dön')}</Link></Button>
      </StateScreen>
    )
  } else {
    content = (
      <form className="space-y-5" onSubmit={(e) => { e.preventDefault(); if (email.trim()) send.mutate() }}>
        <div>
          <span className="mb-3 inline-flex size-10 items-center justify-center rounded-xl bg-primary/12 text-primary">
            <Mail className="size-5" aria-hidden="true" />
          </span>
          <h1 className="text-[22px] font-semibold tracking-tight">{tx('Parolamı unuttum')}</h1>
          <p className="mt-1.5 text-[13.5px] leading-relaxed text-muted-foreground">
            {tx('Hesabınızın e-posta adresini girin; tek kullanımlık bir sıfırlama linki gönderelim.')}
          </p>
        </div>
        <div className="space-y-2">
          <Label htmlFor="email">{tx('E-posta')}</Label>
          <Input id="email" type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} required maxLength={320} />
        </div>
        {send.isError && (
          <p role="alert" className="text-[12.5px] text-destructive">{tx('İstek gönderilemedi. Biraz sonra tekrar deneyin.')}</p>
        )}
        <Button type="submit" size="lg" className="w-full" disabled={send.isPending || !email.trim()}>
          {send.isPending ? <LoaderCircle className="size-4 animate-spin" /> : <Mail className="size-4" />}
          {tx('Sıfırlama linki gönder')}
        </Button>
        <Link to="/giris" className="flex items-center justify-center gap-1.5 text-[12.5px] text-muted-foreground hover:text-foreground">
          <ArrowLeft className="size-3.5" />{tx('Girişe dön')}
        </Link>
      </form>
    )
  }

  return (
    <PublicShell>
      <AnimatePresence mode="wait" initial={false}>
        <motion.div key={config.isPending ? 'l' : !byEmail ? 'admin' : send.isSuccess ? 'sent' : 'form'}
          initial={reduced ? false : { opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} exit={reduced ? undefined : { opacity: 0, y: -10 }}
          transition={{ duration: 0.35, ease: EASE }}>
          {content}
        </motion.div>
      </AnimatePresence>
    </PublicShell>
  )
}
