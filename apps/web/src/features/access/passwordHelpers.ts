import { encode } from 'uqr'
import { tx } from '@/lib/i18n'

/** Kalan süre metni: "2 gün 3 sa", "4 sa 10 dk", "45 dk". */
export function remainingText(ms: number): string {
  if (ms <= 0) return tx('Süresi doldu')
  const min = Math.floor(ms / 60_000)
  const d = Math.floor(min / 1440)
  const h = Math.floor((min % 1440) / 60)
  const m = min % 60
  if (d > 0) return tx('{0} gün {1} sa', [d, h])
  if (h > 0) return tx('{0} sa {1} dk', [h, m])
  return tx('{0} dk', [Math.max(1, m)])
}

/** Parola gücü (0-4): uzunluk + karakter çeşitliliği. Yalnızca gösterge; kesin kural Keycloak politikası. */
export function passwordScore(value: string): number {
  if (!value) return 0
  const variety = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(value)).length
  return Math.max(0, Math.min(4, Math.floor(value.length / 4) + variety - 1))
}

/** QR modüllerini tek SVG yolu olarak üretir (tarayıcıda; harici servis yok). */
export function qrPath(value: string): { path: string; size: number } {
  const qr = encode(value, { ecc: 'M', border: 2 })
  let d = ''
  qr.data.forEach((row, y) => {
    row.forEach((on, x) => {
      if (on) d += `M${x} ${y}h1v1h-1z`
    })
  })
  return { path: d, size: qr.size }
}

/** Keycloak parola politikası kodu → anlaşılır metin. */
export function policyText(code: string | undefined): string {
  switch (code) {
    case 'invalidPasswordMinLengthMessage': return tx('Parola çok kısa. Daha uzun bir parola seçin.')
    case 'invalidPasswordMaxLengthMessage': return tx('Parola çok uzun.')
    case 'invalidPasswordMinDigitsMessage': return tx('Parola en az bir rakam içermeli.')
    case 'invalidPasswordMinUpperCaseCharsMessage': return tx('Parola en az bir büyük harf içermeli.')
    case 'invalidPasswordMinLowerCaseCharsMessage': return tx('Parola en az bir küçük harf içermeli.')
    case 'invalidPasswordMinSpecialCharsMessage': return tx('Parola en az bir özel karakter (ör. ! ? * #) içermeli.')
    case 'invalidPasswordNotUsernameMessage':
    case 'invalidPasswordNotContainsUsernameMessage': return tx('Parola kullanıcı adınızı içeremez.')
    case 'invalidPasswordNotEmailMessage': return tx('Parola e-posta adresiniz olamaz.')
    case 'invalidPasswordHistoryMessage': return tx('Bu parolayı yakın zamanda kullandınız; farklı bir parola seçin.')
    case 'invalidPasswordBlacklistedMessage': return tx('Bu parola çok yaygın; daha güçlü bir parola seçin.')
    default: return tx('Parola, parola kurallarına uymuyor.')
  }
}
