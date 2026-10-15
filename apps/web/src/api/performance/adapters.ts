/**
 * API sınırındaki toleranslı okuyucular.
 *
 * Canlı örneği elimizde olan yanıtlar (metrik, puan dökümü, çalışan önerisi)
 * zaten `types.ts` ile birebir aynı; bunlar yine de buradan geçer ki eksik
 * bir alan ekranı çökertmesin. Örneği olmayan yanıtlarda olası alan adı
 * varyantları (`employeeScore` / `employee`, `items` / düz dizi…) tek yerde
 * karşılanır. Canlıda farklı bir ad çıkarsa değişiklik yalnızca buraya düşer.
 */

import type {
  CategoryBreakdown,
  CycleAnalytics,
  CycleCompare,
  CycleMovement,
  CycleReadiness,
  CycleScoreEntry,
  DistributionBucket,
  EmployeeAnalytics,
  EmployeeCycleHistory,
  Feedback,
  FeedbackSummary,
  Goal,
  GoalBreakdown,
  MemberScore,
  Metric,
  MetricBreakdown,
  MlLayer,
  MyAnalytics,
  PersonRef,
  Recommendation,
  RecommendationList,
  Review,
  ReviewCycle,
  ScoreResult,
  ScoringConfig,
  SeriesPoint,
  Team,
  TeamAnalytics,
  TeamComparison,
  TeamMember,
  Thresholds,
  VsTeamAnalytics,
} from './types'

type Raw = Record<string, unknown>

/* --------------------------------- ilkel okuyucular ---------------------------- */

const isObj = (v: unknown): v is Raw => typeof v === 'object' && v !== null && !Array.isArray(v)
const obj = (v: unknown): Raw => (isObj(v) ? v : {})
const arr = (v: unknown): unknown[] => (Array.isArray(v) ? v : [])

/** İlk tanımlı alanı döndürür — ad varyantları için. */
function pick(r: Raw, ...keys: string[]): unknown {
  for (const k of keys) if (r[k] !== undefined) return r[k]
  return undefined
}

function num(v: unknown): number | null {
  if (typeof v === 'number' && Number.isFinite(v)) return v
  if (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v))) return Number(v)
  return null
}
const num0 = (v: unknown): number => num(v) ?? 0
const str = (v: unknown): string | null => (typeof v === 'string' && v !== '' ? v : null)
const str0 = (v: unknown): string => (typeof v === 'string' ? v : '')
const bool = (v: unknown): boolean => v === true || v === 'true'

/* ------------------------------------ ortak ------------------------------------ */

export function toPerson(v: unknown): PersonRef {
  const r = obj(v)
  return {
    employeeId: str0(pick(r, 'employeeId', 'id')),
    name: str0(pick(r, 'name', 'fullName', 'employeeName')),
  }
}

function toThresholds(v: unknown): Thresholds | null {
  if (!isObj(v)) return null
  return {
    critical: num0(pick(v, 'critical', 'criticalThreshold')),
    improvement: num0(pick(v, 'improvement', 'improvementThreshold')),
    recognition: num0(pick(v, 'recognition', 'recognitionThreshold')),
    promotion: num0(pick(v, 'promotion', 'promotionThreshold')),
  }
}

/* ----------------------------------- metrikler ---------------------------------- */

export function toMetric(v: unknown): Metric {
  const r = obj(v)
  const scale = (str(r.scale) ?? 'OneToFive') as Metric['scale']
  const fallback = scale === 'Percentage' ? { min: 0, max: 100 } : scale === 'OneToTen' ? { min: 1, max: 10 } : { min: 1, max: 5 }
  const range = obj(r.range)
  return {
    id: str0(r.id),
    code: str0(r.code),
    name: str0(r.name),
    description: str(r.description),
    category: (str(r.category) ?? 'Custom') as Metric['category'],
    scale,
    weight: num0(r.weight),
    departmentId: str(r.departmentId),
    isRequired: bool(r.isRequired),
    // Eski adıyla gelirse (`isArchived`) de doğru okunur.
    isActive: r.isActive !== undefined ? bool(r.isActive) : !bool(r.isArchived),
    sortOrder: num0(r.sortOrder),
    range: {
      min: num(range.min) ?? fallback.min,
      max: num(range.max) ?? fallback.max,
    },
    usageCount: num(r.usageCount) ?? undefined,
  }
}

