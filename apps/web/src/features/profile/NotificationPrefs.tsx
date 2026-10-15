import { useEffect, useId, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { BellRing, Keyboard, Lock, MoonStar, Newspaper, Save } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { ShortcutsHelp } from '@/components/layout/KeyboardShortcuts'
import { notificationPrefsApi, type NotificationCategoryPref, type NotificationChannelPrefs } from '@/api/notification'
import { errMsg, useAction } from '@/features/shared/kit'
import { useShortcutsEnabled } from '@/lib/shortcuts'
import { tx } from '@/lib/i18n'

type Channel = 'inApp' | 'email' | 'push' | 'chat'

const CHANNELS: Array<{ key: Channel; label: string }> = [
  { key: 'inApp', label: tx('Uygulama içi') },
  { key: 'email', label: tx('E-posta') },
  { key: 'push', label: tx('Anlık bildirim') },
  { key: 'chat', label: tx('Sohbet (Slack/Teams)') },
]

/** Pazartesiden başlayan gün sırası; değerler .NET DayOfWeek (0 = Pazar). */
const DAYS: Array<{ value: number; label: string; short: string }> = [
  { value: 1, label: tx('Pazartesi'), short: tx('Pzt') },
  { value: 2, label: tx('Salı'), short: tx('Sal') },
  { value: 3, label: tx('Çarşamba'), short: tx('Çar') },
  { value: 4, label: tx('Perşembe'), short: tx('Per') },
  { value: 5, label: tx('Cuma'), short: tx('Cum') },
  { value: 6, label: tx('Cumartesi'), short: tx('Cmt') },
  { value: 0, label: tx('Pazar'), short: tx('Paz') },
]

const HOURS = Array.from({ length: 24 }, (_, h) => ({ value: String(h), label: `${String(h).padStart(2, '0')}:00` }))

/**
 * Profilim › Bildirimler (G11): kategori x kanal tercihleri, sessiz saatler ve günlük özet.
 * Zorunlu (yasal) kategorilerde uygulama içi bildirim kapatılamaz.
 */
export function NotificationPrefsPanel() {
  const q = useQuery({ queryKey: ['notification-prefs'], queryFn: ({ signal }) => notificationPrefsApi.get(signal) })
  if (q.isPending) return <RowsSkeleton rows={6} columns={5} />
  if (q.isError || !q.data) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  return <PrefsForm key={JSON.stringify(q.data)} initial={q.data} />
}

function PrefsForm({ initial }: { initial: NotificationChannelPrefs }) {
  const [cats, setCats] = useState<NotificationCategoryPref[]>(initial.categories)
  const [quiet, setQuiet] = useState(initial.quietHours)
  const [digest, setDigest] = useState(initial.digest)
  const [error, setError] = useState<string | null>(null)
  const quietId = useId()
  const digestId = useId()

  const save = useAction(
    () => notificationPrefsApi.save({
      categories: cats.map(({ key, inApp, email, push, chat }) => ({ key, inApp, email, push, chat })),
      quietHours: quiet,
      digest,
    }),
    { success: tx('Bildirim tercihleriniz kaydedildi'), invalidate: [['notification-prefs'], ['notifications'], ['unread-count']] },
  )

  const toggle = (key: string, ch: Channel, on: boolean) =>
    setCats((xs) => xs.map((c) => (c.key === key ? { ...c, [ch]: on } : c)))

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (quiet.enabled && quiet.start === quiet.end) return setError(tx('Sessiz saatlerin başlangıcı ve bitişi aynı olamaz.'))
    if (quiet.enabled && quiet.days.length === 0) return setError(tx('Sessiz saatler için en az bir gün seçin.'))
    setError(null)
    save.mutate(undefined)
  }

  return (
    <form onSubmit={submit} className="space-y-5">
      {!initial.linked && <InfoNote>{tx('Hesabınız bir çalışan kaydına bağlı değil; tercihler kaydedilemez.')}</InfoNote>}
      <Panel>
        <PanelHead
          title={<span className="flex items-center gap-2"><BellRing aria-hidden="true" className="size-4 text-primary" />{tx('Bildirim tercihleri')}</span>}
          note={tx('Hangi konuda hangi kanaldan haber almak istediğinizi seçin. Bordro, KVKK ve disiplin bildirimleri yasal zorunluluk olduğundan uygulama içinde her zaman gösterilir.')}
        />
        <PanelBody className="overflow-x-auto p-0">
          <table className="w-full min-w-[560px] text-[13px]">
            <caption className="sr-only">{tx('Bildirim kategorileri ve kanalları')}</caption>
            <thead>
              <tr className="border-b border-border text-left text-[12px] text-muted-foreground">
                <th scope="col" className="px-5 py-2.5 font-medium">{tx('Kategori')}</th>
                {CHANNELS.map((c) => <th key={c.key} scope="col" className="px-3 py-2.5 text-center font-medium">{c.label}</th>)}
              </tr>
            </thead>
            <tbody>
              {cats.map((c) => (
                <tr key={c.key} className="border-b border-border/60 last:border-0">
                  <th scope="row" className="px-5 py-3 text-left font-medium">
                    <span className="flex flex-wrap items-center gap-2">
                      {c.label}
                      {c.critical
                        ? <StatusBadge tone="warning"><Lock aria-hidden="true" className="size-3" />{tx('Kapatılamaz')}</StatusBadge>
                        : c.mandatory && <StatusBadge tone="info"><Lock aria-hidden="true" className="size-3" />{tx('Zorunlu')}</StatusBadge>}
                    </span>
                  </th>
                  {CHANNELS.map((ch) => {
                    const locked = c.critical || (ch.key === 'inApp' && c.mandatory)
                    return (
                      <td key={ch.key} className="px-3 py-3 text-center">
                        <Checkbox
                          checked={locked ? true : c[ch.key]}
                          disabled={locked || !initial.linked}
                          onCheckedChange={(v) => toggle(c.key, ch.key, v === true)}
                          aria-label={locked ? tx('{0} – {1} (zorunlu, kapatılamaz)', [c.label, ch.label]) : tx('{0} – {1}', [c.label, ch.label])}
                        />
                      </td>
                    )
                  })}
                </tr>
              ))}
            </tbody>
          </table>
          <div className="border-t border-border px-5 py-3">
            <InfoNote>
              {tx('Güvenlik açısından kritik bildirimler (kişisel veri ihlali bildirimi, hesap güvenliği uyarıları, parola/hesap işlemleri ve tek kullanımlık doğrulama kodları) tercihlerinizden bağımsız olarak tüm kanallardan hemen iletilir; sessiz saatler ve günlük özet bunlara uygulanmaz.')}
            </InfoNote>
          </div>
        </PanelBody>
      </Panel>

      <div className="grid gap-5 lg:grid-cols-2">
        <Panel>
          <PanelHead
            title={<span className="flex items-center gap-2"><MoonStar aria-hidden="true" className="size-4 text-primary" />{tx('Sessiz saatler')}</span>}
            note={tx('Bu saatlerde e-posta ve anlık bildirimler bekletilir, sessiz saat bitince iletilir (silinmez). Uygulama içi bildirimler etkilenmez. Saatler {0} saatine göredir.', [tx('İstanbul')])}
          />
          <PanelBody className="space-y-4">
            <label className="flex items-center gap-2 text-[13px]">
              <Checkbox checked={quiet.enabled} disabled={!initial.linked} onCheckedChange={(v) => setQuiet((x) => ({ ...x, enabled: v === true }))} />
              {tx('Sessiz saatleri kullan')}
            </label>
            <div className="grid gap-4 sm:grid-cols-2">
              <TextField label={tx('Başlangıç')} type="time" value={quiet.start} disabled={!quiet.enabled} onChange={(e) => setQuiet((x) => ({ ...x, start: e.target.value }))} hint={tx('Örn. 22:00')} />
              <TextField label={tx('Bitiş')} type="time" value={quiet.end} disabled={!quiet.enabled} onChange={(e) => setQuiet((x) => ({ ...x, end: e.target.value }))} hint={tx('Gece yarısını geçebilir (ör. 08:00)')} />
            </div>
            <fieldset disabled={!quiet.enabled} className="space-y-2 disabled:opacity-60">
              <legend className="mb-1.5 text-[13px] font-medium">{tx('Geçerli günler')}</legend>
              <div className="flex flex-wrap gap-2">
                {DAYS.map((d) => {
                  const on = quiet.days.includes(d.value)
                  return (
                    <label key={d.value} className="flex items-center gap-1.5 rounded-full border border-border px-2.5 py-1 text-[12.5px]">
                      <Checkbox
                        checked={on}
                        disabled={!quiet.enabled}
                        aria-label={d.label}
                        onCheckedChange={(v) => setQuiet((x) => ({ ...x, days: v === true ? [...x.days, d.value] : x.days.filter((y) => y !== d.value) }))}
                      />
                      <span aria-hidden="true">{d.short}</span>
                    </label>
                  )
                })}
              </div>
              <p className="text-[12px] text-muted-foreground">{tx('Gece yarısını geçen pencere, başladığı günün seçimine göre uygulanır.')}</p>
            </fieldset>
            {error && <p id={`${quietId}-err`} role="alert" className="text-[12.5px] text-destructive">{error}</p>}
          </PanelBody>
        </Panel>

        <Panel>
          <PanelHead
            title={<span className="flex items-center gap-2"><Newspaper aria-hidden="true" className="size-4 text-primary" />{tx('Günlük özet')}</span>}
            note={tx('Acil olmayan kategorilerin e-postaları tek tek gelmez; günde bir kez, seçtiğiniz saatte tek e-postada toplanır. Onay bekleyen işler, bordro ve yasal bildirimler hemen gönderilir.')}
          />
          <PanelBody className="space-y-4">
            <label className="flex items-center gap-2 text-[13px]" htmlFor={digestId}>
              <Checkbox id={digestId} checked={digest.enabled} disabled={!initial.linked} onCheckedChange={(v) => setDigest((x) => ({ ...x, enabled: v === true }))} />
              {tx('E-postaları günlük özette topla')}
            </label>
            <SelectField
              label={tx('Özet saati')}
              value={String(digest.hour)}
              disabled={!digest.enabled}
              onChange={(v) => setDigest((x) => ({ ...x, hour: Number(v) }))}
              options={HOURS}
            />
            <p className="text-[12.5px] text-muted-foreground">
              {tx('Özete giren kategoriler:')}{' '}{cats.filter((c) => c.digestible).map((c) => c.label).join(', ')}
            </p>
            <InfoNote>{tx('KVKK: Özet e-postasında yalnızca bildirim konuları yer alır; ayrıntılar oturum açınca uygulamada görülür.')}</InfoNote>
          </PanelBody>
        </Panel>
      </div>

      <div className="flex justify-end">
        <Button type="submit" disabled={save.isPending || !initial.linked}><Save aria-hidden="true" className="size-4" />{tx('Tercihleri kaydet')}</Button>
      </div>
    </form>
  )
}

