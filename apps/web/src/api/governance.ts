import { apiFetch, platformTenantHeaders, qs } from './client'
import { getValidToken } from '@/auth/keycloak'
import { env } from '@/lib/env'
import { downloadAuthed } from './engagement'
import { tx } from '@/lib/i18n'

/* ============================== governance-service ==============================
 * Denetim, canlı olaylar, zaman makinesi, KVKK, belge şablonları, kural
 * motoru, webhook/API anahtarı, Slack/Teams, faturalama, takvim, analitik,
 * İK asistanı, teklif→işe alım sagası, plan özellikleri.
 * ============================================================================== */

const BASE = '/api/governance'

/* -------------------------------------------------------------- denetim */
export interface AuditEntry {
  id: number
  service: string
  entityType: string
  entityId: string | null
  action: 'Created' | 'Updated' | 'Deleted' | string
  changes: Record<string, unknown> | null
  userId: string | null
  userName: string | null
  correlationId: string | null
  ipAddress: string | null
  occurredAt: string
}
export interface AuditFilter {
  service?: string
  entityType?: string
  entityId?: string
  userId?: string
  action?: string
  q?: string
  from?: string
  to?: string
  page?: number
  pageSize?: number
}
export interface AuditFacets {
  services: Array<{ name: string; count: number }>
  entityTypes: Array<{ name: string; count: number }>
  users: Array<{ id: string | null; name: string | null; count: number }>
  daily: Array<{ day: string; count: number }>
}

/* ---------------------------------------------------------------- olaylar */
export interface RadarEvent {
  id: string
  tenantSlug: string | null
  topic: string
  eventType: string
  payload: Record<string, unknown> | null
  occurredAt: string
  summary: string
}

/* ---------------------------------------------------------- zaman makinesi */
export interface TimeSnapshot {
  date: string
  headcount: number
  headcountToday: number
  /** O tarihte var olan departmanlar (o güne kadar kurulmuş ya da o gün kişi bulunan). */
  departmentIds?: string[]
  departments: Array<{
    /** Atanmamışlar için null. */
    departmentId?: string | null
    department: string
    count: number
    head: string | null
    people: Array<{ employeeId: string; name: string; position: string | null; hireDate: string; isHead: boolean }>
  }>
  changesSince: Array<{ entityType: string; action: string; count: number }>
}
/* ---------------------------------------------------------- ekip ağı */
export interface TeamNetworkNode {
  /** Ekip kimliği ya da birleşik küçük ekipler için "other". */
  id: string
  name: string
  department: string | null
  members: number
  /** "Diğer" düğümünde birleşen ekip sayısı (normal ekipte 1). */
  mergedTeams: number
  internal: { kudos: number; oneOnOnes: number; sharedGoals: number }
}
export interface TeamNetworkEdge {
  source: string
  target: string
  weight: number
  kudos: number
  oneOnOnes: number
  sharedGoals: number
}
export interface TeamNetwork {
  days: number
  since: string
  minTeamSize: number
  minEdgeCount: number
  nodes: TeamNetworkNode[]
  edges: TeamNetworkEdge[]
  hiddenTeams: number
  hiddenEdges: number
}

export interface TimelinePoint { month: string; headcount: number; hires: number; exits: number }

/* --------------------------------------------------------------- analitik */
export interface AnalyticsOverview {
  months: number
  timeline: TimelinePoint[]
  leave: Array<{ month: string; type: string; days: number; requests: number }>
  overtime: Array<{ month: string; workedHours: number; overtimeHours: number }>
  departments: Array<{ department: string; headcount: number }>
  tenure: Array<{ bucket: string; count: number }>
  expense: Array<{ month: string; amount: number; claims: number }>
  /** KVKK: küçük grup gizlemesiyle gösterilmeyen kişi sayıları. */
  hiddenPeople?: { departments: number; tenure: number }
  minGroup?: number
  kpis: { headcount: number; hires: number; exits: number; turnoverPercent: number; leaveDays: number; overtimeHours: number; expenseTotal: number }
}

/* ------------------------------------------------------------------- KVKK */
export interface ConsentState {
  type: string
  title: string
  version: string
  required: boolean
  text: string
  granted: boolean | null
  recordedAt: string | null
  outdated: boolean
  history: Array<{ granted: boolean; version: string; recordedAt: string }>
}
export interface ConsentSummary {
  population: number
  types: Array<{ type: string; title: string; required: boolean; version: string; granted: number; denied: number; pending: number; outdated?: number }>
  missingRequired: Array<{ employeeId: string; name: string; department: string | null }>
}
export type DataRequestKind = 'Access' | 'Rectification' | 'Erasure' | 'Objection'
export const dataRequestLabels: Record<DataRequestKind, string> = {
  Access: tx('Bilgi/erişim talebi'), Rectification: tx('Düzeltme'), Erasure: 'Silme/yok etme', Objection: tx('İtiraz'),
}
export interface DataRequest {
  id: string
  personName: string
  employeeId: string | null
  kind: DataRequestKind
  details: string | null
  /** Withdrawn: başvurucu yanıtlanmadan geri çekti. */
  status: 'Received' | 'InProgress' | 'Completed' | 'Rejected' | 'Withdrawn'
  response: string | null
  dueAt: string
  createdAt: string
  completedAt: string | null
  overdue: boolean
  daysLeft: number
  channel?: 'Panel' | 'Email' | 'Kep' | 'Mail' | 'InPerson'
  contact?: string | null
  identityVerified?: boolean
  verificationMethod?: string | null
  verifiedBy?: string | null
  verifiedAt?: string | null
  /** Dalga 10: açık başvurunun kaçıncı gününde olduğu (yasal süre 30 gün) ve gönderilen uyarılar. */
  day?: number | null
  legalDays?: number
  reminder20At?: string | null
  reminder27At?: string | null
  overdueAlertAt?: string | null
  /** Erişim başvurusu veri paketi (ZIP); başvurucu sonuçlandıktan sonra indirir. */
  package?: { createdAt: string; expiresAt: string; sizeBytes: number; downloads: number; sha256: string | null; downloadable: boolean } | null
}
export interface RetentionPolicy {
  id: string
  category: string
  label: string
  allowedActions: string[]
  retentionMonths: number
  action: string
  isEnabled: boolean
  lastRunAt: string | null
  lastAffected: number
}

