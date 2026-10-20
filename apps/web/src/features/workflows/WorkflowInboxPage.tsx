import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Check, Plus, UserRoundCog, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { DataTable, type Column } from '@/components/ui/data-table'
import { StatusBadge } from '@/components/ui/status-badge'
import { WorkflowStatusBadge } from '@/components/ui/module-badges'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/tabs'
import { InfoNote } from '@/components/ui/states'
import { useAuth } from '@/auth/useAuth'
import { useMyEmployeeId, useOverdueWorkflows, useWorkflows } from '@/api/queries'
import { useDirectory } from '@/api/directory'
import { useConfirm } from '@/components/ui/confirm'
import { Modal } from '@/components/ui/modal'
import { TextAreaField } from '@/components/ui/field'
import { workflowApi } from '@/api/workflows'
import { useAction } from '@/features/shared/kit'
import { DelegationsModal } from './DelegationsModal'
import {
  workflowStatusLabels,
  workflowTypeLabels,
  type Workflow,
  type WorkflowStatus,
} from '@/api/types'
import { formatDate, formatRelativeToNow } from '@/lib/format'
import { NewWorkflowModal } from './NewWorkflowModal'
import { useNewParam } from '@/lib/useNewParam'
import { tx, txServer } from '@/lib/i18n'

type TabKey = WorkflowStatus | 'all' | 'gecikmis'

/** Toplu kararın kalem sonucu (sunucu iletileri apiFetch'te arayüz diline çevrilir). */
/** workflow-service toplu karar iletisi (iptal edilmiş talepler için de aynı ileti döner). */
const ALREADY_DECIDED = 'Zaten karara bağlanmış'

interface BulkOutcome { workflowId: string; label: string; ok: boolean; error: string | null }

