import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, CalendarCheck, Copy, Pencil, Trash2, Video } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { governanceApi, type CalendarProviderInfo, type CalendarProviderName } from '@/api/governance'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const META: Record<CalendarProviderName, { title: string; what: string; steps: string[] }> = {
  Google: {
    title: tx('Google Workspace'),
    what: tx('Çalışanlar Google Takvim\'lerini bağlar; izinler takvime yazılır, toplantılara Google Meet bağlantısı eklenir.'),
    steps: [
      tx('Google Cloud Console › APIs & Services › Library: “Google Calendar API”yi etkinleştirin.'),
      tx('OAuth consent screen: kullanıcı türü “Internal” (yalnızca şirketiniz).'),
      tx('Credentials › Create credentials › OAuth client ID › Web application. “Authorized redirect URI” olarak aşağıdaki adresi ekleyin.'),
      tx('İstemci kimliği ve gizli anahtarı buraya girin.'),
    ],
  },
  Microsoft: {
    title: tx('Microsoft 365 (Outlook ve Teams)'),
    what: tx('Çalışanlar Outlook takvimlerini bağlar; izinler “dışarıda” olarak takvime yazılır, toplantılara Teams bağlantısı eklenir.'),
    steps: [
      tx('Entra ID (Azure AD) › App registrations › New registration. Desteklenen hesaplar: “Yalnızca bu kuruluş dizini”.'),
      tx('Redirect URI: “Web” türünde aşağıdaki adresi ekleyin.'),
      tx('API permissions › Microsoft Graph › Delegated: User.Read, Calendars.ReadWrite, offline_access. “Grant admin consent” deyin.'),
      tx('Certificates & secrets › New client secret. Uygulama (istemci) kimliğini, gizli anahtarı ve dizin (kiracı) kimliğini buraya girin.'),
    ],
  },
  Zoom: {
    title: tx('Zoom'),
    what: tx('1:1 ve mülakatlara tek tıkla Zoom toplantısı açılır; toplantı, düzenleyicinin (e-postasıyla eşleşen) Zoom hesabı adına oluşturulur.'),
    steps: [
      tx('marketplace.zoom.us › Develop › Build App › “Server-to-Server OAuth”.'),
      tx('Scopes: meeting:write:meeting:admin, meeting:delete:meeting:admin, user:read:user:admin. Uygulamayı etkinleştirin (Activate).'),
      tx('Account ID, Client ID ve Client Secret\'ı buraya girin. Zoom hesabı olmayan düzenleyiciler için isteğe bağlı bir varsayılan sunucu kullanıcısı (e-posta) belirleyin.'),
    ],
  },
}

function Editor({ info, onClose }: { info: CalendarProviderInfo; onClose: () => void }) {
  const [f, setF] = useState({
    clientId: info.clientId ?? '', clientSecret: '', msTenant: info.msTenant ?? '', zoomAccountId: info.zoomAccountId ?? '',
    zoomDefaultHost: info.zoomDefaultHost ?? '', isEnabled: info.configured ? info.isEnabled : true,
  })
  const toast = useToast()
  const save = useAction(() => governanceApi.saveCalendarProvider(info.provider, f), { success: tx('Kaydedildi'), invalidate: [['calendar-providers']], onDone: onClose })
  const m = META[info.provider]
  const ready = !!f.clientId && (info.hasSecret || !!f.clientSecret) && (info.provider !== 'Zoom' || !!f.zoomAccountId)
  return (
    <Modal open onClose={onClose} size="lg" title={m.title}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ready || save.isPending} onClick={() => save.mutate(undefined)}>{save.isPending ? tx('Doğrulanıyor…') : tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <ol className="space-y-1.5 rounded-xl border border-border bg-muted/30 p-4 text-[12.5px] leading-relaxed">
          {m.steps.map((s, i) => <li key={i}><b>{i + 1}.</b> {s}</li>)}
        </ol>
        {info.redirectUri && (
          <div className="flex items-center gap-2 text-[12px]">
            <span className="shrink-0 text-muted-foreground">{tx('Yönlendirme adresi')}</span>
            <span className="min-w-0 flex-1 truncate rounded-md bg-muted/60 px-2 py-1 font-mono text-[11px]">{info.redirectUri}</span>
            <button className="cursor-pointer text-muted-foreground hover:text-foreground" aria-label={tx('Kopyala')} onClick={() => navigator.clipboard.writeText(info.redirectUri!).then(() => toast.ok(tx('Kopyalandı')))}><Copy className="size-3.5" /></button>
          </div>
        )}
        <div className="grid gap-3 sm:grid-cols-2">
          {info.provider === 'Zoom' && <TextField label={tx('Account ID')} value={f.zoomAccountId} onChange={(e) => setF({ ...f, zoomAccountId: e.target.value })} />}
          <TextField label={info.provider === 'Microsoft' ? tx('Uygulama (istemci) kimliği') : tx('Client ID')} value={f.clientId} onChange={(e) => setF({ ...f, clientId: e.target.value })} />
          <TextField label={tx('Client secret')} type="password" autoComplete="off" placeholder={info.hasSecret ? tx('•••• (değiştirmek için girin)') : ''} value={f.clientSecret} onChange={(e) => setF({ ...f, clientSecret: e.target.value })} />
          {info.provider === 'Microsoft' && <TextField label={tx('Dizin (kiracı) kimliği')} placeholder="organizations" value={f.msTenant} onChange={(e) => setF({ ...f, msTenant: e.target.value })} hint={tx('Boş bırakılırsa her kurumsal hesap kabul edilir.')} />}
          {info.provider === 'Zoom' && <TextField label={tx('Varsayılan sunucu kullanıcısı (isteğe bağlı)')} placeholder="toplanti@sirket.com" value={f.zoomDefaultHost} onChange={(e) => setF({ ...f, zoomDefaultHost: e.target.value })} />}
        </div>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.isEnabled} onCheckedChange={(v) => setF({ ...f, isEnabled: v === true })} />{' '}{tx('Etkin')}</label>
        <p className="text-[12px] text-muted-foreground">{tx('Gizli anahtar şifreli saklanır ve bir daha gösterilmez.{0}', [info.provider === 'Zoom' ? tx(' Kaydederken Zoom\'dan jeton alınarak doğrulanır.') : ''])}</p>
      </div>
    </Modal>
  )
}

