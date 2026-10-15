import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import QRCode from 'qrcode'
import { CheckCircle2, KeyRound, MapPin, Maximize2, Plus, QrCode, ScanLine, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { useDirectory } from '@/api/directory'
import { currentPosition, timeClockApi, type ClockPunch, type ClockSite, type SiteInput } from '@/api/timeclock'
import { formatDateTime } from '@/lib/format'
import { PersonSelect, errMsg, useAction } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'

const METHOD: Record<ClockPunch['method'], string> = {
  Manual: tx('Elle'), Device: tx('Cihaz'), Import: tx('Aktarım'), Qr: tx('QR'), Card: tx('Kart'), Pin: tx('PIN'), Web: tx('Web'), Chat: tx('Sohbet'),
}

const KVKK_NOTE = tx('Biyometrik veri (parmak izi, yüz) kullanılmaz. Konum yalnızca noktada konum denetimi açıksa giriş-çıkış anında bir kez istenir; sürekli takip yoktur. Saklanan: "noktada mı" ve uzaklık aralığı (ör. 50-100 m); ham koordinat yalnızca şirket açtıysa ve sınırlı süre tutulur.')

/** Konum aydınlatma metni (KVKK m.10): konum ilk kez istenmeden önce gösterilir. */
const LOCATION_NOTICE = tx('Bu noktada giriş-çıkışınızın işyerinde yapıldığını doğrulamak için tarayıcınızdan yalnızca şimdi, bir kez konum alınacak. Amaç: puantaj kaydının doğruluğu (iş sözleşmesinin ifası, KVKK m.5/2-c). Saklanan: noktada olup olmadığınız ve uzaklık aralığı; şirket ayrıca ham konum saklamayı açtıysa bu bilgi en çok ayarlanan gün kadar tutulup silinir. Konum izni vermezseniz bu noktada giriş-çıkış yapılamaz; İK’ya başvurabilirsiniz. Haklarınız için KVKK m.11.')
const NOTICE_KEY = 'hr360.timeclock.locationNotice'
function noticeSeen(): boolean { try { return localStorage.getItem(NOTICE_KEY) === '1' } catch { return false } }
function markNoticeSeen() { try { localStorage.setItem(NOTICE_KEY, '1') } catch { /* gizli pencere */ } }

/* ------------------------------------------------------------------ çalışan */

/** /panel/giris-cikis — kişinin kendi giriş-çıkışı. QR okutulunca ?k=<jeton> ile açılır ve hemen işlenir. */
export function TimeClockPage() {
  const toast = useToast()
  const qc = useQueryClient()
  const [params, setParams] = useSearchParams()
  const token = params.get('k')
  const me = useQuery({ queryKey: ['timeclock', 'me'], queryFn: ({ signal }) => timeClockApi.me(signal) })
  const sites = useQuery({ queryKey: ['timeclock', 'sites'], queryFn: ({ signal }) => timeClockApi.sites(signal) })
  const [siteId, setSiteId] = useState('')
  const [busy, setBusy] = useState(false)
  const [pin, setPin] = useState('')
  const done = useRef(false)
  const confirm = useConfirm()

  async function punch(body: { token?: string; siteId?: string }, needsLocation: boolean) {
    setBusy(true)
    try {
      let pos: { latitude: number; longitude: number } | undefined
      if (needsLocation) {
        // Aydınlatma metni konum ilk kez istenmeden önce bir kez gösterilir.
        if (!noticeSeen()) {
          const ok = await confirm({ title: tx('Konum doğrulaması'), note: LOCATION_NOTICE, action: tx('Anladım, konumu paylaş') })
          if (!ok) { setBusy(false); return }
          markNoticeSeen()
        }
        try { pos = await currentPosition() } catch { /* sunucu "konum gerekli" der */ }
      }
      const r = await timeClockApi.punch({ ...body, ...pos })
      toast.ok(r.punch.kind === 'In' ? tx('Giriş kaydedildi{0}', [r.site ? ` — ${r.site}` : '']) : tx('Çıkış kaydedildi{0}', [r.site ? ` — ${r.site}` : '']))
      void qc.invalidateQueries({ queryKey: ['timeclock'] })
      void qc.invalidateQueries({ queryKey: ['timeshift'] })
    } catch (e) {
      // Konum denetimi açık noktada konum alınmadıysa bir kez konumla yeniden denenir.
      if (!needsLocation && /konum|location/i.test(errMsg(e))) { setBusy(false); return punch(body, true) }
      toast.stop(errMsg(e))
    } finally {
      setBusy(false)
    }
  }

  // QR ile gelindiyse jeton bir kez işlenir ve adresten kaldırılır (geri tuşu tekrar işlemesin).
  useEffect(() => {
    if (!token || done.current) return
    done.current = true
    void punch({ token }, false).then(() => { params.delete('k'); setParams(params, { replace: true }) })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token])

  const site = sites.data?.find((s) => s.id === siteId)
  const setPinAction = useAction(() => timeClockApi.setPin(pin), { success: tx('PIN kaydedildi'), invalidate: [['timeclock', 'me']], onDone: () => setPin('') })
  const m = me.data

  return (
    <>
      <PageHeader title={tx('Giriş-çıkış')} description={tx('Ofisteki ekranda QR kodu telefonunuzla okutun ya da noktayı seçip düğmeye basın.')} />
      <div className="grid gap-5 lg:grid-cols-[1fr_340px]">
        <Panel>
          <PanelHead title={tx('Şimdi')} note={new Date().toLocaleDateString(appLocale, { dateStyle: 'full' })} />
          <PanelBody className="space-y-4">
            {m && !m.linked && <InfoNote>{tx('Hesabınıza bağlı çalışan kaydı yok; giriş-çıkış yapılamaz.')}</InfoNote>}
            {me.isPending ? <RowsSkeleton rows={2} /> : (
              <div className="flex flex-wrap items-center gap-3">
                {m?.clockedIn
                  ? <StatusBadge tone="success">{tx('İçeride · {0} itibarıyla', [formatDateTime(m.openSince)])}</StatusBadge>
                  : <StatusBadge>{tx('Dışarıda')}</StatusBadge>}
              </div>
            )}
            <div className="grid gap-3 sm:grid-cols-[1fr_auto] sm:items-end">
              <SelectField label={tx('Nokta')} value={siteId} onChange={setSiteId}
                options={[{ value: '', label: tx('Nokta seçmeden (uzaktan çalışma)') }, ...(sites.data ?? []).map((s) => ({ value: s.id, label: s.name }))]} />
              <Button size="lg" disabled={busy || m?.linked === false} onClick={() => void punch({ siteId: siteId || undefined }, !!site?.checkLocation)}>
                <ScanLine className="size-4" /> {m?.clockedIn ? tx('Çıkış yap') : tx('Giriş yap')}
              </Button>
            </div>
            {site?.checkLocation && <p className="flex items-center gap-1.5 text-[12.5px] text-muted-foreground"><MapPin className="size-3.5" /> {tx('Bu noktada konum giriş-çıkış anında bir kez denetlenir; yalnızca "noktada mı" ve uzaklık aralığı saklanır.')}</p>}
            <InfoNote>{KVKK_NOTE}</InfoNote>
          </PanelBody>
        </Panel>
        <div className="space-y-5">
          {/* Çalışan kaydı olmayan hesapta PIN ucu 403 döner: panel gösterilmez (üstteki not durumu açıklar). */}
          {m?.linked !== false && (
          <Panel>
            <PanelHead title={<span className="flex items-center gap-2"><KeyRound className="size-4 text-primary" />{' '}{tx('Terminal PIN')}</span>}
              note={m?.badgeCode ? tx('Sicil kodunuz: {0}', [m.badgeCode]) : tx('Sicil kodunuzu İK atar.')} />
            <PanelBody className="space-y-3">
              <p className="text-[12.5px] text-muted-foreground">{m?.hasPin ? tx('PIN tanımlı. Değiştirmek için yenisini girin.') : tx('Kart okuyucu terminalinde sicil kodu + PIN ile giriş yapabilmek için PIN belirleyin.')}</p>
              <div className="flex gap-2">
                <TextField label={tx('Yeni PIN (4-8 rakam)')} type="password" inputMode="numeric" autoComplete="new-password" value={pin} onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))} />
                <Button className="self-end" disabled={pin.length < 4 || setPinAction.isPending} onClick={() => setPinAction.mutate(undefined)}>{tx('Kaydet')}</Button>
              </div>
            </PanelBody>
          </Panel>
          )}
          <Panel>
            <PanelHead title={tx('Son hareketler')} />
            <PanelBody className="p-0">
              {!m?.punches.length ? <p className="p-4 text-[13px] text-muted-foreground">{tx('Henüz hareket yok.')}</p> : (
                <ul className="divide-y divide-border text-[13px]">
                  {m.punches.map((p) => (
                    <li key={p.id} className="flex items-center gap-2 px-4 py-2">
                      <StatusBadge tone={p.kind === 'In' ? 'success' : 'neutral'}>{p.kind === 'In' ? tx('Giriş') : tx('Çıkış')}</StatusBadge>
                      <span className="flex-1">{formatDateTime(p.at)}</span>
                      {p.onSite === false && <StatusBadge tone="warning">{tx('noktada değil')}</StatusBadge>}
                      {p.distanceBucket && <span className="text-[11.5px] text-muted-foreground">{tx('{0} m', [p.distanceBucket])}</span>}
                      <span className="text-muted-foreground">{METHOD[p.method] ?? p.method}</span>
                    </li>
                  ))}
                </ul>
              )}
            </PanelBody>
          </Panel>
        </div>
      </div>
    </>
  )
}

