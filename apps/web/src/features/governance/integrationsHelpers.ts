/* Dalga 12 (madde 93-94): webhook yeniden deneme planı ve API anahtarı yetkileri için yardımcılar. */
import { tx } from '@/lib/i18n'

/** Saniyeyi kısa süre metnine çevirir: 90 → "1 dk 30 sn", 7200 → "2 sa". */
export function formatBackoff(seconds: number): string {
  if (seconds < 60) return tx('{0} sn', [seconds])
  if (seconds < 3600) {
    const m = Math.floor(seconds / 60)
    const s = seconds % 60
    return s ? tx('{0} dk {1} sn', [m, s]) : tx('{0} dk', [m])
  }
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  return m ? tx('{0} sa {1} dk', [h, m]) : tx('{0} sa', [h])
}

/** Yetki açıklaması (ekranda kod yanında gösterilir). */
export function scopeLabel(scope: string): string {
  switch (scope) {
    case 'read-only': return tx('Salt okunur — tüm okuma yetkileri (yazma yok)')
    case 'employees:read': return tx('Çalışan listesi')
    case 'departments:read': return tx('Departmanlar')
    case 'leaves:read': return tx('İzinler')
    case 'events:read': return tx('Olay akışı')
    case 'hooks:write': return tx('REST hook aboneliği (Zapier / n8n)')
    default: return scope
  }
}

/** "read-only" seçiliyken tekil okuma yetkileri gereksizdir; seçimi sadeleştirir. */
export function simplifyScopes(scopes: string[]): string[] {
  const set = [...new Set(scopes)]
  return set.includes('read-only') ? set.filter((s) => s === 'read-only' || !s.endsWith(':read')) : set
}

/** Teslimat yeniden deneme durumu → rozet tonu ve metin. */
export function retryStateView(state: string | null | undefined): { tone: 'neutral' | 'success' | 'warning' | 'danger' | 'info'; label: string } | null {
  switch (state) {
    case 'pending': return { tone: 'warning', label: tx('Yeniden denenecek') }
    case 'retrying': return { tone: 'info', label: tx('Deneniyor') }
    case 'retried': return { tone: 'neutral', label: tx('Yeniden denendi') }
    case 'resent': return { tone: 'neutral', label: tx('Elle gönderildi') }
    case 'gave_up': return { tone: 'danger', label: tx('Vazgeçildi') }
    default: return null
  }
}
