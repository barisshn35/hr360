/**
 * Adresi ekran şablonuna çevirir: kimlik, tarih ve sayı içeren parçalar ":id" olur; sorgu ve çapa atılır.
 * Kullanım ölçümü ve geri bildirim kişiye ya da kayda bağlanmasın diye (KVKK) yalnızca şablon gönderilir.
 */
export function pageTemplate(pathname: string): string | null {
  const clean = pathname.split(/[?#]/)[0].replace(/\/+$/, '') || '/'
  if (!clean.startsWith('/panel')) return null
  const parts = clean.split('/').slice(1).map((p) => {
    const s = decodeURIComponent(p).toLowerCase()
    if (/^[a-z-]+$/.test(s) || /^\d{1,4}$/.test(s)) return s // ekran adı ya da "360" gibi kısa sayı
    return ':id'
  })
  return '/' + parts.slice(0, 7).join('/')
}
