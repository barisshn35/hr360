/**
 * Organizasyon şeması zaman kaydırıcısı (adres: `tarih=YYYY-AA-GG`) — saf hesap.
 *
 * Veri zaman makinesinden gelir (`GET /api/governance/time-machine?date=`): o güne ait
 * görevlendirmeler (EffectiveFrom/To) ve o tarihte var olan departmanlar. Bu dosya yanıtı
 * `buildChart`ın beklediği biçime çevirir ve iki tarih arasındaki farkı (değişim animasyonu
 * için) hesaplar. Departman geçmişi tutulmadığından silinmiş departmanlar görünmez; üst
 * departman bağı bugünkü hâliyle kullanılır.
 */

import type { Department, Employee } from '@/api/types'
import type { TimeSnapshot } from '@/api/governance'
import type { ChartModel } from './orgChartModel'

export const DATE_PARAM = 'tarih'

const ISO_RE = /^\d{4}-\d{2}-\d{2}$/

const pad = (n: number) => String(n).padStart(2, '0')
const iso = (y: number, m: number, d: number) => `${y}-${pad(m)}-${pad(d)}`

/** Adresteki tarihi doğrular: geçerli bir gün ve bugünden önce olmalı (bugün = canlı görünüm). */
export function parseDateParam(v: string | null, today: string): string | null {
  if (!v || !ISO_RE.test(v)) return null
  const [y, m, d] = v.split('-').map(Number)
  const t = new Date(Date.UTC(y, m - 1, d))
  if (t.getUTCFullYear() !== y || t.getUTCMonth() !== m - 1 || t.getUTCDate() !== d) return null
  return v < today ? v : null
}

/**
 * Kaydırıcı durakları: son `months` ayın ay sonları (eskiden yeniye) + bugün (son durak).
 * Bu ayın sonu henüz gelmediği için bu ay yalnızca "bugün" ile temsil edilir.
 */
export function monthStops(today: string, months = 24): string[] {
  const [y, m] = today.split('-').map(Number)
  const out: string[] = []
  for (let k = months; k >= 1; k--) {
    // k ay önceki ayın son günü: (m - k + 1). ayın 0. günü.
    const d = new Date(Date.UTC(y, m - k, 0))
    out.push(iso(d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate()))
  }
  out.push(today)
  return out
}

/** Verilen tarihe en yakın durak (adresteki serbest tarih kaydırıcıda konumlansın). */
export function nearestStop(stops: string[], date: string | null): number {
  if (!date) return stops.length - 1
  let best = stops.length - 1
  let bestDiff = Infinity
  stops.forEach((s, i) => {
    const diff = Math.abs(Date.parse(s) - Date.parse(date))
    if (diff < bestDiff) {
      bestDiff = diff
      best = i
    }
  })
  return best
}

/** O tarihte var olan departmanlar (yanıtta liste yoksa hepsi). Kökü kaybolan alt departman köke çıkar. */
export function departmentsAt(departments: Department[], snapshot: Pick<TimeSnapshot, 'departmentIds'>): Department[] {
  if (!snapshot.departmentIds) return departments
  const keep = new Set(snapshot.departmentIds)
  return departments.filter((d) => keep.has(d.id))
}

/**
 * Anlık görüntüdeki kişileri `buildChart` girdisine çevirir: her kişinin o tarihteki tek
 * görevlendirmesi aktif atama olur. Atanmamışlar (departmentId null) atamasız kalır.
 */
export function snapshotEmployees(snapshot: Pick<TimeSnapshot, 'departments' | 'date'>): Employee[] {
  const out: Employee[] = []
  for (const g of snapshot.departments) {
    for (const p of g.people) {
      out.push({
        id: p.employeeId,
        firstName: p.name,
        lastName: '',
        email: '',
        hireDate: p.hireDate,
        status: 0 as Employee['status'],
        assignments: g.departmentId
          ? [{ id: `tm-${p.employeeId}`, departmentId: g.departmentId, positionTitle: p.position, effectiveFrom: p.hireDate, effectiveTo: null }]
          : [],
      })
    }
  }
  return out
}

export interface TimeDelta {
  /** Kişi sayısı (yalnızca kendi üyeleri) değişen departmanlar: önce → sonra. */
  changed: Map<string, { before: number; after: number }>
  /** Önceki görünümde olmayıp yenide olan departmanlar. */
  added: Set<string>
  /** Önceki görünümde olup yenide olmayan departmanlar. */
  removed: Set<string>
  /** Departmanı değişen kişi sayısı (atanmamış ↔ departman dahil değil). */
  moved: number
}

/** İki model (ör. iki tarih) arasındaki fark — kaydırıcıda değişen düğümler kısa süre vurgulanır. */
export function timeDelta(prev: ChartModel, next: ChartModel): TimeDelta {
  const changed = new Map<string, { before: number; after: number }>()
  const added = new Set<string>()
  const removed = new Set<string>()
  for (const [id, n] of next.byId) {
    const p = prev.byId.get(id)
    if (!p) {
      added.add(id)
      continue
    }
    if (p.members.length !== n.members.length) changed.set(id, { before: p.members.length, after: n.members.length })
  }
  for (const id of prev.byId.keys()) if (!next.byId.has(id)) removed.add(id)
  let moved = 0
  for (const [emp, d] of next.deptOf) {
    const before = prev.deptOf.get(emp)
    if (before && before !== d) moved++
  }
  return { changed, added, removed, moved }
}
