import { useEffect, useState } from 'react'
import { MySecurityPanel } from '@/features/governance/AccessControlPanels'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { CalendarPlus, Copy, Download, Eye, EyeOff, Fingerprint, KeyRound, RefreshCw, Save, ShieldCheck } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { useToast } from '@/components/ui/Toast'
import { apiFetch } from '@/api/client'
import { engagementApi, type MyProfile } from '@/api/engagement'
import { dataRequestLabels, governanceApi, type DataRequestKind } from '@/api/governance'
import { kvkk10Api } from '@/api/wave10'
import { useAuth } from '@/auth/useAuth'
import { useMyEmployeeId } from '@/api/queries'
import { formatDate } from '@/lib/format'
import { ChipInput, errMsg, useAction } from '@/features/shared/kit'
import { PhotoUploader } from '@/components/people/PhotoUploader'
import { CalendarConnections } from '@/features/shared/Meetings'
import { SkillSuggest } from '@/features/shared/SkillSuggest'
import { MyAccessLog, MyObjections } from './PrivacyExtras'
import { MyChatAccounts } from './ChatAccounts'
import { DevicePanel } from './DevicePanel'
import { AccessibilityPanel, NotificationPrefsPanel } from './NotificationPrefs'
import { ExtraInfoPanel } from '@/features/governance/CustomFields'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'
import { useConfirm } from '@/components/ui/Confirm'

type TabKey = 'bilgiler' | 'bildirimler' | 'gizlilik' | 'takvim' | 'guvenlik' | 'erisilebilirlik'

const SKILL_HINTS = [tx('İletişim'), tx('Excel'), tx('Proje yönetimi'), tx('SQL'), tx('React'), tx('.NET'), tx('Satış'), tx('Liderlik'), tx('İngilizce')]

