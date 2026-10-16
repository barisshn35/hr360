/**
 * HR360 Android kabuğu (yenilik haritası #6) ile köprü. Uygulama WebView içinde açıldığında yerel
 * nesne `window.HR360Native` bulunur (apps/mobile/android/.../MainActivity.java); tarayıcıda yoktur ve
 * bu dosyadaki hiçbir şey çalışmaz.
 *
 * Android WebView `<a download>` ile blob/data indirmelerini kaydedemez. Kabukta indirme bağlantıları
 * yakalanır ve dosya yerel köprüyle kaydedilip uygun uygulamayla açılır. Birçok yer tıklamadan hemen
 * sonra `URL.revokeObjectURL` çağırdığından, blob'lar oluşturulurken hatırlanır ve iptal bir dakika ertelenir.
 */
interface NativeBridge {
  getServer(): string
  resetServer(): void
  saveFile(base64: string, fileName: string, mime: string): boolean
}

declare global {
  interface Window { HR360Native?: NativeBridge }
}

export const nativeShell = (): NativeBridge | undefined => (typeof window !== 'undefined' ? window.HR360Native : undefined)

async function toBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer())
  let bin = ''
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return btoa(bin)
}

export function installNativeShell(): void {
  const native = nativeShell()
  if (!native) return
  const blobs = new Map<string, Blob>()
  const create = URL.createObjectURL.bind(URL)
  const revoke = URL.revokeObjectURL.bind(URL)
  URL.createObjectURL = (obj: Blob | MediaSource) => {
    const url = create(obj)
    if (obj instanceof Blob) blobs.set(url, obj)
    return url
  }
  URL.revokeObjectURL = (url: string) => {
    setTimeout(() => { blobs.delete(url); revoke(url) }, 60_000)
  }

  async function save(a: HTMLAnchorElement): Promise<void> {
    const href = a.href
    const blob = blobs.get(href) ?? (href.startsWith('data:') || href.startsWith('blob:') ? await (await fetch(href)).blob() : null)
    if (!blob) return
    native!.saveFile(await toBase64(blob), a.download || 'dosya', blob.type || 'application/octet-stream')
  }
  const isDownload = (a: HTMLAnchorElement) => a.hasAttribute('download') && (a.href.startsWith('blob:') || a.href.startsWith('data:'))

  // Kodla tetiklenen indirmeler (a.click()) ve kullanıcı tıklamaları.
  const click = HTMLAnchorElement.prototype.click
  HTMLAnchorElement.prototype.click = function (this: HTMLAnchorElement) {
    if (isDownload(this)) { void save(this); return }
    click.call(this)
  }
  document.addEventListener('click', (e) => {
    const a = (e.target as Element | null)?.closest?.('a')
    if (a instanceof HTMLAnchorElement && isDownload(a)) { e.preventDefault(); void save(a) }
  }, true)
}
