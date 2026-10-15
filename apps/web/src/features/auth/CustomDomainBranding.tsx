import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { identityDirectoryApi, type PublicBranding } from '@/api/identityDirectory'
import { keycloak, scopeForTenant, writePreferredTenant } from '@/auth/keycloak'
import { applyTenantBrandColor } from '@/lib/tenant-brand'
import { lang, tx } from '@/lib/i18n'

/**
 * G28: giriş ekranı şirketin doğrulanmış özel alan adından (ör. ik.sirket.com.tr) açıldıysa
 * şirket markasını getirir. Yalnızca doğrulanmış alan adı + etkin şirket için yanıt gelir;
 * aksi hâlde null (platform markası kalır).
 */
export function useCustomDomainBranding() {
  const host = typeof window !== 'undefined' ? window.location.host : ''
  const q = useQuery({
    queryKey: ['public-branding', host],
    queryFn: ({ signal }) => identityDirectoryApi.publicBranding(host, signal),
    enabled: !!host,
    staleTime: 5 * 60_000,
    retry: false,
  })
  const brand = q.data ?? null
  useEffect(() => {
    if (brand?.primaryColorHex) applyTenantBrandColor(brand.primaryColorHex)
  }, [brand?.primaryColorHex])
  return brand
}

/**
 * Şirket ipucuyla giriş: Keycloak'a organization:&lt;slug&gt; kapsamı gönderilir; böylece
 * jetona bu alan adının şirketi yazılır (kullanıcı o şirketin üyesi değilse claim boş kalır,
 * başka şirkete erişim açılmaz). Seçim sekme boyunca hatırlanır.
 */
export function loginWithTenantHint(brand: PublicBranding, next: string) {
  writePreferredTenant(brand.slug)
  void keycloak.login({
    redirectUri: new URL(next, window.location.origin).href,
    locale: lang,
    scope: scopeForTenant(brand.slug),
  })
}

/** Giriş kartının üstünde şirket logosu ve adı. */
export function CustomDomainBrandBadge({ brand }: { brand: PublicBranding }) {
  return (
    <div className="mb-5 flex items-center gap-3 rounded-xl border border-border bg-card/60 p-3">
      {brand.logoUrl
        ? <img src={brand.logoUrl} alt="" className="h-9 max-w-[64px] object-contain" />
        : <span className="flex size-9 items-center justify-center rounded-lg bg-primary/15 text-[15px] font-semibold text-primary">{brand.name.slice(0, 1)}</span>}
      <div className="min-w-0">
        <p className="truncate text-[14px] font-semibold">{brand.name}</p>
        <p className="text-[12px] text-muted-foreground">{tx('Şirket hesabınızla giriş yapın')}</p>
      </div>
    </div>
  )
}
