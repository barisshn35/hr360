import { apiFetch } from './client'

/** Arayüzden bakım işleri (tenant-service /api/platform/ops). Komutları sunucudaki çalıştırıcı yürütür. */
const B = '/api/tenant/platform/ops'

export type OpsKind = 'backup' | 'backup-verify' | 'schedule' | 'unschedule' | 'check-updates' | 'update' | 'rollback' | 'restore'
export type OpsStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'

export interface OpsBackup { name: string; size: number; createdAt: string; encrypted: boolean; manifest: boolean }
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
