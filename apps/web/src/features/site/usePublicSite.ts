import { useQuery } from '@tanstack/react-query'
import { passwordLinkApi, type SiteSettings } from '@/api/password-links'

/** Herkese açık site bilgileri (yasal metinler, SSS, iletişim). Hata durumunda varsayılan şablonlar gösterilir. */
export function usePublicSite() {
  return useQuery<SiteSettings | null>({
    queryKey: ['site', 'public'] as const,
    queryFn: async ({ signal }) => {
      try {
        return await passwordLinkApi.publicSite(signal)
      } catch {
        return null
      }
    },
    staleTime: 60_000,
    retry: false,
  })
}
