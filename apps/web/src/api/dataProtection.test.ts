import { describe, expect, it } from 'vitest'
import { payrollCapableCount, removableAccess, reviewProgress } from './dataProtection'

describe('veri koruma yardımcıları', () => {
  it('bordro yetkilisi sayısı: giriş hesabı açık İK/şirket yöneticileri', () => {
    expect(payrollCapableCount([
      { hasLoginAccess: true, roles: ['hr-admin'] },
      { hasLoginAccess: true, roles: ['tenant-admin', 'hr-admin'] },
      { hasLoginAccess: false, roles: ['hr-admin'] },
      { hasLoginAccess: true, roles: ['manager'] },
    ])).toBe(2)
  })

  it('bordro yetkilisi sayısı çalışan kaydı olmayan yönetici hesabını da sayar', () => {
    expect(payrollCapableCount([
      { hasLoginAccess: true, roles: ['hr-admin'] },
      { hasLoginAccess: true, roles: ['tenant-admin'], hasEmployeeRecord: false } as { hasLoginAccess: boolean; roles: string[] },
    ])).toBe(2)
  })

  it('kaldırma seçenekleri temel çalışan rolünü içermez', () => {
    expect(removableAccess({ roles: ['employee', 'manager'], permissions: ['compensation:view'] })).toEqual(['manager', 'compensation:view'])
  })

  it('ilerleme yüzdesi', () => {
    expect(reviewProgress({ total: 0, decided: 0 })).toBe(0)
    expect(reviewProgress({ total: 3, decided: 1 })).toBe(33)
  })
})
