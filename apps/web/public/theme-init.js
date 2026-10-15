// Uygulama paketi yuklenmeden temayi uygula: acilista beyaz parlama olmasin.
// Varsayilan koyu (lib/theme.ts DEFAULT_THEME ile ayni). Ayri dosyada: CSP satir ici
// betige izin vermez.
try {
  var t = localStorage.getItem('hr360.theme') || 'dark'
  var dark = t === 'dark' || (t === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
  if (dark) document.documentElement.classList.add('dark')
} catch (e) {
  document.documentElement.classList.add('dark')
}
