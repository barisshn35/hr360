import { describe, expect, it } from 'vitest'
import { hasPlatformGrant, invalidatePlatformGrants, needsPlatformGrant, platformTenantFor } from './platformAccess'

describe('platformTenantFor', () => {
  it('platform yöneticisi değilse başlık gönderilmez', () => {
    expect(platformTenantFor(['tenant-admin'], null, 'demo')).toBeNull()
  })
  it('önce jetondaki organizasyon, yoksa seçili kiracı', () => {
    expect(platformTenantFor(['platform-admin'], 'acme', 'demo')).toBe('acme')
    expect(platformTenantFor(['platform-admin'], null, 'Demo')).toBe('demo')
  })
  it('kiracı seçilmemişse ya da biçim geçersizse null', () => {
    expect(platformTenantFor(['platform-admin'], null, null)).toBeNull()
    expect(platformTenantFor(['platform-admin'], null, 'demo;x')).toBeNull()
  })
})

describe('needsPlatformGrant', () => {
  it('kiracı verisi uçları izin ister; tenant uçları istemez', () => {
    expect(needsPlatformGrant('/api/employee/employees/directory')).toBe(true)
    expect(needsPlatformGrant('/api/compensation/compensation/payroll/periods?year=2026')).toBe(true)
    expect(needsPlatformGrant('/api/tenant/tenants')).toBe(false)
    expect(needsPlatformGrant('/ml/health')).toBe(false)
    expect(needsPlatformGrant('/api/governance/model/versions')).toBe(false)
    expect(needsPlatformGrant('/api/governance/model/settings')).toBe(true)
    expect(needsPlatformGrant('/api/notification/notifications/ui-prefs')).toBe(false)
    expect(needsPlatformGrant('/api/notification/notifications/ui-prefs/dashboard')).toBe(false)
    expect(needsPlatformGrant('/api/notification/notifications/unread-count')).toBe(true)
  })
})

describe('hasPlatformGrant', () => {
  it('yalnızca etkin ve süresi dolmamış izni sayar, listeyi önbellekler', async () => {
    invalidatePlatformGrants()
    let calls = 0
    const future = new Date(Date.now() + 3600_000).toISOString()
    const load = async () => {
      calls++
      return [{ tenantSlug: 'demo', active: true, expiresAt: future }, { tenantSlug: 'acme', active: false, expiresAt: future }]
    }
    expect(await hasPlatformGrant('demo', load)).toBe(true)
    expect(await hasPlatformGrant('acme', load)).toBe(false)
    expect(calls).toBe(1)
    invalidatePlatformGrants()
  })
})