/* -------------------------------- puanlama ayarı ------------------------------- */

export function toScoringConfig(v: unknown): ScoringConfig {
  const r = obj(v)
  return {
    id: str(r.id) ?? undefined,
    version: num0(r.version),
    createdAt: str(pick(r, 'createdAt', 'effectiveFrom')),
    createdByName: str(pick(r, 'createdByName', 'createdBy')),
    isCurrent: r.isCurrent !== undefined ? bool(r.isCurrent) : r.isActive !== undefined ? bool(r.isActive) : undefined,
    goalWeightPercent: num0(r.goalWeightPercent),
    metricWeightPercent: num0(r.metricWeightPercent),
    technicalWeight: num0(r.technicalWeight),
    behavioralWeight: num0(r.behavioralWeight),
    leadershipWeight: num0(r.leadershipWeight),
    deliveryWeight: num0(r.deliveryWeight),
    customWeight: num0(r.customWeight),
    selfReviewWeight: num0(r.selfReviewWeight),
    managerReviewWeight: num0(r.managerReviewWeight),
    teamLeadReviewWeight: num0(r.teamLeadReviewWeight),
    peerReviewWeight: num0(r.peerReviewWeight),
    upwardReviewWeight: num0(r.upwardReviewWeight),
    minReviewsForValidScore: num0(r.minReviewsForValidScore),
    allowSelfOnlyScore: bool(r.allowSelfOnlyScore),
    criticalThreshold: num0(r.criticalThreshold),
    improvementThreshold: num0(r.improvementThreshold),
    recognitionThreshold: num0(r.recognitionThreshold),
    promotionThreshold: num0(r.promotionThreshold),
    promotionConsecutivePeriods: num0(r.promotionConsecutivePeriods),
  }
}

/* ------------------------------------ ekipler ----------------------------------- */

export function toTeamMember(v: unknown): TeamMember {
  const r = obj(v)
  return {
    id: str0(pick(r, 'id', 'memberId')),
    employeeId: str0(r.employeeId),
    employeeName: str(pick(r, 'employeeName', 'name')),
    roleInTeam: str(r.roleInTeam),
    joinedOn: str0(r.joinedOn),
    leftOn: str(r.leftOn),
    isLead: bool(r.isLead),
  }
}

export function toTeam(v: unknown): Team {
  const r = obj(v)
  const members = Array.isArray(r.members) ? r.members.map(toTeamMember) : undefined
  const lead = pick(r, 'leadEmployeeId', 'leadId')
  return {
    // teams/by-employee üyelik kaydı döner ({teamId, teamName, isLead...}); ekip
    // nesnesi gibi okunabilsin diye bu adlar da kabul edilir.
    id: str0(pick(r, 'id', 'teamId')),
    name: str0(pick(r, 'name', 'teamName')),
    description: str(r.description),
    departmentId: str0(r.departmentId),
    leadEmployeeId: str(lead) ?? (isObj(r.lead) ? str(pick(r.lead, 'employeeId', 'id')) : null),
    isActive: r.isActive === undefined ? true : bool(r.isActive),
    memberCount: num(r.memberCount) ?? members?.filter((m) => !m.leftOn).length ?? 0,
    members,
  }
}

/* ----------------------------------- dönemler ----------------------------------- */