/* -------------------------------------------------------- belge şablonları */
export interface DocTemplate { id: string; name: string; category: string; body: string; selfService: boolean; requiresApproval: boolean; createdAt: string; updatedAt: string }
export type DocRequestStatus = 'Pending' | 'Issued' | 'Rejected'
export interface DocRequest {
  id: string; employeeId: string; templateId: string; templateName: string; purpose: string | null; status: DocRequestStatus
  decisionNote: string | null; createdAt: string; issuedAt: string | null; verificationCode: string | null
  /** Y28: OTP ile basit e-imza atıldı mı (yalnızca "taleplerim"). */
  signed?: boolean
}
export interface DocVerification {
  valid: boolean; document?: string; issuedAt?: string; holder?: string; company?: string | null; hash?: string
  signature?: { signed: boolean; signedAt: string; method: string; documentSha256: string; evidenceSha256: string; matchesDocument: boolean; disclaimer: string } | null
}
export interface RenderedDoc { employeeId: string; name: string; html: string }

/* ---------------------------------------------------------- kural motoru */
export interface RuleCondition { field: string; op: string; value: string }
export interface RuleAction { type: 'notify' | 'slack' | 'teams' | 'webhook'; target?: string | null; message: string }
export interface Rule {
  id: string
  name: string
  description: string | null
  trigger: string
  conditions: RuleCondition[]
  actions: RuleAction[]
  isEnabled: boolean
  fireCount: number
  lastFiredAt: string | null
  createdAt: string
}
export interface RuleCatalog {
  events: Array<{ type: string; label: string; fields: string[] }>
  operators: Array<{ op: string; label: string }>
  actions: Array<{ type: string; label: string }>
}
export interface RuleRun { id: string; ruleId: string; ruleName: string; eventType: string; result: string; occurredAt: string }

/* ---------------------------------------------- webhook / API / entegrasyon */
export interface Webhook {
  id: string
  name: string
  url: string
  secret: string
  events: string[]
  isEnabled: boolean
  lastStatus: number | null
  lastDeliveredAt: string | null
  failureCount: number
  createdAt: string
}
export type WebhookRetryState = 'pending' | 'retrying' | 'retried' | 'gave_up' | 'resent' | null
export interface WebhookDelivery {
  id: string; eventType: string; statusCode: number | null; error: string | null; durationMs: number; occurredAt: string
  eventId?: string | null; attempt?: number; nextRetryAt?: string | null; retryState?: WebhookRetryState; manual?: boolean; triggeredByName?: string | null
}
/** Dalga 12: kiracının tüm teslimatları (hata ekranı). */
export interface WebhookDeliveryRow extends WebhookDelivery {
  webhookId: string; webhookName: string | null; webhookUrl: string | null; webhookEnabled: boolean; canResend: boolean
}
export interface WebhookDeliveryList {
  items: WebhookDeliveryRow[]
  summary: { total24h: number; failed24h: number; pendingRetries: number; gaveUp7d: number }
  retry: { maxAttempts: number; scheduleSeconds: number[] }
}
export interface ApiKeyRow {
  id: string; name: string; prefix: string; scopes: string[]; createdByName: string | null; createdAt: string; lastUsedAt: string | null; revokedAt: string | null; active: boolean
  expiresAt?: string | null; rotatedFromId?: string | null; usageCount?: number; lastUsedScope?: string | null; last30?: number; denied30?: number
  status?: 'active' | 'revoked' | 'expired'; expiresSoon?: boolean
}
export interface ApiKeyUsage {
  id: string; days: number; total: number; denied: number
  byDay: Array<{ day: string; count: number; denied: number }>
  byScope: Array<{ scope: string; count: number; denied: number }>
  lastUsedAt: string | null; lastUsedScope: string | null; usageCount: number
}
export interface Integration {
  id: string
  kind: 'Slack' | 'Teams'
  name: string
  webhookUrl: string
  events: string[]
  isEnabled: boolean
  lastStatus: number | null
  createdAt: string
  hasSigningSecret: boolean
}

/* ---------------------------------------------------------- asistan/rapor */
export interface NlReport {
  understood: boolean
  interpretation: string
  metric: string
  groupBy: string
  from: string
  to: string
  columns: string[]
  rows: Array<Array<string | number | null>>
  chart: 'bar' | 'line' | 'number' | 'none' | 'funnel'
  sql: string
  suggestions: string[]
}
export interface AssistantReply {
  reply: string
  source: 'help' | 'data' | 'kb' | 'report' | 'fallback' | 'llm'
  links?: Array<{ label: string; path: string }>
  report?: NlReport
  related?: string[]
}
export interface KbArticle { id: string; title: string; body: string; tags: string[]; createdAt: string; updatedAt: string }

/* ----------------------------------------------------------------- saga */
export interface HireSaga {
  applicationId: string
  candidate: string
  email: string
  posting: string
  applicationStatus: 'Offer' | 'Hired'
  stage: 'Offer' | 'AwaitingEmployee' | 'AwaitingOnboarding' | 'Completed'
  steps: Array<{ key: string; label: string; done: boolean }>
  employeeId: string | null
  onboardingPlanId: string | null
  onboardingStatus: string | null
  daysInStage: number
  stuck: boolean
}

