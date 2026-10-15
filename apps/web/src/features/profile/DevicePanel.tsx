import { useEffect, useState } from 'react'
import { BellRing, CloudOff, Smartphone } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { useToast } from '@/components/ui/Toast'
import { currentPushSubscription, disablePush, enablePush, flushOfflineQueue, loadOfflineQueue, offlineQueue, pushSupported, queuedKind, queuedSummary, removeQueued, sendTestPush } from '@/lib/push'
import { errMsg } from '@/features/shared/kit'
import { formatDateTime } from '@/lib/format'
import { tx } from '@/lib/i18n'

const kindLabel = (path: string) => {
  const k = queuedKind(path)
  return k === 'leave' ? tx('İzin talebi') : k === 'expense' ? tx('Masraf talebi') : tx('Fazla mesai talebi')
}

/** Profilim › Güvenlik: bu cihazda anlık bildirim ve çevrimdışı kuyruk. */
export function DevicePanel() {
  const toast = useToast()
  const [on, setOn] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [queue, setQueue] = useState(offlineQueue())
  useEffect(() => {
    void currentPushSubscription().then((s) => setOn(!!s)).catch(() => setOn(false))
    const l = () => setQueue(offlineQueue())
    window.addEventListener('hr360:offline-queue', l)
    return () => window.removeEventListener('hr360:offline-queue', l)
  }, [])
  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true)
    try { await fn(); toast.ok(ok); setOn(!!(await currentPushSubscription())) } catch (e) { toast.stop(errMsg(e)) } finally { setBusy(false) }
  }
  return (
    <Panel className="max-w-3xl">
      <PanelHead title={<span className="flex items-center gap-2"><Smartphone className="size-4 text-primary" />{' '}{tx('Bu cihaz')}</span>}
        note={tx('Anlık bildirimde kişisel bilgi gösterilmez ("Yeni bir bildiriminiz var"); ayrıntı uygulamada görünür. Oturumu kapatınca bu cihazın aboneliği ve sıradaki talepler silinir.')} />
      <PanelBody className="space-y-4">
        <div className="flex flex-wrap items-center gap-3 text-[13px]">
          <BellRing className="size-4 text-muted-foreground" />
          <span className="flex-1">{tx('Anlık bildirimler')}</span>
          {!pushSupported() ? <StatusBadge>{tx('Tarayıcı desteklemiyor')}</StatusBadge>
            : on ? (
              <>
                <StatusBadge tone="success">{tx('Açık')}</StatusBadge>
                <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(sendTestPush, tx('Deneme bildirimi gönderildi'))}>{tx('Deneme gönder')}</Button>
                <Button size="sm" variant="ghost" disabled={busy} onClick={() => void run(disablePush, tx('Anlık bildirimler kapatıldı'))}>{tx('Kapat')}</Button>
              </>
            ) : <Button size="sm" disabled={busy || on === null} onClick={() => void run(enablePush, tx('Anlık bildirimler açıldı'))}>{tx('Aç')}</Button>}
        </div>
        <div className="flex flex-wrap items-center gap-3 text-[13px]">
          <CloudOff className="size-4 text-muted-foreground" />
          <span className="flex-1">{tx('Çevrimdışı sırada bekleyen talepler')}</span>
          <StatusBadge tone={queue.length ? 'warning' : 'neutral'}>{queue.length}</StatusBadge>
          {queue.length > 0 && <Button size="sm" variant="outline" onClick={() => void flushOfflineQueue().then((r) => toast.ok(tx('{0} talep gönderildi', [r.sent])))}>{tx('Şimdi gönder')}</Button>}
        </div>
        {queue.length > 0 && (
          <ul className="space-y-1 text-[12.5px] text-muted-foreground">
            {queue.map((q) => (
              <li key={q.id} className="flex items-center gap-2">
                <span className="flex-1 truncate">{kindLabel(q.path)} · {queuedSummary(q)} · {formatDateTime(q.at)}</span>
                <Button size="sm" variant="ghost" onClick={() => void removeQueued(q.id)}>{tx('Sil')}</Button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

/** Uygulama kabuğunda: bağlantı gelince sıradaki talepleri gönderir ve sonucu bildirir. */
export function OfflineQueueWatcher() {
  const toast = useToast()
  useEffect(() => {
    const flush = () => {
      if (!offlineQueue().length) return
      void flushOfflineQueue().then(({ sent, failed }) => {
        if (sent) toast.ok(tx('Bağlantı geldi: sıradaki {0} talep gönderildi', [sent]))
        for (const f of failed) toast.stop(tx('Sıradaki talep reddedildi: {0}', [f]))
      })
    }
    // Kalıcı kuyruk (IndexedDB) yüklenince bağlantı varsa hemen gönderilir.
    void loadOfflineQueue().then(flush)
    window.addEventListener('online', flush)
    return () => window.removeEventListener('online', flush)
  }, [toast])
  return null
}
