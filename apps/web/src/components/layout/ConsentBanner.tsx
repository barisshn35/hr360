import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ShieldCheck, X } from 'lucide-react'
import { kvkk10Api } from '@/api/wave10'
import { useAuth } from '@/auth/useAuth'
import { useTenantScope } from '@/lib/tenant-scope'
import { tx } from '@/lib/i18n'

/**
 * Dalga 10 (madde 55): girişte bilgilendirme bandı. Okunmamış aydınlatma metni ya da yeniden onay
 * kampanyası açık olan güncellenmiş metin varsa gösterilir ve Profilim › Gizlilik ekranına götürür.
 * Rıza zorlanmaz: bant kapatılabilir (oturum boyunca), hiçbir işlem engellenmez. Bağlantıya tıklamak
 * bandı gizlemez; kullanıcı yanıt verince bekleyen liste yenilenir ve bant kendiliğinden kalkar.
 */
export function ConsentBanner() {
  const { status, tenantSlug, roles } = useAuth()
  const { hasTenant } = useTenantScope()
  const [hidden, setHidden] = useState(() => {
    try { return sessionStorage.getItem('hr360.consentBanner') === '1' } catch { return false }
  })
  // Platform yöneticisi şirketin çalışanı değildir: aydınlatma/rıza bandı ona hiç gösterilmez
  // (sunucu da platform yöneticisinin rıza kaydını reddeder).
  const enabled = status === 'authenticated' && Boolean(tenantSlug) && !roles.includes('platform-admin') && hasTenant && !hidden
  const q = useQuery({ queryKey: ['privacy', 'consents', 'pending'], queryFn: ({ signal }) => kvkk10Api.pendingConsents(signal), enabled, staleTime: 5 * 60_000, retry: false })
  if (!enabled || !q.data?.length) return null
  const hide = () => {
    try { sessionStorage.setItem('hr360.consentBanner', '1') } catch { /* özel pencere */ }
    setHidden(true)
  }
  const titles = q.data.map((p) => p.title).join(', ')
  const updated = q.data.some((p) => p.reason === 'updated')
  return (
    <div className="mx-auto mt-3 w-full max-w-[1480px] px-3 sm:px-5">
      <div role="status" className="flex items-start gap-2.5 rounded-xl border border-primary/30 bg-primary/10 px-4 py-3 text-[13px] leading-relaxed">
        <ShieldCheck aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-primary" />
        <p className="flex-1">
          {updated ? tx('Kişisel verilerle ilgili güncellenen metinler var: {0}.', [titles]) : tx('Okumanız gereken bilgilendirme metni var: {0}.', [titles])}{' '}
          <Link to="/panel/profil?sekme=gizlilik" className="font-medium text-primary underline-offset-2 hover:underline">{tx('Oku ve yanıtla')}</Link>
        </p>
        <button type="button" onClick={hide} aria-label={tx('Kapat')} className="rounded-md p-0.5 text-muted-foreground hover:text-foreground"><X className="size-4" /></button>
      </div>
    </div>
  )
}