/** Server-Sent Events'i fetch akışıyla okur (EventSource Authorization başlığı gönderemez). */
export async function streamEvents(onEvent: (e: RadarEvent) => void, signal: AbortSignal, onOpen?: () => void) {
  const token = await getValidToken()
  const res = await fetch(`${env.apiBase}${BASE}/events/stream`, {
    headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream', ...platformTenantHeaders() },
    signal,
  })
  if (!res.ok || !res.body) throw new Error(tx('Akış açılamadı (HTTP {0})', [res.status]))
  onOpen?.()
  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })
    let idx
    while ((idx = buffer.indexOf('\n\n')) >= 0) {
      const chunk = buffer.slice(0, idx)
      buffer = buffer.slice(idx + 2)
      const data = chunk.split('\n').filter((l) => l.startsWith('data:')).map((l) => l.slice(5).trim()).join('')
      if (data) {
        try { onEvent(JSON.parse(data) as RadarEvent) } catch { /* bozuk parça */ }
      }
    }
  }
}

/* ------------------------------------------------- sohbet uygulamaları (Slack / Teams botu) */
export type ChatPlatform = 'Slack' | 'Teams' | 'Mattermost' | 'RocketChat'
export interface ChatApp {
  id: string
  platform: ChatPlatform
  name: string
  isEnabled: boolean
  notifyApprovals: boolean
  notifyRequesters: boolean
  slackTeamId: string | null
  slackTeamName: string | null
  teamsAppId: string | null
  teamsAzureTenantId: string | null
  hasSlackToken: boolean
  hasSigningSecret: boolean
  hasTeamsPassword: boolean
  lastError: string | null
  lastActivityAt: string | null
  createdAt: string
  linkedUsers: number
  knownUsers: number
  requireVerifiedIdentity: boolean
  messageDetail: 'Minimal' | 'Standard'
  dailyDigest: boolean
  endpoints: { commands?: string; interactivity?: string; events?: string; messaging?: string; webhook?: string }
  publicOriginIsHttps: boolean
  /* Dalga 5e: Mattermost / Rocket.Chat ve bot ayarları */
  serverUrl?: string | null
  botUserId?: string | null
  hasBotToken?: boolean
  hasIncomingToken?: boolean
  disabledFeatures?: string[]
  channelId?: string | null
  celebrationsEnabled?: boolean
  respectQuietHours?: boolean
  buttonTtlDays?: number
  transferKey?: string | null
  selfHosted?: boolean
}
export interface ChatAppInput {
  platform: ChatPlatform
  name: string
  isEnabled: boolean
  notifyApprovals: boolean
  notifyRequesters: boolean
  slackBotToken?: string
  slackSigningSecret?: string
  teamsAppId?: string
  teamsAppPassword?: string
  teamsAzureTenantId?: string
  requireVerifiedIdentity?: boolean
  messageDetail?: 'Minimal' | 'Standard'
  dailyDigest?: boolean
  serverUrl?: string
  botToken?: string
  botUserId?: string
  incomingToken?: string
  channelId?: string
  celebrationsEnabled?: boolean
  respectQuietHours?: boolean
  buttonTtlDays?: number
}
export interface MyChatIdentity {
  id: string
  platform: ChatPlatform
  appName: string | null
  displayName: string | null
  email: string | null
  verifiedAt: string | null
  lastSeenAt: string | null
}
export interface ChatLinkPreview {
  platform: ChatPlatform
  appName: string | null
  displayName: string | null
  email: string | null
  emailMatches: boolean
  myName: string | null
}
export interface ChatIdentityRow {
  id: string
  externalUserId: string
  email: string | null
  displayName: string | null
  employeeId: string | null
  employeeName: string | null
  canReceive: boolean
  linkedAt: string
  lastSeenAt: string | null
  verifiedAt: string | null
}

/* ------------------------------------------------- takvim ve toplantı */
export type CalendarProviderName = 'Google' | 'Microsoft' | 'Zoom'
export interface CalendarProviderInfo {
  provider: CalendarProviderName
  configured: boolean
  isEnabled: boolean
  clientId: string | null
  hasSecret: boolean
  msTenant: string | null
  zoomAccountId: string | null
  zoomDefaultHost: string | null
  lastError: string | null
  connections: number
  redirectUri: string | null
  scopes: string
  publicOriginIsHttps: boolean
}
export interface CalendarConnectionInfo {
  id: string
  provider: 'Google' | 'Microsoft'
  accountEmail: string | null
  syncLeaves: boolean
  status: 'Active' | 'Error'
  lastError: string | null
  lastSyncAt: string | null
  createdAt: string
}
export type MeetingProvider = 'zoom' | 'teams' | 'google' | 'none'
export interface MeetingInfo {
  id: string
  sourceType: 'one-on-one' | 'interview' | 'custom'
  sourceId: string | null
  title: string
  description: string | null
  startsAt: string
  durationMinutes: number
  provider: MeetingProvider
  joinUrl: string | null
  status: string
  organizerEmployeeId: string
  participantEmployeeIds: string[]
  externalEmails: string[]
  warnings: string[] | null
  createdAt: string
}
export interface AvailabilityResult {
  people: { employeeId: string; name: string; calendarConnected: boolean; busy: { start: string; end: string; source: string }[] }[]
  suggestions: string[]
}

