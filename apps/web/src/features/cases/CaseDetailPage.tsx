import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { ArrowLeft, LoaderCircle } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { DataField, Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { CenteredSpinner, EmptyState, ErrorState } from '@/components/ui/States'
import { CasePriorityBadge, CaseStatusBadge } from '@/components/ui/ModuleBadges'
import { Modal } from '@/components/ui/Modal'
import { TextAreaField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { expenseApi } from '@/api/expense'
import { useHrCase, useMyEmployeeId } from '@/api/queries'
import { caseCategoryLabels } from '@/api/types'
import { formatDateTime } from '@/lib/format'
import { useEmployeeName } from '@/lib/useEmployeeName'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'

export function CaseDetailPage() {
  const { caseId } = useParams<{ caseId: string }>()
  const { roles } = useAuth()
  const me = useMyEmployeeId()
  const toast = useToast()
  const queryClient = useQueryClient()
  const reduced = useReducedMotion()

  const [assignOpen, setAssignOpen] = useState(false)
  const [assignee, setAssignee] = useState('')
  const [resolveOpen, setResolveOpen] = useState(false)
  const [resolution, setResolution] = useState('')
  const [error, setError] = useState<string | undefined>()

  const hrCase = useHrCase(caseId)
  const nameOf = useEmployeeName()

  const assign = useMutation({
    mutationFn: () => expenseApi.assignCase(caseId!, assignee),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Vaka atandı'))
      setAssignOpen(false)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Vaka atanamadı.')),
  })

  const resolve = useMutation({
    mutationFn: () => expenseApi.resolveCase(caseId!, resolution.trim()),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Vaka çözüldü olarak kapatıldı'))
      setResolveOpen(false)
      setResolution('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Vaka kapatılamadı.')),
  })

  if (hrCase.isPending) return <CenteredSpinner label={tx('Vaka yükleniyor')} />

  if (hrCase.isError) {
    return (
      <Panel>
        <ErrorState
          message={hrCase.error instanceof Error ? hrCase.error.message : undefined}
          onRetry={() => void hrCase.refetch()}
        />
      </Panel>
    )
  }

  if (!hrCase.data) {
    return (
      <Panel>
        <EmptyState title={tx('Vaka bulunamadı')} detail={tx('Bu vaka silinmiş olabilir.')} />
      </Panel>
    )
  }

  const c = hrCase.data
  const closed = c.status === 'Resolved' || c.status === 'Closed'
  // Backend kurali (HrCasesController): atama yalnızca İK'ya; kapatma İK'ya ya da
  // vakanın atandığı kişiye; kimse kendi açtığı vakayı kapatamaz. Yöneticiler
  // 'case:manage' izni nedeniyle bu düğmeleri görüp 403 alıyordu.
  const isCaseAdmin = roles.some((r) => r === 'hr-admin' || r === 'tenant-admin' || r === 'platform-admin' || (r as string) === 'ext-case-manage')
  const canAssign = isCaseAdmin
  const canResolve =
    (isCaseAdmin || (Boolean(me.employeeId) && c.assignedToEmployeeId === me.employeeId)) &&
    c.employeeId !== me.employeeId

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/ik-vakalari">
          <ArrowLeft className="size-4" />
          {tx('İK vakaları')}
        </Link>
      </Button>

      <PageHeader
        title={c.subject}
        description={caseCategoryLabels[c.category]}
        actions={
          <>
            <CaseStatusBadge status={c.status} />
            <CasePriorityBadge priority={c.priority} />
            {!closed && canAssign && (
              <Button
                variant="outline"
                className="cursor-pointer"
                onClick={() => setAssignOpen(true)}
              >
                {tx('Ata')}
              </Button>
            )}
            {!closed && canResolve && (
              <Button className="cursor-pointer" onClick={() => setResolveOpen(true)}>
                {tx('Çözüldü olarak kapat')}
              </Button>
            )}
          </>
        }
      />

      {c.description && (
        <Panel>
          <PanelHead title={tx('Açıklama')} />
          <PanelBody>
            <p className="text-[14px] leading-relaxed whitespace-pre-line text-muted-foreground">
              {c.description}
            </p>
          </PanelBody>
        </Panel>
      )}

      <AnimatePresence>
        {c.resolution && (
          <motion.div
            initial={reduced ? false : { opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.32, ease: 'easeOut' }}
          >
            <Panel className="border-[hsl(var(--success))]/40 bg-[hsl(var(--success))]/5">
              <PanelHead
                title={tx('Çözüm')}
                note={c.resolvedAt ? formatDateTime(c.resolvedAt) : undefined}
              />
              <PanelBody>
                <p className="text-[14px] leading-relaxed whitespace-pre-line">{c.resolution}</p>
              </PanelBody>
            </Panel>
          </motion.div>
        )}
      </AnimatePresence>

      <Panel>
        <PanelHead title={tx('Vaka bilgisi')} />
        <PanelBody>
          <dl className="grid gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-4">
            <DataField label={tx('Açan çalışan')}>{nameOf(c.employeeId)}</DataField>
            <DataField label={tx('Açılış')}>{formatDateTime(c.createdAt)}</DataField>
            <DataField label={tx('Atanan')}>
              {c.assignedToEmployeeId ? (
                nameOf(c.assignedToEmployeeId)
              ) : (
                <span className="text-muted-foreground">{tx('Atanmadı')}</span>
              )}
            </DataField>
            <DataField label={tx('Kapanış')}>
              {c.resolvedAt ? formatDateTime(c.resolvedAt) : '—'}
            </DataField>
          </dl>
        </PanelBody>
      </Panel>

      <Modal
        open={assignOpen}
        onClose={() => setAssignOpen(false)}
        title={tx('Vakayı ata')}
        note={c.subject}
        footer={
          <>
            <Button
              variant="outline"
              className="cursor-pointer"
              onClick={() => setAssignOpen(false)}
              disabled={assign.isPending}
            >
              {tx('Vazgeç')}
            </Button>
            <Button
              className="cursor-pointer"
              disabled={assign.isPending}
              onClick={() => (assignee ? assign.mutate() : setError(tx('Çalışan seçilmeli.')))}
            >
              {assign.isPending && <LoaderCircle className="size-4 animate-spin" />}
              {tx('Ata')}
            </Button>
          </>
        }
      >
        <EmployeePicker
          id="case-assignee"
          label={tx('Sorumlu')}
          value={assignee}
          onChange={setAssignee}
          hint={error?.includes('Çalışan') ? error : undefined}
        />
      </Modal>

      <Modal
        open={resolveOpen}
        onClose={() => setResolveOpen(false)}
        title={tx('Vakayı kapat')}
        note={tx('Çözüm metni kayda geçer ve çalışana bildirilir.')}
        footer={
          <>
            <Button
              variant="outline"
              className="cursor-pointer"
              onClick={() => setResolveOpen(false)}
              disabled={resolve.isPending}
            >
              {tx('Vazgeç')}
            </Button>
            <Button
              className="cursor-pointer"
              disabled={resolve.isPending}
              onClick={() =>
                resolution.trim().length >= 5
                  ? resolve.mutate()
                  : setError(tx('Çözüm en az 5 karakter olmalı.'))
              }
            >
              {resolve.isPending && <LoaderCircle className="size-4 animate-spin" />}
              {tx('Kapat')}
            </Button>
          </>
        }
      >
        <TextAreaField
          id="case-resolution"
          label={tx('Çözüm')}
          rows={4}
          required
          value={resolution}
          onChange={(e) => setResolution(e.target.value)}
          error={error?.includes('Çözüm') ? error : undefined}
        />
        <PiiHint text={resolution} className="mt-2" />
      </Modal>
    </div>
  )
}
