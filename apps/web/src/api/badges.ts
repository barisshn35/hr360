import { useQuery } from '@tanstack/react-query'
import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

/** Rozet koleksiyonu (engagement-service /api/badges). Başkasının rozetlerinde ilerleme yoktur. */
export interface EarnedBadge { id: string; group: BadgeGroup; earned: boolean; earnedAt: string | null; value?: number; target?: number }
export type BadgeGroup = 'milestone' | 'recognition' | 'community' | 'growth'

export const badgesApi = {
  get: (employeeId?: string, signal?: AbortSignal) => apiFetch<{ mine: boolean; badges: EarnedBadge[] }>(`/api/engagement/badges${qs({ employeeId })}`, { signal }),
}

export function useBadges(employeeId?: string) {
  return useQuery({ queryKey: ['badges', employeeId ?? 'me'], queryFn: ({ signal }) => badgesApi.get(employeeId, signal), staleTime: 5 * 60_000, retry: false })
}

export const badgeGroupLabels = (): Record<BadgeGroup, string> => ({
  milestone: tx('Kilometre taşları'), recognition: tx('Takdir'), community: tx('Topluluk'), growth: tx('Gelişim'),
})

/** Rozet görünümü: simge, ad ve nasıl kazanıldığı. */
export function badgeInfo(id: string): { emoji: string; title: string; how: string } {
  const m: Record<string, [string, string, string]> = {
    'tenure-1': ['🌱', tx('İlk yıl'), tx('Şirkette 1. yılını doldur')],
    'tenure-3': ['🌿', tx('3 yıl'), tx('Şirkette 3. yılını doldur')],
    'tenure-5': ['🌳', tx('5 yıl'), tx('Şirkette 5. yılını doldur')],
    'tenure-10': ['🏛️', tx('10 yıl'), tx('Şirkette 10. yılını doldur')],
    'kudos-received-1': ['🙏', tx('İlk takdir'), tx('İlk takdirini al')],
    'kudos-received-10': ['⭐', tx('Takdir yıldızı'), tx('10 takdir al')],
    'kudos-received-50': ['🌟', tx('Takdir efsanesi'), tx('50 takdir al')],
    'kudos-given-10': ['🤝', tx('Takdir eden'), tx('10 takdir gönder')],
    'kudos-given-50': ['💐', tx('Kültür elçisi'), tx('50 takdir gönder')],
    'eom-1': ['👑', tx('Ayın çalışanı'), tx('Ayın çalışanı seçil')],
    'eom-3': ['🏆', tx('Üç kez ayın çalışanı'), tx('3 kez ayın çalışanı seçil')],
    'idea-1': ['💡', tx('Fikir sahibi'), tx('Fikir kutusuna ilk fikrini paylaş')],
    'idea-done-1': ['🚀', tx('Fikri hayata geçti'), tx('Bir fikrin uygulansın')],
    'volunteer-10': ['🤲', tx('Gönüllü'), tx('10 saat gönüllülük yap')],
    'volunteer-50': ['💚', tx('Gönüllü kahraman'), tx('50 saat gönüllülük yap')],
    'events-5': ['🎪', tx('Etkinlik müdavimi'), tx('5 etkinliğe katıl')],
    'coffee-3': ['☕', tx('Kahve dostu'), tx('3 kahve eşleşmesinde görüş')],
    'cards-10': ['✍️', tx('İyi dilekçi'), tx('10 grup kartı imzala')],
    'cert-1': ['📜', tx('İlk sertifika'), tx('İlk sertifikanı al')],
    'cert-5': ['🎓', tx('Sertifika koleksiyoncusu'), tx('5 sertifika al')],
  }
  const [emoji, title, how] = m[id] ?? ['🏅', id, '']
  return { emoji, title, how }
}