/* ------------------------------------------------- yapay zekâ (LLM) */
export interface AiSettingsInfo {
  configured: boolean
  configError: string | null
  provider: string
  model: string
  local: boolean
  enabled: boolean
  tenantEnabled: boolean
  allowPersonalData: boolean
  hourlyLimit: number
  usedThisWindow: number
  windowMinutes: number
  usage: { task: string; calls: number; failed: number; inputTokens: number; outputTokens: number }[] | null
}

/* ------------------------------------------------- KVKK temeli */
export type TransferStatus = 'Missing' | 'NotifyPending' | 'NotifyOverdue' | 'Ok'
export interface InventoryActivity {
  id: string
  module: string
  activity: string
  subjects: string[]
  dataCategories: string[]
  purpose: string
  legalBasis: string
  special: boolean
  retention: string
  retentionCategory: string | null
  recipients: string[]
  measures: string
  retentionPolicy: { retentionMonths: number; action: string; isEnabled: boolean } | null
  transfers: { key: string; name: string; inUse: boolean; status: TransferStatus }[]
}
export interface TransferProviderInfo {
  key: string
  name: string
  country: string
  dataSent: string
  usedBy: string
  inUse: boolean
  status: TransferStatus
  notifyDeadline: string | null
  agreement: { mechanism: string; signedAt: string; notifiedAt: string | null; reference: string | null; notes: string | null; updatedBy: string; updatedAt: string } | null
}
export interface TransfersInfo {
  mechanisms: { value: string; label: string }[]
  llmLocal: boolean
  providers: TransferProviderInfo[]
}
export interface DestructionLogRow {
  id: string
  category: string
  label: string
  action: 'Anonymize' | 'Delete'
  affected: number
  retentionMonths: number
  trigger: 'Periodic' | 'Manual' | 'Request' | 'Restore'
  actor: string
  method: string
  ranAt: string
}
export interface AccessLogRow {
  at: string
  service: string
  entity: string
  employeeId: string | null
  action: 'Revealed' | 'SensitiveViewed' | 'Exported' | 'AutomatedAnalysis' | 'PlatformAccess'
  field: string | null
  reason: string | null
  viewer: string
  ip: string | null
  person: string | null
}
export interface ComplianceCheck {
  key: string
  title: string
  status: 'ok' | 'warn' | 'error' | 'info'
  detail: string
  tab: string | null
}
export interface AnalysisObjection {
  id: string
  employeeId: string
  personName: string
  analysis: string
  analysisLabel: string
  reason: string | null
  status: 'Open' | 'Upheld' | 'Rejected'
  response: string | null
  decidedBy: string | null
  createdAt: string
  decidedAt: string | null
  dueAt: string
  overdue: boolean
}

