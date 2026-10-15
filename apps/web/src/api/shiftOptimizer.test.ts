import { describe, expect, it } from 'vitest'
import { applyBody, type ProposalDiffRow } from './shiftOptimizer'

const row = (kind: ProposalDiffRow['kind'], i: number): ProposalDiffRow => ({
  employeeId: `e${i}`, name: null, date: '2026-10-12', kind,
  currentAssignmentId: kind === 'add' ? null : `a${i}`, currentShiftId: kind === 'add' ? null : 's1', currentShift: null,
  proposedShiftId: kind === 'remove' ? null : 's2', proposedShift: null,
})

describe('öneri uygulama gövdesi', () => {
  it('yalnızca seçilen değişiklikler; aynı kalanlar atlanır', () => {
    const rows = [row('add', 1), row('change', 2), row('remove', 3), row('same', 4), row('add', 5)]
    const key = (r: ProposalDiffRow) => `${r.employeeId}|${r.date}`
    const b = applyBody(rows, new Set(['e1|2026-10-12', 'e2|2026-10-12', 'e3|2026-10-12', 'e4|2026-10-12']), key)
    expect(b.upserts).toEqual([{ employeeId: 'e1', date: '2026-10-12', shiftId: 's2' }, { employeeId: 'e2', date: '2026-10-12', shiftId: 's2' }])
    expect(b.removeAssignmentIds).toEqual(['a3'])
  })
})
