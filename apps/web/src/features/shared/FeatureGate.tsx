import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Building2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { useTenantScope } from '@/lib/tenant-scope'
import { useAuth } from '@/auth/useAuth'
import { tx } from '@/lib/i18n'

/**
 * Kiracısız oturumda şirket düzeyindeki ekran yerine gösterilen kart (TenantGate ve TenantOnly ortak).
 * Platform yöneticisi hiçbir şirkete bağlı değildir ve "şirket değiştirme" yoktur: şirket verisini
 * Kiracılar ekranından yönetir. Başka bir kullanıcıda kiracı yoksa oturum eksik açılmıştır.
 */
export function NoTenantNotice() {
  const { roles } = useAuth()
  const platform = roles.includes('platform-admin')
  return (
    <Card className="mx-auto mt-10 max-w-lg items-center gap-3 p-8 text-center">
      <span className="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary"><Building2 className="size-5" /></span>
      <h2 className="text-[17px] font-semibold">{tx('Bu ekran şirket düzeyindedir')}</h2>
      {platform ? (
        <>
          <p className="text-[13.5px] text-muted-foreground">{tx('Platform yöneticisi hiçbir şirkete bağlı değildir; şirket verisini Kiracılar ekranından yönetir.')}</p>
          <Button asChild className="mt-2">
            <Link to="/panel/platform/kiracilar">{tx('Kiracılara git')}</Link>
          </Button>
        </>
      ) : (
        <p className="text-[13.5px] text-muted-foreground">{tx('Hesabınız bir şirkete bağlı görünmüyor. Çıkış yapıp yeniden giriş yapın; sorun sürerse yöneticinize başvurun.')}</p>
      )}
    </Card>
  )
}

/**
 * Şirket düzeyindeki ekranlar için: kiracısız oturumda (şirket seçmemiş platform yöneticisi)
 * ekran yerine bilgi kartı gösterir.
 * `allowPlatform`: kiracılar arası paylaşımlı ekranlar (ör. model kartı) platform yöneticisine
 * kiracı şartı olmadan açılır.
 */
export function TenantGate({ allowPlatform = false, children }: { allowPlatform?: boolean; children: ReactNode }) {
  const { noTenant } = useTenantScope()
  const { roles } = useAuth()
  if (noTenant) {
    if (allowPlatform && roles.includes('platform-admin')) return <>{children}</>
    return <NoTenantNotice />
  }
  return <>{children}</>
}
