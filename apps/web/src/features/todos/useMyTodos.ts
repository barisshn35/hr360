import { useQueries } from '@tanstack/react-query'
import { workflowApi } from '@/api/workflows'
import { docSignatureApi } from '@/api/docSignature'
import { complianceApi } from '@/api/compliance'
import { learningW11Api } from '@/api/learningW11'
import { engagementApi } from '@/api/engagement'
import { f360Api } from '@/api/performanceGrowth'
import { kvkk10Api } from '@/api/wave10'
import { onboardingApi } from '@/api/onboarding'
import { eomApi } from '@/api/eom'
import { useMyEmployeeId } from '@/api/queries'
import { workflowTypeLabels, type Workflow } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { tx } from '@/lib/i18n'

export type TodoKind = 'approval' | 'signature' | 'defence' | 'training' | 'onboarding' | 'survey' | 'consent' | 'announcement' | 'feedback360' | 'vote'

export interface Todo {
  id: string
  kind: TodoKind
  title: string
  detail?: string
  /** Son tarih (yyyy-aa-gg ya da ISO); yoksa sona sıralanır. */
  due?: string | null
  href: string
}

export const todoKindLabels = (): Record<TodoKind, string> => ({
  approval: tx('Onay'), signature: tx('İmza'), defence: tx('Savunma'), training: tx('Eğitim'), onboarding: tx('İşe giriş görevi'),
  survey: tx('Anket'), consent: tx('Bilgilendirme metni'), announcement: tx('Duyuru'), feedback360: tx('360 geri bildirim'), vote: tx('Ayın çalışanı'),
})

const ONE_MIN = 60_000

/**
 * Yapılacaklarım (yenilik haritası #2): modüllerde kişinin işlem yapması beklenen kayıtları tek listede
 * toplar. Her kaynak ayrı sorgudur ve yetkiye bağlıdır; biri hata verse de diğerleri gösterilir.
 * Sonuç son tarihe göre sıralıdır (tarihsizler sonda).
 */
export function useMyTodos() {
  const { can, tenantSlug } = useAuth()
  const me = useMyEmployeeId(!!tenantSlug).employeeId
  const on = !!tenantSlug
  const opt = { staleTime: ONE_MIN, retry: false } as const

  const results = useQueries({
    queries: [
      { queryKey: ['todos', 'approvals'], enabled: on && !!me && can('workflow:view'), ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => workflowApi.list({ status: 'Pending' }, signal).then((list): Todo[] => list
          .filter((w: Workflow) => {
            const step = [...(w.steps ?? [])].filter((s) => s.decision === 'Pending').sort((a, b) => a.order - b.order)[0]
            return step && w.requesterEmployeeId !== me && (step.approverEmployeeId === me || step.delegatedToEmployeeId === me)
          })
          .map((w) => ({ id: `wf:${w.id}`, kind: 'approval', title: w.subject || workflowTypeLabels[w.type], due: w.slaDueAt ?? null, href: `/panel/onaylar/${w.id}` }))) },
      { queryKey: ['todos', 'signatures'], enabled: on, ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => docSignatureApi.mine(signal).then((r): Todo[] => r.items
          .filter((s) => s.status === 'Pending')
          .map((s) => ({ id: `sig:${s.id}`, kind: 'signature', title: s.document?.fileName ?? tx('Belge'), detail: s.message ?? undefined, href: '/panel/imzalarim' }))) },
      { queryKey: ['todos', 'defences'], enabled: on, ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => complianceApi.myCases(signal).then((list): Todo[] => list
          .filter((c) => c.canSubmitDefence)
          .map((c) => ({ id: `def:${c.id}`, kind: 'defence', title: c.categoryLabel, due: c.defenceDeadline ?? null, href: '/panel/disiplin/savunmalarim' }))) },
      { queryKey: ['todos', 'training'], enabled: on && can('learning:view'), ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => learningW11Api.myDue(signal).then((r): Todo[] => r.items
          .filter((i) => i.status !== 'Completed' && i.status !== 'Done')
          .map((i) => ({ id: `tr:${i.kind}:${i.sourceId}`, kind: 'training', title: i.title, detail: i.mandatory ? tx('Zorunlu') : undefined, due: i.dueOn, href: '/panel/egitim-takibi' }))) },
      { queryKey: ['todos', 'onboarding', me], enabled: on && !!me && can('onboarding:view'), ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => onboardingApi.listPlans({ employeeId: me! }, signal).then((plans): Todo[] => plans
          .filter((p) => p.status !== 'Completed' && p.status !== 'Cancelled')
          .flatMap((p) => (p.tasks ?? [])
            .filter((t) => t.status !== 'Done' && (!t.assigneeEmployeeId || t.assigneeEmployeeId === me))
            .map((t) => ({ id: `ob:${t.id}`, kind: 'onboarding' as const, title: t.title, due: t.dueDate ?? null, href: `/panel/onboarding/${p.id}` })))) },
      { queryKey: ['todos', 'surveys'], enabled: on, ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => engagementApi.surveys(signal).then((list): Todo[] => list
          .filter((s) => s.status === 'Open' && !s.answered)
          .map((s) => ({ id: `sv:${s.id}`, kind: 'survey', title: s.title, due: s.closesAt ?? null, href: '/panel/anketler' }))) },
      { queryKey: ['todos', 'consents'], enabled: on, ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => kvkk10Api.pendingConsents(signal).then((list): Todo[] => list
          .map((c) => ({ id: `kv:${c.type}`, kind: 'consent', title: c.title, detail: c.reason === 'updated' ? tx('Güncellendi') : undefined, href: '/panel/profil?sekme=gizlilik' }))) },
      { queryKey: ['todos', 'announcements'], enabled: on, ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => complianceApi.announcements(signal).then((list): Todo[] => list
          .filter((a) => a.requiresAck && !a.read)
          .map((a) => ({ id: `an:${a.id}`, kind: 'announcement', title: a.title, detail: tx('Okundu onayı bekleniyor'), due: a.expireAt ?? null, href: '/panel/duyurular' }))) },
      { queryKey: ['todos', 'f360'], enabled: on && can('performance:view'), ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => f360Api.mine(signal).then((list): Todo[] => list
          .filter((i) => !i.submitted)
          .map((i) => ({ id: `f3:${i.id}`, kind: 'feedback360', title: i.subjectName ? tx('{0} için geri bildirim', [i.subjectName]) : i.title, detail: i.title, due: i.dueDate ?? null, href: '/panel/performans/360' }))) },
      { queryKey: ['todos', 'eom'], enabled: on, ...opt,
        queryFn: ({ signal }: { signal: AbortSignal }) => eomApi.me(signal).then((d): Todo[] => (d.units ?? [])
          .filter((u) => u.action && !u.myChoice)
          .map((u) => ({ id: `eom:${u.id}`, kind: 'vote', title: u.action === 'nominate' ? tx('{0}: aday gösterin', [u.name]) : tx('{0}: oyunuzu kullanın', [u.name]),
            due: u.stage === 2 ? d.period?.stage2EndsAt : u.action === 'nominate' ? d.period?.votingStartsAt : d.period?.stage1EndsAt, href: '/panel/ayin-calisani' }))) },
    ],
  })

  const items = results.flatMap((r) => (r.data as Todo[] | undefined) ?? [])
    .sort((a, b) => (a.due ? 0 : 1) - (b.due ? 0 : 1) || (a.due ?? '').localeCompare(b.due ?? ''))
  return { items, isPending: results.some((r) => r.isPending && r.fetchStatus !== 'idle'), failed: results.filter((r) => r.isError).length }
}
