import { useEffect, useState } from 'react'
import { CloudOff, Send, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/ui/Toast'
import { flushOfflineQueue, loadOfflineQueue, offlineQueue, queuedKind, queuedSummary, removeQueued, type QueuedKind } from '@/lib/push'
import { formatDateTime } from '@/lib/format'
import { tx } from '@/lib/i18n'

/** Bağlantı durumu (çevrimiçi/çevrimdışı) — olaylarla güncellenir. */
export function useOnline(): boolean {
  const [online, setOnline] = useState(() => (typeof navigator === 'undefined' ? true : navigator.onLine))
  useEffect(() => {
    const on = () => setOnline(true)
    const off = () => setOnline(false)
    window.addEventListener('online', on)
    window.addEventListener('offline', off)
    return () => {
      window.removeEventListener('online', on)
      window.removeEventListener('offline', off)
    }
  }, [])
  return online
}

/**
 * Dalga 12 (madde 85): bu cihazda bekleyen çevrimdışı taslaklar (izin / masraf). Bağlantı gelince
 * uygulama kabuğu (OfflineQueueWatcher) kendiliğinden gönderir; burada görülür, elle gönderilir ya da
 * silinir. Taslak yalnızca bu cihazdadır ve oturum kapanınca silinir (KVKK: cihazda en az veri).
 */
export function OfflineDrafts({ kind }: { kind: QueuedKind }) {
  const toast = useToast()
  const online = useOnline()
  const [items, setItems] = useState(() => offlineQueue().filter((r) => queuedKind(r.path) === kind))
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    const l = () => setItems(offlineQueue().filter((r) => queuedKind(r.path) === kind))
    window.addEventListener('hr360:offline-queue', l)
    void loadOfflineQueue().then(l)
    return () => window.removeEventListener('hr360:offline-queue', l)
  }, [kind])

  if (!items.length && online) return null

  return (
    <div role="status" className="rounded-xl border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/[0.06] px-4 py-3 text-[13px]">
      <div className="flex flex-wrap items-center gap-2">
        <CloudOff className="size-4 text-[hsl(var(--warning))]" />
        <span className="flex-1 font-medium">
          {!online
            ? tx('Çevrimdışısınız. Yeni talepler bu cihazda taslak olarak saklanır ve bağlantı gelince gönderilir.')
            : tx('Bu cihazda gönderilmeyi bekleyen {0} taslak var.', [items.length])}
        </span>
        {online && items.length > 0 && (
          <Button size="sm" variant="outline" disabled={busy} onClick={async () => {
            setBusy(true)
            try {
              const r = await flushOfflineQueue()
              if (r.sent) toast.ok(tx('{0} talep gönderildi', [r.sent]))
              for (const f of r.failed) toast.stop(tx('Sıradaki talep reddedildi: {0}', [f]))
            } finally {
              setBusy(false)
            }
          }}>
            <Send className="size-3.5" />
            {tx('Şimdi gönder')}
          </Button>
        )}
      </div>
      {items.length > 0 && (
        <ul className="mt-2 space-y-1 text-[12.5px] text-muted-foreground">
          {items.map((r) => (
            <li key={r.id} className="flex items-center gap-2">
              <span className="flex-1 truncate">{queuedSummary(r)} · {tx('kaydedildi: {0}', [formatDateTime(r.at)])}</span>
              <Button size="sm" variant="ghost" aria-label={tx('Taslağı sil')} onClick={() => void removeQueued(r.id)}>
                <Trash2 className="size-3.5" />
              </Button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
