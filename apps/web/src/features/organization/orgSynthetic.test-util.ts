/**
 * Testler için sentetik organizasyon (gerçek veri yok): `depts` departmanlı, `people`
 * kişilik deterministik ağaç. Yalnızca birim testleri kullanır.
 */
import type { Department, Employee } from '@/api/types'

/** Basit LCG: aynı tohum → aynı organizasyon. */
function rng(seed: number) {
  let s = seed >>> 0
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0
    return s / 2 ** 32
  }
}

const uuid = (prefix: number, n: number) =>
  `${prefix.toString(16).padStart(8, '0')}-0000-4000-8000-${n.toString(16).padStart(12, '0')}`

export function syntheticOrg(depts: number, people: number, seed = 42): { departments: Department[]; employees: Employee[] } {
  const r = rng(seed)
  const departments: Department[] = []
  for (let i = 0; i < depts; i++) {
    // İlk 6 departman kök; diğerleri kendinden önceki rastgele bir departmanın altında (derinlik doğal oluşur).
    const parent = i < 6 ? null : departments[Math.floor(r() * i)].id
    departments.push({ id: uuid(0xd0000000 + i, i), name: `Departman ${i}`, companyId: 'c1', parentDepartmentId: parent, headEmployeeId: null })
  }
  const employees: Employee[] = []
  for (let i = 0; i < people; i++) {
    const d = departments[Math.floor(r() * depts)]
    const year = 2005 + Math.floor(r() * 20)
    employees.push({
      id: uuid(0xe0000000 + i, i),
      firstName: `Ad${i}`,
      lastName: `Soyad${i}`,
      email: `p${i}@example.test`,
      hireDate: `${year}-03-01`,
      status: 0 as Employee['status'],
      assignments: [{ id: uuid(0xa0000000 + i, i), departmentId: d.id, positionTitle: 'Uzman', effectiveFrom: `${year}-03-01`, effectiveTo: null }],
    })
  }
  return { departments, employees }
}
