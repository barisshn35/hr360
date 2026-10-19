import { useQuery } from '@tanstack/react-query'
import { passwordLinkApi, type SiteSettings } from '@/api/password-links'

export const publicSiteKey = ['site', 'public'] as const

/** Hata (eski sunucu, çevrimdışı, taklit edilmemiş uç) null döner; çağıranlar varsayılana düşer. */
export async function fetchPublicSite({ signal }: { signal?: AbortSignal }): Promise<SiteSettings | null> {
  try {
    return await passwordLinkApi.publicSite(signal)
  } catch {
    return null
  }
}

/** Herkese açık site bilgileri (yasal metinler, SSS, iletişim). Hata durumunda varsayılan şablonlar gösterilir. */
export function usePublicSite() {
  return useQuery<SiteSettings | null>({
    queryKey: publicSiteKey,
    queryFn: fetchPublicSite,
    staleTime: 60_000,
    retry: false,
  })
}