/** Profilim › Erişilebilirlik: klavye kısayolları açık/kapalı (cihaza özgü). */
export function AccessibilityPanel() {
  const [enabled, setEnabled] = useShortcutsEnabled()
  const [help, setHelp] = useState(false)
  const [reduced, setReduced] = useState(false)
  useEffect(() => {
    const mq = window.matchMedia?.('(prefers-reduced-motion: reduce)')
    if (!mq) return
    setReduced(mq.matches)
    const on = () => setReduced(mq.matches)
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [])
  return (
    <Panel className="max-w-3xl">
      <PanelHead
        title={<span className="flex items-center gap-2"><Keyboard aria-hidden="true" className="size-4 text-primary" />{tx('Klavye kısayolları')}</span>}
        note={tx('Tek tuşlu kısayollar (? yardım, / arama, g ardından harf ile sayfaya git). Ekran okuyucu kullanıyorsanız kapatabilirsiniz; bu ayar bu cihazda saklanır.')}
      />
      <PanelBody className="space-y-4">
        <label className="flex items-center gap-2 text-[13px]">
          <Checkbox checked={enabled} onCheckedChange={(v) => setEnabled(v === true)} />
          {tx('Klavye kısayollarını kullan')}
        </label>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => setHelp(true)}>{tx('Kısayol listesini göster')}</Button>
        </div>
        <InfoNote>
          {reduced
            ? tx('İşletim sisteminizde “hareketi azalt” açık: arayüz animasyonları kapatıldı.')
            : tx('İşletim sisteminizde “hareketi azalt” seçeneğini açarsanız arayüz animasyonları kapanır.')}
        </InfoNote>
      </PanelBody>
      <ShortcutsHelp open={help} onClose={() => setHelp(false)} />
    </Panel>
  )
}
