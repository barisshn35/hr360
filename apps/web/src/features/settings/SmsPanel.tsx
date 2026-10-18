import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { MessageSquareText, Send, Trash2 } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextField } from '@/components/ui/Field'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { useAction } from '@/features/shared/kit'
import { smsProviderLabels, smsSettingsApi, type SmsProvider, type SmsSettings } from '@/api/notification'
import { tx } from '@/lib/i18n'

const KEY = ['sms-settings']

/**
 * Ayarlar › SMS: şirketin yurt içi SMS sağlayıcısı (Netgsm / İleti Merkezi). Bildirim gönderirken
 * "SMS" kanalı bu hesapla gider. Gizli anahtar sunucuda şifreli saklanır ve buraya geri gelmez.
 */
export function SmsPanel() {
  const q = useQuery({ queryKey: KEY, queryFn: ({ signal }) => smsSettingsApi.get(signal) })
  return (
    <Panel>
      <PanelHead
        title={<span className="flex items-center gap-2"><MessageSquareText aria-hidden="true" className="size-4 text-primary" />{tx('SMS')}</span>}
        note={tx('Bildirimleri çalışanların telefonuna SMS olarak göndermek için şirketinizin SMS sağlayıcı hesabını bağlayın.')}
      />
      <PanelBody className="space-y-4">
        {q.isPending ? <RowsSkeleton rows={3} /> : q.data ? <SmsForm initial={q.data} /> : null}
      </PanelBody>
    </Panel>
  )
}

function SmsForm({ initial }: { initial: SmsSettings }) {
  const [enabled, setEnabled] = useState(initial.configured ? initial.enabled : true)
  const [provider, setProvider] = useState<SmsProvider>(initial.provider)
  const [username, setUsername] = useState(initial.username)
  const [secret, setSecret] = useState('')
  const [sender, setSender] = useState(initial.sender)
  const [limit, setLimit] = useState(String(initial.dailyLimit))
  const [phone, setPhone] = useState('')
  const confirm = useConfirm()
  useEffect(() => { setSecret('') }, [initial.updatedAt])

  const save = useAction(
    () => smsSettingsApi.save({ enabled, provider, username: username.trim(), secret: secret || null, sender: sender.trim(), dailyLimit: Number(limit) || 0 }),
    { success: tx('SMS ayarları kaydedildi'), invalidate: [KEY] },
  )
  const test = useAction(() => smsSettingsApi.test(phone), { success: tx('Deneme SMS\'i gönderildi') })
  const remove = useAction(() => smsSettingsApi.remove(), { success: tx('SMS ayarları silindi'), invalidate: [KEY] })

  const isNetgsm = provider === 'netgsm'
  const providerChanged = initial.configured && provider !== initial.provider
  const needSecret = !initial.hasSecret || providerChanged
  return (
    <form
      className="space-y-4"
      onSubmit={(e) => { e.preventDefault(); save.mutate(undefined) }}
    >
      <InfoNote>{tx('Yalnızca yurt içi sağlayıcılar desteklenir; telefon numaraları ve iletiler yurt dışına aktarılmaz (KVKK m.9). İletiler bilgilendirme amaçlıdır, ticari ileti (İYS) gönderilmez.')}</InfoNote>
      <div className="grid gap-3 sm:grid-cols-2">
        <SelectField
          label={tx('Sağlayıcı')}
          value={provider}
          onChange={(v) => setProvider(v as SmsProvider)}
          options={initial.providers.map((p) => ({ value: p, label: smsProviderLabels[p] ?? p }))}
        />
        <TextField
          label={tx('Gönderici adı (başlık)')} placeholder="FIRMAADI" maxLength={11}
          hint={tx('Sağlayıcıda onaylı başlık, en fazla 11 karakter')}
          value={sender} onChange={(e) => setSender(e.target.value)} required
        />
        <TextField
          label={isNetgsm ? tx('Abone numarası (kullanıcı adı)') : tx('API anahtarı')}
          value={username} onChange={(e) => setUsername(e.target.value)} required autoComplete="off"
        />
        <TextField
          label={isNetgsm ? tx('API parolası') : tx('API gizli anahtarı')} type="password" autoComplete="new-password"
          placeholder={needSecret ? '' : tx('(değiştirmek için girin)')}
          hint={providerChanged ? tx('Sağlayıcı değişti; yeni sağlayıcının anahtarını girin') : undefined}
          value={secret} onChange={(e) => setSecret(e.target.value)} required={needSecret}
        />
        <TextField
          label={tx('Günlük gönderim sınırı')} type="number" min={1} max={100000}
          hint={tx('Bugün gönderilen: {0}. Sınır dolunca kalanlar ertesi sabah gider.', [initial.sentToday])}
          value={limit} onChange={(e) => setLimit(e.target.value)}
        />
      </div>
      <label className="flex items-center gap-2 text-[13px]">
        <Checkbox checked={enabled} onCheckedChange={(v) => setEnabled(v === true)} />
        {tx('SMS gönderimi açık')}
      </label>
      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" disabled={save.isPending || !username.trim() || !sender.trim() || (needSecret && !secret)}>
          {tx('Kaydet')}
        </Button>
        {initial.configured && (
          <Button type="button" size="sm" variant="outline" disabled={remove.isPending} onClick={async () => {
            if (await confirm({ title: tx('SMS ayarları silinsin mi?'), note: tx('Kayıtlı hesap bilgileri silinir; bekleyen SMS\'ler gönderilmez.'), action: tx('Sil') }))
              remove.mutate(undefined)
          }}><Trash2 aria-hidden="true" className="size-4" />{tx('Sil')}</Button>
        )}
      </div>
      {initial.configured && (
        <div className="flex flex-wrap items-end gap-2 border-t border-border pt-4">
          <TextField
            label={tx('Deneme numarası')} type="tel" placeholder="0532 123 45 67" className="min-w-[200px] flex-1"
            value={phone} onChange={(e) => setPhone(e.target.value)}
          />
          <Button type="button" size="sm" variant="outline" disabled={test.isPending || !phone.trim()} onClick={() => test.mutate(undefined)}>
            <Send aria-hidden="true" className="size-4" />{test.isPending ? tx('Gönderiliyor…') : tx('Deneme SMS\'i gönder')}
          </Button>
        </div>
      )}
    </form>
  )
}
