import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { PlanStatusBadge } from '@/components/ui/ModuleBadges'
import { ProgressBar } from '@/components/ui/Progress'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { Modal } from '@/components/ui/Modal'
import { TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { opsApi } from '@/api/opsPlus'
import { useEmployees, useOnboardingPlans } from '@/api/queries'
import { planStatusLabels, type OnboardingPlan, type PlanStatus } from '@/api/types'
import { formatDate, fullName } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { localISODate } from '@/lib/dates'
import { PersonSelect } from '@/features/shared/kit'
import { TemplatesPanel, WelcomeSettingsPanel, displayPlanStatus } from './OnboardingOps'

type TabKey = PlanStatus | 'all'

const TABS: Array<TabDef<TabKey>> = [
  { key: 'InProgress', label: tx('Sürüyor') },
  { key: 'NotStarted', label: tx('Başlamadı') },
  { key: 'Completed', label: tx('Tamamlandı') },
  { key: 'all', label: tx('Tümü') },
]

/** Tamamlanan görev oranı — plan listesinde tek bakışta ilerleme. */
function planProgress(plan: OnboardingPlan) {
  const tasks = plan.tasks ?? []
  return { done: tasks.filter((t) => t.status === 'Done').length, total: tasks.length }
}

function NewPlanModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [employeeId, setEmployeeId] = useState('')
  const [startDate, setStartDate] = useState('')
  const [templateName, setTemplateName] = useState('')
  const [useDefaultTasks, setUseDefaultTasks] = useState(true)
  const [applyTemplates, setApplyTemplates] = useState(true)
  const [buddyId, setBuddyId] = useState('')
  const [location, setLocation] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      opsApi.createPlan({
        employeeId,
        startDate,
        templateName: templateName.trim() || undefined,
        useDefaultTasks,
        applyTemplates,
        buddyEmployeeId: buddyId || null,
        location: location.trim() || null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['onboarding'] })
      toast.ok(useDefaultTasks ? tx('Plan oluşturuldu, standart görevler eklendi') : tx('Plan oluşturuldu'))
      onClose()
      setStartDate('')
      setTemplateName('')
      setBuddyId('')
      setLocation('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Plan oluşturulamadı.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!employeeId) return setError(tx('Çalışan seçilmeli.'))
    if (!startDate) return setError(tx('Başlangıç tarihi zorunlu.'))
    // Sunucuyla aynı aralık: 01.01.1950 – bugün + 1 yıl.
    const max = new Date()
    max.setFullYear(max.getFullYear() + 1)
    if (startDate < '1950-01-01' || startDate > localISODate(max))
      return setError(tx('Başlangıç tarihi 01.01.1950 ile bugünden bir yıl sonrası arasında olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni onboarding planı')}
      note={tx('Plan açıldığında görevler kategori kategori takip edilir.')}
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
            form="new-plan"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Planı oluştur')}
          </Button>
        </>
      }
    >
      <form id="new-plan" onSubmit={submit} noValidate className="space-y-4">
        <EmployeePicker
          id="plan-employee"
          value={employeeId}
          onChange={setEmployeeId}
          hint={error?.includes('Çalışan') ? error : tx('Plan bu çalışan için açılır.')}
        />

        <TextField
          id="plan-start"
          label={tx('İşe başlama tarihi')}
          type="date"
          required
          value={startDate}
          onChange={(e) => setStartDate(e.target.value)}
          error={error?.includes('Başlangıç') ? error : undefined}
        />

        <TextField
          id="plan-template"
          label={tx('Şablon adı')}
          hint={tx('İsteğe bağlı. Örn. Yazılım ekibi.')}
          value={templateName}
          onChange={(e) => setTemplateName(e.target.value)}
        />

        <PersonSelect
          id="plan-buddy"
          label={tx('Yol arkadaşı (buddy)')}
          value={buddyId}
          exclude={employeeId ? [employeeId] : []}
          onChange={setBuddyId}
          hint={tx('İsteğe bağlı. Seçilen kişiye görev listesi ve bildirim gider.')}
        />

        <TextField
          id="plan-location"
          label={tx('İlk gün buluşma yeri')}
          hint={tx('İsteğe bağlı; karşılama iletisinde kullanılır. Örn. İstanbul ofis, 3. kat resepsiyon.')}
          maxLength={200}
          value={location}
          onChange={(e) => setLocation(e.target.value)}
        />

        <label className="flex min-h-11 cursor-pointer items-start gap-2.5">
          <Checkbox
            checked={applyTemplates}
            onCheckedChange={(v) => setApplyTemplates(v === true)}
            className="mt-0.5"
          />
          <span className="text-[13px]">
            {tx('Unvan ve departmana uyan rol şablonlarını uygula')}
            <span className="block text-[12px] text-muted-foreground">
              {tx('Görevler sahibine (yönetici, yol arkadaşı, yeni çalışan) otomatik atanır.')}
            </span>
          </span>
        </label>

        <label className="flex min-h-11 cursor-pointer items-start gap-2.5">
          <Checkbox
            checked={useDefaultTasks}
            onCheckedChange={(v) => setUseDefaultTasks(v === true)}
            className="mt-0.5"
          />
          <span className="text-[13px]">
            {tx('Standart görevleri otomatik oluştur')}
            <span className="block text-[12px] text-muted-foreground">
              {tx('Hazır görev seti eklenir; sonra düzenleyebilirsiniz.')}
            </span>
          </span>
        </label>
      </form>
    </Modal>
  )
}

