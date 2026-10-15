/**
 * Departman atama kuralları (`POST /employees/{id}/assignments`).
 *
 * Backend ile kararlaştırılan davranış (22.09.2026):
 *  - Yeni atamanın başlangıcı aktif atamanınkinden ÖNCE ise → 400. İki kayıt
 *    çakışır, veri bütünlüğü bozulur.
 *  - AYNI gün ise → yeni kayıt açılmaz, aktif atama güncellenir (departman +
 *    unvan). "Az önce taşıdım, şimdi tekrar taşıyorum" akışı hata vermez.
 *  - SONRA ise → aktif atama `effectiveFrom - 1 gün` ile kapanır, yenisi eklenir.
 *
 * Arayüzün iyimser güncellemesi ve mock backend aynı fonksiyonu kullanır;
 * böylece ekranda görünen ile sunucunun yazdığı birbirinden sapmaz.
 */

import type { Assignment } from '@/api/types'
import { tx } from '@/lib/i18n'

export interface AssignmentInput {
  departmentId: string
  positionTitle: string | null
  effectiveFrom: string
}

const dayBefore = (iso: string) => {
  const d = new Date(`${iso.slice(0, 10)}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() - 1)
  return d.toISOString().slice(0, 10)
}

/** Aktif atama: `effectiveTo` boş olan; birden fazlaysa en yeni başlayan. */
export function activeOf(assignments: Assignment[]): Assignment | null {
  const open = assignments.filter((a) => !a.effectiveTo)
  if (open.length === 0) return null
  return [...open].sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom))[0]
}

export type ApplyResult =
  | { ok: true; assignments: Assignment[]; row: Assignment; updated: boolean }
  | { ok: false; message: string }

export function applyAssignment(assignments: Assignment[], input: AssignmentInput, newId: string): ApplyResult {
  const from = input.effectiveFrom.slice(0, 10)
  const active = activeOf(assignments)
  const activeFrom = active?.effectiveFrom.slice(0, 10)

  if (active && activeFrom && from < activeFrom) {
    return {
      ok: false,
      message: tx('Başlangıç tarihi, mevcut atamanın başlangıcından ({0}) önce olamaz.', [activeFrom.split('-').reverse().join('.')]),
    }
  }

  if (active && from === activeFrom) {
    const row: Assignment = { ...active, departmentId: input.departmentId, positionTitle: input.positionTitle }
    return { ok: true, assignments: assignments.map((a) => (a.id === active.id ? row : a)), row, updated: true }
  }

  const row: Assignment = {
    id: newId,
    departmentId: input.departmentId,
    positionTitle: input.positionTitle,
    effectiveFrom: from,
    effectiveTo: null,
  }
  return {
    ok: true,
    assignments: [...assignments.map((a) => (a.effectiveTo ? a : { ...a, effectiveTo: dayBefore(from) })), row],
    row,
    updated: false,
  }
}