function SensitiveField({ label, field, employeeId, masked, has, onChange, value }: {
  label: string; field: 'iban' | 'nationalId'; employeeId: string; masked: string | null; has: boolean; value: string; onChange: (v: string) => void
}) {
  const toast = useToast()
  const [revealed, setRevealed] = useState<string | null>(null)
  const reveal = async () => {
    if (revealed) return setRevealed(null)
    try {
      const r = await engagementApi.reveal(employeeId, field)
      setRevealed(r.value ?? '')
    } catch (e) {
      toast.stop(errMsg(e))
    }
  }
  return (
    <div className="space-y-1.5">
      <div className="flex items-end gap-2">
        <div className="flex-1">
          <TextField label={label} value={value} onChange={(e) => onChange(e.target.value)} placeholder={has ? (revealed ?? masked ?? '') : field === 'iban' ? 'TR00 0000 0000 0000 0000 0000 00' : '11 haneli'} className="font-mono" />
        </div>
        {has && (
          <Button type="button" variant="outline" size="icon" onClick={reveal} aria-label={revealed ? tx('Gizle') : tx('Göster')} title={tx('Göster (denetim kaydına yazılır)')}>
            {revealed ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
          </Button>
        )}
      </div>
      <p className="text-[11.5px] text-muted-foreground">
        {has ? <>{tx('Kayıtlı:')}{' '}<span className="font-mono">{revealed ?? masked}</span>{' '}{tx('· açık hâli her görüntülemede denetim kaydına yazılır.')}</> : tx('Boş bırakırsanız değişmez.')}
      </p>
    </div>
  )
}

function InfoTab({ p }: { p: MyProfile }) {
  const [f, setF] = useState({
    birthDate: p.birthDate ?? '', showBirthday: p.showBirthday, pronouns: p.pronouns ?? '', bio: p.bio ?? '', linkedInUrl: p.linkedInUrl ?? '',
    address: p.address ?? '', emergencyContactName: p.emergencyContactName ?? '', emergencyContactPhone: p.emergencyContactPhone ?? '',
    skills: p.skills, interests: p.interests, iban: '', nationalId: '',
  })
  const me = useQuery({ queryKey: ['my-employee-record'], queryFn: ({ signal }) => apiFetch<{ phone: string | null } | undefined>('/api/employee/employees/me?optional=true', { signal }).then((r) => r ?? null) })
  const [phone, setPhone] = useState('')
  useEffect(() => setPhone(me.data?.phone ?? ''), [me.data?.phone])
  const save = useAction(async () => {
    const r = await engagementApi.updateMyProfile({
      ...f, birthDate: f.birthDate || null, iban: f.iban.trim() || undefined, nationalId: f.nationalId.trim() || undefined,
    })
    if ((me.data?.phone ?? '') !== phone) await apiFetch('/api/employee/employees/me/contact', { method: 'PUT', body: { phone } })
    return r
  }, { success: tx('Profiliniz güncellendi'), invalidate: [['profile'], ['my-employee-record']], onDone: () => setF((x) => ({ ...x, iban: '', nationalId: '' })) })
  const set = <K extends keyof typeof f>(k: K, v: (typeof f)[K]) => setF((x) => ({ ...x, [k]: v }))

  return (
    <form onSubmit={(e) => { e.preventDefault(); save.mutate(undefined) }} className="grid gap-5 lg:grid-cols-2">
      <Panel>
        <PanelHead title={tx('Kişisel bilgiler')} note={tx('Ad, e-posta ve işe giriş tarihi İK tarafından yönetilir.')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <TextField label={tx('Doğum tarihi')} type="date" value={f.birthDate} onChange={(e) => set('birthDate', e.target.value)} />
            <SelectField label={tx('Hitap')} value={f.pronouns || 'none'} onChange={(v) => set('pronouns', v === 'none' ? '' : v)} options={[{ value: 'none', label: tx('Belirtmek istemiyorum') }, { value: 'O (kadın)', label: tx('O (kadın)') }, { value: 'O (erkek)', label: tx('O (erkek)') }]} />
          </div>
          <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.showBirthday} onCheckedChange={(v) => set('showBirthday', v === true)} />{' '}{tx('Doğum günüm kutlamalar sayfasında görünsün (yıl gösterilmez)')}</label>
          <TextField label={tx('Telefon')} value={phone} onChange={(e) => setPhone(e.target.value)} placeholder={tx('+90 5xx xxx xx xx')} />
          <TextAreaField label={tx('Hakkımda')} rows={3} maxLength={500} value={f.bio} onChange={(e) => set('bio', e.target.value)} />
          <TextField label={tx('LinkedIn')} value={f.linkedInUrl} onChange={(e) => set('linkedInUrl', e.target.value)} placeholder="https://linkedin.com/in/..." />
          <TextAreaField label={tx('Adres')} rows={2} value={f.address} onChange={(e) => set('address', e.target.value)} />
          <div className="grid gap-4 sm:grid-cols-2">
            <TextField label={tx('Acil durumda aranacak kişi')} value={f.emergencyContactName} onChange={(e) => set('emergencyContactName', e.target.value)} />
            <TextField label={tx('Acil durum telefonu')} value={f.emergencyContactPhone} onChange={(e) => set('emergencyContactPhone', e.target.value)} />
          </div>
        </PanelBody>
      </Panel>
      <div className="space-y-5">
        <Panel>
          <PanelHead title={tx('Beceriler ve ilgi alanları')} note={tx('Yetenek dizininde ve mentor eşleştirmede kullanılır.')} />
          <PanelBody className="space-y-4">
            <ChipInput id="skills" label={tx('Beceriler')} value={f.skills} onChange={(v) => set('skills', v)} suggestions={SKILL_HINTS} />
            <SkillSuggest text={f.bio} existing={f.skills} onAdd={(s) => set('skills', [...f.skills, s].slice(0, 50))} />
            <ChipInput id="interests" label={tx('İlgi alanları')} value={f.interests} onChange={(v) => set('interests', v)} suggestions={[tx('Fotoğraf'), tx('Koşu'), tx('Satranç'), tx('Müzik'), tx('Gönüllülük')]} />
          </PanelBody>
        </Panel>
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><ShieldCheck className="size-4 text-primary" />{' '}{tx('Hassas bilgiler')}</span>} note={tx('Maskeli gösterilir; yalnızca siz ve İK açık hâlini görebilir.')} />
          <PanelBody className="space-y-4">
            <SensitiveField label={tx('IBAN (maaş hesabı)')} field="iban" employeeId={p.employeeId} masked={p.iban} has={p.hasIban} value={f.iban} onChange={(v) => set('iban', v)} />
            <SensitiveField label={tx('T.C. kimlik no')} field="nationalId" employeeId={p.employeeId} masked={p.nationalId} has={p.hasNationalId} value={f.nationalId} onChange={(v) => set('nationalId', v)} />
          </PanelBody>
        </Panel>
        <div className="flex justify-end">
          <Button type="submit" disabled={save.isPending}><Save className="size-4" />{' '}{tx('Kaydet')}</Button>
        </div>
      </div>
    </form>
  )
}

