/**
 * İsteğe bağlı modüller ("Hafif" kurulum). Açık modül listesi herkese açık site
 * ucundan (`/api/tenant/public/site`) gelir; alan yoksa (eski sunucu) ya da uç
 * yanıt vermezse her şey açık sayılır — arayüz bugünkü gibi davranır.
 */
import { useEffect, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { SiteSettings } from '@/api/password-links'
import { fetchPublicSite, publicSiteKey } from '@/features/site/usePublicSite'

export const MODULE_IDS = ['recruitment', 'onboarding', 'timeshift', 'performance', 'learning', 'compensation', 'ml', 'expense'] as const
export type ModuleId = (typeof MODULE_IDS)[number]
export type Tenancy = 'single' | 'multi'

export interface ModuleState {
  enabled: Set<ModuleId>
  /** Menü/ekran için: liste gelene kadar açık sayılır (tam kurulumda titreme olmasın). */
  isEnabled: (id: ModuleId | undefined) => boolean
  /** Sorgu için: liste gelmeden kapalı modüle istek atılmasın diye yüklenirken false. */
  canQuery: (id: ModuleId | undefined) => boolean
  tenancy: Tenancy
  loading: boolean
}

const ALL: ReadonlySet<ModuleId> = new Set(MODULE_IDS)

/** Site yanıtını modül kümesine çevirir; eksik/bozuk alan = hepsi açık, çok kiracılı. */
export function parseModules(site: Pick<SiteSettings, 'modules' | 'tenancy'> | null | undefined): { enabled: Set<ModuleId>; tenancy: Tenancy } {
  const list = site?.modules
  const enabled = Array.isArray(list)
    ? new Set(list.filter((m): m is ModuleId => ALL.has(m as ModuleId)))
    : new Set(MODULE_IDS)
  return { enabled, tenancy: site?.tenancy === 'single' ? 'single' : 'multi' }
}

/* Son bilinen liste: ilk çizimde modül sayfası bekletilmesin, menü titremesin. */
const CACHE_KEY = 'hr360.modules'

function readCached(): SiteSettings | undefined {
  try {
    const raw = localStorage.getItem(CACHE_KEY)
    return raw ? (JSON.parse(raw) as SiteSettings) : undefined
  } catch {
    return undefined
  }
}

function writeCached(site: SiteSettings | null) {
  try {
    if (site && Array.isArray(site.modules)) localStorage.setItem(CACHE_KEY, JSON.stringify({ modules: site.modules, tenancy: site.tenancy }))
    else localStorage.removeItem(CACHE_KEY)
  } catch {
    /* depolama yok */
  }
}

export function useModules(): ModuleState {
  const q = useQuery<SiteSettings | null>({
    queryKey: publicSiteKey,
    queryFn: fetchPublicSite,
    // Kurulumda nadiren değişir.
    staleTime: 30 * 60_000,
    retry: false,
    placeholderData: readCached,
  })
  const data = q.data
  const fresh = q.isSuccess && !q.isPlaceholderData
  useEffect(() => {
    if (fresh) writeCached(data ?? null)
  }, [fresh, data])
  return useMemo(() => {
    const { enabled, tenancy } = parseModules(data)
    const loading = data === undefined && q.isLoading
    const isEnabled = (id: ModuleId | undefined) => !id || enabled.has(id)
    return {
      enabled,
      isEnabled,
      canQuery: (id: ModuleId | undefined) => !loading && isEnabled(id),
      tenancy,
      loading,
    }
  }, [data, q.isLoading])
}
