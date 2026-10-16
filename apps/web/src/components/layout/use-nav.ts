import { useMemo } from 'react'
import { useAuth } from '@/auth/useAuth'
import { useTenantScope } from '@/lib/tenant-scope'
import { useEomWinners } from '@/api/eom'
import { useRecognitionSettings } from '@/api/recognition'
import { useCoffeeEnabled } from '@/api/coffee'
import { filterByPermission, navGroups } from './nav-config'

/**
 * Kullanıcının izinleriyle süzülmüş menü ağacı. İsteğe bağlı modüller (ör. ayın çalışanı) kiracıda
 * kapalıysa gizlenir; İK yine görür ki ayarlardan açabilsin.
 */
export function useNavGroups() {
  const { can, roles } = useAuth()
  const { hasTenant } = useTenantScope()
  const isHr = (roles as string[]).some((r) => r === 'hr-admin' || r === 'tenant-admin' || r === 'ext-engagement-manage')
  const eom = useEomWinners(hasTenant && !isHr).data?.enabled === true
  const points = useRecognitionSettings(hasTenant && !isHr).data?.enabled === true
  const coffee = useCoffeeEnabled(hasTenant && !isHr).data === true
  return useMemo(() => filterByPermission(navGroups, can, roles, hasTenant, { eom: isHr || eom, points: isHr || points, coffee: isHr || coffee }), [can, roles, hasTenant, isHr, eom, points, coffee])
}