function PrivacyTab({ employeeId }: { employeeId?: string }) {
  const consents = useQuery({ queryKey: ['privacy', 'consents', 'me'], queryFn: ({ signal }) => governanceApi.myConsents(signal) })
  const requests = useQuery({ queryKey: ['privacy', 'requests'], queryFn: ({ signal }) => governanceApi.dataRequests(signal) })
  const record = useAction(({ type, granted }: { type: string; granted: boolean }) => governanceApi.recordConsent(type, granted), { success: tx('Tercihiniz kaydedildi'), invalidate: [['privacy']] })
  const [kind, setKind] = useState<DataRequestKind>('Access')
  const [details, setDetails] = useState('')
  const create = useAction(() => governanceApi.createDataRequest(kind, details.trim()), { success: tx('Başvurunuz alındı; en geç 30 gün içinde yanıtlanır.'), invalidate: [['privacy']], onDone: () => setDetails('') })
  const withdraw = useAction((id: string) => governanceApi.withdrawDataRequest(id), { success: tx('Başvurunuz geri çekildi'), invalidate: [['privacy']] })
  const confirm = useConfirm()
  const askWithdraw = async (id: string, label: string) => {
    if (await confirm({ title: tx('Başvuru geri çekilsin mi?'), note: tx('{0} başvurunuz geri çekilir ve İK tarafından işlenmez. Gerekirse yeniden başvurabilirsiniz.', [label]), action: tx('Geri çek') })) withdraw.mutate(id)
  }
  // Sunucuyla aynı kural: İK'nın başvuruyu karşılayabilmesi için en az 10 karakter açıklama.
  const detailsShort = details.trim().length < 10
  const toast = useToast()
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      <Panel>
        <PanelHead title={tx('Aydınlatma ve açık rıza')} note={tx('Rızalarınızı istediğiniz zaman geri alabilirsiniz (KVKK m.5, m.11).')} />
        <PanelBody className="space-y-3">
          {consents.isPending ? <RowsSkeleton rows={3} /> : consents.isError ? <p className="text-[13px] text-muted-foreground">{errMsg(consents.error)}</p> : !consents.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Bu hesap için bilgilendirme metni yok.')}</p> : consents.data.map((c) => (
            <div key={c.type} className="rounded-xl border border-border p-3.5">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="text-[13.5px] font-medium">{c.title} {c.required && <span className="text-[11px] text-muted-foreground">{tx('(bilgilendirme)')}</span>}</p>
                  <p className="mt-1 text-[12.5px] leading-relaxed text-muted-foreground">{c.text}</p>
                </div>
                {c.granted == null ? <StatusBadge tone="warning">{tx('Bekliyor')}</StatusBadge> : c.granted ? <StatusBadge tone="success">{c.required ? tx('Okundu') : tx('Onaylı')}</StatusBadge> : <StatusBadge>{tx('Reddedildi')}</StatusBadge>}
              </div>
              <div className="mt-3 flex flex-wrap items-center gap-2">
                {c.required ? (
                  <Button size="sm" variant={c.granted ? 'outline' : 'default'} onClick={() => record.mutate({ type: c.type, granted: true })}>{tx('Okudum, anladım')}</Button>
                ) : (
                  <>
                    <Button size="sm" variant={c.granted ? 'default' : 'outline'} onClick={() => record.mutate({ type: c.type, granted: true })}>{tx('Onaylıyorum')}</Button>
                    <Button size="sm" variant={c.granted === false ? 'default' : 'outline'} onClick={() => record.mutate({ type: c.type, granted: false })}>{tx('Onaylamıyorum')}</Button>
                  </>
                )}
                {c.recordedAt && <span className="text-[11.5px] text-muted-foreground">{tx('Son kayıt {0} · sürüm {1}', [formatDate(c.recordedAt), c.version])}</span>}
              </div>
            </div>
          ))}
        </PanelBody>
      </Panel>
      <div className="space-y-5">
        <Panel>
          <PanelHead title={tx('Verilerim')} note={tx('HR360\'ta sizinle ilgili tutulan tüm kişisel verilerin dökümü.')} />
          <PanelBody>
            {employeeId
              ? <Button onClick={() => governanceApi.exportPersonalData(employeeId).catch((e) => toast.stop(errMsg(e)))}><Download className="size-4" />{' '}{tx('Verilerimi indir (JSON)')}</Button>
              : <p className="text-[12.5px] text-muted-foreground">{tx('Veri dışa aktarma çalışan kaydı ister; hesabınız bir çalışan kaydına bağlanınca açılır.')}</p>}
          </PanelBody>
        </Panel>
        <Panel>
          <PanelHead title={tx('İlgili kişi başvurusu')} note={tx('KVKK m.11 haklarınız için başvuru yapın.')} />
          <PanelBody className="space-y-3">
            <SelectField label={tx('Başvuru türü')} value={kind} onChange={(v) => setKind(v as DataRequestKind)} options={(Object.keys(dataRequestLabels) as DataRequestKind[]).map((k) => ({ value: k, label: dataRequestLabels[k] }))} />
            <TextAreaField label={tx('Açıklama')} rows={3} value={details} maxLength={4000}
              hint={tx('Talebinizi en az 10 karakterle açıklayın (ör. hangi verinin düzeltilmesini istediğiniz).')}
              error={details.length > 0 && detailsShort ? tx('Başvurunuzu en az 10 karakterle açıklayın.') : undefined}
              onChange={(e) => setDetails(e.target.value)} />
            <PiiHint text={details} />
            <Button onClick={() => create.mutate(undefined)} disabled={create.isPending || detailsShort}>{tx('Başvur')}</Button>
            {(requests.data ?? []).length > 0 && (
              <ul className="mt-2 divide-y divide-border rounded-xl border border-border">
                {requests.data!.map((r) => (
                  <li key={r.id} className="px-3.5 py-2.5 text-[13px]">
                    <div className="flex items-center justify-between gap-2">
                      <span>{dataRequestLabels[r.kind]} <span className="text-[11.5px] text-muted-foreground">· {formatDate(r.createdAt)}</span></span>
                      <span className="flex items-center gap-2">
                        {(r.status === 'Received' || r.status === 'InProgress') && (
                          <Button size="sm" variant="ghost" className="h-7 px-2 text-[12px]" disabled={withdraw.isPending} onClick={() => void askWithdraw(r.id, dataRequestLabels[r.kind])}>{tx('Geri çek')}</Button>
                        )}
                        <StatusBadge tone={r.status === 'Completed' ? 'success' : r.status === 'Rejected' ? 'danger' : r.status === 'Withdrawn' ? 'neutral' : r.overdue ? 'danger' : 'warning'}>
                          {r.status === 'Completed' ? tx('Yanıtlandı') : r.status === 'Rejected' ? tx('Reddedildi') : r.status === 'Withdrawn' ? tx('Geri çekildi') : tx('{0} gün içinde', [r.daysLeft])}
                        </StatusBadge>
                      </span>
                    </div>
                    {r.details && <p className="mt-1 text-[12.5px] whitespace-pre-line text-muted-foreground">{tx('Açıklamanız: {0}', [r.details])}</p>}
                    {r.response && <p className="mt-1 text-[12.5px] text-muted-foreground">{tx('Yanıt: {0}', [r.response])}</p>}
                    {r.package?.downloadable && (
                      <Button size="sm" variant="outline" className="mt-2" onClick={() => kvkk10Api.downloadPackage(r.id).catch((e) => toast.stop(errMsg(e)))}>
                        <Download className="size-4" />{' '}{tx('Veri paketimi indir (ZIP)')}
                      </Button>
                    )}
                    {r.kind === 'Access' && r.package && !r.package.downloadable && (r.status === 'Received' || r.status === 'InProgress') && (
                      <p className="mt-1 text-[12px] text-muted-foreground">{tx('Veri paketiniz hazır; başvurunuz sonuçlandığında buradan indirebilirsiniz.')}</p>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </PanelBody>
        </Panel>
        <MyObjections />
        <MyAccessLog />
      </div>
    </div>
  )
}

function CalendarTab() {
  const toast = useToast()
  const feed = useQuery({ queryKey: ['calendar-feed'], queryFn: ({ signal }) => governanceApi.calendarFeed(signal) })
  const rotate = useAction(() => governanceApi.rotateCalendarFeed(), { success: tx('Yeni bağlantı oluşturuldu; eski bağlantı artık çalışmaz.'), invalidate: [['calendar-feed']] })
  const url = feed.data ? `${window.location.origin}${feed.data.path}` : ''
  return (
    <div className="max-w-3xl space-y-5">
      <CalendarConnections />
      <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><CalendarPlus className="size-4 text-primary" />{' '}{tx('Takvim aboneliği (.ics)')}</span>} note={tx('İzinleriniz, ekip izinleri, resmî tatiller, 1:1\'ler ve masa rezervasyonlarınız takviminizde.')} />
      <PanelBody className="space-y-4">
        {feed.isPending ? <RowsSkeleton rows={1} /> : feed.isError ? <ErrorState message={(feed.error as Error).message} /> : (
          <>
            <div className="flex gap-2">
              <input readOnly value={url} className="h-10 flex-1 rounded-xl border border-input bg-muted/40 px-3 font-mono text-[12px]" onFocus={(e) => e.currentTarget.select()} />
              <Button variant="outline" onClick={() => navigator.clipboard.writeText(url).then(() => toast.ok(tx('Kopyalandı')))}><Copy className="size-4" />{' '}{tx('Kopyala')}</Button>
              <Button variant="outline" onClick={() => rotate.mutate(undefined)} title={tx('Bağlantı sızdıysa yenileyin')}><RefreshCw className="size-4" /></Button>
            </div>
            <ul className="space-y-1.5 text-[13px] text-muted-foreground">
              <li><b className="text-foreground">{tx('Google Takvim:')}</b>{' '}{tx('Diğer takvimler → URL ile ekle → bağlantıyı yapıştırın.')}</li>
              <li><b className="text-foreground">{tx('Outlook:')}</b>{' '}{tx('Takvim ekle → İnternetten abone ol.')}</li>
              <li><b className="text-foreground">{tx('Apple Takvim:')}</b>{' '}{tx('Dosya → Yeni takvim aboneliği.')}</li>
            </ul>
            <InfoNote>{tx('Bağlantı size özeldir ve oturum gerektirmez; kimseyle paylaşmayın. Sızdığını düşünüyorsanız yenileyin.')}</InfoNote>
          </>
        )}
      </PanelBody>
      </Panel>
    </div>
  )
}

function SecurityTab() {
  const { accountUrl } = useAuth()
  return (
    <Panel className="max-w-3xl">
      <PanelHead title={<span className="flex items-center gap-2"><Fingerprint className="size-4 text-primary" />{' '}{tx('İki adımlı doğrulama')}</span>} note={tx('Parolanıza ek olarak telefonunuzdaki doğrulayıcı uygulamanın kodu istenir.')} />
      <PanelBody className="space-y-4">
        <ol className="list-decimal space-y-1.5 pl-5 text-[13px] text-muted-foreground">
          <li>{tx('Telefonunuza Google Authenticator, Microsoft Authenticator veya FreeOTP kurun.')}</li>
          <li>{tx('Aşağıdaki düğmeyle hesap güvenliği sayfasına gidin ve “Doğrulayıcı uygulama” ekleyin.')}</li>
          <li>{tx('Ekrandaki QR kodu uygulamayla okutun ve üretilen 6 haneli kodu girin.')}</li>
        </ol>
        <div className="flex flex-wrap gap-2">
          <Button asChild><a href={`${accountUrl}#/account-security/signing-in`} target="_blank" rel="noreferrer"><KeyRound className="size-4" />{' '}{tx('Hesap güvenliğini aç')}</a></Button>
          <Button asChild variant="outline"><a href={`${accountUrl}#/account-security/device-activity`} target="_blank" rel="noreferrer">{tx('Açık oturumlarım')}</a></Button>
        </div>
      </PanelBody>
    </Panel>
  )
}

/**
 * Çalışan kaydına bağlı olmayan hesaplar (ör. şirketi kaydeden kurucu, yalnızca yönetici hesabı): kişisel
 * bilgiler ve takvim çalışan kaydı ister, ama aydınlatma metni, bildirim, güvenlik ve erişilebilirlik
 * ayarları hesaba bağlıdır. Giriş bandındaki "Oku ve yanıtla" buraya geldiğinde metin okunabilmeli.
 */
function AccountOnlyProfile({ tab, setTab }: { tab: TabKey; setTab: (t: TabKey) => void }) {
  const current: TabKey = tab === 'bilgiler' || tab === 'takvim' ? 'gizlilik' : tab
  return (
    <>
      <div className="mb-5"><InfoNote>{tx('Hesabınız bir çalışan kaydına bağlı değil (ör. şirketi kaydeden yönetici hesabı). Kişisel bilgiler ve takvim, İK sizi çalışan olarak eklediğinde açılır; aşağıdaki ayarlar hesabınıza aittir.')}</InfoNote></div>
      <div className="mb-5">
        <Tabs label={tx('Profil bölümleri')} value={current} onChange={setTab} tabs={[{ key: 'gizlilik', label: tx('Gizlilik (KVKK)') }, { key: 'bildirimler', label: tx('Bildirimler') }, { key: 'guvenlik', label: tx('Güvenlik') }, { key: 'erisilebilirlik', label: tx('Erişilebilirlik') }]} />
      </div>
      {current === 'gizlilik' && <PrivacyTab />}
      {current === 'bildirimler' && <NotificationPrefsPanel />}
      {current === 'erisilebilirlik' && <AccessibilityPanel />}
      {current === 'guvenlik' && <div className="space-y-5"><SecurityTab /><MySecurityPanel /><DevicePanel /><MyChatAccounts /></div>}
    </>
  )
}

export function ProfilePage() {
  const [tab, setTab] = useTabParam<TabKey>('sekme', 'bilgiler')
  const me = useMyEmployeeId()
  const employeeId = me.employeeId
  const q = useQuery({ queryKey: ['profile', 'me'], queryFn: ({ signal }) => engagementApi.myProfile(signal), retry: false, enabled: !!employeeId })
  const p = q.data
  return (
    <>
      <PageHeader title={tx('Profilim')} description={tx('Kişisel bilgilerinizi, gizlilik tercihlerinizi ve takvim aboneliğinizi tek yerden yönetin.')} eyebrow={[tx('Hesabım')]} />
      {me.isPending || (employeeId && q.isPending) ? <RowsSkeleton /> : !employeeId || q.isError || !p ? (
        // Çalışan kaydı yok (404) ya da kimlik çözülemedi (ör. platform yöneticisi): hesap ayarları yine açık.
        <AccountOnlyProfile tab={tab} setTab={setTab} />
      ) : (
        <>
          <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} className="surface mb-6 flex flex-wrap items-center gap-4 rounded-3xl border border-border p-5">
            <PhotoUploader employeeId={p.employeeId} name={p.name} size={64} />
            <div className="min-w-0 flex-1">
              <h2 className="text-[20px] font-semibold tracking-tight">{p.name}</h2>
              <p className="text-[13.5px] text-muted-foreground">{p.position ?? tx('Pozisyon atanmamış')} · {p.department ?? tx('Departman yok')}</p>
              <p className="mt-0.5 text-[12.5px] text-muted-foreground">{tx('{0} · işe giriş {1}', [p.email, formatDate(p.hireDate)])}</p>
            </div>
            <div className="flex flex-wrap gap-1.5">{p.skills.slice(0, 6).map((s) => <span key={s} className="rounded-full bg-primary/10 px-2.5 py-0.5 text-[12px] text-primary">{s}</span>)}</div>
          </motion.div>
          <div className="mb-5">
            <Tabs label={tx('Profil bölümleri')} value={tab} onChange={setTab} tabs={[{ key: 'bilgiler', label: tx('Bilgilerim') }, { key: 'bildirimler', label: tx('Bildirimler') }, { key: 'gizlilik', label: tx('Gizlilik (KVKK)') }, { key: 'takvim', label: tx('Takvim') }, { key: 'guvenlik', label: tx('Güvenlik') }, { key: 'erisilebilirlik', label: tx('Erişilebilirlik') }]} />
          </div>
          {tab === 'bilgiler' && <InfoTab key={p.updatedAt ?? 'new'} p={p} />}
          {tab === 'bilgiler' && <div className="mt-5"><ExtraInfoPanel /></div>}
          {tab === 'bildirimler' && <NotificationPrefsPanel />}
          {tab === 'erisilebilirlik' && <AccessibilityPanel />}
          {tab === 'gizlilik' && <PrivacyTab employeeId={p.employeeId} />}
          {tab === 'takvim' && <CalendarTab />}
          {tab === 'guvenlik' && <div className="space-y-5"><SecurityTab /><MySecurityPanel /><DevicePanel /><MyChatAccounts /></div>}
        </>
      )}
    </>
  )
}
