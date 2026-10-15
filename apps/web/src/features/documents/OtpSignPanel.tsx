import { useEffect, useState } from 'react'
import { FileSignature, KeyRound, ShieldAlert, ShieldCheck } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { InfoNote } from '@/components/ui/States'
import { ApiError } from '@/api/client'
import { formatDateTime } from '@/lib/format'
import { errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * Y28 — tek imza motorunun (governance) ortak istemci bileşenleri. Hem düzenlenmiş belge
 * talepleri (SignDocument) hem İK özlük dokümanları (DocumentSignature) bunları kullanır:
 * kod iste → 6 haneli kodu gir → kalan deneme / süre doldu / kilit iletileri → kanıt + uyarı.
 */

/** Her imza ekranında görünen yasal açıklama (API'deki "disclaimer" ile aynı metin). */
export function SimpleSignatureDisclaimer() {
  return (
    <p className="flex gap-2 rounded-lg border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-[12.5px] leading-relaxed text-amber-800 dark:text-amber-300">
      <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      {tx('Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir.')}
    </p>
  )
}

/** Kanıtın ortak görünümü (governance kanıtı ve eski expense kanıtı buna eşlenir). */
export interface UnifiedEvidence {
  signedAt: string
  method: string
  documentSha256: string
  evidenceSha256: string
  ipPrefix: string | null
  integrityOk?: boolean
  documentId?: string
  documentVersion?: number
  signerEmployeeId?: string
  userAgentHash?: string | null
  title?: string | null
}

export const methodLabel = (m: string) => {
  const c = m.replace(/^OTP-/, '')
  return c === 'InApp+Email' ? tx('Tek kullanımlık kod — uygulama içi bildirim + e-posta')
    : c === 'Email' ? tx('E-postayla tek kullanımlık kod')
    : c === 'InApp' ? tx('Uygulama içi tek kullanımlık kod')
    : tx('Tek kullanımlık kod (OTP)')
}

export function SignatureEvidenceDetails({ e }: { e: UnifiedEvidence }) {
  return (
    <div className="space-y-2">
      <dl className="grid grid-cols-[150px_1fr] gap-x-3 gap-y-1.5 text-[12.5px]">
        {e.title && <><dt className="text-muted-foreground">{tx('Belge')}</dt><dd className="font-medium">{e.title}</dd></>}
        <dt className="text-muted-foreground">{tx('İmza zamanı')}</dt><dd>{formatDateTime(e.signedAt)}</dd>
        <dt className="text-muted-foreground">{tx('Yöntem')}</dt><dd>{methodLabel(e.method)}</dd>
        {e.documentId && (
          <><dt className="text-muted-foreground">{tx('Belge / sürüm')}</dt><dd className="font-mono text-[11.5px]">{e.documentId.slice(0, 8)} · v{e.documentVersion ?? 1}</dd></>
        )}
        {e.signerEmployeeId && <><dt className="text-muted-foreground">{tx('İmzalayan çalışan')}</dt><dd className="font-mono text-[11.5px]">{e.signerEmployeeId}</dd></>}
        <dt className="text-muted-foreground">{tx('Belge özeti (SHA-256)')}</dt><dd className="break-all font-mono text-[11px]">{e.documentSha256}</dd>
        <dt className="text-muted-foreground">{tx('IP (kısaltılmış)')}</dt><dd className="font-mono text-[11.5px]">{e.ipPrefix ?? '—'}</dd>
        {e.userAgentHash && <><dt className="text-muted-foreground">{tx('Tarayıcı özeti')}</dt><dd className="break-all font-mono text-[11px]">{e.userAgentHash}</dd></>}
        <dt className="text-muted-foreground">{tx('Kanıt özeti')}</dt><dd className="break-all font-mono text-[11px]">{e.evidenceSha256}</dd>
      </dl>
      {e.integrityOk !== undefined && (
        <p className="flex items-center gap-1.5 text-[12px]">
          {e.integrityOk
            ? <><ShieldCheck className="size-3.5 text-[hsl(var(--success))]" />{' '}{tx('Kanıt bütünlüğü doğrulandı')}</>
            : <><ShieldAlert className="size-3.5 text-destructive" />{' '}{tx('Kanıt özeti uyuşmuyor')}</>}
        </p>
      )}
    </div>
  )
}

/** Kod isteğinin yanıtı (iki uç da bu alanları döner). */
export interface OtpChallengeLike {
  otpId?: string
  channel: string
  expiresAt: string
  maxAttempts?: number
  attemptsLeft?: number
  sendsLeft?: number
}

const RESEND_SECONDS = 30

/** İmza motoru hata kodlarının kullanıcı iletisi; durum: kod artık kullanılamaz mı. */
export function otpErrorText(e: unknown): { text: string; dead: boolean; attemptsLeft?: number } {
  const d = e instanceof ApiError ? (e.detail as { code?: string; attemptsLeft?: number } | undefined) : undefined
  switch (d?.code) {
    case 'otp_invalid':
      return { text: tx('Kod hatalı. Kalan deneme: {0}.', [d.attemptsLeft ?? 0]), dead: false, attemptsLeft: d.attemptsLeft }
    case 'otp_locked':
      return { text: tx('Çok fazla hatalı deneme; kod kilitlendi. Yeni kod isteyin.'), dead: true, attemptsLeft: 0 }
    case 'otp_expired':
      return { text: tx('Kodun süresi doldu; yeni kod isteyin.'), dead: true }
    case 'otp_used':
    case 'otp_not_found':
      return { text: tx('Bu kod artık geçerli değil; yeni kod isteyin.'), dead: true }
    case 'otp_cooldown':
      return { text: tx('Yeni kod için lütfen biraz bekleyin (30 sn).'), dead: false }
    case 'otp_rate_limited':
      return { text: tx('Bu belge için saatlik kod sınırına ulaşıldı; daha sonra tekrar deneyin.'), dead: false }
    case 'already_signed':
      return { text: tx('Belge zaten imzalanmış.'), dead: true }
    case 'no_email':
      return { text: tx('Kayıtlı e-posta adresiniz yok; uygulama içi kodu kullanın.'), dead: false }
    case 'document_changed':
      return { text: tx('Belge imzaya gönderildikten sonra değişti; İK\'dan yeni talep isteyin.'), dead: true }
    default:
      return { text: errMsg(e), dead: false }
  }
}

/**
 * Ortak OTP imza paneli. <paramref name="onRequest"/> kodu ister (kod bildirimle gelir, yanıtta
 * yoktur); <paramref name="onSign"/> kodla imzalar ve kanıtı döner. Kanıt panelde gösterilir.
 */
export function OtpSignPanel<E>({
  onRequest, onSign, toEvidence, onSigned, disabled = false, disabledHint, chooseChannel = false, sentNote,
}: {
  onRequest: (channel: 'InApp' | 'Email') => Promise<OtpChallengeLike>
  onSign: (challenge: OtpChallengeLike, code: string) => Promise<E>
  toEvidence: (r: E) => UnifiedEvidence | null
  onSigned?: (r: E) => void
  /** Ör. açıklama onaylanmadan kod istenmesin. */
  disabled?: boolean
  disabledHint?: string
  /** Kod kanalı seçimi (uygulama içi / e-posta). */
  chooseChannel?: boolean
  /** Kod gönderildikten sonra genel "kod gönderildi" iletisi yerine gösterilecek not. */
  sentNote?: string
}) {
  const [channel, setChannel] = useState<'InApp' | 'Email'>('InApp')
  const [challenge, setChallenge] = useState<OtpChallengeLike | null>(null)
  const [sentAt, setSentAt] = useState(0)
  const [attemptsLeft, setAttemptsLeft] = useState<number | null>(null)
  const [dead, setDead] = useState(false)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [info, setInfo] = useState<string | null>(null)
  const [busy, setBusy] = useState<'request' | 'sign' | null>(null)
  const [evidence, setEvidence] = useState<UnifiedEvidence | null>(null)
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (!challenge || evidence) return
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [challenge, evidence])

  const wait = Math.max(0, RESEND_SECONDS - Math.floor((now - sentAt) / 1000))
  const expired = !!challenge && new Date(challenge.expiresAt).getTime() <= now
  const usable = !!challenge && !dead && !expired && (attemptsLeft ?? 1) > 0

  const request = async () => {
    setBusy('request'); setError(null); setInfo(null)
    try {
      const c = await onRequest(channel)
      setChallenge(c)
      setSentAt(Date.now()); setNow(Date.now())
      setAttemptsLeft(c.attemptsLeft ?? c.maxAttempts ?? 5)
      setDead(false); setCode('')
      setInfo(c.channel === 'Email' ? tx('Kod e-posta adresinize gönderildi.')
        : c.channel === 'InApp+Email' ? tx('Kod uygulama içi bildirimlerinize ve e-posta adresinize gönderildi.')
        : tx('Kod uygulama içi bildirimlerinize gönderildi.'))
    } catch (e) {
      setError(otpErrorText(e).text)
    } finally { setBusy(null) }
  }

  const sign = async () => {
    if (!challenge) return
    setBusy('sign'); setError(null)
    try {
      const r = await onSign(challenge, code.trim())
      setEvidence(toEvidence(r))
      onSigned?.(r)
    } catch (e) {
      const x = otpErrorText(e)
      setError(x.text)
      if (x.attemptsLeft !== undefined) setAttemptsLeft(x.attemptsLeft)
      if (x.dead) { setDead(true); setCode('') }
    } finally { setBusy(null) }
  }

  if (evidence) {
    return (
      <div className="space-y-3">
        <InfoNote>{tx('Belge imzalandı. İmza kanıtı kaydedildi ve belgenin saklama süresi boyunca tutulur.')}</InfoNote>
        <SignatureEvidenceDetails e={evidence} />
      </div>
    )
  }

  return (
    <div className="space-y-3 rounded-xl border border-border p-3">
      <p className="text-[12.5px] text-muted-foreground">
        {tx('Tek kullanımlık kod 10 dakika geçerlidir; en fazla 5 hatalı deneme hakkınız var. Kodu kimseyle paylaşmayın.')}
      </p>
      {chooseChannel && !challenge && (
        <SelectField label={tx('Kod nereye gönderilsin?')} value={channel} onChange={(v) => setChannel(v as 'InApp' | 'Email')}
          options={[{ value: 'InApp', label: tx('Uygulama içi bildirim') }, { value: 'Email', label: tx('E-posta') }]} />
      )}
      <div className="flex flex-wrap items-end gap-3">
        <Button variant={challenge ? 'outline' : 'default'} onClick={() => void request()}
          disabled={disabled || busy !== null || (!!challenge && wait > 0)}>
          <KeyRound className="size-4" />{' '}
          {!challenge ? tx('Kod gönder') : wait > 0 ? tx('Yeni kod ({0} sn)', [wait]) : tx('Yeni kod gönder')}
        </Button>
        {challenge && (
          <>
            <TextField label={tx('6 haneli kod')} inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code}
              onChange={(e) => setCode(e.target.value.replace(/\D/g, '').slice(0, 6))} disabled={!usable}
              className="w-36 font-mono tracking-[0.3em]" />
            <Button onClick={() => void sign()} disabled={disabled || !usable || busy !== null || !/^\d{6}$/.test(code.trim())}>
              <FileSignature className="size-4" />{' '}{tx('İmzala')}
            </Button>
          </>
        )}
      </div>
      {disabled && disabledHint && <p className="text-[12px] text-muted-foreground">{disabledHint}</p>}
      {challenge && (
        <p className="text-[12px] text-muted-foreground" aria-live="polite">
          {/* sentNote (ör. maskeli adresle "Kod a***@x adresine gönderildi") varsa genel iletinin yerine geçer; ileti iki kez görünmesin. */}
          {sentNote ?? info}{' '}
          {expired
            ? tx('Kodun süresi doldu; yeni kod isteyin.')
            : tx('Son geçerlilik: {0}. Kalan deneme: {1}.', [formatDateTime(challenge.expiresAt), attemptsLeft ?? 0])}
        </p>
      )}
      {error && <p role="alert" className="text-[12.5px] text-destructive">{error}</p>}
    </div>
  )
}