export function WorkflowInboxPage() {
  const { can } = useAuth()
  const navigate = useNavigate()
  const [modalOpen, setModalOpen] = useState(false)
  useNewParam(can('workflow:create'), () => setModalOpen(true))
  const [delegating, setDelegating] = useState(false)
  const me = useMyEmployeeId()
  const [tab, setTab] = useTabParam<TabKey>('durum', 'Pending')

  // "Süresi geçen" ayrı bir uçtan gelir; yalnızca karar verebilenlere gösterilir.
  const canSeeOverdue = can('workflow:decide')
  const isOverdueTab = tab === 'gecikmis' && canSeeOverdue

  const list = useWorkflows(tab === 'all' || tab === 'gecikmis' ? {} : { status: tab }, {
    enabled: !isOverdueTab,
  })
  const overdue = useOverdueWorkflows({ enabled: isOverdueTab })
  // Ad çözümlemesi herkesin erişebildiği dizinden (tam çalışan listesi çalışan rolüne 403 döner).
  const directory = useDirectory()

  const query = isOverdueTab ? overdue : list

  const employeeNames = useMemo(() => {
    const map = new Map<string, string>()
    for (const e of directory.data ?? []) map.set(e.id, e.fullName)
    return map
  }, [directory.data])

  // Çalışan kaydı olmayan İK kullanıcısının açtığı akışta (ör. iş teklifi) talep eden boş kimliktir.
  const nameOf = (id: string) =>
    employeeNames.get(id) ?? (id === '00000000-0000-0000-0000-000000000000' ? tx('İK (çalışan kaydı yok)') : `${id.slice(0, 8)}…`)

  const rows = useMemo(
    () =>
      [...(query.data ?? [])].sort(
        (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
      ),
    [query.data],
  )

  // Toplu karar: seçilen taleplerde sırası gelmiş ve onaycısı (ya da vekili) ben olan adım.
  const myStep = (w: Workflow) => {
    const step = [...(w.steps ?? [])].filter((s) => s.decision === 'Pending').sort((a, b) => a.order - b.order)[0]
    return step && me.employeeId && w.requesterEmployeeId !== me.employeeId
      && (step.approverEmployeeId === me.employeeId || step.delegatedToEmployeeId === me.employeeId) ? step : undefined
  }
  const [bulkResult, setBulkResult] = useState<{ done: number; approve: boolean; results: BulkOutcome[] } | null>(null)
  // Toplu karardan sonra tablodaki seçim temizlenir ("2 satır seçili" şeridi kalmasın).
  const [selectionReset, setSelectionReset] = useState(0)
  // Toplu karar (en fazla 50'lik parçalar): sırası bana gelmemiş seçimler istemcide atlanır; sunucu her
  // kalemi tekil karar ucuyla AYNI kurallarla (kendi talebi, onaycı/vekil, sıralı onay) yeniden denetler.
  const bulk = useAction(async ({ ids, approve, comment }: { ids: string[]; approve: boolean; comment?: string }) => {
    const chosen = rows.filter((w) => ids.includes(w.id))
    const label = (w: Workflow) => w.subject || workflowTypeLabels[w.type]
    const results: BulkOutcome[] = []
    const items: Array<{ workflowId: string; stepId: string }> = []
    for (const w of chosen) {
      const st = myStep(w)
      if (st) items.push({ workflowId: w.id, stepId: st.id })
      else results.push({ workflowId: w.id, label: label(w), ok: false, error: tx('Sırası size gelmemiş ya da onaycısı değilsiniz') })
    }
    let done = 0
    for (let i = 0; i < items.length; i += 50) {
      const r = await workflowApi.bulkDecide(items.slice(i, i + 50), approve ? 'Approved' : 'Rejected', comment)
      done += r.done
      for (const it of r.results) {
        const w = chosen.find((x) => x.id === it.workflowId)
        results.push({ workflowId: it.workflowId, label: w ? label(w) : it.workflowId.slice(0, 8), ok: it.ok, error: it.error ? txServer(it.error) : it.error })
      }
    }
    // Sunucu "zaten karara bağlanmış" der; arada talep sahibi iptal ettiyse bunu açıkça söyleyelim.
    await Promise.all(results.map(async (res, i) => {
      if (res.ok || res.error !== txServer(ALREADY_DECIDED)) return
      try {
        const cur = await workflowApi.get(res.workflowId)
        if (cur.status === 'Cancelled') results[i] = { ...res, error: tx('Talep sahibi tarafından iptal edilmiş') }
        else if (cur.status === 'Approved' || cur.status === 'Rejected') results[i] = { ...res, error: tx('Zaten karara bağlanmış ({0})', [workflowStatusLabels[cur.status]]) }
      } catch { /* görülemiyorsa sunucu iletisi kalır */ }
    }))
    return { done, approve, results }
  }, {
    success: (r) => tx('{0} talep karara bağlandı', [r.done]) + (r.results.length > r.done ? ' · ' + tx('{0} talep atlandı', [r.results.length - r.done]) : ''),
    invalidate: [['workflows']],
    onDone: (r) => { setBulkResult(r); setSelectionReset((n) => n + 1) },
  })
  // Toplu karar geri alınamaz: onayda kaç talep olduğu sorulur; rette (tekil retteki gibi)
  // talep sahiplerinin göreceği gerekçe zorunludur.
  const confirm = useConfirm()
  const [rejecting, setRejecting] = useState<{ ids: string[]; n: number } | null>(null)
  const [rejectReason, setRejectReason] = useState('')
  const [rejectError, setRejectError] = useState<string | undefined>()
  const askBulkApprove = async (ids: string[], n: number) => {
    if (await confirm({
      title: tx('{0} talep onaylansın mı?', [n]),
      note: tx('Seçtiğiniz taleplerde sırası size gelmiş adımlar onaylanır. Bu işlem geri alınamaz.'),
      action: tx('Toplu onayla'),
      destructive: false,
    })) bulk.mutate({ ids, approve: true })
  }
  const submitBulkReject = () => {
    if (!rejecting) return
    const reason = rejectReason.trim()
    if (reason.length < 3) {
      setRejectError(tx('Ret gerekçesi zorunlu (en az 3 karakter).'))
      return
    }
    bulk.mutate({ ids: rejecting.ids, approve: false, comment: reason }, {
      onSuccess: () => { setRejecting(null); setRejectReason(''); setRejectError(undefined) },
    })
  }

  const tabs: Array<TabDef<TabKey>> = [
    { key: 'Pending', label: workflowStatusLabels.Pending },
    ...(canSeeOverdue ? [{ key: 'gecikmis' as TabKey, label: tx('Süresi geçen') }] : []),
    { key: 'Approved', label: workflowStatusLabels.Approved },
    { key: 'Rejected', label: workflowStatusLabels.Rejected },
    { key: 'all', label: tx('Tümü') },
  ]

  const columns: Array<Column<Workflow>> = [
    {
      id: 'subject',
      header: tx('Talep'),
      searchText: (w) => `${w.subject ?? ''} ${workflowTypeLabels[w.type]}`,
      sortValue: (w) => w.subject || workflowTypeLabels[w.type],
      cell: (w) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">
            {w.subject || workflowTypeLabels[w.type]}
          </p>
          <p className="mt-0.5 truncate text-[12px] text-muted-foreground">
            {workflowTypeLabels[w.type]}
          </p>
          {/* Dar ekranda (390px) talep eden ve durum sütunları sığmıyor: burada gösterilir. */}
          <div className="mt-1 flex flex-wrap items-center gap-2 md:hidden">
            <WorkflowStatusBadge status={w.status} />
            <span className="truncate text-[12px] text-muted-foreground">{nameOf(w.requesterEmployeeId)}</span>
          </div>
        </div>
      ),
    },
    {
      id: 'requester',
      header: tx('Talep eden'),
      hideBelow: 'md',
      searchText: (w) => nameOf(w.requesterEmployeeId),
      sortValue: (w) => nameOf(w.requesterEmployeeId),
      exportText: (w) => nameOf(w.requesterEmployeeId),
      cell: (w) => <span className="text-muted-foreground">{nameOf(w.requesterEmployeeId)}</span>,
    },
    {
      id: 'createdAt',
      header: tx('Açılış'),
      hideBelow: 'lg',
      sortValue: (w) => new Date(w.createdAt).getTime(),
      exportText: (w) => formatDate(w.createdAt),
      cell: (w) => <span className="tabular text-muted-foreground">{formatDate(w.createdAt)}</span>,
    },
    {
      id: 'sla',
      header: tx('SLA'),
      hideBelow: 'sm',
      sortValue: (w) => (w.slaDueAt ? new Date(w.slaDueAt).getTime() : Number.MAX_SAFE_INTEGER),
      // Ekranla aynı: SLA'sız talepte "—".
      exportText: (w) => (w.slaDueAt ? formatDate(w.slaDueAt) : '—'),
      cell: (w) => {
        if (!w.slaDueAt) return <span className="text-muted-foreground">—</span>
        const late = new Date(w.slaDueAt).getTime() < Date.now()
        if (w.status !== 'Pending')
          return <span className="tabular text-muted-foreground">{formatDate(w.slaDueAt)}</span>
        return (
          <StatusBadge tone={late ? 'danger' : 'neutral'}>
            {late ? tx('Süresi geçti') : formatRelativeToNow(w.slaDueAt)}
          </StatusBadge>
        )
      },
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      hideBelow: 'md',
      sortValue: (w) => workflowStatusLabels[w.status],
      exportText: (w) => workflowStatusLabels[w.status],
      cell: (w) => <WorkflowStatusBadge status={w.status} />,
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Onay kutusu')}
        description={tx('İzin, masraf ve pozisyon talepleri tanımlı sırayla ilerler.')}
        actions={
          <div className="flex flex-wrap gap-2">
            {can('workflow:decide') && (
              <Button variant="outline" className="cursor-pointer" onClick={() => setDelegating(true)}>
                <UserRoundCog className="size-4" />
                {tx('Vekâlet')}
              </Button>
            )}
            {can('workflow:create') && (
              <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
                <Plus className="size-4" />
                {tx('Yeni talep')}
              </Button>
            )}
          </div>
        }
      />

      <Tabs tabs={tabs} value={tab} onChange={setTab} label={tx('Talep durumu')} />

      <DataTable
        rows={rows}
        rowKey={(w) => w.id}
        columns={columns}
        isLoading={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        onRowClick={(w) => navigate(`/panel/onaylar/${w.id}`)}
        searchPlaceholder={tx('Talep veya tür ara')}
        exportFileName="onay-talepleri"
        viewKey="workflow-inbox"
        pageSize={12}
        selectable={can('workflow:decide') && (tab === 'Pending' || isOverdueTab)}
        selectionResetKey={selectionReset}
        bulkActions={(ids) => {
          const n = rows.filter((w) => ids.includes(w.id) && myStep(w)).length
          return (
            <>
              <span className="text-[12.5px] text-muted-foreground">{tx('Kararınızı bekleyen: {0}', [n])}</span>
              <Button size="sm" disabled={!n || bulk.isPending} onClick={() => void askBulkApprove(ids, n)}><Check className="size-4" /> {tx('Toplu onayla')}</Button>
              <Button size="sm" variant="outline" disabled={!n || bulk.isPending} onClick={() => { setRejectReason(''); setRejectError(undefined); setRejecting({ ids, n }) }}><X className="size-4" /> {tx('Toplu reddet')}</Button>
            </>
          )
        }}
        emptyTitle={isOverdueTab ? tx('Süresi geçen talep yok') : tx('Bu durumda talep yok')}
        emptyDetail={
          isOverdueTab
            ? tx('Açık taleplerin tümü SLA süresi içinde ilerliyor.')
            : tx('Başka bir durum sekmesine geçerek diğer talepleri görebilirsiniz.')
        }
        notice={
          <InfoNote>
            {can('workflow:decide')
              ? tx('Kararı talebin sayfasındaki onay zincirinden ya da birden çok talebi seçip toplu olarak verebilirsiniz. Bir izin talebi onaylandığında izin kaydı ve bakiye kendiliğinden güncellenir.')
              : tx('Açtığınız ve onaycısı olduğunuz talepler burada listelenir. Ayrıntı ve onay zinciri için talebe tıklayın.')}
          </InfoNote>
        }
      />

      <Modal
        open={rejecting !== null}
        onClose={() => !bulk.isPending && setRejecting(null)}
        title={tx('{0} talep reddedilsin mi?', [rejecting?.n ?? 0])}
        note={tx('Gerekçe tüm talep sahiplerine gösterilir ve onay geçmişine yazılır.')}
        footer={
          <>
            <Button variant="outline" onClick={() => setRejecting(null)} disabled={bulk.isPending}>{tx('Vazgeç')}</Button>
            <Button type="submit" form="bulk-reject-form" variant="destructive" disabled={bulk.isPending}>{tx('Toplu reddet')}</Button>
          </>
        }
      >
        <form id="bulk-reject-form" noValidate onSubmit={(e) => { e.preventDefault(); submitBulkReject() }}>
          <TextAreaField
            id="bulk-reject-reason"
            label={tx('Gerekçe')}
            required
            rows={4}
            value={rejectReason}
            maxLength={1000}
            hint={tx('Zorunlu. Talep sahibi bu gerekçeyi görür.')}
            onChange={(e) => setRejectReason(e.target.value)}
            error={rejectError}
          />
        </form>
      </Modal>

      {/* Dalga 12: toplu kararın kalem kalem sonucu. */}
      <Modal
        open={bulkResult !== null}
        onClose={() => setBulkResult(null)}
        title={bulkResult?.approve ? tx('Toplu onay sonucu') : tx('Toplu ret sonucu')}
        note={tx('{0} talep karara bağlandı, {1} talep atlandı.', [bulkResult?.done ?? 0, (bulkResult?.results.length ?? 0) - (bulkResult?.done ?? 0)])}
        footer={<Button onClick={() => setBulkResult(null)}>{tx('Tamam')}</Button>}
      >
        <ul className="max-h-[50vh] divide-y divide-border overflow-y-auto text-[13px]">
          {bulkResult?.results.map((r) => (
            <li key={r.workflowId} className="flex items-start gap-2 py-2">
              {r.ok ? <Check className="mt-0.5 size-4 shrink-0 text-primary" /> : <X className="mt-0.5 size-4 shrink-0 text-destructive" />}
              <span className="min-w-0 flex-1">
                <button type="button" className="block max-w-full cursor-pointer truncate text-left font-medium hover:underline" onClick={() => navigate(`/panel/onaylar/${r.workflowId}`)}>{r.label}</button>
                <span className="block text-[12px] text-muted-foreground">{r.ok ? (bulkResult.approve ? tx('Onaylandı') : tx('Reddedildi')) : r.error}</span>
              </span>
            </li>
          ))}
        </ul>
      </Modal>

      <NewWorkflowModal open={modalOpen} onClose={() => setModalOpen(false)} />
      {delegating && <DelegationsModal onClose={() => setDelegating(false)} myId={me.employeeId} />}
    </div>
  )
}