export function toCycle(v: unknown): ReviewCycle {
  const r = obj(v)
  return {
    id: str0(r.id),
    name: str0(r.name),
    year: num0(r.year),
    period: (str(r.period) ?? 'Annual') as ReviewCycle['period'],
    status: (str(r.status) ?? 'Planned') as ReviewCycle['status'],
    startDate: str0(r.startDate),
    endDate: str0(r.endDate),
    finalizedEmployeeCount: num(r.finalizedEmployeeCount),
    closedAt: str(r.closedAt),
  }
}

export function toReadiness(v: unknown, cycleId: string): CycleReadiness {
  const r = obj(v)
  return {
    cycleId: str(r.cycleId) ?? cycleId,
    readyCount: num0(r.readyCount),
    provisionalCount: num0(r.provisionalCount),
    pendingReviewTotal: num0(r.pendingReviewTotal),
    employees: arr(r.employees).map((e) => {
      const x = obj(e)
      return {
        employeeId: str0(x.employeeId),
        name: str(pick(x, 'name', 'employeeName')),
        isProvisional: x.isProvisional === undefined ? str(x.reason) !== null : bool(x.isProvisional),
        reason: str(x.reason),
        reviewCount: num(x.reviewCount) ?? undefined,
        pendingReviews: num(pick(x, 'pendingReviews', 'pendingReviewCount')) ?? undefined,
      }
    }),
  }
}

/* ----------------------------------- hedefler ----------------------------------- */

export function toGoal(v: unknown): Goal {
  const r = obj(v)
  return {
    id: str0(r.id),
    cycleId: str0(r.cycleId),
    employeeId: str0(r.employeeId),
    title: str0(r.title),
    description: str(r.description),
    weight: num0(r.weight),
    targetValue: num(r.targetValue),
    currentValue: num(r.currentValue),
    unit: str(r.unit),
    status: (str(r.status) ?? 'Draft') as Goal['status'],
    dueDate: str(r.dueDate)?.slice(0, 10) ?? null,
  }
}

/* -------------------------------- değerlendirme --------------------------------- */

export function toReview(v: unknown): Review {
  const r = obj(v)
  const submittedAt = str(r.submittedAt)
  const status = str(r.status)
  return {
    id: str0(r.id),
    cycleId: str0(r.cycleId),
    employeeId: str0(r.employeeId),
    reviewerEmployeeId: str(r.reviewerEmployeeId),
    type: (str(pick(r, 'type', 'raterType')) ?? 'Manager') as Review['type'],
    submittedAt,
    isSubmitted:
      r.isSubmitted !== undefined ? bool(r.isSubmitted) : submittedAt !== null || status === 'Submitted',
    strengths: str(r.strengths),
    improvements: str(r.improvements),
    comments: str(r.comments),
    scores: arr(r.scores).map((s) => {
      const x = obj(s)
      return { metricId: str0(x.metricId), value: num0(x.value), comment: str(x.comment) ?? undefined }
    }),
    createdAt: str(r.createdAt),
  }
}

/* ---------------------------------- puan dökümü -------------------------------- */

function toMetricBreakdown(v: unknown): MetricBreakdown {
  const r = obj(v)
  return {
    metricId: str0(r.metricId),
    code: str0(r.code),
    name: str0(r.name),
    normalizedScore: num(pick(r, 'normalizedScore', 'score')),
    weight: num0(r.weight),
    reviewCount: num0(r.reviewCount),
  }
}

function toCategoryBreakdown(v: unknown): CategoryBreakdown {
  const r = obj(v)
  return {
    category: (str(r.category) ?? 'Custom') as CategoryBreakdown['category'],
    score: num(r.score),
    weight: num0(r.weight),
    metrics: arr(r.metrics).map(toMetricBreakdown),
  }
}

function toGoalBreakdown(v: unknown): GoalBreakdown {
  const r = obj(v)
  return {
    goalId: str0(pick(r, 'goalId', 'id')),
    title: str0(r.title),
    weight: num0(r.weight),
    achievement: num(pick(r, 'achievement', 'progress', 'progressPercent', 'normalizedScore', 'score')),
    status: (str(r.status) ?? null) as GoalBreakdown['status'],
  }
}