/* ------------------------------------------------------------------ İK: noktalar, kiosk, kartlar */

function Kiosk({ site, onClose }: { site: ClockSite; onClose: () => void }) {
  const [svg, setSvg] = useState('')
  const [left, setLeft] = useState(30)
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    let timer: number | undefined
    let stop = false
    const load = async () => {
      try {
        const r = await timeClockApi.qr(site.id)
        const url = `${window.location.origin}/panel/giris-cikis?k=${encodeURIComponent(r.token)}`
        setSvg(await QRCode.toString(url, { type: 'svg', margin: 1, errorCorrectionLevel: 'M' }))
        const ms = Math.max(2000, new Date(r.expiresAt).getTime() - Date.now())
        setLeft(Math.round(ms / 1000))
        if (!stop) timer = window.setTimeout(load, ms + 300)
      } catch {
        if (!stop) timer = window.setTimeout(load, 5000)
      }
    }
    void load()
    const tick = window.setInterval(() => setLeft((s) => Math.max(0, s - 1)), 1000)
    return () => { stop = true; window.clearTimeout(timer); window.clearInterval(tick) }
  }, [site.id])
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Kiosk — {0}', [site.name])} note={tx('Önizleme (İK oturumuyla). Paylaşılan tablette İK oturumu açık bırakmayın: tablette {0} adresini açıp noktanın terminal anahtarını girin. Kod 30 saniyede bir yenilenir.', [`${window.location.origin}/kiosk`])}
      footer={<Button variant="outline" onClick={() => void ref.current?.requestFullscreen?.()}><Maximize2 className="size-4" /> {tx('Tam ekran')}</Button>}>
      <div ref={ref} className="flex flex-col items-center gap-3 bg-white p-6 text-black">
        <p className="text-xl font-semibold">{site.name}</p>
        {/* qrcode kütüphanesinin ürettiği SVG; kullanıcı girdisi içermez */}
        <div className="w-72 max-w-full" dangerouslySetInnerHTML={{ __html: svg }} />
        <p className="text-sm">{tx('Telefonunuzun kamerasıyla okutun · {0} sn', [left])}</p>
      </div>
    </Modal>
  )
}

