import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { loadLocale } from './lib/i18n'
import './styles/index.css'
// Hareket katmanı ayrı dosyada: index.css devir paketinden geldiği gibi kalıyor.
import './styles/motion.css'

const container = document.getElementById('root')
if (!container) throw new Error('#root öğesi bulunamadı.')

/**
 * PWA: üretim derlemesinde servis çalışanını kaydet. Uygulama kabuğu çevrimdışı da açılır.
 */
if (import.meta.env.PROD && 'serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch((e: unknown) => console.warn('[HR360] Servis çalışanı kaydedilemedi:', e))
  })
}

/**
 * Dil sözlüğü uygulama modüllerinden ÖNCE yüklenir: menü ve durum etiketleri gibi
 * modül düzeyindeki metinler içe aktarılırken seçili dilde oluşur.
 */
loadLocale()
  .catch((e: unknown) => console.error('[HR360] Dil dosyası yüklenemedi:', e))
  .then(() => import('./App'))
  .then(({ App }) => {
    createRoot(container).render(
      <StrictMode>
        <App />
      </StrictMode>,
    )
    hideSplash()
  })
  .catch((e: unknown) => {
    // Uygulama paketi inemediyse açılış ekranı sonsuza dek dönmesin.
    console.error('[HR360] Uygulama yüklenemedi:', e)
    const label = document.querySelector('#hr-splash .hr-sp-label')
    if (label) label.textContent = 'HR360 yüklenemedi — sayfayı yenileyin. / Could not load — please refresh.'
  })

/**
 * index.html'deki JS öncesi açılış ekranı: React ilk kareyi çizdikten sonra
 * yumuşakça kaybolur. Altındaki ilk ekran (oturum doğrulanıyor) aynı yörünge
 * düzenini kullandığından geçiş fark edilmez.
 */
function hideSplash() {
  const splash = document.getElementById('hr-splash')
  if (!splash) return
  requestAnimationFrame(() =>
    requestAnimationFrame(() => {
      splash.classList.add('hr-splash-out')
      window.setTimeout(() => splash.remove(), 600)
    }),
  )
}