export function toScore(v: unknown): ScoreResult {
  const r = obj(v)
  const b = obj(r.breakdown)
  return {
    score: num(r.score),
    goalScore: num(r.goalScore),
    metricScore: num(r.metricScore),
    isProvisional: bool(r.isProvisional),
    provisionalReason: str(r.provisionalReason),
    reviewCount: num0(r.reviewCount),
    configVersion: num0(r.configVersion),
    goalWeightPercent: num(r.goalWeightPercent),
    metricWeightPercent: num(r.metricWeightPercent),
    breakdown: {
      categories: arr(b.categories).map(toCategoryBreakdown),
      goals: arr(b.goals).map(toGoalBreakdown),
    },
  }
}

/* --------------------------------- sürekli izleme ------------------------------ */

function toSeriesPoint(v: unknown): SeriesPoint {
  const r = obj(v)
  return {
    bucket: str0(pick(r, 'bucket', 'label')),
    score: num(pick(r, 'score', 'value')),
    isProvisional: bool(r.isProvisional),
    reviewCount: num(r.reviewCount) ?? undefined,
  }
}

export function toEmployeeAnalytics(v: unknown): EmployeeAnalytics {
  const r = obj(v)
  return {
    employeeId: str0(r.employeeId),
    period: (str(r.period) ?? 'quarter') as EmployeeAnalytics['period'],
    periodLabel: str0(r.periodLabel),
    series: arr(r.series).map(toSeriesPoint),
    current: num(pick(r, 'current', 'currentScore')),
    change: num(r.change),
    trend: str(r.trend),
    trendLabel: str(r.trendLabel),
  }
}

export function toVsTeam(v: unknown): VsTeamAnalytics {
  const r = obj(v)
  return {
    employeeId: str0(r.employeeId),
    teamId: str0(r.teamId),
    teamName: str(r.teamName),
    periodLabel: str0(r.periodLabel),
    series: arr(r.series).map((p) => {
      const x = obj(p)
      return {
        bucket: str0(pick(x, 'bucket', 'label')),
        employee: num(pick(x, 'employee', 'employeeScore', 'score')),
        team: num(pick(x, 'team', 'teamAverage', 'teamScore')),
        isProvisional: bool(x.isProvisional),
      }
    }),
  }
}

function toDistribution(v: unknown): DistributionBucket[] {
  return arr(v).map((b) => {
    const x = obj(b)
    const from = num0(x.from)
    const to = num0(x.to)
    return { label: str(x.label) ?? `${from}–${to}`, from, to, count: num0(x.count) }
  })
}

function toMemberScore(v: unknown): MemberScore {
  const r = obj(v)
  return {
    employeeId: str0(r.employeeId),
    name: str(pick(r, 'name', 'employeeName')),
    score: num0(r.score),
    isProvisional: bool(r.isProvisional),
    rank: num(r.rank),
  }
}

function toComparison(v: unknown): TeamComparison {
  const r = obj(v)
  return {
    average: num(r.average),
    median: num(r.median),
    spread: num(r.spread),
    spreadNote: str(r.spreadNote),
  }
}

export function toTeamAnalytics(v: unknown): TeamAnalytics {
  const r = obj(v)
  return {
    teamId: str0(r.teamId),
    teamName: str(r.teamName),
    periodLabel: str0(r.periodLabel),
    series: arr(r.series).map(toSeriesPoint),
    distribution: toDistribution(r.distribution),
    members: arr(r.members).map(toMemberScore),
    unscored: arr(r.unscored).map(toPerson),
    comparison: toComparison(r.comparison),
  }
}

/* -------------------------------- dönem sonuçları ------------------------------ */

