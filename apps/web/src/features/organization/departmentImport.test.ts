import { describe, expect, it } from 'vitest'
import { planDepartmentImport } from './departmentImport'

const rows = (...r: [string, string?, string?][]) =>
  r.map(([name, parent = '', head = '']) => ({ 'Departman adı': name, 'Üst departman': parent, 'Departman başı e-posta': head }))

describe('planDepartmentImport', () => {
  it('üst departmanı dosyada sonra gelse de önce oluşturulacak şekilde sıralar', () => {
    const plan = planDepartmentImport(rows(['Mobil', 'Yazılım'], ['Yazılım', 'Teknoloji'], ['Teknoloji']), new Set(), new Map())
    expect(plan.map((r) => r.data.name)).toEqual(['Teknoloji', 'Yazılım', 'Mobil'])
    expect(plan.every((r) => !r.error)).toBe(true)
  })

  it('mevcut departmana bağlanabilir, aynı adı ikinci kez oluşturmaz', () => {
    const plan = planDepartmentImport(rows(['Satış', 'Ticari'], ['Ticari']), new Set(['ticari']), new Map())
    expect(plan.find((r) => r.data.name === 'Satış')?.error).toBeUndefined()
    expect(plan.find((r) => r.data.name === 'Ticari')?.error).toBeTruthy()
  })

  it('döngüyü, bilinmeyen üst departmanı ve kendine bağlılığı yakalar', () => {
    const plan = planDepartmentImport(rows(['A', 'B'], ['B', 'A'], ['C', 'Yok'], ['D', 'd']), new Set(), new Map())
    const err = (n: string) => plan.find((r) => r.data.name === n)?.error
    expect(err('A')).toBeTruthy()
    expect(err('B')).toBeTruthy()
    expect(err('C')).toContain('Yok')
    expect(err('D')).toBeTruthy()
  })

  it('departman başını e-postayla eşler; bulunamazsa satır hatalı', () => {
    const plan = planDepartmentImport(rows(['İK', '', 'Elif@Sirket.com'], ['Hukuk', '', 'yok@sirket.com']), new Set(), new Map([['elif@sirket.com', 'e1']]))
    expect(plan[0].data.headId).toBe('e1')
    expect(plan[1].error).toBeTruthy()
  })

  it('üst satırı hatalıysa alt satır da atlanır', () => {
    const plan = planDepartmentImport(rows(['Alt', 'Üst'], ['Üst', '', 'yok@x.com']), new Set(), new Map())
    expect(plan.find((r) => r.data.name === 'Alt')?.error).toBeTruthy()
  })
})