export function OnboardingPage() {
  const { can } = useAuth()
  const navigate = useNavigate()
  const [tab, setTab] = useTabParam<TabKey>('durum', 'InProgress')
  const [modalOpen, setModalOpen] = useState(false)
  const [configOpen, setConfigOpen] = useState(false)
  const plans = useOnboardingPlans({ status: tab === 'all' ? undefined : tab })
  const employees = useEmployees({ enabled: can('employee:viewAll') })

  const canManage = can('onboarding:manage')

  const nameOf = useMemo(() => {
    const map = new Map<string, string>()
    for (const e of employees.data ?? []) map.set(e.id, fullName(e))
    return (id: string) => map.get(id) ?? `${id.slice(0, 8)}…`
  }, [employees.data])

  const columns: Array<Column<OnboardingPlan>> = [
    {
      id: 'employee',
      header: tx('Çalışan'),
      searchText: (p) => `${nameOf(p.employeeId)} ${p.templateName ?? ''}`,
      sortValue: (p) => nameOf(p.employeeId),
      exportText: (p) => nameOf(p.employeeId),
      cell: (p) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">{nameOf(p.employeeId)}</p>
          {p.templateName && (
            <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{p.templateName}</p>
          )}
        </div>
      ),
    },
    {
      id: 'startDate',
      header: tx('Başlangıç'),
      hideBelow: 'sm',
      sortValue: (p) => new Date(p.startDate).getTime(),
      exportText: (p) => formatDate(p.startDate),
      cell: (p) => <span className="tabular text-muted-foreground">{formatDate(p.startDate)}</span>,
    },
    {
      id: 'progress',
      header: tx('İlerleme'),
      hideBelow: 'md',
      sortValue: (p) => {
        const { done, total } = planProgress(p)
        return total > 0 ? done / total : 0
      },
      exportText: (p) => {
        const { done, total } = planProgress(p)
        return `${done}/${total}`
      },
      cell: (p) => {
        const { done, total } = planProgress(p)
        if (total === 0) return <span className="text-muted-foreground">{tx('Görev yok')}</span>
        return (
          <div className="flex min-w-32 items-center gap-3">
            <ProgressBar
              value={done}
              max={total}
              tone={done === total ? 'success' : 'info'}
              label={tx('{0} görev ilerlemesi', [nameOf(p.employeeId)])}
            />
            <span className="tabular shrink-0 text-[12px] text-muted-foreground">
              {done}/{total}
            </span>
          </div>
        )
      },
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (p) => planStatusLabels[displayPlanStatus(p)] ?? '',
      exportText: (p) => planStatusLabels[displayPlanStatus(p)] ?? '',
      cell: (p) => <PlanStatusBadge status={displayPlanStatus(p)} />,
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Onboarding')}
        description={tx('İşe yeni başlayanların görev planları ve ilerlemeleri.')}
        actions={
          canManage && (
            <>
              <Button variant="outline" className="cursor-pointer" onClick={() => setConfigOpen(true)}>
                {tx('Şablonlar ve karşılama')}
              </Button>
              <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
                <Plus className="size-4" />
                {tx('Yeni plan')}
              </Button>
            </>
          )
        }
      />

      <Tabs tabs={TABS} value={tab} onChange={setTab} label={tx('Plan durumu')} />

      <DataTable
        rows={plans.data}
        rowKey={(p) => p.id}
        columns={columns}
        isLoading={plans.isPending}
        error={plans.error}
        onRetry={() => void plans.refetch()}
        onRowClick={(p) => navigate(`/panel/onboarding/${p.id}`)}
        searchPlaceholder={tx('Çalışan veya şablon ara')}
        exportFileName="onboarding-planlari"
        emptyTitle={tx('Bu durumda plan yok')}
        emptyDetail={tx('Yeni bir çalışan için plan açtığınızda burada görünür.')}
        emptyAction={
          canManage ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
              {tx('Yeni plan')}
            </Button>
          ) : undefined
        }
      />

      <NewPlanModal open={modalOpen} onClose={() => setModalOpen(false)} />
      {configOpen && (
        <Modal open size="xl" onClose={() => setConfigOpen(false)} title={tx('Şablonlar ve karşılama')}
          note={tx('Rol şablonları plan açılırken otomatik uygulanır; karşılama iletisi başlangıç günü gönderilir.')}>
          <div className="space-y-5">
            <TemplatesPanel />
            <WelcomeSettingsPanel />
          </div>
        </Modal>
      )}
    </div>
  )
}
