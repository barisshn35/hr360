import { createContext } from 'react'
import type { Permission, Role } from './roles'
import type { Tenant } from '@/api/tenant'

export interface AuthUser {
  id: string
  username: string
  fullName: string
  email: string | null
  roles: Role[]
  /** Baş harfler — avatar ve komut paletinde kullanılıyor. */
  initials: string
}

export interface AuthContextValue {
  status: 'loading' | 'authenticated' | 'anonymous' | 'error'
  user: AuthUser | null
  error: string | null
  login: (redirectTo?: string) => void
  logout: () => void
  accountUrl: string

  /** İzin kontrolü. Yalnızca arayüz katmanı — gerçek yetki backend'de. */
  can: (permission: Permission) => boolean
  hasRole: (role: Role) => boolean
  roles: Role[]

  /* ----------------------------- Çok kiracılılık ----------------------------- */

  /** JWT'deki `organization` claim'i. Yoksa kullanıcı hiçbir veri göremez. */
  tenantSlug: string | null
  /** `GET /api/tenant/my-tenant` yanıtı. Servise ulaşılamazsa null kalır. */
  tenant: Tenant | null
  tenantLoading: boolean
  /** Tenant Service'e ulaşılamadıysa insan okuyabilir mesaj. */
  tenantError: string | null
  canSwitchTenant: boolean
  availableTenants: Tenant[]
  switchTenant: (slug: string) => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)
