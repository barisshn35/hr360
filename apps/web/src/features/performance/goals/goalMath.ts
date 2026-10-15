/**
 * Hedef ilerlemesi — backend'in hesabıyla aynı:
 *   sayısal hedef  → current / target, %100'de kırpılır (aşım puana yansımaz)
 *   sayısal değil  → durumdan: Gerçekleşti %100, Gerçekleşmedi %0, Devam ediyor %50
 *   iptal          → hesaba girmez
 */

import type { Goal, GoalStatus } from '@/api/performance'
import { appLocale, pct } from '@/lib/i18n'

export const STATUS_PROGRESS: Partial<Record<GoalStatus, number>> = { Achieved: 100, Missed: 0, Active: 50, Draft: 0 }

export interface GoalProgress {
  /** Puana yansıyan (0–100). İptalde null. */
  pct: number | null
  /** Kırpılmamış gerçekleşme — aşımı göstermek için. */
  raw: number | null
  source: 'numeric' | 'status' | 'cancelled'
}

export function progressOf(g: Pick<Goal, 'targetValue' | 'currentValue' | 'status'>): GoalProgress {
  if (g.status === 'Cancelled') return { pct: null, raw: null, source: 'cancelled' }
  if (g.targetValue !== null && g.targetValue > 0) {
    const raw = ((g.currentValue ?? 0) / g.targetValue) * 100
    return { pct: Math.max(0, Math.min(100, raw)), raw, source: 'numeric' }
  }
  const p = STATUS_PROGRESS[g.status] ?? 0
  return { pct: p, raw: p, source: 'status' }
}

/** Hedef ayağı puanı: ilerlemelerin ağırlıklı ortalaması. Hedef yoksa null. */
export function goalSideScore(goals: Goal[]): number | null {
  const counted = goals.filter((g) => g.status !== 'Cancelled')
  const w = counted.reduce((a, g) => a + g.weight, 0)
  if (!w) return null
  return counted.reduce((a, g) => a + (progressOf(g).pct ?? 0) * g.weight, 0) / w
}

const num = new Intl.NumberFormat(appLocale, { maximumFractionDigits: 2 })

export function formatGoalValue(value: number | null, unit: string | null): string {
  if (value === null) return '—'
  if (unit === '₺') return `${num.format(value)} ₺`
  if (unit === '%') return pct(num.format(value))
  return unit ? `${num.format(value)} ${unit}` : num.format(value)
}