export const governanceApi = {
  /* denetim */
  audit: (f: AuditFilter, signal?: AbortSignal) =>
    apiFetch<{ total: number; page: number; pageSize: number; items: AuditEntry[] }>(`${BASE}/audit${qs(f)}`, { signal }),
  auditFacets: (signal?: AbortSignal) => apiFetch<AuditFacets>(`${BASE}/audit/facets`, { signal }),
  auditCorrelation: (id: string, signal?: AbortSignal) => apiFetch<AuditEntry[]>(`${BASE}/audit/correlation/${encodeURIComponent(id)}`, { signal }),
  auditExport: (f: AuditFilter) => downloadAuthed(`${BASE}/audit/export${qs(f)}`, `denetim-kaydi.csv`),

  /* olaylar */
  recentEvents: (limit = 100, signal?: AbortSignal) => apiFetch<RadarEvent[]>(`${BASE}/events/recent${qs({ limit })}`, { signal }),
  eventStats: (signal?: AbortSignal) =>
    apiFetch<{ byType: Array<{ type: string; count: number }>; hourly: Array<{ hour: string; count: number }>; listeners: number }>(`${BASE}/events/stats`, { signal }),

  /* ekip ağı (yalnızca İK; ekip düzeyi, küçük ekip/sayı gizlemeli) */
  teamNetwork: (days: number, signal?: AbortSignal) => apiFetch<TeamNetwork>(`${BASE}/team-network${qs({ days })}`, { signal }),

  /* zaman makinesi */
  snapshot: (date: string, signal?: AbortSignal) => apiFetch<TimeSnapshot>(`${BASE}/time-machine${qs({ date })}`, { signal }),
  timeline: (months = 24, signal?: AbortSignal) =>
    apiFetch<{ series: TimelinePoint[]; earliest: string | null }>(`${BASE}/time-machine/timeline${qs({ months })}`, { signal }),

  /* analitik */
  analytics: (months = 12, signal?: AbortSignal) => apiFetch<AnalyticsOverview>(`${BASE}/analytics/overview${qs({ months })}`, { signal }),

  /* KVKK */
  myConsents: (signal?: AbortSignal) => apiFetch<ConsentState[]>(`${BASE}/privacy/consents/me`, { signal }),
  recordConsent: (consentType: string, granted: boolean) =>
    apiFetch<unknown>(`${BASE}/privacy/consents/me`, { method: 'POST', body: { consentType, granted } }),
  consentSummary: (signal?: AbortSignal) => apiFetch<ConsentSummary>(`${BASE}/privacy/consents`, { signal }),
  dataRequests: (signal?: AbortSignal) => apiFetch<DataRequest[]>(`${BASE}/privacy/requests`, { signal }),
  createDataRequest: (kind: DataRequestKind, details?: string) =>
    apiFetch<DataRequest>(`${BASE}/privacy/requests`, { method: 'POST', body: { kind, details } }),
  withdrawDataRequest: (id: string) =>
    apiFetch<{ id: string; status: DataRequest['status'] }>(`${BASE}/privacy/requests/${id}/withdraw`, { method: 'POST' }),
  updateDataRequest: (id: string, status: DataRequest['status'], response?: string) =>
    apiFetch<DataRequest>(`${BASE}/privacy/requests/${id}`, { method: 'PATCH', body: { status, response } }),
  exportPersonalData: (employeeId: string) => downloadAuthed(`${BASE}/privacy/export/${employeeId}`, `kisisel-veri-${employeeId.slice(0, 8)}.json`),
  anonymize: (employeeId: string) => apiFetch<{ anonymized: number }>(`${BASE}/privacy/anonymize/${employeeId}`, { method: 'POST' }),
  retention: (signal?: AbortSignal) => apiFetch<RetentionPolicy[]>(`${BASE}/privacy/retention`, { signal }),
  updateRetention: (id: string, body: { retentionMonths: number; action: string; isEnabled: boolean }) =>
    apiFetch<RetentionPolicy>(`${BASE}/privacy/retention/${id}`, { method: 'PUT', body }),
  runRetention: (id: string) => apiFetch<{ affected: number }>(`${BASE}/privacy/retention/${id}/run`, { method: 'POST' }),
  inventory: (signal?: AbortSignal) => apiFetch<{ generatedAt: string; activities: InventoryActivity[] }>(`${BASE}/privacy/inventory`, { signal }),
  transfers: (signal?: AbortSignal) => apiFetch<TransfersInfo>(`${BASE}/privacy/transfers`, { signal }),
  saveTransfer: (provider: string, body: { mechanism: string; signedAt: string; notifiedAt: string | null; reference?: string; notes?: string }) =>
    apiFetch<{ provider: string; status: TransferStatus }>(`${BASE}/privacy/transfers/${provider}`, { method: 'PUT', body }),
  deleteTransfer: (provider: string) => apiFetch<void>(`${BASE}/privacy/transfers/${provider}`, { method: 'DELETE' }),
  destructionLogs: (f: { from?: string; to?: string }, signal?: AbortSignal) =>
    apiFetch<DestructionLogRow[]>(`${BASE}/privacy/destruction-logs${qs(f)}`, { signal }),
  accessLog: (f: { employeeId?: string; days?: number }, signal?: AbortSignal) => apiFetch<AccessLogRow[]>(`${BASE}/privacy/access-log${qs(f)}`, { signal }),
  myAccessLog: (signal?: AbortSignal) => apiFetch<AccessLogRow[]>(`${BASE}/privacy/access-log/me`, { signal }),
  compliance: (signal?: AbortSignal) => apiFetch<ComplianceCheck[]>(`${BASE}/privacy/compliance`, { signal }),
  analyses: (signal?: AbortSignal) => apiFetch<{ value: string; label: string }[]>(`${BASE}/privacy/analyses`, { signal }),
  objections: (signal?: AbortSignal) => apiFetch<AnalysisObjection[]>(`${BASE}/privacy/objections`, { signal }),
  createObjection: (analysis: string, reason?: string) =>
    apiFetch<AnalysisObjection>(`${BASE}/privacy/objections`, { method: 'POST', body: { analysis, reason } }),
  decideObjection: (id: string, status: 'Upheld' | 'Rejected', response: string) =>
    apiFetch<AnalysisObjection>(`${BASE}/privacy/objections/${id}`, { method: 'PATCH', body: { status, response } }),
  objectionStatus: (employeeId: string, analysis: string, signal?: AbortSignal) =>
    apiFetch<{ blocked: boolean; status: string | null; since: string | null }>(`${BASE}/privacy/objections/status/${employeeId}${qs({ analysis })}`, { signal }),
  attritionRisk: (employeeId: string, features: number[]) =>
    apiFetch<{ prediction: import('./types').PredictResponse; explanation: import('./types').ExplainResponse | null }>(
      `${BASE}/privacy/analysis/attrition/${employeeId}`, { method: 'POST', body: { features } }),

  /* belge şablonları */
  templates: (signal?: AbortSignal) => apiFetch<DocTemplate[]>(`${BASE}/documents/templates`, { signal }),
  placeholders: (signal?: AbortSignal) => apiFetch<Array<{ key: string; label: string }>>(`${BASE}/documents/templates/placeholders`, { signal }),
  createTemplate: (body: { name: string; category: string; body: string; selfService?: boolean; requiresApproval?: boolean }) =>
    apiFetch<DocTemplate>(`${BASE}/documents/templates`, { method: 'POST', body }),
  updateTemplate: (id: string, body: { name: string; category: string; body: string; selfService?: boolean; requiresApproval?: boolean }) =>
    apiFetch<DocTemplate>(`${BASE}/documents/templates/${id}`, { method: 'PUT', body }),
  deleteTemplate: (id: string) => apiFetch<void>(`${BASE}/documents/templates/${id}`, { method: 'DELETE' }),
  requestableTemplates: (signal?: AbortSignal) =>
    apiFetch<Array<{ id: string; name: string; category: string; requiresApproval: boolean }>>(`${BASE}/documents/requests/templates`, { signal }),
  myDocRequests: (signal?: AbortSignal) => apiFetch<DocRequest[]>(`${BASE}/documents/requests/mine`, { signal }),
  docRequests: (status?: DocRequestStatus, signal?: AbortSignal) =>
    apiFetch<DocRequest[]>(`${BASE}/documents/requests${status ? `?status=${status}` : ''}`, { signal }),
  requestDocument: (templateId: string, purpose?: string) =>
    apiFetch<DocRequest>(`${BASE}/documents/requests`, { method: 'POST', body: { templateId, purpose } }),
  decideDocRequest: (id: string, approve: boolean, note?: string) =>
    apiFetch<DocRequest>(`${BASE}/documents/requests/${id}/decide`, { method: 'POST', body: { approve, note } }),
  docRequestDocument: (id: string) =>
    apiFetch<{ templateName: string; html: string; verificationCode: string; issuedAt: string }>(`${BASE}/documents/requests/${id}/document`),
  verifyDocument: (code: string, signal?: AbortSignal) =>
    apiFetch<DocVerification>(`${BASE}/documents/verify/${encodeURIComponent(code)}`, { signal, anonymous: true }),
  sampleTemplates: () => apiFetch<{ added: number }>(`${BASE}/documents/templates/samples`, { method: 'POST' }),
  renderTemplate: (id: string, employeeIds: string[]) =>
    apiFetch<{ template: string; documents: RenderedDoc[] }>(`${BASE}/documents/templates/${id}/render`, { method: 'POST', body: { employeeIds } }),

  /* kural motoru */
  ruleCatalog: (signal?: AbortSignal) => apiFetch<RuleCatalog>(`${BASE}/rules/catalog`, { signal }),
  rules: (signal?: AbortSignal) => apiFetch<Rule[]>(`${BASE}/rules`, { signal }),
  createRule: (body: Omit<Rule, 'id' | 'fireCount' | 'lastFiredAt' | 'createdAt'>) => apiFetch<Rule>(`${BASE}/rules`, { method: 'POST', body }),
  updateRule: (id: string, body: Omit<Rule, 'id' | 'fireCount' | 'lastFiredAt' | 'createdAt'>) =>
    apiFetch<Rule>(`${BASE}/rules/${id}`, { method: 'PUT', body }),
  deleteRule: (id: string) => apiFetch<void>(`${BASE}/rules/${id}`, { method: 'DELETE' }),
  testRule: (body: { conditions: RuleCondition[]; actions: RuleAction[]; payload: Record<string, unknown> }) =>
    apiFetch<{ matched: boolean; reason: string; actions: Array<{ type: string; target: string | null; message: string }> | null }>(`${BASE}/rules/test`, { method: 'POST', body }),
  ruleRuns: (ruleId?: string, signal?: AbortSignal) => apiFetch<RuleRun[]>(`${BASE}/rules/runs${qs({ ruleId })}`, { signal }),
  sampleRules: () => apiFetch<{ added: number }>(`${BASE}/rules/samples`, { method: 'POST' }),

  /* webhook */
  webhooks: (signal?: AbortSignal) => apiFetch<Webhook[]>(`${BASE}/webhooks`, { signal }),
  createWebhook: (body: { name: string; url: string; events: string[]; isEnabled: boolean }) => apiFetch<Webhook>(`${BASE}/webhooks`, { method: 'POST', body }),
  updateWebhook: (id: string, body: { name: string; url: string; events: string[]; isEnabled: boolean }) =>
    apiFetch<Webhook>(`${BASE}/webhooks/${id}`, { method: 'PUT', body }),
  deleteWebhook: (id: string) => apiFetch<void>(`${BASE}/webhooks/${id}`, { method: 'DELETE' }),
  pingWebhook: (id: string) => apiFetch<{ lastStatus: number | null; ok: boolean }>(`${BASE}/webhooks/${id}/ping`, { method: 'POST' }),
  rotateWebhookSecret: (id: string) => apiFetch<{ secret: string }>(`${BASE}/webhooks/${id}/rotate-secret`, { method: 'POST' }),
  webhookDeliveries: (id: string, signal?: AbortSignal) => apiFetch<WebhookDelivery[]>(`${BASE}/webhooks/${id}/deliveries`, { signal }),
  allWebhookDeliveries: (params: { state?: string; webhookId?: string; eventType?: string }, signal?: AbortSignal) => {
    const q = new URLSearchParams(Object.entries(params).filter(([, v]) => !!v) as [string, string][]).toString()
    return apiFetch<WebhookDeliveryList>(`${BASE}/webhooks/deliveries${q ? `?${q}` : ''}`, { signal })
  },
  resendWebhookDelivery: (id: string) =>
    apiFetch<{ id: string; statusCode: number | null; error: string | null; attempt: number; retryState: WebhookRetryState; nextRetryAt: string | null; ok: boolean }>(`${BASE}/webhooks/deliveries/${id}/resend`, { method: 'POST' }),
  cancelWebhookRetry: (id: string) => apiFetch<{ id: string; retryState: string }>(`${BASE}/webhooks/deliveries/${id}/cancel-retry`, { method: 'POST' }),
  createTestReceiver: () => apiFetch<{ id: string; token: string }>(`${BASE}/webhooks/test-receiver`, { method: 'POST' }),
  webhookInbox: (token: string, signal?: AbortSignal) =>
    apiFetch<Array<{ receivedAt: string; event: string | null; signatureValid: boolean | null; body: string }>>(`${BASE}/webhooks/inbox/${token}`, { signal }),

  /* API anahtarı */
  apiKeys: (signal?: AbortSignal) => apiFetch<ApiKeyRow[]>(`${BASE}/api-keys`, { signal }),
  apiScopes: (signal?: AbortSignal) => apiFetch<string[]>(`${BASE}/api-keys/scopes`, { signal }),
  createApiKey: (name: string, scopes: string[], expiresInDays?: number | null) =>
    apiFetch<{ id: string; name: string; prefix: string; scopes: string[]; expiresAt: string | null; key: string }>(`${BASE}/api-keys`, { method: 'POST', body: { name, scopes, expiresInDays: expiresInDays ?? null } }),
  rotateApiKey: (id: string, graceHours: number) =>
    apiFetch<{ id: string; name: string; prefix: string; scopes: string[]; expiresAt: string | null; key: string; oldKeyValidUntil: string | null }>(`${BASE}/api-keys/${id}/rotate`, { method: 'POST', body: { graceHours } }),
  apiKeyUsage: (id: string, days = 30, signal?: AbortSignal) => apiFetch<ApiKeyUsage>(`${BASE}/api-keys/${id}/usage?days=${days}`, { signal }),
  revokeApiKey: (id: string) => apiFetch<void>(`${BASE}/api-keys/${id}`, { method: 'DELETE' }),

  /* Slack / Teams */
  integrations: (signal?: AbortSignal) => apiFetch<Integration[]>(`${BASE}/integrations`, { signal }),
  createIntegration: (body: { kind: string; name: string; webhookUrl: string; events: string[]; signingSecret?: string | null; isEnabled: boolean }) =>
    apiFetch<{ id: string }>(`${BASE}/integrations`, { method: 'POST', body }),
  updateIntegration: (id: string, body: { kind: string; name: string; webhookUrl: string; events: string[]; signingSecret?: string | null; isEnabled: boolean }) =>
    apiFetch<{ id: string }>(`${BASE}/integrations/${id}`, { method: 'PUT', body }),
  deleteIntegration: (id: string) => apiFetch<void>(`${BASE}/integrations/${id}`, { method: 'DELETE' }),
  chatApps: (signal?: AbortSignal) => apiFetch<ChatApp[]>(`${BASE}/chat-apps`, { signal }),
  createChatApp: (body: ChatAppInput) => apiFetch<ChatApp>(`${BASE}/chat-apps`, { method: 'POST', body }),
  updateChatApp: (id: string, body: ChatAppInput) => apiFetch<ChatApp>(`${BASE}/chat-apps/${id}`, { method: 'PUT', body }),
  deleteChatApp: (id: string) => apiFetch<void>(`${BASE}/chat-apps/${id}`, { method: 'DELETE' }),
  testChatApp: (id: string) => apiFetch<{ sent: boolean }>(`${BASE}/chat-apps/${id}/test`, { method: 'POST' }),
  chatIdentities: (id: string, signal?: AbortSignal) => apiFetch<ChatIdentityRow[]>(`${BASE}/chat-apps/${id}/identities`, { signal }),
  sendChatDigest: (appId: string) => apiFetch<{ sent: number }>(`${BASE}/chat-apps/${appId}/digest`, { method: 'POST' }),
  revokeChatIdentity: (appId: string, identityId: string) => apiFetch<void>(`${BASE}/chat-apps/${appId}/identities/${identityId}/revoke`, { method: 'POST' }),
  chatLinkPreview: (code: string, signal?: AbortSignal) => apiFetch<ChatLinkPreview>(`${BASE}/chat/link/${encodeURIComponent(code)}`, { signal }),
  chatLink: (code: string) => apiFetch<{ linked: boolean; platform: ChatPlatform }>(`${BASE}/chat/link`, { method: 'POST', body: { code } }),
  myChatIdentities: (signal?: AbortSignal) => apiFetch<MyChatIdentity[]>(`${BASE}/chat/link/mine`, { signal }),
  unlinkMyChatIdentity: (id: string) => apiFetch<void>(`${BASE}/chat/link/mine/${id}`, { method: 'DELETE' }),
  slackManifest: (id?: string, name?: string) =>
    apiFetch<unknown>(id ? `${BASE}/chat-apps/${id}/slack-manifest` : `${BASE}/chat-apps/slack-manifest${qs({ name })}`),
  teamsPackage: (id: string) => downloadAuthed(`${BASE}/chat-apps/${id}/teams-package`, 'hr360-teams.zip'),
  calendarProviders: (signal?: AbortSignal) => apiFetch<CalendarProviderInfo[]>(`${BASE}/calendar/providers`, { signal }),
  saveCalendarProvider: (provider: CalendarProviderName, body: { clientId: string; clientSecret?: string; msTenant?: string; zoomAccountId?: string; zoomDefaultHost?: string; isEnabled: boolean }) =>
    apiFetch<{ provider: string }>(`${BASE}/calendar/providers/${provider.toLowerCase()}`, { method: 'PUT', body }),
  deleteCalendarProvider: (provider: CalendarProviderName) => apiFetch<void>(`${BASE}/calendar/providers/${provider.toLowerCase()}`, { method: 'DELETE' }),
  calendarConnections: (signal?: AbortSignal) =>
    apiFetch<{ available: ('Google' | 'Microsoft')[]; linked: boolean; connections: CalendarConnectionInfo[] }>(`${BASE}/calendar/connections`, { signal }),
  connectCalendar: (provider: 'Google' | 'Microsoft') => apiFetch<{ authorizeUrl: string }>(`${BASE}/calendar/connect/${provider.toLowerCase()}`, { method: 'POST' }),
  patchCalendarConnection: (id: string, syncLeaves: boolean) => apiFetch<unknown>(`${BASE}/calendar/connections/${id}`, { method: 'PATCH', body: { syncLeaves } }),
  disconnectCalendar: (id: string) => apiFetch<void>(`${BASE}/calendar/connections/${id}`, { method: 'DELETE' }),
  meetingOptions: (signal?: AbortSignal) => apiFetch<{ zoom: boolean; teams: boolean; google: boolean; calendar: boolean }>(`${BASE}/meetings/options`, { signal }),
  meetings: (sourceType?: string, sourceId?: string, signal?: AbortSignal) => apiFetch<MeetingInfo[]>(`${BASE}/meetings${qs({ sourceType, sourceId })}`, { signal }),
  createMeeting: (body: { sourceType: string; sourceId?: string; title?: string; description?: string; startsAt?: string; durationMinutes: number; provider: MeetingProvider;
    participantEmployeeIds?: string[]; externalEmails?: string[]; includeCandidate?: boolean; addToCalendars: boolean }) =>
    apiFetch<MeetingInfo>(`${BASE}/meetings`, { method: 'POST', body }),
  cancelMeeting: (id: string) => apiFetch<void>(`${BASE}/meetings/${id}`, { method: 'DELETE' }),
  availability: (body: { employeeIds: string[]; from: string; to: string; durationMinutes: number }) =>
    apiFetch<AvailabilityResult>(`${BASE}/meetings/availability`, { method: 'POST', body }),
  aiSettings: (signal?: AbortSignal) => apiFetch<AiSettingsInfo>(`${BASE}/ai/settings`, { signal }),
  saveAiSettings: (enabled: boolean, allowPersonalData: boolean) => apiFetch<unknown>(`${BASE}/ai/settings`, { method: 'PUT', body: { enabled, allowPersonalData } }),
  aiJobDraft: (body: { title: string; department?: string; level?: string; skills?: string[]; responsibilities?: string[]; location?: string; workModel?: string; employmentType?: string; benefits?: string[]; tone?: string }) =>
    apiFetch<{ text: string; bias: import('./ai').BiasResult | null; model: string }>(`${BASE}/ai/job-draft`, { method: 'POST', body }),
  aiRewrite: (text: string, phrases: string[]) =>
    apiFetch<{ text: string; bias: import('./ai').BiasResult | null }>(`${BASE}/ai/inclusive-rewrite`, { method: 'POST', body: { text, phrases } }),
  aiPerfSummary: (body: { name: string; score?: number | null; previousScore?: number | null; goals: { title: string; progress: number | null }[];
    reviews: { type?: string | null; strengths?: string | null; improvements?: string | null; comments?: string | null }[]; feedback: string[] }) =>
    apiFetch<{ text: string; model: string }>(`${BASE}/ai/perf-summary`, { method: 'POST', body }),
  testIntegration: (id: string) => apiFetch<{ lastStatus: number | null; ok: boolean }>(`${BASE}/integrations/${id}/test`, { method: 'POST' }),


  /* takvim */
  calendarFeed: (signal?: AbortSignal) => apiFetch<{ path: string; createdAt: string }>(`${BASE}/calendar/feed`, { signal }),
  rotateCalendarFeed: () => apiFetch<{ path: string; createdAt: string }>(`${BASE}/calendar/feed/rotate`, { method: 'POST' }),

  /* asistan & rapor */
  examples: (signal?: AbortSignal) => apiFetch<string[]>(`${BASE}/insights/examples`, { signal }),
  report: (question: string) => apiFetch<NlReport>(`${BASE}/insights/report`, { method: 'POST', body: { question } }),
  assistant: (question: string) => apiFetch<AssistantReply>(`${BASE}/insights/assistant`, { method: 'POST', body: { question } }),
  kb: (signal?: AbortSignal) => apiFetch<KbArticle[]>(`${BASE}/insights/kb`, { signal }),
  createKb: (body: { title: string; body: string; tags: string[] }) => apiFetch<KbArticle>(`${BASE}/insights/kb`, { method: 'POST', body }),
  updateKb: (id: string, body: { title: string; body: string; tags: string[] }) => apiFetch<KbArticle>(`${BASE}/insights/kb/${id}`, { method: 'PUT', body }),
  deleteKb: (id: string) => apiFetch<void>(`${BASE}/insights/kb/${id}`, { method: 'DELETE' }),
  sampleKb: () => apiFetch<{ added: number }>(`${BASE}/insights/kb/samples`, { method: 'POST' }),

  /* saga */
  hireSagas: (signal?: AbortSignal) => apiFetch<HireSaga[]>(`${BASE}/sagas/offer-to-hire`, { signal }),
  advanceSaga: (applicationId: string, startDate?: string) =>
    apiFetch<{ log: string[]; saga: HireSaga }>(`${BASE}/sagas/offer-to-hire/${applicationId}/advance`, { method: 'POST', body: { startDate } }),
}

