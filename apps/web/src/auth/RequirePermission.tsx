import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { ErrorScene } from '@/components/errors/ErrorScene'
import { LockedShieldArt } from '@/components/errors/art'
import { useAuth } from './useAuth'
import { primaryRole, roleLabels, hasStandardRole, type Permission, type Role } from './roles'
import { tx } from '@/lib/i18n'

/**
 * Yetkisiz erişimde boş sayfa yerine ne olduğunu ve ne yapılacağını söyler.
 *
 * `requireRoles` verilirse, `permission` (Ek İzin dahil) yerine SADECE bu
 * sabit rollerden birine sahip olup olmadığı kontrol edilir - bkz.
 * hasStandardRole'daki not: bazı ekranların arkasındaki backend uçları
 * "ext-*" ile genişletilmemiş, o yüzden Ek İzin ile buraya girip veri
 * çekememe durumunu önceden engeller.
 */
export function RequirePermission({
  permission,
  requireRoles,
  children,
}: {
  permission: Permission
  requireRoles?: Role[]
  children: ReactNode
}) {
  const { can, roles } = useAuth()
  const allowed = requireRoles ? hasStandardRole(roles, requireRoles) : can(permission)
  if (allowed) return <>{children}</>

  return (
    <ErrorScene
      testId="forbidden"
      tone="destructive"
      art={<LockedShieldArt />}
      code="403"
      title={tx('Bu bölüme erişiminiz yok')}
      detail={tx('Rolünüz ({0}) bu sayfayı kapsamıyor. Erişim gerekiyorsa İK yöneticinizden rol talep edin.', [roleLabels[primaryRole(roles)]])}
      actions={
        <Button asChild>
          <Link to="/panel">{tx('Genel bakışa dön')}</Link>
        </Button>
      }
    />
  )
}
