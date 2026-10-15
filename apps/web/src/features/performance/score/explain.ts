/**
 * Puan dökümünün açıklanabilir hâli: her parçanın nihai puana KATKISI.
 *
 *   nihai = hedef × hedef payı + metrik × metrik payı
 *   metrik = Σ kategori × (kategori ağırlığı / sayılan kategori toplamı)
 *   kategori = Σ metrik × (metrik ağırlığı / puanlanan metrik toplamı)
 *
 * Katkılar toplandığında nihai puanı verir; ekranda "+30,6 puan" gibi
 * gösterilir. Bir ayak boşsa diğeri tam ağırlıkla sayılır.
 *
 * Paylar önce puan yanıtının kendisinden okunur (backend, puanı hesapladığı
 * ayar sürümünün paylarını gönderir). Yanıtta yoksa ayar geçmişinden, o da
 * yoksa puanlardan geri hesaplanır.
 */

import type { CategoryBreakdown, GoalBreakdown, MetricBreakdown, ScoreResult, ScoringConfig } from '@/api/performance'

export interface ExplainedMetric extends MetricBreakdown {
  share: number
  contribution: number | null
}

export interface ExplainedCategory extends CategoryBreakdown {
  counted: boolean
  share: number
  contribution: number | null
  metrics: ExplainedMetric[]
}

export interface ExplainedGoal extends GoalBreakdown {
  share: number
  contribution: number | null
}

export interface Explained {
  hasGoals: boolean
  hasMetrics: boolean
  /** Ayarın payları (0–100). */
  goalPct: number
  metricPct: number
  /** Boş ayak düşüldükten sonra uygulanan paylar. */
  effGoalPct: number
  effMetricPct: number
  goalContribution: number
  metricContribution: number
  categories: ExplainedCategory[]
  goals: ExplainedGoal[]
  /** Paylar ne yanıtta ne ayar geçmişinde bulunamadı; puanlardan çıkarıldı. */
  inferred: boolean
}

export function explain(s: ScoreResult, cfg: Pick<ScoringConfig, 'goalWeightPercent' | 'metricWeightPercent'> | null): Explained {
  const goalsW = s.breakdown.goals.reduce((a, g) => a + g.weight, 0)
  const hasGoals = s.breakdown.goals.length > 0 && goalsW > 0
  const counted = s.breakdown.categories.filter((c) => c.weight > 0 && c.score !== null)
  const hasMetrics = s.metricScore !== null && counted.length > 0

  let goalPct = s.goalWeightPercent ?? cfg?.goalWeightPercent ?? null
  let inferred = false
  if (goalPct === null) {
    // Sürüm bilinmiyorsa: nihai = g·p + m·(1−p) → p = (nihai − m) / (g − m)
    if (hasGoals && hasMetrics && s.score !== null && s.goalScore !== null && s.metricScore !== null && s.goalScore !== s.metricScore) {
      goalPct = Math.max(0, Math.min(100, ((s.score - s.metricScore) / (s.goalScore - s.metricScore)) * 100))
    } else goalPct = 40
    inferred = true
  }
  const metricPct = s.goalWeightPercent !== null ? (s.metricWeightPercent ?? 100 - goalPct) : (cfg?.metricWeightPercent ?? 100 - goalPct)
  const effGoalPct = hasGoals && hasMetrics ? goalPct : hasGoals ? 100 : 0
  const effMetricPct = hasGoals && hasMetrics ? metricPct : hasMetrics ? 100 : 0

  const catTotal = counted.reduce((a, c) => a + c.weight, 0)
  const categories: ExplainedCategory[] = s.breakdown.categories.map((c) => {
    const isCounted = c.weight > 0 && c.score !== null
    const share = isCounted && catTotal ? c.weight / catTotal : 0
    const scored = c.metrics.filter((m) => m.normalizedScore !== null)
    const mTotal = scored.reduce((a, m) => a + m.weight, 0)
    return {
      ...c,
      counted: isCounted,
      share: share * 100,
      contribution: isCounted ? (c.score as number) * share * (effMetricPct / 100) : null,
      metrics: c.metrics.map((m) => {
        const ms = m.normalizedScore !== null && mTotal ? m.weight / mTotal : 0
        return {
          ...m,
          share: ms * 100,
          contribution: isCounted && m.normalizedScore !== null ? m.normalizedScore * ms * share * (effMetricPct / 100) : null,
        }
      }),
    }
  })

  const goals: ExplainedGoal[] = s.breakdown.goals.map((g) => {
    const share = goalsW ? g.weight / goalsW : 0
    return { ...g, share: share * 100, contribution: hasGoals && g.achievement !== null ? g.achievement * share * (effGoalPct / 100) : null }
  })

  return {
    hasGoals,
    hasMetrics,
    goalPct,
    metricPct,
    effGoalPct,
    effMetricPct,
    goalContribution: hasGoals ? (s.goalScore ?? 0) * (effGoalPct / 100) : 0,
    metricContribution: hasMetrics ? (s.metricScore ?? 0) * (effMetricPct / 100) : 0,
    categories,
    goals,
    inferred,
  }
}
