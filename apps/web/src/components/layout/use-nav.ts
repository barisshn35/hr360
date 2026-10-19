import { useMemo } from 'react'
import { useAuth } from '@/auth/useAuth'
import { useTenantScope } from '@/lib/tenant-scope'
import { useEomWinners } from '@/api/eom'
import { useRecognitionSettings } from '@/api/recognition'
import { useCoffeeEnabled } from '@/api/coffee'
import { useModules } from '@/lib/modules'
import { filterByPermission, locate, navGroupsFor } from './nav-config'

/**
 * Kullanıcının izinleriyle süzülmüş menü ağacı. İsteğe bağlı modüller (ör. ayın çalışanı) kiracıda
 * kapalıysa gizlenir; İK yine görür ki ayarlardan açabilsin. Kurulumda kapalı modüller ("Hafif")
 * herkesten gizlenir.
 */
export function useNavGroups() {
  const { can, roles } = useAuth()
  const { hasTenant } = useTenantScope()
  const isHr = (roles as string[]).some((r) => r === 'hr-admin' || r === 'tenant-admin' || r === 'ext-engagement-manage')
  const eom = useEomWinners(hasTenant && !isHr).data?.enabled === true
  const points = useRecognitionSettings(hasTenant && !isHr).data?.enabled === true
  const coffee = useCoffeeEnabled(hasTenant && !isHr).data === true
  const { isEnabled, tenancy } = useModules()
  return useMemo(
    () => filterByPermission(navGroupsFor(tenancy), can, roles, hasTenant, { eom: isHr || eom, points: isHr || points, coffee: isHr || coffee }, isEnabled),
    [can, roles, hasTenant, isHr, eom, points, coffee, isEnabled, tenancy],
  )
}

/** Adresin menüdeki konumu; tek şirket kipinde Sistem ayarları/Bakım "Yönetim" altında görünür. */
export function useLocate(pathname: string) {
  const { tenancy } = useModules()
  return useMemo(() => locate(pathname, navGroupsFor(tenancy)), [pathname, tenancy])
}