/* ============================== tenant-service: güvenlik ============================== */
export interface SsoStatus {
  domain: string | null
  providers: Array<{ alias: string; displayName: string | null; providerId: string | null; enabled: boolean; redirectUri: string }>
  supported: Array<{ id: 'google' | 'microsoft'; label: string; redirectUri: string }>
}
export interface MfaStatus {
  /** Güvenlik dalgası 2A: şirketin iki adımlı doğrulama politikası. */
  policy?: 'off' | 'privileged' | 'all'
  privilegedRoles?: string[]
  platformAdminRequired?: boolean
  members: number
  /** TOTP ya da passkey'i olan kullanıcılar. */
  withOtp: number
  pendingSetup: number
  without: number
  requiredWithout?: number
  users: Array<{ userId: string; username: string | null; hasOtp: boolean; hasPasskey?: boolean; pendingSetup: boolean; privileged?: boolean; required?: boolean }>
}
export const securityApi = {
  sso: (signal?: AbortSignal) => apiFetch<SsoStatus>('/api/tenant/security/sso', { signal }),
  addSso: (body: { provider: 'google' | 'microsoft'; clientId: string; clientSecret: string; directoryId?: string; domain?: string }) =>
    apiFetch<{ alias: string; redirectUri: string }>('/api/tenant/security/sso', { method: 'POST', body }),
  removeSso: (alias: string) => apiFetch<void>(`/api/tenant/security/sso/${encodeURIComponent(alias)}`, { method: 'DELETE' }),
  mfa: (signal?: AbortSignal) => apiFetch<MfaStatus>('/api/tenant/security/mfa', { signal }),
  enforceMfa: () => apiFetch<{ required: number; members: number }>('/api/tenant/security/mfa/enforce', { method: 'POST' }),
}