function SiteModal({ site, onClose }: { site?: ClockSite; onClose: () => void }) {
  const [f, setF] = useState<SiteInput>({
    name: site?.name ?? '', allowQr: site?.allowQr ?? true, allowTerminal: site?.allowTerminal ?? true, checkLocation: site?.checkLocation ?? false,
    isActive: site?.isActive ?? true,
  })
  // Konum alanları ham metin olarak tutulur (her tuşta sayıya çevrilirse "41.0082" yazılamaz); kaydederken ayrıştırılır.
  const [lat, setLat] = useState(site?.latitude?.toString() ?? '')
  const [lon, setLon] = useState(site?.longitude?.toString() ?? '')
  const [radius, setRadius] = useState(String(site?.radiusMeters ?? 200))
  // Koordinatta binlik ayıracı olmaz: virgül ondalık noktaya çevrilir ("41,008" = 41.008).
  const coord = (s: string) => { const t = s.trim().replace(',', '.'); return /^[-+]?\d+(\.\d+)?$/.test(t) ? Number(t) : null }
  const latV = coord(lat)
  const lonV = coord(lon)
  const radiusV = /^\d+$/.test(radius.trim()) ? Number(radius.trim()) : null
  const latErr = !f.checkLocation ? undefined : lat.trim() === '' ? tx('Enlem gerekli.') : latV === null || latV < -90 || latV > 90 ? tx('Enlem -90 ile 90 arasında olmalı.') : undefined
  const lonErr = !f.checkLocation ? undefined : lon.trim() === '' ? tx('Boylam gerekli.') : lonV === null || lonV < -180 || lonV > 180 ? tx('Boylam -180 ile 180 arasında olmalı.') : undefined
  const radiusErr = !f.checkLocation ? undefined : radiusV === null || radiusV < 20 || radiusV > 5000 ? tx('Yarıçap 20 ile 5000 metre arasında olmalı.') : undefined
  const invalid = !!(latErr || lonErr || radiusErr)
  const body = (): SiteInput => ({
    ...f,
    latitude: latV ?? site?.latitude ?? null, longitude: lonV ?? site?.longitude ?? null,
    radiusMeters: radiusV !== null && radiusV >= 20 && radiusV <= 5000 ? radiusV : site?.radiusMeters,
  })
  const save = useAction(() => (site ? timeClockApi.updateSite(site.id, body()) : timeClockApi.createSite(body())), { success: tx('Nokta kaydedildi'), invalidate: [['timeclock']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={site ? tx('Noktayı düzenle') : tx('Yeni giriş-çıkış noktası')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!f.name.trim() || invalid || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <TextField label={tx('Ad')} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder={tx('Ör. Merkez ofis giriş')} />
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.allowQr} onCheckedChange={(v) => setF({ ...f, allowQr: v === true })} /> {tx('QR kod ile giriş-çıkış')}</label>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.allowTerminal} onCheckedChange={(v) => setF({ ...f, allowTerminal: v === true })} /> {tx('Kart okuyucu / PIN terminali')}</label>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.checkLocation} onCheckedChange={(v) => setF({ ...f, checkLocation: v === true })} /> {tx('Web/mobil girişte konum denetimi (isteğe bağlı)')}</label>
        {f.checkLocation && (
          <div className="grid gap-3 sm:grid-cols-3">
            <TextField label={tx('Enlem')} inputMode="decimal" placeholder="41.0082" value={lat} error={latErr} onChange={(e) => setLat(e.target.value)} />
            <TextField label={tx('Boylam')} inputMode="decimal" placeholder="28.9784" value={lon} error={lonErr} onChange={(e) => setLon(e.target.value)} />
            <TextField label={tx('Yarıçap (m)')} inputMode="numeric" value={radius} error={radiusErr} onChange={(e) => setRadius(e.target.value)} />
          </div>
        )}
        <InfoNote>{KVKK_NOTE}</InfoNote>
      </div>
    </Modal>
  )
}

