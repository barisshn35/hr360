/**
 * PWA yardımcıları: anlık bildirim (Web Push) aboneliği ve çevrimdışı talep kuyruğu.
 *
 * KVKK — cihazda en az veri:
 *  - Push içeriği kişisel veri taşımaz; ayrıntı uygulamada görülür.
 *  - Çevrimdışı kuyruğa yalnızca izin verilen talep türleri (izin, fazla mesai, masraf taslağı) yazılır,
 *    bağlantı gelince gönderilip silinir; oturum kapanınca kuyruk ve abonelik silinir.
 */
import { apiFetch } from '@/api/client'
import { tx } from '@/lib/i18n'
import { idbAll, idbApply } from '@/lib/offlineStore'
import { clearLocalUiPrefs } from '@/lib/uiPrefs'

const NOTIF = '/api/notification/notifications/push'

function b64ToBytes(s: string) {
  const pad = '='.repeat((4 - (s.length % 4)) % 4)
  const raw = atob((s + pad).replace(/-/g, '+').replace(/_/g, '/'))
  return Uint8Array.from(raw, (c) => c.charCodeAt(0))
}

export const pushSupported = () => typeof window !== 'undefined' && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window

async function registration() {
  const reg = await navigator.serviceWorker.getRegistration()
  if (!reg) throw new Error(tx('Uygulama servis çalışanı etkin değil (yalnızca kurulu/yayındaki sürümde çalışır).'))
  return reg
}

export async function currentPushSubscription() {
  if (!pushSupported()) return null
  const reg = await navigator.serviceWorker.getRegistration()
  return (await reg?.pushManager.getSubscription()) ?? null
}

export async function enablePush() {
  if (!pushSupported()) throw new Error(tx('Bu tarayıcı anlık bildirimi desteklemiyor.'))
  const perm = await Notification.requestPermission()
  if (perm !== 'granted') throw new Error(tx('Bildirim izni verilmedi.'))
  const reg = await registration()
  const { publicKey } = await apiFetch<{ publicKey: string }>(`${NOTIF}/public-key`)
  const sub = (await reg.pushManager.getSubscription()) ?? (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64ToBytes(publicKey) }))
  const json = sub.toJSON() as { endpoint: string; keys: { p256dh: string; auth: string } }
  await apiFetch(`${NOTIF}/subscriptions`, { method: 'POST', body: { endpoint: json.endpoint, keys: json.keys, device: navigator.userAgent.slice(0, 120) } })
}

export async function disablePush() {
  const sub = await currentPushSubscription()
  if (!sub) return
  try { await apiFetch(`${NOTIF}/subscriptions/delete`, { method: 'POST', body: { endpoint: sub.endpoint } }) } catch { /* sunucuda zaten yok */ }
  await sub.unsubscribe()
}

export const sendTestPush = () => apiFetch(`${NOTIF}/test`, { method: 'POST' })

/* ------------------------------------------------------------------ çevrimdışı kuyruk */

/*
 * Dalga 12 (madde 85): kuyruk IndexedDB'de tutulur (yoksa localStorage). Eski sürümün
 * localStorage kuyruğu ilk yüklemede IndexedDB'ye taşınır. Kuyruktaki her kayıt bir "çevrimdışı
 * taslaktır": izin talebi, fazla mesai ya da masraf (taslak) — bağlantı gelince sırayla gönderilir.
 */
const QUEUE_KEY = 'hr360-offline-queue'
/** Bu süreden eski taslaklar gönderilmez (iş kuralları, tarih ve bakiye değişmiş olabilir). */
export const OFFLINE_MAX_AGE_DAYS = 14

export type QueuedKind = 'leave' | 'overtime' | 'expense'
/** Çevrimdışıyken sıraya alınabilecek talepler (yalnızca oluşturma; dosya yükleme sıraya alınmaz). */
const QUEUEABLE: Array<[RegExp, QueuedKind]> = [
  [/^\/api\/leave\/leave-requests$/, 'leave'],
  [/^\/api\/timeshift\/overtime$/, 'overtime'],
  [/^\/api\/expense\/expense-claims$/, 'expense'],
]

