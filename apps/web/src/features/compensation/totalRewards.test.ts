import { describe, expect, it } from 'vitest'
import { totalRewards } from './TotalRewards'
import type { Payslip } from '@/api/payroll'

const slip = (gross: number, net: number, sgkEmployer: number, unemploymentEmployer: number) =>
  ({ gross, net, sgkEmployer, unemploymentEmployer, currency: 'TRY' }) as Payslip

describe('totalRewards', () => {
  it('toplam = brüt + işveren payları + yan haklar; vergi = brüt − net', () => {
    const r = totalRewards([slip(100, 70, 15, 2), slip(100, 70, 15, 2)], 30)
    expect(r).toMatchObject({ net: 140, taxes: 60, employer: 34, benefits: 30, total: 264, months: 2 })
  })
})
