import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { motion, useReducedMotion } from 'motion/react'
import { ArrowLeft, LoaderCircle, Pencil, ShieldAlert, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { DataField, Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ClaimStatusBadge } from '@/components/ui/ModuleBadges'
import { CenteredSpinner, EmptyState, ErrorState, InfoNote } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { expenseApi } from '@/api/expense'
import { useExpenseClaim, useMyEmployeeId, useWorkflow } from '@/api/queries'
import { expenseCategoryLabels } from '@/api/types'
import { formatDate, formatDateTime, formatMoney, formatNumber } from '@/lib/format'
import { useEmployeeName } from '@/lib/useEmployeeName'
import { tx } from '@/lib/i18n'
import { flagSummary, flagText, severityLabel, severityTone } from '@/lib/expenseAudit'
import { NewClaimModal } from './NewClaimModal'

export function ExpenseClaimPage() {
  const { claimId } = useParams<{ claimId: string }>()
  const { can, roles } = useAuth()
  const { employeeId: myEmployeeId } = useMyEmployeeId()
  const toast = useToast()
  const queryClient = useQueryClient()
  const reduced = useReducedMotion()

  const claim = useExpenseClaim(claimId)
  const nameOf = useEmployeeName()
  const confirm = useConfirm()
  const navigate = useNavigate()
  const [editOpen, setEditOpen] = useState(false)
  // Karar yorumu (ret gerekçesi) onay akışının adımlarında tutulur; talep sahibi kendi akışını okuyabilir.
  const decided = claim.data?.status === 'Rejected' || claim.data?.status === 'Approved' || claim.data?.status === 'Paid'
  const workflow = useWorkflow(decided ? (claim.data?.workflowRequestId ?? undefined) : undefined)
  const rejectStep = workflow.data?.steps?.find((s) => s.decision === 'Rejected')

  const remove = useMutation({
    mutationFn: () => expenseApi.deleteClaim(claimId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Taslak silindi'))
      navigate('/panel/masraf')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Taslak silinemedi.')),
  })

  const submit = useMutation({
    mutationFn: () => expenseApi.submitClaim(claimId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      void queryClient.invalidateQueries({ queryKey: ['workflows'] })
      toast.ok(tx('Talep onaya gönderildi'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Talep gönderilemedi.')),
  })

  const markPaid = useMutation({
    mutationFn: () => expenseApi.markPaid(claimId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Talep ödendi olarak işaretlendi'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Talep işaretlenemedi.')),
  })

  if (claim.isPending) return <CenteredSpinner label={tx('Masraf talebi yükleniyor')} />

  if (claim.isError) {
    return (
      <Panel>
        <ErrorState
          message={claim.error instanceof Error ? claim.error.message : undefined}
          onRetry={() => void claim.refetch()}
        />
      </Panel>
    )
  }

  if (!claim.data) {
    return (
      <Panel>
        <EmptyState title={tx('Talep bulunamadı')} detail={tx('Bu masraf talebi silinmiş olabilir.')} />
      </Panel>
    )
  }

  const c = claim.data
  const items = c.items ?? []
  const flags = flagSummary(items)
  const canEditDraft = c.status === 'Draft' && can('expense:create') && (isHr(roles) || c.employeeId === myEmployeeId)

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/masraf">
          <ArrowLeft className="size-4" />
          {tx('Masraf')}
        </Link>
      </Button>

      <PageHeader
        title={c.title}
        description={tx('{0}, {1} kalem', [nameOf(c.employeeId), formatNumber(items.length)])}
        actions={
          <>
            <ClaimStatusBadge status={c.status} />
            {/* Onaya gönderme backend'de yalnızca beyan sahibine ve İK'ya açık. */}
            {canEditDraft && (
              <>
                <Button variant="outline" className="cursor-pointer" onClick={() => setEditOpen(true)}>
                  <Pencil className="size-4" />
                  {tx('Düzenle')}
                </Button>
                <Button
                  variant="outline"
                  className="cursor-pointer"
                  disabled={remove.isPending}
                  onClick={async () => {
                    if (await confirm({ title: tx('Taslak silinsin mi?'), note: tx('Taslak ve tüm kalemleri kalıcı olarak silinir.'), action: tx('Sil') }))
                      remove.mutate()
                  }}
                >
                  {remove.isPending ? <LoaderCircle className="size-4 animate-spin" /> : <Trash2 className="size-4" />}
                  {tx('Sil')}
                </Button>
              </>
            )}
            {canEditDraft && (
              <Button
                className="cursor-pointer"
                disabled={submit.isPending}
                onClick={async () => {
                  if (await confirm({
                    title: tx('Talep onaya gönderilsin mi?'),
                    note: tx('Gönderildikten sonra talep düzenlenemez ve silinemez.'),
                    action: tx('Onaya gönder'),
                    destructive: false,
                  })) submit.mutate()
                }}
              >
                {submit.isPending && <LoaderCircle className="size-4 animate-spin" />}
                {tx('Onaya gönder')}
              </Button>
            )}
            {c.status === 'Approved' && can('expense:markPaid') && (
              <Button
                className="cursor-pointer"
                disabled={markPaid.isPending}
                onClick={() => markPaid.mutate()}
              >
                {markPaid.isPending && <LoaderCircle className="size-4 animate-spin" />}
                {tx('Ödendi işaretle')}
              </Button>
            )}
          </>
        }
      />

      <Panel>
        <PanelBody className="flex flex-wrap items-end justify-between gap-6">
          <div>
            <span className="block text-[12px] text-muted-foreground">{tx('Toplam')}</span>
            <motion.span
              className="tabular mt-1 block text-[34px] leading-none font-bold"
              initial={reduced ? false : { opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.35, ease: 'easeOut' }}
            >
              {formatMoney(c.totalAmount, c.currency)}
            </motion.span>
          </div>
        </PanelBody>
      </Panel>

      {flags.count > 0 && (
        <div role="status" className="flex items-start gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-3 text-[13px]">
          <ShieldAlert className="mt-0.5 size-4 shrink-0" />
          <span>
            {tx('Masraf denetimi {0} işaret üretti (aşağıda kalem bazında). İşaretler yalnızca insan incelemesi içindir; beyan otomatik reddedilmez, kararı onaycı verir.', [formatNumber(flags.count)])}
          </span>
        </div>
      )}

      <Panel>
        <PanelHead title={tx('Kalemler')} note={tx('{0} kalem', [formatNumber(items.length)])} />
        {items.length === 0 ? (
          <EmptyState title={tx('Kalem yok')} detail={tx('Bu talebe kalem eklenmemiş.')} />
        ) : (
          <ul className="divide-y divide-border">
            {items.map((item, i) => (
              <motion.li
                key={item.id ?? i}
                className="flex flex-wrap items-baseline gap-x-4 gap-y-1 px-4 py-3.5"
                initial={reduced ? false : { opacity: 0, y: 4 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.28, ease: 'easeOut', delay: Math.min(i * 0.04, 0.2) }}
              >
                <span className="min-w-0 flex-1 basis-52">
                  <span className="block text-[14px]">{expenseCategoryLabels[item.category]}</span>
                  {item.description && (
                    <span className="mt-0.5 block text-[12px] text-muted-foreground">
                      {item.description}
                    </span>
                  )}
                  {(item.invoiceNo || item.supplierTaxId) && (
                    <span className="tabular mt-0.5 block text-[12px] text-muted-foreground">
                      {[item.supplierTaxId && tx('VKN/TCKN {0}', [item.supplierTaxId]), item.invoiceNo && tx('No {0}', [item.invoiceNo]),
                        item.vatAmount != null && tx('KDV {0}', [formatMoney(item.vatAmount, c.currency)])].filter(Boolean).join(' · ')}
                    </span>
                  )}
                  {(item.anomalyFlags ?? []).length > 0 && (
                    <ul className="mt-1.5 space-y-1">
                      {(item.anomalyFlags ?? []).map((f, j) => (
                        <li key={`${f.code}-${j}`} className="flex flex-wrap items-center gap-1.5 text-[12px]">
                          <StatusBadge tone={severityTone[f.severity] ?? 'neutral'}>{severityLabel(f.severity)}</StatusBadge>
                          <span>{flagText(f)}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </span>
                <span className="tabular shrink-0 text-[12px] text-muted-foreground">
                  {formatDate(item.expenseDate)}
                </span>
                <span className="tabular shrink-0 text-[14px] font-semibold">
                  {formatMoney(item.amount, c.currency)}
                </span>
              </motion.li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel>
        <PanelHead title={tx('Talep bilgisi')} />
        <PanelBody className="space-y-4">
          <dl className="grid gap-x-6 gap-y-4 sm:grid-cols-3">
            <DataField label={tx('Talep sahibi')}>{nameOf(c.employeeId)}</DataField>
            <DataField label={tx('Oluşturulma')}>{formatDateTime(c.createdAt)}</DataField>
            <DataField label={tx('Onay akışı')}>
              {c.status === 'Rejected' ? (
                <StatusBadge tone="danger">{tx('Reddedildi')}</StatusBadge>
              ) : c.status === 'Approved' ? (
                <StatusBadge tone="success">{tx('Onaylandı')}</StatusBadge>
              ) : c.status === 'Paid' ? (
                <StatusBadge tone="success">{tx('Onaylandı, ödendi')}</StatusBadge>
              ) : c.status === 'Submitted' ? (
                <StatusBadge tone="info">{tx('Onay kutusunda bekliyor')}</StatusBadge>
              ) : (
                <span className="text-muted-foreground">{tx('Henüz gönderilmedi')}</span>
              )}
            </DataField>
          </dl>
          {c.status === 'Rejected' && (
            <InfoNote>
              {rejectStep?.comment
                ? tx('Ret gerekçesi: {0}', [rejectStep.comment])
                : workflow.isPending && c.workflowRequestId
                  ? tx('Ret gerekçesi yükleniyor…')
                  : workflow.isError
                    ? tx('Ret gerekçesi görüntülenemiyor.')
                    : tx('Ret gerekçesi belirtilmemiş.')}
            </InfoNote>
          )}
          {c.status === 'Submitted' && (
            <InfoNote>
              {tx('Talep onay kutusunda bekliyor. Onay verildiğinde durum arka planda kendiliğinden güncellenir; burada ayrıca bir işlem yapmanız gerekmez.')}
            </InfoNote>
          )}
        </PanelBody>
      </Panel>
      {canEditDraft && <NewClaimModal open={editOpen} onClose={() => setEditOpen(false)} claim={c} />}
    </div>
  )
}
