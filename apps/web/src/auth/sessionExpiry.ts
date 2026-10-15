/**
 * Oturumun kendiliğinden düştüğünü (token yenilenemedi, 401, başka sekmede çıkış)
 * sekme süresince hatırlar; panel bu durumda girişe sessizce atmak yerine
 * "oturum sona erdi" ekranını gösterir.
 */
const KEY = 'hr360.sessionExpired'

export function markSessionExpired(): void {
  try {
    sessionStorage.setItem(KEY, '1')
  } catch {
    /* depolama kapalı: ekran gösterilmez, giriş sayfasına düşülür */
  }
}

export function isSessionExpired(): boolean {
  try {
    return sessionStorage.getItem(KEY) === '1'
  } catch {
    return false
  }
}

export function clearSessionExpired(): void {
  try {
    sessionStorage.removeItem(KEY)
  } catch {
    /* yok say */
  }
}
