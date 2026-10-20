import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ExternalLink } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/status-badge'
import { WorkflowStatusBadge } from '@/components/ui/module-badges'
import { CenteredSpinner, ErrorState } from '@/components/ui/states'
import { useToast } from '@/components/ui/toast'
import { Callout } from '@/components/ui/callout'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { workflowApi } from '@/api/workflows'
import { qk, useWorkflow, useMyEmployeeId } from '@/api/queries'
import { useDirectory } from '@/api/directory'
import { workflowTypeLabels, type ApprovalStep } from '@/api/types'
import { formatDate, formatDateTime, formatMoney, formatNumber, formatRelativeToNow } from '@/lib/format'
import { leaveApi, leaveTypeLabels, type LeaveType } from '@/api/leave'
import { cn } from '@/lib/utils'
import { ApprovalChain, activeStepId } from './ApprovalChain'
import { DecisionModal } from './DecisionModal'
import { DelegateModal } from './DelegateModal'
import { tx } from '@/lib/i18n'
import { useModules } from '@/lib/modules'

/** Talep yükündeki bilinen anahtarların okunur adları ("Talep bilgileri" listesi için). */
const PAYLOAD_LABELS: Record<string, string> = {
  type: tx('Tür'),
  startDate: tx('Başlangıç'),
  endDate: tx('Bitiş'),
  date: tx('Tarih'),
  days: tx('Gün'),
  hours: tx('Saat'),
  amount: tx('Tutar'),
  totalAmount: tx('Toplam tutar'),
  currency: tx('Para birimi'),
  category: tx('Kategori'),
  position: tx('Pozisyon'),
  candidate: tx('Aday'),
  grossSalary: tx('Brüt ücret'),
  expiresAt: tx('Geçerlilik sonu'),
  reason: tx('Gerekçe'),
  destination: tx('Gidilecek yer'),
  abroad: tx('Yurt dışı'),
  anomalyFlags: tx('Masraf denetimi işareti (insan incelemesi; otomatik ret yok)'),
  teamSize: tx('Ekip büyüklüğü (talep anında)'),
  teamOnLeave: tx('Aynı günlerde izinli / izin bekleyen ekip arkadaşı (talep anında)'),
}
/** Para birimiyle birlikte gösterilen tutar alanları (yükteki "currency" ile). */
const MONEY_KEYS = ['amount', 'totalAmount', 'grossSalary']
const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const ISO_DATE_RE = /^\d{4}-\d{2}-\d{2}(T[\d:.]+(Z|[+-]\d{2}:\d{2})?)?$/

/** Çalışan kaydı olmayan kullanıcının açtığı akışta talep eden kimliği. */
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'

function parsePayload(payload: string | null | undefined): Record<string, unknown> | null {
  if (!payload) return null
  try {
    const v: unknown = JSON.parse(payload)
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null
  } catch {
    return null
  }
}

/** Yükü okunur anahtar-değer listesine çevirir; kimlikler (…Id, GUID) gösterilmez. */
function payloadRows(p: Record<string, unknown>, skip: string[]): Array<{ key: string; label: string; value: string }> {
  const rows: Array<{ key: string; label: string; value: string }> = []
  for (const [key, raw] of Object.entries(p)) {
    if (skip.includes(key) || raw === null || raw === undefined || raw === '') continue
    if (/id$/i.test(key) || (typeof raw === 'string' && GUID_RE.test(raw))) continue
    let value: string
    if (key === 'currency' && Object.keys(p).some((k) => MONEY_KEYS.includes(k))) continue
    if (typeof raw === 'number' && MONEY_KEYS.includes(key))
      value = formatMoney(raw, typeof p.currency === 'string' && /^[A-Z]{3}$/.test(p.currency) ? p.currency : 'TRY')
    else if (typeof raw === 'number') value = formatNumber(raw)
    else if (typeof raw === 'boolean') value = raw ? tx('Evet') : tx('Hayır')
    else if (typeof raw === 'string')
      value =
        key === 'type' && raw in leaveTypeLabels
          ? leaveTypeLabels[raw as LeaveType]
          : ISO_DATE_RE.test(raw)
            ? raw.length > 10 ? formatDateTime(raw) : formatDate(raw)
            : raw
    else value = JSON.stringify(raw)
    rows.push({ key, label: PAYLOAD_LABELS[key] ?? key, value })
  }
  return rows
}