export function CalendarProvidersPanel() {
  const q = useQuery({ queryKey: ['calendar-providers'], queryFn: ({ signal }) => governanceApi.calendarProviders(signal) })
  const [edit, setEdit] = useState<CalendarProviderInfo | null>(null)
  const del = useAction((p: CalendarProviderName) => governanceApi.deleteCalendarProvider(p), { success: tx('Kaldırıldı'), invalidate: [['calendar-providers']] })
  return (
    <div className="space-y-5">
      <InfoNote>{tx('Çalışanlar bağladıktan sonra')}{' '}<b>{tx('onaylanan izinler')}</b>{' '}{tx('takvimlerine yazılır; 1:1 ve mülakatlarda tek tıkla')}{' '}<b>{tx('Zoom, Teams ya da Google Meet')}</b>{' '}{tx('bağlantısı ve takvim daveti oluşur; yöneticiler herkesin boş olduğu saati görür (başlıklar değil, yalnızca dolu aralıklar).')}</InfoNote>
      {q.data?.some((p) => !p.publicOriginIsHttps) && (
        <div className="flex items-start gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-3 text-[12.5px]">
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" />
          <span>{tx('Google ve Microsoft, yönlendirme adresi olarak yalnızca HTTPS kabul eder (localhost hariç). Sunucuda')}{' '}<code className="font-mono">scripts/tls.sh enable</code>{' '}{tx('ile HTTPS açın.')}</span>
        </div>
      )}
      {q.isPending ? <RowsSkeleton /> : (
        <div className="grid gap-4 lg:grid-cols-3">
          {q.data!.map((p) => {
            const m = META[p.provider]
            return (
              <div key={p.provider} className="surface flex flex-col gap-3 rounded-2xl border border-border p-4">
                <div className="flex items-start justify-between gap-2">
                  <span className="grid size-9 place-items-center rounded-xl bg-primary/10 text-primary">{p.provider === 'Zoom' ? <Video className="size-4" /> : <CalendarCheck className="size-4" />}</span>
                  <StatusBadge tone={!p.configured ? 'neutral' : p.isEnabled ? 'success' : 'warning'}>{!p.configured ? tx('Kurulmadı') : p.isEnabled ? tx('Etkin') : tx('Kapalı')}</StatusBadge>
                </div>
                <div>
                  <p className="text-[14px] font-semibold">{m.title}</p>
                  <p className="mt-1 text-[12.5px] text-muted-foreground">{m.what}</p>
                </div>
                {p.configured && <p className="text-[12px] text-muted-foreground">{p.provider === 'Zoom' ? `Hesap: ${p.zoomAccountId}` : tx('{0} çalışan bağladı', [p.connections])}</p>}
                {p.lastError && <p className="text-[12px] text-destructive">{p.lastError}</p>}
                <div className="mt-auto flex gap-1.5">
                  <Button size="sm" variant={p.configured ? 'outline' : 'default'} onClick={() => setEdit(p)}><Pencil className="size-4" /> {p.configured ? tx('Düzenle') : tx('Kur')}</Button>
                  {p.configured && <Button size="sm" variant="ghost" aria-label={tx('Kaldır')} onClick={() => del.mutate(p.provider)}><Trash2 className="size-4" /></Button>}
                </div>
              </div>
            )
          })}
        </div>
      )}
      {edit && <Editor info={edit} onClose={() => setEdit(null)} />}
    </div>
  )
}