function toCycleEntry(v: unknown): CycleScoreEntry {
  const r = obj(v)
  return {
    cycleId: str0(r.cycleId),
    cycleName: str0(pick(r, 'cycleName', 'name')),
    period: (str(r.period) ?? null) as CycleScoreEntry['period'],
    score: num(r.score),
    isProvisional: bool(r.isProvisional),
    isFinal: bool(pick(r, 'isFinal', 'isFinalized')),
    actionLabel: str(r.actionLabel),
  }
}

export function toCycleHistory(v: unknown): EmployeeCycleHistory {
  const r = obj(v)
  return {
    employeeId: str0(r.employeeId),
    year: num(r.year),
    cycles: arr(r.cycles).map(toCycleEntry),
  }
}

export function toCycleAnalytics(v: unknown): CycleAnalytics {
  const r = obj(v)
  return {
    cycleId: str0(r.cycleId),
    cycleName: str0(r.cycleName),
    teamId: str(r.teamId),
    distribution: toDistribution(r.distribution),
    members: arr(r.members).map(toMemberScore),
    unscored: arr(r.unscored).map(toPerson),
    comparison: toComparison(r.comparison),
  }
}

function toMovement(v: unknown): CycleMovement {
  const r = obj(v)
  const from = num0(pick(r, 'from', 'previous', 'fromScore'))
  const to = num0(pick(r, 'to', 'current', 'toScore'))
  return {
    employeeId: str0(r.employeeId),
    name: str(pick(r, 'name', 'employeeName')),
    from,
    to,
    delta: num(pick(r, 'delta', 'change')) ?? to - from,
  }
}

export function toCycleCompare(v: unknown): CycleCompare {
  const r = obj(v)
  const rows = arr(pick(r, 'rows', 'employees')).map((row) => {
    const x = obj(row)
    const scores: Record<string, number | null> = {}
    for (const [k, s] of Object.entries(obj(x.scores))) scores[k] = num(s)
    return { employeeId: str0(x.employeeId), name: str(pick(x, 'name', 'employeeName')), scores }
  })
  const averages: Record<string, number | null> = {}
  for (const [k, s] of Object.entries(obj(r.averages))) averages[k] = num(s)
  return {
    cycles: arr(r.cycles).map((c) => {
      const x = obj(c)
      return { cycleId: str0(pick(x, 'cycleId', 'id')), cycleName: str0(pick(x, 'cycleName', 'name')) }
    }),
    rows,
    averages,
    improved: arr(r.improved).map(toMovement),
    declined: arr(r.declined).map(toMovement),
  }
}

/* --------------------------------- geri bildirim -------------------------------- */

export function toFeedback(v: unknown): Feedback {
  const r = obj(v)
  return {
    id: str0(r.id),
    from: toPerson(r.from),
    to: toPerson(r.to),
    reason: (str(r.reason) ?? 'Other') as Feedback['reason'],
    reasonDetail: str(r.reasonDetail),
    sentiment: (str(r.sentiment) ?? 'Neutral') as Feedback['sentiment'],
    body: str0(r.body),
    metricId: str(r.metricId),
    metricName: str(r.metricName),
    visibleToEmployee: r.visibleToEmployee === undefined ? true : bool(r.visibleToEmployee),
    isRead: bool(r.isRead),
    readAt: str(r.readAt),
    createdAt: str0(r.createdAt),
  }
}

export function toFeedbackSummary(v: unknown): FeedbackSummary {
  const r = obj(v)
  return {
    total: num0(r.total),
    unread: num0(pick(r, 'unread', 'unreadCount')),
    bySentiment: obj(r.bySentiment) as FeedbackSummary['bySentiment'],
    byReason: obj(r.byReason) as FeedbackSummary['byReason'],
  }
}