export interface QueuedRequest { id: string; path: string; method: 'POST'; body: unknown; at: string }

export class QueuedOfflineError extends Error {
  constructor() {
    super(tx('Çevrimdışısınız: talebiniz bu cihazda taslak olarak saklandı, bağlantı gelince gönderilecek.'))
    this.name = 'QueuedOfflineError'
  }
}

export const isQueuedOffline = (e: unknown): e is QueuedOfflineError => e instanceof QueuedOfflineError

/** Yolun taslak türü (izin / fazla mesai / masraf); sıraya alınamıyorsa null. */
export function queuedKind(path: string): QueuedKind | null {
  return QUEUEABLE.find(([r]) => r.test(path))?.[1] ?? null
}

/** Taslağın kısa açıklaması (yalnızca bu cihazda gösterilir). */
export function queuedSummary(r: QueuedRequest): string {
  const b = (r.body ?? {}) as Record<string, unknown>
  switch (queuedKind(r.path)) {
    case 'leave': return b.startDate ? `${String(b.startDate)} – ${String(b.endDate ?? b.startDate)}` : tx('İzin talebi')
    case 'expense': return typeof b.title === 'string' && b.title ? b.title : tx('Masraf talebi')
    case 'overtime': return b.date ? String(b.date) : tx('Fazla mesai talebi')
    default: return r.path
  }
}

/** Kuyruk kararı: başarı → gönderildi; 4xx → iş kuralı reddi (kuyruktan çıkar); diğer → sonra yeniden dene. */
export function queueOutcome(status: number | null): 'sent' | 'rejected' | 'retry' {
  if (status === null) return 'sent'
  return status >= 400 && status < 500 ? 'rejected' : 'retry'
}

/** Taslak çok mu eski? */
export function isExpired(r: QueuedRequest, now = Date.now()): boolean {
  const t = Date.parse(r.at)
  return Number.isFinite(t) && now - t > OFFLINE_MAX_AGE_DAYS * 86_400_000
}

function readLegacy(): QueuedRequest[] {
  try { return JSON.parse(localStorage.getItem(QUEUE_KEY) ?? '[]') as QueuedRequest[] } catch { return [] }
}
function writeLegacy(q: QueuedRequest[]) {
  try { if (q.length) localStorage.setItem(QUEUE_KEY, JSON.stringify(q)); else localStorage.removeItem(QUEUE_KEY) } catch { /* depolama kapalı */ }
}

/** Bellekteki kopya (eşzamanlı okuma için); kalıcı kopya IndexedDB'de. */
let cache: QueuedRequest[] = readLegacy()
let loadPromise: Promise<void> | null = null

function changed() {
  if (typeof window !== 'undefined') window.dispatchEvent(new CustomEvent('hr360:offline-queue'))
}

/** IndexedDB kullanılabiliyor mu (ilk yüklemede belirlenir); değilse localStorage yedeği. */
let idbOk = true

/** Kayıt bazında kalıcı yazım (ekle/sil). IndexedDB yoksa tüm liste localStorage'a yazılır. */
async function persist(ops: { put?: QueuedRequest[]; del?: string[]; clear?: boolean }) {
  if (idbOk && (await idbApply(ops))) return
  idbOk = false
  writeLegacy(cache)
}

/** Diğer sekmelerin eklediği/sildiği taslakları görmek için kalıcı kopyayı yeniden okur. */
async function refreshFromStore() {
  if (!idbOk) { cache = readLegacy(); return }
  const stored = await idbAll<QueuedRequest>()
  if (!stored) return
  cache = [...stored].sort((a, b) => a.at.localeCompare(b.at))
}

