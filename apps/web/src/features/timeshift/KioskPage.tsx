/**
 * Madde 68: QR kiosk (paylaşılan tablet, oturumsuz). Tablet noktanın terminal anahtarıyla
 * (Giriş-çıkış yönetimi > Terminal anahtarı) çalışır; İK oturumu açık bırakılmaz.
 *  - 30 saniyede bir değişen imzalı QR: çalışan kendi telefonunda (oturumu açık) okutur → /panel/giris-cikis?k=…
 *  - Sicil kodu + PIN ile giriş-çıkış (noktada terminal açıksa). 5 hatalı PIN'de kişi 15 dk kilitlenir.
 * Uçlar cihaz başına hız sınırlıdır; her hareket ve hatalı PIN denetim kaydına yazılır. Ekranda yalnızca
 * ad ve işlem gösterilir, birkaç saniye sonra silinir.
 */
import { useEffect, useRef, useState } from 'react'
import QRCode from 'qrcode'
import { KeyRound, LogIn, LogOut, Maximize2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { TextField } from '@/components/ui/Field'
import { errMsg } from '@/features/shared/kit'
import { KIOSK_KEY_STORAGE, kioskApi, type KioskState } from '@/api/timeclock'
import { tx, appLocale } from '@/lib/i18n'

function readKey(): string {
  try { return localStorage.getItem(KIOSK_KEY_STORAGE) ?? '' } catch { return '' }
}
function writeKey(v: string | null) {
  try { if (v) localStorage.setItem(KIOSK_KEY_STORAGE, v); else localStorage.removeItem(KIOSK_KEY_STORAGE) } catch { /* depolama kapalı */ }
}

/** Kalan saniye (QR yenilemesine). Saf; birim testli. */
export function secondsLeft(expiresAt: string, now: number): number {
  const t = Date.parse(expiresAt)
  return Number.isNaN(t) ? 0 : Math.max(0, Math.ceil((t - now) / 1000))
}

export function KioskPage() {
  const [key, setKey] = useState(readKey)
  const [draft, setDraft] = useState('')
  const [state, setState] = useState<KioskState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [svg, setSvg] = useState('')
  const [left, setLeft] = useState(0)
  const [badge, setBadge] = useState('')
  const [pin, setPin] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; text: string; kind?: 'In' | 'Out' } | null>(null)
  const root = useRef<HTMLDivElement>(null)

  // Durum ve QR: süre dolmadan yenilenir; hata olursa 10 sn sonra yeniden denenir.
  useEffect(() => {
    if (!key) return
    let timer: number | undefined
    let stop = false
    const load = async () => {
      try {
        const s = await kioskApi.state(key)
        if (stop) return
        setState(s)
        setError(null)
        setSvg(s.token ? await QRCode.toString(`${window.location.origin}/panel/giris-cikis?k=${encodeURIComponent(s.token)}`, { type: 'svg', margin: 1, errorCorrectionLevel: 'M' }) : '')
        const ms = Math.max(2000, Date.parse(s.expiresAt) - Date.now())
        timer = window.setTimeout(load, ms + 300)
      } catch (e) {
        if (stop) return
        setError(errMsg(e))
        timer = window.setTimeout(load, 10_000)
      }
    }
    void load()
    return () => { stop = true; window.clearTimeout(timer) }
  }, [key])

  useEffect(() => {
    const id = window.setInterval(() => setLeft(state ? secondsLeft(state.expiresAt, Date.now()) : 0), 500)
    return () => window.clearInterval(id)
  }, [state])

  useEffect(() => {
    if (!result) return
    const id = window.setTimeout(() => setResult(null), 5000)
    return () => window.clearTimeout(id)
  }, [result])

  async function submitPin(e: React.FormEvent) {
    e.preventDefault()
    if (!badge.trim() || pin.length < 4) return
    setBusy(true)
    try {
      const r = await kioskApi.pinPunch(key, badge.trim(), pin)
      const time = new Date(r.at).toLocaleTimeString(appLocale, { hour: '2-digit', minute: '2-digit' })
      setResult({ ok: true, kind: r.kind, text: r.kind === 'In' ? tx('Hoş geldiniz {0} — giriş {1}', [r.firstName ?? '', time]) : tx('İyi günler {0} — çıkış {1}', [r.firstName ?? '', time]) })
    } catch (err) {
      setResult({ ok: false, text: errMsg(err) })
    } finally {
      setBadge('')
      setPin('')
      setBusy(false)
    }
  }

  if (!key) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background p-4">
        <form className="w-full max-w-md space-y-4 rounded-2xl border border-border bg-card p-6"
          onSubmit={(e) => { e.preventDefault(); const k = draft.trim(); if (k.startsWith('hrc_')) { writeKey(k); setKey(k) } }}>
          <h1 className="flex items-center gap-2 text-lg font-semibold"><KeyRound className="size-5 text-primary" /> {tx('Kiosk kurulumu')}</h1>
          <p className="text-[13px] text-muted-foreground">{tx('Giriş-çıkış yönetiminde noktanın “Terminal anahtarı”nı oluşturup buraya girin. Anahtar yalnızca bu cihazda saklanır.')}</p>
          <TextField label={tx('Terminal anahtarı')} value={draft} onChange={(e) => setDraft(e.target.value)} autoComplete="off" placeholder="hrc_…" />
          <Button type="submit" disabled={!draft.trim().startsWith('hrc_')}>{tx('Kaydet')}</Button>
        </form>
      </div>
    )
  }

  return (
    <div ref={root} className="flex min-h-screen flex-col items-center gap-6 bg-white p-4 text-black sm:p-8">
      <div className="flex w-full max-w-4xl items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">{state?.site ?? tx('Giriş-çıkış')}</h1>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={() => void root.current?.requestFullscreen?.()}><Maximize2 className="size-4" /> {tx('Tam ekran')}</Button>
          <Button variant="ghost" size="sm" onClick={() => { if (window.confirm(tx('Kiosk anahtarı bu cihazdan silinsin mi?'))) { writeKey(null); setKey(''); setState(null) } }}>{tx('Anahtarı sil')}</Button>
        </div>
      </div>
      {error && <p role="alert" className="text-[14px] text-red-700">{error}</p>}
      <div className="grid w-full max-w-4xl gap-8 md:grid-cols-2">
        {state?.allowQr && (
          <section className="flex flex-col items-center gap-3">
            {/* qrcode kütüphanesinin ürettiği SVG; kullanıcı girdisi içermez */}
            <div className="w-72 max-w-full" aria-label={tx('Giriş-çıkış QR kodu')} dangerouslySetInnerHTML={{ __html: svg }} />
            <p className="text-center text-[15px]">{tx('Telefonunuzun kamerasıyla okutun · {0} sn', [left])}</p>
            {state.checkLocation && <p className="text-center text-[12.5px] text-neutral-600">{tx('Bu noktada telefonunuz konumu bir kez doğrular.')}</p>}
          </section>
        )}
        {state?.allowPin && (
          <section className="space-y-3">
            <h2 className="text-lg font-medium">{tx('Sicil kodu + PIN')}</h2>
            <form onSubmit={submitPin} className="space-y-3">
              <TextField label={tx('Sicil kodu')} value={badge} autoComplete="off" onChange={(e) => setBadge(e.target.value.toUpperCase().slice(0, 12))} />
              <TextField label={tx('PIN')} type="password" inputMode="numeric" autoComplete="off" value={pin} onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))} />
              <Button type="submit" size="lg" className="w-full" disabled={busy || !badge.trim() || pin.length < 4}>{tx('Giriş / çıkış')}</Button>
            </form>
            {result && (
              <p role="status" className={result.ok ? 'flex items-center gap-2 rounded-xl bg-emerald-100 p-3 text-[15px] text-emerald-900' : 'rounded-xl bg-red-100 p-3 text-[14px] text-red-900'}>
                {result.kind === 'In' ? <LogIn className="size-5" /> : result.kind === 'Out' ? <LogOut className="size-5" /> : null} {result.text}
              </p>
            )}
          </section>
        )}
        {state && !state.allowQr && !state.allowPin && <p>{tx('Bu noktada QR ve PIN ile giriş-çıkış kapalı.')}</p>}
      </div>
      <p className="max-w-2xl text-center text-[12px] text-neutral-600">{tx('Biyometrik veri kullanılmaz. Ekranda yalnızca adınız ve işlem görünür, birkaç saniye sonra silinir. İşlemler denetim kaydına yazılır.')}</p>
    </div>
  )
}