function CredentialsPanel() {
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['timeclock', 'credentials'], queryFn: ({ signal }) => timeClockApi.credentials(signal) })
  const [emp, setEmp] = useState('')
  const [badge, setBadge] = useState('')
  const [card, setCard] = useState('')
  const save = useAction(() => timeClockApi.setCredential(emp, { badgeCode: badge, cardNumber: card || undefined }), {
    success: tx('Kaydedildi'), invalidate: [['timeclock', 'credentials']], onDone: () => { setCard(''); setBadge('') },
  })
  const clear = useAction((id: string) => timeClockApi.setCredential(id, { badgeCode: '', clearCard: true }), { success: tx('Kart ve sicil kodu kaldırıldı'), invalidate: [['timeclock', 'credentials']] })
  const confirm = useConfirm()
  return (
    <Panel>
      <PanelHead title={tx('Kart ve sicil kodları')} note={tx('Kart numarası yalnızca özet (hash) olarak saklanır, geri okunamaz. PIN\'i çalışan kendisi belirler.')} />
      <PanelBody className="space-y-4">
        <div className="grid gap-3 md:grid-cols-[1.4fr_1fr_1.2fr_auto] md:items-end">
          <PersonSelect value={emp} onChange={setEmp} />
          <TextField label={tx('Sicil kodu')} value={badge} onChange={(e) => setBadge(e.target.value)} />
          <TextField label={tx('Kart numarası (okutun)')} value={card} onChange={(e) => setCard(e.target.value)} autoComplete="off" />
          <Button disabled={!emp || (!badge && !card) || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
        </div>
        {q.isPending ? <RowsSkeleton rows={2} /> : !q.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Tanımlı kart ya da sicil kodu yok.')}</p> : (
          <ul className="divide-y divide-border text-[13px]">
            {q.data.map((c) => (
              <li key={c.employeeId} className="flex flex-wrap items-center gap-3 py-2">
                <span className="min-w-0 flex-1">{nameOf(c.employeeId)}</span>
                <span className="text-muted-foreground">{c.badgeCode ?? '—'}</span>
                {c.hasCard && <StatusBadge tone="info">{tx('Kart')}</StatusBadge>}
                {c.hasPin && <StatusBadge>{tx('PIN')}</StatusBadge>}
                {c.locked && <StatusBadge tone="danger">{tx('Kilitli')}</StatusBadge>}
                <Button size="icon" variant="ghost" aria-label={tx('Kaldır')} onClick={async () => {
                  if (await confirm({ title: tx('{0} için kart ve sicil kodu kaldırılsın mı?', [nameOf(c.employeeId)]), note: tx('Çalışan, yeni kart/sicil kodu tanımlanana kadar terminali kullanamaz.'), action: tx('Kaldır') })) clear.mutate(c.employeeId)
                }}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

/** /panel/giris-cikis/yonetim — İK: noktalar, kiosk QR ekranı, terminal anahtarı, kartlar. */
export function TimeClockAdminPage() {
  const sites = useQuery({ queryKey: ['timeclock', 'sites'], queryFn: ({ signal }) => timeClockApi.sites(signal) })
  const [editing, setEditing] = useState<ClockSite | 'new' | null>(null)
  const [kiosk, setKiosk] = useState<ClockSite | null>(null)
  const [key, setKey] = useState<string | null>(null)
  const del = useAction((id: string) => timeClockApi.deleteSite(id), { success: tx('Nokta silindi'), invalidate: [['timeclock']] })
  const genKey = useAction((id: string) => timeClockApi.deviceKey(id), { invalidate: [['timeclock']], onDone: (r) => setKey(r.deviceKey) })
  const confirm = useConfirm()
  return (
    <>
      <PageHeader title={tx('Giriş-çıkış yönetimi')} description={tx('Giriş-çıkış noktaları, kiosk QR ekranı, kart okuyucu terminalleri ve çalışan kartları.')}
        actions={<Button onClick={() => setEditing('new')}><Plus className="size-4" /> {tx('Yeni nokta')}</Button>} />
      <div className="space-y-5">
        <Panel>
          <PanelHead title={tx('Noktalar')} />
          <PanelBody className="p-0">
            {sites.isPending ? <div className="p-4"><RowsSkeleton rows={2} /></div> : !sites.data?.length ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Henüz nokta yok. Ofis girişleri için nokta ekleyin.')}</p> : (
              <ul className="divide-y divide-border">
                {sites.data.map((s) => (
                  <li key={s.id} className="flex flex-wrap items-center gap-2 px-5 py-3 text-[13px]">
                    <span className="min-w-0 flex-1 font-medium">{s.name}</span>
                    {s.allowQr && <StatusBadge tone="info">{tx('QR')}</StatusBadge>}
                    {s.allowTerminal && <StatusBadge>{s.hasDeviceKey ? tx('Terminal bağlı') : tx('Terminal')}</StatusBadge>}
                    {s.checkLocation && <StatusBadge tone="warning">{tx('Konum denetimi')}</StatusBadge>}
                    {!s.isActive && <StatusBadge tone="danger">{tx('Pasif')}</StatusBadge>}
                    {s.allowQr && s.isActive && <Button size="sm" variant="outline" onClick={() => setKiosk(s)}><QrCode className="size-4" /> {tx('Kiosk')}</Button>}
                    {s.allowTerminal && <Button size="sm" variant="outline" disabled={genKey.isPending} onClick={async () => {
                      // Anahtar varsa yenileme bağlı terminali hemen devre dışı bırakır: onay istenir.
                      if (!s.hasDeviceKey || await confirm({
                        title: tx('Terminal anahtarı yenilensin mi?'),
                        note: tx('"{0}" noktasındaki terminalin mevcut anahtarı hemen geçersiz olur; yeni anahtar terminale girilene kadar kart/PIN ile giriş-çıkış yapılamaz.', [s.name]),
                        action: tx('Yenile'),
                      })) genKey.mutate(s.id)
                    }}><KeyRound className="size-4" /> {tx('Terminal anahtarı')}</Button>}
                    <Button size="sm" variant="ghost" onClick={() => setEditing(s)}>{tx('Düzenle')}</Button>
                    <Button size="icon" variant="ghost" aria-label={tx('Sil')} disabled={del.isPending} onClick={async () => {
                      if (await confirm({
                        title: tx('"{0}" noktası silinsin mi?', [s.name]),
                        note: tx('Noktanın QR kiosku ve terminal anahtarı çalışmaz hâle gelir. Geçmiş giriş-çıkış kayıtları silinmez.'),
                        action: tx('Sil'),
                      })) del.mutate(s.id)
                    }}><Trash2 className="size-4" /></Button>
                  </li>
                ))}
              </ul>
            )}
          </PanelBody>
        </Panel>
        <CredentialsPanel />
        <InfoNote>{KVKK_NOTE}</InfoNote>
      </div>
      {editing && <SiteModal site={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
      {kiosk && <Kiosk site={kiosk} onClose={() => setKiosk(null)} />}
      {key && (
        <Modal open onClose={() => setKey(null)} title={tx('Terminal anahtarı')} note={tx('Bu anahtar yalnızca şimdi gösterilir; terminalin ayarına girin. Önceki anahtar geçersiz oldu.')}
          footer={<Button onClick={() => { void navigator.clipboard?.writeText(key); setKey(null) }}><CheckCircle2 className="size-4" /> {tx('Kopyala ve kapat')}</Button>}>
          <code className="block break-all rounded-lg bg-muted p-3 text-[12.5px]">{key}</code>
          <p className="mt-3 text-[12.5px] text-muted-foreground">{tx('Terminal, {0} adresine X-Device-Key başlığıyla kart numarası ya da sicil kodu + PIN gönderir.', ['POST /api/timeshift/time-clock/terminal/punch'])}</p>
          <p className="mt-2 text-[12.5px] text-muted-foreground">{tx('Paylaşılan tablet (kiosk) için tablette {0} adresini açıp bu anahtarı girin: 30 saniyede bir değişen QR ve sicil kodu + PIN ekranı gösterilir.', [`${window.location.origin}/kiosk`])}</p>
        </Modal>
      )}
    </>
  )
}
