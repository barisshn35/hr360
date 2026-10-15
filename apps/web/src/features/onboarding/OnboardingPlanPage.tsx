import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { motion, useReducedMotion } from 'motion/react'
import { ArrowLeft, Check, LoaderCircle, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { CenteredSpinner, EmptyState, ErrorState } from '@/components/ui/States'
import { PlanStatusBadge, TaskStatusBadge } from '@/components/ui/ModuleBadges'
import { ProgressBar } from '@/components/ui/Progress'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { onboardingApi } from '@/api/onboarding'
import { useEmployees, useMyEmployeeId, useOnboardingPlan } from '@/api/queries'
import {
  taskCategoryLabels,
  type OnboardingTask,
  type OnboardingTaskStatus,
  type TaskCategory,
} from '@/api/types'
import { formatDate, formatNumber, fullName } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { localISODate as isoLocal } from '@/lib/dates'
import { BuddyPanel, displayPlanStatus } from './OnboardingOps'

const CATEGORY_ORDER: TaskCategory[] = ['IT', 'HR', 'Facility', 'Training', 'Legal', 'Other']

/** Radix Select boş değer kabul etmiyor; "sorumlu atanmadı" için işaret. */
const UNASSIGNED = '__unassigned__'

function NewTaskModal({
  planId,
  planStart,
  open,
  onClose,
}: {
  planId: string
  /** Plan başlangıcı (YYYY-MM-DD); son tarih şablonlarla aynı aralıkta olmalı: -60 / +365 gün. */
  planStart?: string
  open: boolean
  onClose: () => void
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const employees = useEmployees()
  const [title, setTitle] = useState('')
  const [category, setCategory] = useState<TaskCategory>('IT')
  const [dueDate, setDueDate] = useState('')
  const [assignee, setAssignee] = useState(UNASSIGNED)
  const [error, setError] = useState<string | undefined>()
  const [dueError, setDueError] = useState<string | undefined>()
  const shift = (days: number) => {
    if (!planStart) return undefined
    const d = new Date(`${planStart.slice(0, 10)}T00:00:00`)
    d.setDate(d.getDate() + days)
    return isoLocal(d)
  }
  const dueMin = shift(-60)
  const dueMax = shift(365)

  const mutation = useMutation({
    mutationFn: () =>
      onboardingApi.addTask(planId, {
        title: title.trim(),
        category,
        dueDate: dueDate || undefined,
        assigneeEmployeeId: assignee === UNASSIGNED ? undefined : assignee,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(tx('Görev eklendi'))
      onClose()
      setTitle('')
      setDueDate('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Görev eklenemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    const dueErr =
      dueDate && ((dueMin && dueDate < dueMin) || (dueMax && dueDate > dueMax))
        ? tx('Son tarih, plan başlangıcından en fazla 60 gün önce ve 365 gün sonra olabilir.')
        : undefined
    setDueError(dueErr)
    if (title.trim().length < 3) return setError(tx('Görev başlığı en az 3 karakter olmalı.'))
    setError(undefined)
    if (dueErr) return
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni görev')}
      note={tx('Görev kategorisine göre gruplanır.')}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="new-task"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Görevi ekle')}
          </Button>
        </>
      }
    >
      <form id="new-task" onSubmit={submit} noValidate className="space-y-4">
        <TextField
          id="task-title"
          label={tx('Görev')}
          required
          value={title}
          maxLength={200}
          onChange={(e) => setTitle(e.target.value)}
          error={error}
        />
        <SelectField
          id="task-category"
          label={tx('Kategori')}
          value={category}
          onChange={(v) => setCategory(v as TaskCategory)}
          options={CATEGORY_ORDER.map((c) => ({ value: c, label: taskCategoryLabels[c] }))}
        />
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="task-due"
            label={tx('Son tarih')}
            type="date"
            hint={tx('İsteğe bağlı')}
            min={dueMin}
            max={dueMax}
            value={dueDate}
            onChange={(e) => setDueDate(e.target.value)}
            error={dueError}
          />
          <SelectField
            id="task-assignee"
            label={tx('Sorumlu')}
            hint={tx('İsteğe bağlı')}
            value={assignee}
            onChange={setAssignee}
            options={[
              { value: UNASSIGNED, label: tx('Atanmadı') },
              ...(employees.data ?? []).map((e) => ({ value: e.id, label: fullName(e) })),
            ]}
          />
        </div>
      </form>
    </Modal>
  )
}

/**
 * Görev satırı. Onay kutusuna basınca durum döngüsel ilerler:
 * Bekliyor → Sürüyor → Tamam → Bekliyor. Hareket burada bilgi taşır —
 * tik işareti yerine oturunca görevin bittiği anlaşılır.
 */
function TaskRow({
  task,
  planId,
  canEdit,
}: {
  task: OnboardingTask
  planId: string
  canEdit: boolean
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const reduced = useReducedMotion()

  const nextStatus: Record<OnboardingTaskStatus, OnboardingTaskStatus> = {
    Pending: 'InProgress',
    InProgress: 'Done',
    Done: 'Pending',
    Blocked: 'InProgress',
  }

  const mutation = useMutation({
    mutationFn: (status: OnboardingTaskStatus) =>
      onboardingApi.setTaskStatus(planId, task.id, status),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['onboarding'] }),
    onError: (e: unknown) =>
      toast.stop(e instanceof Error ? e.message : tx('Görev durumu güncellenemedi.')),
  })

  const done = task.status === 'Done'

  return (
    <li className="flex items-start gap-3 py-2.5">
      <button
        type="button"
        disabled={!canEdit || mutation.isPending}
        onClick={() => mutation.mutate(nextStatus[task.status])}
        aria-label={tx('{0} durumunu ilerlet', [task.title])}
        className={cn(
          'mt-px flex size-5 shrink-0 items-center justify-center rounded-[6px] border transition-colors',
          done
            ? 'border-[hsl(var(--success))] bg-[hsl(var(--success))] text-background'
            : 'border-border bg-background',
          canEdit ? 'cursor-pointer hover:border-primary' : 'cursor-default',
        )}
      >
        {done && (
          <motion.span
            initial={reduced ? false : { scale: 0.4, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            transition={{ duration: 0.2, ease: 'easeOut' }}
          >
            <Check aria-hidden="true" className="size-3.5" strokeWidth={3} />
          </motion.span>
        )}
      </button>

      <div className="min-w-0 flex-1">
        <p
          className={cn(
            'text-[14px] transition-colors',
            done ? 'text-muted-foreground line-through' : 'text-foreground',
          )}
        >
          {task.title}
        </p>
        <p className="tabular mt-0.5 text-[12px] text-muted-foreground">
          {task.dueDate ? tx('Son tarih {0}', [formatDate(task.dueDate)]) : tx('Son tarih yok')}
        </p>
      </div>

      <span className="shrink-0">
        <TaskStatusBadge status={task.status} />
      </span>
    </li>
  )
}

export function OnboardingPlanPage() {
  const { planId } = useParams<{ planId: string }>()
  const { can } = useAuth()
  const { employeeId: myEmployeeId } = useMyEmployeeId()
  const plan = useOnboardingPlan(planId)
  const employees = useEmployees({ enabled: can('employee:viewAll') })
  const [taskModal, setTaskModal] = useState(false)

  const tasks = useMemo(() => plan.data?.tasks ?? [], [plan.data])
  const done = tasks.filter((t) => t.status === 'Done').length

  /** Görevler kategoriye göre gruplanır — sorumluluk alanı bir arada okunur. */
  const grouped = useMemo(() => {
    const map = new Map<TaskCategory, OnboardingTask[]>()
    for (const t of tasks) {
      const list = map.get(t.category) ?? []
      list.push(t)
      map.set(t.category, list)
    }
    return CATEGORY_ORDER.filter((c) => map.has(c)).map((c) => ({
      category: c,
      items: map.get(c)!,
    }))
  }, [tasks])

  if (plan.isPending) return <CenteredSpinner label={tx('Plan yükleniyor')} />

  if (plan.isError || !plan.data) {
    return (
      <Panel>
        <ErrorState
          title={tx('Plan bulunamadı')}
          message={plan.error instanceof Error ? plan.error.message : undefined}
          onRetry={() => void plan.refetch()}
        />
      </Panel>
    )
  }

  const data = plan.data
  const employee = employees.data?.find((e) => e.id === data.employeeId)
  const who = employee ? fullName(employee) : `${data.employeeId.slice(0, 8)}…`
  const canEdit = can('onboarding:manage')
  // Backend kuralı: yönetenler her görevi; görevin atandığı kişi kendi görevini;
  // planın sahibi (yeni çalışan) hukuki olmayan görevlerini günceller.
  const canEditTask = (t: { assigneeEmployeeId: string | null; category: string }) =>
    canEdit ||
    (Boolean(myEmployeeId) &&
      (t.assigneeEmployeeId === myEmployeeId || (data.employeeId === myEmployeeId && t.category !== 'Legal')))

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/onboarding">
          <ArrowLeft className="size-4" />
          {tx('Onboarding')}
        </Link>
      </Button>

      <PageHeader
        title={who}
        description={tx('{0} tarihinde başlıyor. {1}/{2} görev tamam.', [formatDate(data.startDate), formatNumber(done), formatNumber(tasks.length)])}
        actions={
          <>
            <PlanStatusBadge status={displayPlanStatus(data)} />
            {canEdit && (
              <Button className="cursor-pointer" onClick={() => setTaskModal(true)}>
                <Plus className="size-4" />
                {tx('Görev ekle')}
              </Button>
            )}
          </>
        }
      />

      <Panel>
        <PanelBody>
          <div className="flex items-center gap-4">
            <ProgressBar
              thick
              value={done}
              max={tasks.length || 1}
              tone={done === tasks.length && tasks.length > 0 ? 'success' : 'info'}
              label={tx('Plan ilerlemesi')}
            />
            <span className="tabular shrink-0 text-[18px] font-bold">
              %{tasks.length > 0 ? Math.round((done / tasks.length) * 100) : 0}
            </span>
          </div>
          <p className="mt-2 text-[12px] text-muted-foreground">
            {tx('Tüm görevler tamamlanınca plan kendiliğinden kapanır.')}
          </p>
        </PanelBody>
      </Panel>

      <BuddyPanel plan={data} canEdit={canEdit} />

      {tasks.length === 0 ? (
        <Panel>
          <EmptyState
            title={tx('Görev yok')}
            detail={tx('Bu plana henüz görev eklenmemiş.')}
            action={
              canEdit ? (
                <Button size="sm" className="cursor-pointer" onClick={() => setTaskModal(true)}>
                  {tx('İlk görevi ekle')}
                </Button>
              ) : undefined
            }
          />
        </Panel>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          {grouped.map((group) => {
            const groupDone = group.items.filter((t) => t.status === 'Done').length
            return (
              <Panel key={group.category}>
                <PanelHead
                  title={taskCategoryLabels[group.category]}
                  action={
                    <span className="tabular text-[12px] text-muted-foreground">
                      {groupDone}/{group.items.length}
                    </span>
                  }
                />
                <PanelBody>
                  <ul className="divide-y divide-border">
                    {group.items.map((t) => (
                      <TaskRow key={t.id} task={t} planId={data.id} canEdit={canEditTask(t)} />
                    ))}
                  </ul>
                </PanelBody>
              </Panel>
            )
          })}
        </div>
      )}

      {planId && (
        <NewTaskModal planId={planId} planStart={data.startDate} open={taskModal} onClose={() => setTaskModal(false)} />
      )}
    </div>
  )
}
