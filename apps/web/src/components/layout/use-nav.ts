import { useMemo } from 'react'
import { useAuth } from '@/auth/useAuth'
import { useTenantScope } from '@/lib/tenant-scope'
import { filterByPermission, navGroups } from './nav-config'

/** Kullanıcının izinleriyle süzülmüş menü ağacı. */
export function useNavGroups() {
  const { can, roles } = useAuth()
  const { hasTenant } = useTenantScope()
  return useMemo(() => filterByPermission(navGroups, can, roles, hasTenant), [can, roles, hasTenant])
}