/** Kalıcı kuyruğu yükler (uygulama açılışında bir kez); eski localStorage kuyruğunu taşır. */
export function loadOfflineQueue(): Promise<void> {
  if (!loadPromise) {
    loadPromise = (async () => {
      const stored = await idbAll<QueuedRequest>()
      if (stored) {
        const ids = new Set(stored.map((r) => r.id))
        const legacy = cache.filter((r) => !ids.has(r.id))
        cache = [...stored, ...legacy].sort((a, b) => a.at.localeCompare(b.at))
        if (legacy.length && (await idbApply({ put: legacy }))) writeLegacy([])
      } else {
        idbOk = false
      }
      changed()
    })()
  }
  return loadPromise
}

/**
 * Sekmeler arası kilit (Web Locks API varsa): iki sekme kuyruğu aynı anda boşaltıp aynı taslağı iki kez
 * göndermesin. Desteklenmiyorsa doğrudan çalışır (sekme içi kilit flushOfflineQueue'da).
 */
function withQueueLock<T>(fn: () => Promise<T>): Promise<T> {
  const locks = typeof navigator !== 'undefined' ? (navigator as Navigator & { locks?: LockManager }).locks : undefined
  if (locks?.request) return locks.request('hr360-offline-queue', fn) as Promise<T>
  return fn()
}

export const isQueueable = (path: string, method: string) => method === 'POST' && queuedKind(path) !== null
export const offlineQueue = () => cache

export function enqueueOffline(path: string, body: unknown) {
  const item: QueuedRequest = { id: crypto.randomUUID(), path, method: 'POST', body, at: new Date().toISOString() }
  cache = [...cache, item]
  void persist({ put: [item] })
  changed()
}

/** Taslağı göndermeden siler (kullanıcı vazgeçti). */
export async function removeQueued(id: string) {
  cache = cache.filter((r) => r.id !== id)
  await persist({ del: [id] })
  changed()
}

let flushing: Promise<{ sent: number; failed: string[] }> | null = null

/** Bağlantı gelince sıradaki talepleri sırayla gönderir. Sonuç: (gönderilen, reddedilen iletileri). */
export function flushOfflineQueue(): Promise<{ sent: number; failed: string[] }> {
  // Aynı anda iki boşaltma (ör. "online" olayı + düğme) aynı taslağı iki kez göndermesin.
  flushing ??= withQueueLock(doFlush).finally(() => { flushing = null })
  return flushing
}

async function doFlush(): Promise<{ sent: number; failed: string[] }> {
  await loadOfflineQueue()
  await refreshFromStore() // başka sekmenin gönderdiği/eklediği taslaklar
  const q = [...cache]
  if (!q.length || !navigator.onLine) return { sent: 0, failed: [] }
  let sent = 0
  const failed: string[] = []
  const done = new Set<string>()
  for (const r of q) {
    if (isExpired(r)) {
      failed.push(tx('{0} gün geçtiği için gönderilmedi: {1}', [OFFLINE_MAX_AGE_DAYS, queuedSummary(r)]))
      done.add(r.id)
      continue
    }
    try {
      await apiFetch(r.path, { method: 'POST', body: r.body, noQueue: true })
      sent++
      done.add(r.id)
    } catch (e) {
      const status = (e as { status?: number }).status ?? 0
      if (queueOutcome(status) === 'rejected') { // iş kuralı hatası: kuyruktan çıkar
        failed.push(e instanceof Error ? e.message : String(e))
        done.add(r.id)
      } // ağ ya da sunucu hatası: sonra yeniden dene
    }
  }
  // Gönderim sırasında eklenen yeni taslaklar korunur.
  cache = cache.filter((r) => !done.has(r.id))
  await persist({ del: [...done] })
  changed()
  return { sent, failed }
}

/** Oturum kapanırken: cihazdaki kuyruk, arayüz tercihleri ve bu cihazın anlık bildirim aboneliği silinir. */
export async function clearDeviceData() {
  cache = []
  writeLegacy([])
  await idbApply({ clear: true })
  clearLocalUiPrefs()
  changed()
  try { await disablePush() } catch { /* yoksay */ }
}