export function WorkflowDetailPage() {
  const { workflowId } = useParams<{ workflowId: string }>()
  const { can, roles } = useAuth()
  const { isEnabled } = useModules()
  const { employeeId: myEmployeeId } = useMyEmployeeId()
  const toast = useToast()
  const queryClient = useQueryClient()

  const workflow = useWorkflow(workflowId)
  // Ad çözümlemesi herkesin erişebildiği dizinden (tam liste çalışan rolüne 403 döner;
  // onaycı adı kimlik parçası olarak görünüyordu).
  const directory = useDirectory()
  const isLeave = workflow.data?.type === 'LeaveRequest'
  const leaveBalance = useQuery({
    queryKey: ['workflows', workflowId, 'leave-balance'],
    queryFn: ({ signal }) => workflowApi.leaveBalance(workflowId!, signal),
    enabled: Boolean(workflowId) && isLeave,
    retry: false,
  })

  // Madde 69: onaycıya ekip çakışmasının güncel hâli (adlar yalnızca ekibi görebilen yöneticiye/İK'ya döner).
  const leaveRequestId = isLeave ? (parsePayload(workflow.data?.payload)?.leaveRequestId as string | undefined) : undefined
  const teamConflict = useQuery({
    queryKey: ['workflows', workflowId, 'team-conflict', leaveRequestId],
    queryFn: ({ signal }) => leaveApi.teamConflict({ leaveRequestId }, signal),
    enabled: Boolean(leaveRequestId) && workflow.data?.requesterEmployeeId !== myEmployeeId,
    retry: false,
  })

  const [decision, setDecision] = useState<{ step: ApprovalStep; approve: boolean } | null>(null)
  const [delegateStep, setDelegateStep] = useState<ApprovalStep | null>(null)

  const employeeNames = useMemo(() => {
    const map = new Map<string, string>()
    for (const e of directory.data ?? []) map.set(e.id, e.fullName)
    return map
  }, [directory.data])

  const nameOf = (id: string | null | undefined) =>
    (id && employeeNames.get(id)) || (id === EMPTY_GUID ? tx('İK (çalışan kaydı yok)') : id ? `${id.slice(0, 8)}…` : '—')

  const steps = useMemo(
    () => [...(workflow.data?.steps ?? [])].sort((a, b) => a.order - b.order),
    [workflow.data],
  )

  const invalidate = () => {
    if (workflowId) void queryClient.invalidateQueries({ queryKey: qk.workflow(workflowId) })
    void queryClient.invalidateQueries({ queryKey: ['workflows'] })
    // Onay, izin ve bildirim tarafını Kafka üzerinden tetikliyor — o listeler de tazelensin.
    void queryClient.invalidateQueries({ queryKey: ['leave'] })
    void queryClient.invalidateQueries({ queryKey: ['notification'] })
  }

  const decide = useMutation({
    mutationFn: (v: { stepId: string; decision: 'Approved' | 'Rejected'; comment?: string }) =>
      workflowApi.decide(workflowId!, v.stepId, { decision: v.decision, comment: v.comment }),
    onSuccess: (_, v) => {
      invalidate()
      toast.ok(v.decision === 'Approved' ? tx('Adım onaylandı') : tx('Adım reddedildi'))
      setDecision(null)
    },
    onError: (e: unknown) =>
      toast.stop(
        e instanceof Error
          ? e.message
          : tx('Karar kaydedilemedi. Sayfayı yenileyip tekrar deneyin.'),
      ),
  })

  const delegate = useMutation({
    mutationFn: (v: { stepId: string; delegateToEmployeeId: string; comment?: string }) =>
      workflowApi.delegate(workflowId!, v.stepId, {
        delegateToEmployeeId: v.delegateToEmployeeId,
        comment: v.comment,
      }),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Adım devredildi'))
      setDelegateStep(null)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Devretme kaydedilemedi.')),
  })

  if (workflow.isPending) return <CenteredSpinner label={tx('Talep yükleniyor')} />

  if (workflow.isError || !workflow.data) {
    return (
      <Panel>
        <ErrorState
          title={tx('Talep bulunamadı')}
          message={workflow.error instanceof Error ? workflow.error.message : undefined}
          onRetry={() => void workflow.refetch()}
        />
      </Panel>
    )
  }

  const data = workflow.data
  const late = data.slaDueAt ? new Date(data.slaDueAt).getTime() < Date.now() : false
  const isOpen = data.status === 'Pending'
  const currentStep = steps.find((s) => s.id === activeStepId(steps))
  // Backend kuralları: talep sahibi kendi talebine karar veremez; İK dışındakiler
  // yalnızca kendilerine (ya da vekil olarak kendilerine devredilmiş) adıma karar
  // verir ve yalnızca kendi adımlarını devredebilir.
  const hr = isHr(roles)
  const isRequester = Boolean(myEmployeeId) && data.requesterEmployeeId === myEmployeeId
  const myStep =
    Boolean(myEmployeeId) &&
    (currentStep?.approverEmployeeId === myEmployeeId || currentStep?.delegatedToEmployeeId === myEmployeeId)
  // Akış tanımı (belirli kişi) ya da vekâletle yönetici olmayan biri de onaycı/vekil olabilir:
  // kendi adımı varsa rolüne bakılmaksızın karar verebilir (backend aynı kuralla denetler).
  const canDecide = isOpen && Boolean(myEmployeeId) && !isRequester && ((hr && can('workflow:decide')) || Boolean(myStep))
  const canDelegate =
    can('workflow:decide') && isOpen && (hr || (Boolean(myEmployeeId) && currentStep?.approverEmployeeId === myEmployeeId))
  const title = data.subject || workflowTypeLabels[data.type]
  // Gerekçe onaycıya ve İK'ya gösterilir; akış tanımında gizlenmişse sunucu onaycıya döndürmez.
  const payload = parsePayload(data.payload)
  const reason = typeof payload?.reason === 'string' && payload.reason.trim() ? payload.reason.trim() : null
  // Talep içeriği (tutar, gün, tarih...) onaycıya da gösterilir: bilgi olmadan karar verilmesin.
  // Akış tanımında gizlenen alanları sunucu onaycıya zaten döndürmez.
  // Çalışan kaydı olmayan İK kullanıcısının açtığı akışta (ör. iş teklifi) talep edenin adı yükte gelir.
  const requestedBy = typeof payload?.requestedBy === 'string' && payload.requestedBy.trim() ? payload.requestedBy.trim() : null
  const detailRows = payload ? payloadRows(payload, ['reason', 'requestedBy']) : []
  const expenseClaimId = data.type === 'ExpenseClaim' && typeof payload?.expenseClaimId === 'string' ? payload.expenseClaimId : null
  const bal = leaveBalance.data

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/onaylar">
          <ArrowLeft className="size-4" />
          {tx('Onay kutusu')}
        </Link>
      </Button>

      <PageHeader
        title={title}
        description={
          isOpen && currentStep
            ? tx('{0}. adımda: {1} karar veriyor.', [currentStep.order, nameOf(currentStep.approverEmployeeId)])
            : undefined
        }
        actions={<WorkflowStatusBadge status={data.status} />}
      />

      {late && isOpen && (
        <div role="alert">
          <Callout tone="danger" title={tx('SLA süresi doldu')}>
            {tx('Hedef {0} idi, {1}.', [formatDateTime(data.slaDueAt), formatRelativeToNow(data.slaDueAt)])}
          </Callout>
        </div>
      )}

      <div className="grid gap-4 xl:grid-cols-[1.5fr_1fr]">
        <Panel>
          <PanelHead
            title={tx('Onay zinciri')}
            note={tx('Adımlar sırayla işler; önceki karara bağlanmadan sonraki açılmaz')}
            action={
              <span className="tabular text-[12px] text-muted-foreground">
                {tx('{0}/{1} adım', [steps.filter((s) => s.decision !== 'Pending').length, steps.length])}</span>
            }
          />
          {steps.length === 0 ? (
            <p className="px-4 py-10 text-center text-[13px] text-muted-foreground">
              {tx('Bu talebe onay adımı tanımlanmamış.')}
            </p>
          ) : (
            <ApprovalChain
              steps={steps}
              nameOf={nameOf}
              canDecide={canDecide}
              canDelegate={canDelegate}
              onDecide={(step, approve) => setDecision({ step, approve })}
              onDelegate={setDelegateStep}
              busyStepId={decide.isPending ? decision?.step.id : null}
            />
          )}
        </Panel>

        <div className="space-y-4">
          <Panel>
            <PanelHead title={tx('Talep bilgileri')} />
            <PanelBody>
              <dl className="divide-y divide-border">
                <div className="flex items-baseline justify-between gap-4 pb-2.5">
                  <dt className="text-[12px] text-muted-foreground">{tx('Talep eden')}</dt>
                  <dd className="text-right text-[13px]">{data.requesterEmployeeId === EMPTY_GUID && requestedBy ? tx('{0} (İK)', [requestedBy]) : nameOf(data.requesterEmployeeId)}</dd>
                </div>
                <div className="flex items-baseline justify-between gap-4 py-2.5">
                  <dt className="text-[12px] text-muted-foreground">{tx('Tür')}</dt>
                  <dd className="text-right text-[13px]">{workflowTypeLabels[data.type]}</dd>
                </div>
                <div className="flex items-baseline justify-between gap-4 py-2.5">
                  <dt className="text-[12px] text-muted-foreground">{tx('Açılış')}</dt>
                  <dd className="tabular text-right text-[13px]">
                    {formatDateTime(data.createdAt)}
                  </dd>
                </div>
                {detailRows.map((r) => (
                  <div key={r.key} className="flex items-baseline justify-between gap-4 py-2.5">
                    <dt className="text-[12px] text-muted-foreground">{r.label}</dt>
                    <dd className="tabular text-right text-[13px] break-words">{r.value}</dd>
                  </div>
                ))}
                {bal?.available && (
                  <div className="flex items-baseline justify-between gap-4 py-2.5">
                    <dt className="text-[12px] text-muted-foreground">
                      {tx('Kalan bakiye ({0})', [bal.year ?? ''])}
                    </dt>
                    <dd className="tabular text-right text-[13px]">
                      <span className="font-semibold">{tx('{0} gün', [formatNumber(bal.remainingDays)])}</span>
                      <span className="block text-[11.5px] text-muted-foreground">
                        {tx('Hak {0} · kullanılan {1} · bekleyen {2}', [formatNumber(bal.entitledDays), formatNumber(bal.usedDays), formatNumber(bal.pendingDays)])}
                      </span>
                    </dd>
                  </div>
                )}
                {teamConflict.data?.enabled && teamConflict.data.overlapping != null && (
                  <div className="flex items-baseline justify-between gap-4 py-2.5">
                    <dt className="text-[12px] text-muted-foreground">{tx('Ekip çakışması (güncel)')}</dt>
                    <dd className="text-right text-[13px]">
                      <span className={cn('font-semibold', teamConflict.data.exceeds && 'text-[hsl(var(--warning))]')}>
                        {tx('{0} / {1} kişi · %{2} (eşik %{3})', [teamConflict.data.overlapping, teamConflict.data.teamSize ?? 0, teamConflict.data.percent ?? 0, teamConflict.data.thresholdPercent])}
                      </span>
                      {teamConflict.data.people && teamConflict.data.people.length > 0 && (
                        <span className="block text-[11.5px] text-muted-foreground">
                          {teamConflict.data.people.map((p) => `${p.name ?? '—'} (${formatDate(p.startDate)}–${formatDate(p.endDate)})`).join(', ')}
                        </span>
                      )}
                    </dd>
                  </div>
                )}
                <div className={cn('flex items-baseline justify-between gap-4', reason ? 'py-2.5' : 'pt-2.5')}>
                  <dt className="text-[12px] text-muted-foreground">{tx('SLA hedefi')}</dt>
                  <dd
                    className={cn(
                      'tabular text-right text-[13px]',
                      late && 'font-semibold text-destructive',
                    )}
                  >
                    {data.slaDueAt ? formatDateTime(data.slaDueAt) : tx('Tanımlanmamış')}
                  </dd>
                </div>
                {reason && (
                  <div className="pt-2.5">
                    <dt className="text-[12px] text-muted-foreground">{tx('Gerekçe')}</dt>
                    <dd className="mt-1 text-[13px] leading-relaxed break-words whitespace-pre-wrap">{reason}</dd>
                  </div>
                )}
              </dl>

              {expenseClaimId && can('expense:manage') && isEnabled('expense') && (
                <div className="mt-4 border-t border-border pt-3">
                  <Button variant="outline" size="sm" asChild>
                    <Link to={`/panel/masraf/${expenseClaimId}`}>
                      <ExternalLink className="size-4" />
                      {tx('Masraf beyanını ve kalemleri aç')}
                    </Link>
                  </Button>
                </div>
              )}

              {data.slaDueAt && isOpen && (
                <div className="mt-4 border-t border-border pt-3">
                  <StatusBadge tone={late ? 'danger' : 'neutral'}>
                    {late ? tx('Süresi geçti') : tx('Kalan süre {0}', [formatRelativeToNow(data.slaDueAt)])}
                  </StatusBadge>
                </div>
              )}
            </PanelBody>
          </Panel>

        </div>
      </div>

      <DecisionModal
        state={decision}
        onClose={() => setDecision(null)}
        pending={decide.isPending}
        onConfirm={(comment) => {
          if (!decision) return
          decide.mutate({
            stepId: decision.step.id,
            decision: decision.approve ? 'Approved' : 'Rejected',
            comment,
          })
        }}
      />

      <DelegateModal
        step={delegateStep}
        requesterEmployeeId={data.requesterEmployeeId}
        onClose={() => setDelegateStep(null)}
        pending={delegate.isPending}
        onConfirm={(delegateToEmployeeId, comment) => {
          if (!delegateStep) return
          delegate.mutate({ stepId: delegateStep.id, delegateToEmployeeId, comment })
        }}
      />
    </div>
  )
}
