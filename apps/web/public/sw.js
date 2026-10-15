/*
 * HR360 servis çalışanı (PWA).
 *
 * - /assets/* içerik hash'li ve değişmez: önce önbellek.
 * - Sayfa gezintileri: önce ağ, ağ yoksa önbellekteki uygulama kabuğu
 *   (index.html) — uygulama çevrimdışı da açılır, veri ekranları "bağlantı
 *   yok" der. Kabuk henüz önbellekte yoksa statik çevrimdışı sayfası
 *   (offline.html) gösterilir.
 * - /api, /auth, /ml ve /logos ASLA önbelleklenmez: kişisel veri ve oturum
 *   bilgisi cihazda kalmamalı.
 * - Anlık bildirim (Web Push): içerik kişisel veri taşımaz ("Yeni bir bildiriminiz
 *   var"); tıklanınca uygulama açılır, ayrıntı oturum açıkken görülür.
 */
const VERSION = 'hr360-v3'
const SHELL = ['/', '/index.html', '/offline.html', '/manifest.webmanifest', '/favicon.svg', '/appicon-dark-192.png', '/icon-white.svg']

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(VERSION).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()))
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k)))).then(() => self.clients.claim()),
  )
})

const NEVER = /^\/(api|auth|ml|logos|gateway)\//

self.addEventListener('fetch', (event) => {
  const req = event.request
  if (req.method !== 'GET') return
  const url = new URL(req.url)
  if (url.origin !== self.location.origin || NEVER.test(url.pathname)) return

  if (req.mode === 'navigate') {
    event.respondWith(
      fetch(req)
        .then((res) => {
          // Yalnızca başarılı HTML kabuk önbelleğe: bakım (502/503) sayfası ya da tarayıcıda açılan
          // robots.txt / sitemap.xml uygulama kabuğunun yerine geçmesin.
          if (res.ok && (res.headers.get('content-type') || '').includes('text/html')) {
            const copy = res.clone()
            caches.open(VERSION).then((c) => c.put('/index.html', copy))
          }
          return res
        })
        .catch(() => caches.match('/index.html').then((hit) => hit || caches.match('/offline.html'))),
    )
    return
  }

  if (url.pathname.startsWith('/assets/') || SHELL.includes(url.pathname) || /\.(png|svg|woff2?)$/.test(url.pathname)) {
    event.respondWith(
      caches.match(req).then((hit) => hit || fetch(req).then((res) => {
        if (res.ok) { const copy = res.clone(); caches.open(VERSION).then((c) => c.put(req, copy)) }
        return res
      })),
    )
  }
})

self.addEventListener('push', (event) => {
  let data = {}
  try { data = event.data ? event.data.json() : {} } catch { data = {} }
  const title = data.title || 'HR360'
  event.waitUntil(self.registration.showNotification(title, {
    body: data.body || '',
    icon: '/appicon-dark-192.png',
    badge: '/favicon-48.png',
    tag: data.tag || 'hr360',
    renotify: true,
    data: { url: data.url || '/panel/bildirimler' },
  }))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = (event.notification.data && event.notification.data.url) || '/panel'
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
      for (const c of list) {
        if (new URL(c.url).origin === self.location.origin && 'focus' in c) { c.navigate(url); return c.focus() }
      }
      return self.clients.openWindow(url)
    }),
  )
})
