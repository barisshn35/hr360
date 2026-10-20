import { useEffect, useState } from 'react'
import { KeyRound, LogOut } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Callout } from '@/components/ui/callout'
import { platformAccessApi } from '@/api/platformAccess'
import { useAuth } from '@/auth/useAuth'
import { invalidatePlatformGrants, writeGrantTenant } from '@/auth/platformAccess'
import { tx } from '@/lib/i18n'

/** Kalan dakika (en az 0). */
export function minutesLeft(expiresAt: string | null | undefined, now: number): number {
  if (!expiresAt) return 0
  const t = Date.parse(expiresAt)
  return Number.isFinite(t) ? Math.max(0, Math.ceil((t - now) / 60_000)) : 0
}

/** "1 sa 25 dk" / "25 dk". */
export function remainingLabel(minutes: number): string {
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return h > 0 ? tx('{0} sa {1} dk', [h, m]) : tx('{0} dk', [m])
}

/**
 * Güvenlik dalgası 2A: platform yöneticisi bir şirketin görünümündeyken her sayfada görünen bant —
 * şirket adı, süreli erişim izninin kalan süresi, "Çık" (şirket görünümünden çıkar) ve "İzni kapat".
 * İzin yoksa ya da süresi dolduysa şirket verisi açılmaz; bant bunu söyler.
 */
export function PlatformAccessBanner() {
  const { roles, tenantSlug, tenant } = useAuth()
  const [now, setNow] = useState(() => Date.now())
  const [busy, setBusy] = useState(false)
  const active = roles.includes('platform-admin') && Boolean(tenantSlug)
  useEffect(() => {
    if (!active) return
    const id = window.setInterval(() => setNow(Date.now()), 30_000)
    return () => window.clearInterval(id)
  }, [active])
  if (!active) return null
  const grant = tenant?.platformAccess ?? null
  const left = minutesLeft(grant?.expiresAt, now)
  const leave = () => {
    writeGrantTenant(null)
    invalidatePlatformGrants()
    window.location.assign('/panel/platform/kiracilar')
  }
  const revokeAndLeave = async () => {
    if (!grant) return leave()
    setBusy(true)
    try { await platformAccessApi.revoke(grant.grantId) } catch { /* izin zaten kapanmış olabilir */ }
    leave()
  }
  const name = tenant?.name ?? tenantSlug
  return (
    <div className="mx-auto mt-3 w-full max-w-[1480px] px-3 sm:px-5">
      <div role="status">
        <Callout
          tone="warning"
          icon={KeyRound}
          action={<>
            {grant && left > 0 && (
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => void revokeAndLeave()}>{tx('İzni kapat')}</Button>
            )}
            <Button size="sm" variant="outline" onClick={leave}><LogOut className="size-4" />{' '}{tx('Çık')}</Button>
          </>}
        >
          {grant && left > 0
            ? tx('Platform yöneticisi olarak {0} şirketini görüntülüyorsunuz (salt okunur). Erişim izninin kalan süresi: {1}.', [name, remainingLabel(left)])
            : tx('{0} şirketi için etkin erişim izniniz yok ya da süresi doldu; şirket verisi açılmaz. Kiracılar ekranından gerekçeli izin açın.', [name])}
        </Callout>
      </div>
    </div>
  )
}
