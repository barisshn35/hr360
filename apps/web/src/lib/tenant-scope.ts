import { useAuth } from '@/auth/useAuth'

/**
 * Oturumun bir şirkete (kiracıya) bağlı olup olmadığı. Şirket düzeyindeki modüller
 * kiracı bağlamında çalışır; şirket seçmemiş platform yöneticisine gösterilmez
 * (aksi hâlde her uç "kiracı yok" der).
 */
export function useTenantScope() {
  const { status, tenantSlug } = useAuth()
  const noTenant = status === 'authenticated' && !tenantSlug
  return { noTenant, hasTenant: !noTenant }
}