export function toMyAnalytics(v: unknown): MyAnalytics {
  const r = obj(v)
  const feedback = arr(r.feedback).map(toFeedback)
  // Backend'in /analytics/me yaniti {employee, report, feedback, unreadFeedback}
  // seklinde - "employeeId" KOKTE degil, "report" nesnesinin ICINDE duruyor.
  // Once buraya dogrudan "r" veriliyordu, toEmployeeAnalytics'in aradigi
  // r.employeeId hicbir zaman bulunamiyordu (undefined kaliyordu) - bu da
  // useMyEmployeeId hook'unun HER ZAMAN bos donmesine, "Yeni degerlendirme"
  // gibi butonlarin JWT duzeltmesinden SONRA BILE devre disi gorunmesine
  // sebep oluyordu.
  return {
    ...toEmployeeAnalytics(r.report),
    feedback,
    unreadCount: num(pick(r, 'unreadCount', 'unreadFeedback')) ?? feedback.filter((f) => !f.isRead).length,
  }
}

/* ------------------------------------ öneriler ---------------------------------- */

function toMlLayer(v: unknown): MlLayer | null {
  if (!isObj(v)) return null
  return {
    note: str0(v.note),
    skipReason: str(v.skipReason),
    signals: arr(v.signals).map((s) => {
      const x = obj(s)
      return {
        code: str0(pick(x, 'code', 'key')),
        label: str0(x.label),
        value: str0(pick(x, 'value', 'text')),
        confidenceLabel: str(pick(x, 'confidenceLabel', 'confidence')),
      }
    }),
  }
}

export function toRecommendation(v: unknown): Recommendation {
  const r = obj(v)
  return {
    employeeId: str0(r.employeeId),
    employeeName: str(pick(r, 'employeeName', 'name')),
    action: str0(r.action),
    actionLabel: str0(r.actionLabel),
    confidence: Math.max(0, Math.min(1, num0(r.confidence))),
    summary: str0(r.summary),
    currentScore: num(pick(r, 'currentScore', 'score')),
    factors: arr(r.factors).map((f) => {
      const x = obj(f)
      return {
        code: str0(pick(x, 'code', 'key')),
        label: str0(x.label),
        contribution: num0(pick(x, 'contribution', 'impact')),
        explanation: str0(pick(x, 'explanation', 'detail')),
      }
    }),
    cautions: arr(r.cautions).filter((c): c is string => typeof c === 'string'),
    thresholds: toThresholds(r.thresholds),
    configVersion: num(r.configVersion),
    mlLayer: toMlLayer(pick(r, 'mlLayer', 'ml')),
  }
}

/** Liste düz dizi de gelse, zarflı da gelse aynı şekle çevrilir. */
export function toRecommendationList(v: unknown): RecommendationList {
  if (Array.isArray(v)) {
    const items = v.map(toRecommendation)
    return {
      configVersion: items.find((i) => i.configVersion !== null)?.configVersion ?? null,
      thresholds: items.find((i) => i.thresholds)?.thresholds ?? null,
      promotionConsecutivePeriods: null,
      items,
      mlLayer: null,
    }
  }
  const r = obj(v)
  const items = arr(pick(r, 'items', 'recommendations')).map(toRecommendation)
  const ml = toMlLayer(r.mlLayer)
  return {
    configVersion: num(r.configVersion) ?? items.find((i) => i.configVersion !== null)?.configVersion ?? null,
    thresholds: toThresholds(r.thresholds) ?? items.find((i) => i.thresholds)?.thresholds ?? null,
    promotionConsecutivePeriods: num(r.promotionConsecutivePeriods),
    items,
    mlLayer: ml ? { note: ml.note, skipReason: ml.skipReason } : null,
  }
}

/* ----------------------------------- hata ayrıntısı ------------------------------ */

/**
 * Zorunlu metrik eksikken backend 400 ile `missing[]` döndürür. Liste metrik
 * kimliği ya da `{ metricId }` nesnesi olabilir.
 */
export function missingMetricIds(detail: unknown): string[] {
  const r = obj(detail)
  return arr(pick(r, 'missing', 'missingMetricIds', 'missingMetrics'))
    .map((m) => (typeof m === 'string' ? m : str0(pick(obj(m), 'metricId', 'id'))))
    .filter(Boolean)
}
