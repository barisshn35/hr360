/**
 * Değerlendirme yardımcıları: uygulanabilir metrikler, istek gövdesi ve
 * tarayıcı yedeği.
 */

import { useMemo } from 'react'
import { useMetrics, useTeamsByEmployee, type Metric, type MetricScoreInput, type SubmitReviewInput } from '@/api/performance'
import { useDepartments, usePeople } from '../hooks'

/**
 * Bu çalışana uygulanan metrikler: genel metrikler + çalışanın departmanına
 * (ve üst departmanlarına) özgü olanlar.
 *
 * Departman, tam çalışan listesine erişim varsa oradan; yoksa (çalışan
 * rolü) çalışanın ekiplerinin departmanlarından çıkarılır. Hiçbiri
 * bilinmiyorsa yalnızca genel metrikler gösterilir; zorunlu metrik
 * eksikse backend `missing[]` ile söyler ve form onları vurgular.
 */
export function useApplicableMetrics(employeeId: string | undefined) {
  const metrics = useMetrics()
  const people = usePeople()
  const teams = useTeamsByEmployee(employeeId)
  const depts = useDepartments()

  return useMemo(() => {
    const direct = employeeId ? people.departmentOf(employeeId) : null
    const seeds = direct ? [direct] : (teams.data ?? []).map((t) => t.departmentId)
    const parentOf = new Map(depts.list.map((d) => [d.id, d.parentDepartmentId]))
    const ids = new Set<string>()
    for (const s of seeds) {
      let cur: string | null | undefined = s
      let guard = 0
      while (cur && !ids.has(cur) && guard++ < 8) {
        ids.add(cur)
        cur = parentOf.get(cur) ?? null
      }
    }
    const list: Metric[] = (metrics.data ?? []).filter((m) => m.isActive && (m.departmentId === null || ids.has(m.departmentId)))
    return {
      metrics: list,
      departmentKnown: ids.size > 0,
      isPending: metrics.isPending || (!direct && teams.isPending && Boolean(employeeId)),
      error: metrics.error,
    }
  }, [employeeId, metrics.data, metrics.isPending, metrics.error, people, teams.data, teams.isPending, depts.list])
}

/* --------------------------------- tarayıcı yedeği -------------------------------- */

/**
 * Taslağın asıl yeri sunucu (`PUT /reviews/{id}/draft`). Tarayıcıdaki kopya
 * yalnızca sunucuya henüz ulaşmamış değişiklikler içindir: her düzenlemede
 * yazılır, kayıt başarılı olunca silinir. Açılışta bulunursa "sunucuya
 * ulaşmamış değişiklik" olarak geri önerilir.
 */
export interface LocalDraft {
  scores: Record<string, { value: number | null; comment: string }>
  strengths: string
  improvements: string
  comments: string
  savedAt: string
}

const key = (id: string) => `hr360.review-draft.${id}`

export function readDraft(id: string): LocalDraft | null {
  try {
    const raw = window.localStorage.getItem(key(id))
    return raw ? (JSON.parse(raw) as LocalDraft) : null
  } catch {
    return null
  }
}

export function writeDraft(id: string, draft: Omit<LocalDraft, 'savedAt'>): string {
  const savedAt = new Date().toISOString()
  try {
    window.localStorage.setItem(key(id), JSON.stringify({ ...draft, savedAt }))
  } catch {
    /* depolama dolu ya da kapalı — form yine çalışır */
  }
  return savedAt
}

export function clearDraft(id: string) {
  try {
    window.localStorage.removeItem(key(id))
  } catch {
    /* yok say */
  }
}

export function toScoreInputs(scores: LocalDraft['scores']): MetricScoreInput[] {
  return Object.entries(scores)
    .filter(([, s]) => s.value !== null)
    .map(([metricId, s]) => ({ metricId, value: s.value as number, ...(s.comment.trim() ? { comment: s.comment.trim() } : {}) }))
}

/**
 * Formu istek gövdesine çevirir. Artık uygulanmayan (ör. arşivlenmiş)
 * metrikler gönderilmez. Taslakta boş metin alanı da gönderilir ki silinen
 * yorum sunucuda eski hâliyle kalmasın; gönderimde boş alan hiç yollanmaz.
 */
export function toReviewInput(f: Omit<LocalDraft, 'savedAt'>, metricIds: ReadonlySet<string>, mode: 'draft' | 'submit'): SubmitReviewInput {
  const text = (v: string) => (mode === 'draft' ? v.trim() : v.trim() || undefined)
  return {
    scores: toScoreInputs(f.scores).filter((s) => metricIds.has(s.metricId)),
    strengths: text(f.strengths),
    improvements: text(f.improvements),
    comments: text(f.comments),
  }
}

/** İki formun içerikçe aynı olup olmadığı (boşluk ve sıra farkı sayılmaz). */
export function sameContent(a: Omit<LocalDraft, 'savedAt'>, b: Omit<LocalDraft, 'savedAt'>): boolean {
  const norm = (f: Omit<LocalDraft, 'savedAt'>) =>
    JSON.stringify([
      Object.entries(f.scores)
        .filter(([, s]) => s.value !== null || s.comment.trim() !== '')
        .map(([id, s]) => [id, s.value, s.comment.trim()])
        .sort((x, y) => String(x[0]).localeCompare(String(y[0]))),
      f.strengths.trim(),
      f.improvements.trim(),
      f.comments.trim(),
    ])
  return norm(a) === norm(b)
}

/** Puanı verilmemiş metriğe yazılmış yorum sunucuya taşınamaz; yedek bu yüzden tutulur. */
export function hasUnsendable(f: Omit<LocalDraft, 'savedAt'>): boolean {
  return Object.values(f.scores).some((s) => s.value === null && s.comment.trim() !== '')
}
