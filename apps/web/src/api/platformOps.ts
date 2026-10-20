import { apiFetch } from './client'

/** Arayüzden bakım işleri (tenant-service /api/platform/ops). Komutları sunucudaki çalıştırıcı yürütür. */
const B = '/api/tenant/platform/ops'

export type OpsKind = 'backup' | 'backup-verify' | 'schedule' | 'unschedule' | 'check-updates' | 'update' | 'rollback' | 'restore' | 'db-isolation'
  | 'pitr-enable' | 'pitr-disable' | 'pitr-restore' | 'pitr-cleanup' | 'modules-set' | 'tenancy-multi' | 'tenancy-single'
  | 'monitoring-enable' | 'monitoring-disable' | 'monitoring-alerts' | 'monitoring-test' | 'monitoring-tracing'
export type OpsStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'

export interface OpsBackup { name: string; size: number; createdAt: string; encrypted: boolean; manifest: boolean }
/** Sunucu güvenlik duruşu (yalnızca açık/kapalı; sır değeri yok). Eski çalıştırıcıda yoktur. */
export interface OpsSecurity {
  dbIsolationOff: boolean
  dbRoles: number
  rlsTables: string
  tls: boolean
  backupEncrypted: boolean
  backupScheduled: boolean
  backupOffsite: boolean
  keyRotation: boolean
  redisPassword: boolean
  monitoring: boolean
  publicSignup: boolean
}
/** Anlık geri dönüş (scripts/pitr.sh status). Zamanlar ISO UTC; boyutlar "180M" gibi. Eski çalıştırıcıda yoktur. */
export interface OpsPitr {
  state: 'on' | 'off' | 'not-ready' | 'unknown'
  enabled: boolean
  walFiles: number | null
  walSize: string | null
  baseSize: string | null
  bases: { name: string; finishedAt: string }[]
  /** Geri dönülebilecek en eski an: en eski temel yedeğin bitişi. */
  oldestRecoverable: string | null
  lastArchivedWal: string | null
  lastArchivedAt: string | null
  failedCount: number
  lastFailedWal: string | null
  /** Geri dönüşten önceki veri dizini kopyaları (silinene kadar disk kaplar). */
  preRestore: { name: string; size: string | null }[]
}
/** İsteğe bağlı modüller (scripts/modules.sh json). Kimlikler tablo sırasıyla. Eski çalıştırıcıda yoktur. */
export interface OpsModules {
  enabled: string[]
  all: string[]
  details: { id: string; name: string; memoryMb: number | null; services: string[] }[]
}
/** Şirket kipi (scripts/tenancy.sh status). tenants null: veritabanına ulaşılamadı. Eski çalıştırıcıda yoktur. */
export interface OpsTenancy {
  mode: 'single' | 'multi' | 'unknown'
  tenants: number | null
}
/** İzleme yığını (Prometheus, Grafana, Alertmanager…). Webhook adresi yerine yalnızca kökeni gelir. Eski çalıştırıcıda yoktur. */
export interface OpsMonitoring {
  enabled: boolean
  /** Çalışan bileşen sayısı; izleme kapalıysa ya da okunamadıysa null. */
  running: number | null
  total: number
  alerts: { email: string[]; slack: string | null; teams: string | null }
  tracing: { on: boolean; ratio: string | null }
  /** Grafana'ya HR360 hesabıyla giriş ayarlı mı. */
  sso: boolean
}
export interface OpsSnapshot {
  version: string | null
  commit: string
  commitDate: string
  tags: string[]
  backups: OpsBackup[]
  backupStatus: string
  updateStatus: string
  diskFree: number
  diskTotal: number
  host: string
  runnerAt: string
  security?: OpsSecurity
  pitr?: OpsPitr
  modules?: OpsModules
  tenancy?: OpsTenancy
  monitoring?: OpsMonitoring
  /** Sunucu belleği (bayt). */
  memory?: { total: number; available: number }
}
export interface OpsJob {
  id: string
  kind: OpsKind
  args: Record<string, unknown>
  status: OpsStatus
  requestedBy: string | null
  requestedAt: string
  startedAt: string | null
  finishedAt: string | null
  exitCode: number | null
  output: string | null
}

export const platformOpsApi = {
  status: (signal?: AbortSignal) => apiFetch<{ runnerOk: boolean; updatedAt: string | null; snapshot: OpsSnapshot | null; activeJobs: number }>(`${B}/status`, { signal }),
  jobs: (signal?: AbortSignal) => apiFetch<OpsJob[]>(`${B}/jobs`, { signal }),
  job: (id: string, signal?: AbortSignal) => apiFetch<OpsJob>(`${B}/jobs/${id}`, { signal }),
  enqueue: (kind: OpsKind, args: Record<string, unknown> = {}, confirm?: string) => apiFetch<{ id: string; status: OpsStatus }>(`${B}/jobs`, { method: 'POST', body: { kind, args, confirm } }),
  cancel: (id: string) => apiFetch<{ id: string }>(`${B}/jobs/${id}/cancel`, { method: 'POST' }),
}

/** "v1.2.3" → [1,2,3]; karşılaştırma için. */
export function semver(v: string | null | undefined): number[] {
  return (v ?? '').replace(/^v/, '').split('.').map((x) => Number.parseInt(x, 10) || 0)
}
export function newerThan(a: string, b: string | null | undefined): boolean {
  const x = semver(a), y = semver(b)
  for (let i = 0; i < 3; i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) > (y[i] ?? 0)
  return false
}

const TZ = 'Europe/Istanbul'
const istanbulParts = new Intl.DateTimeFormat('en-CA', { timeZone: TZ, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })

/** Anı İstanbul saatiyle "YYYY-AA-GG SS:DD" yazar (geri dönüş hedefi bu biçimde ve bu saatte gönderilir). */
export function istanbulMinute(d: Date): string {
  const p = Object.fromEntries(istanbulParts.formatToParts(d).map((x) => [x.type, x.value]))
  return `${p.year}-${p.month}-${p.day} ${p.hour}:${p.minute}`
}

/** Geri dönülebilecek aralık (İstanbul saati, dakika): en eski temel yedeğin bitişinden sonraki ilk dakika … şimdiki dakika. */
export function pitrWindow(oldestRecoverable: string | null, now = new Date()): { min: string; max: string } | null {
  const t = oldestRecoverable ? Date.parse(oldestRecoverable) : NaN
  if (Number.isNaN(t)) return null
  const min = istanbulMinute(new Date(Math.floor(t / 60_000) * 60_000 + 60_000))
  const max = istanbulMinute(now)
  return min <= max ? { min, max } : null
}

/** Hedef bu aralıkta ve sunucunun beklediği biçimde mi (sunucu ayrıca doğrular). */
export function validPitrTarget(target: string, w: { min: string; max: string } | null): boolean {
  return !!w && /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/.test(target) && target >= w.min && target <= w.max
}
