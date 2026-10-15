import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { DataTable, type Column, type TableFilter } from '@/components/ui/DataTable'
import { CasePriorityBadge, CaseStatusBadge } from '@/components/ui/ModuleBadges'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { expenseApi } from '@/api/expense'
import { useHrCases, useMyEmployeeId } from '@/api/queries'
import {
  caseCategoryLabels,
  casePriorityLabels,
  caseStatusLabels,
  type CaseCategory,
  type CasePriority,
  type CaseStatus,
  type HrCase,
} from '@/api/types'
import { formatDate, formatRelativeToNow } from '@/lib/format'
import { useEmployeeName } from '@/lib/useEmployeeName'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'

const ALL = '__all__'

/** Öncelik, satırın sol kenarındaki çizgiyle de okunur — rozet tek taşıyıcı değil. */
const PRIORITY_EDGE: Record<CasePriority, string> = {
  Low: 'border-transparent',
  Normal: 'border-border',
  High: 'border-[hsl(var(--warning))]',
  Urgent: 'border-destructive',
}

function NewCaseModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  // Başkası adına vaka yalnızca İK açabilir (backend 403 döner).
  const { roles } = useAuth()
  const isCaseAdmin = roles.some((r) => r === 'hr-admin' || r === 'tenant-admin' || r === 'platform-admin' || (r as string) === 'ext-case-manage')
  const me = useMyEmployeeId(!isCaseAdmin)
  const [pickedEmployeeId, setEmployeeId] = useState('')
  const employeeId = isCaseAdmin ? pickedEmployeeId : (me.employeeId ?? '')
  const [subject, setSubject] = useState('')
  const [description, setDescription] = useState('')
  const [category, setCategory] = useState<CaseCategory>('Payroll')
  const [priority, setPriority] = useState<CasePriority>('Normal')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      expenseApi.createCase({
        employeeId,
        subject: subject.trim(),
        description: description.trim() || undefined,
        category,
        priority,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Vaka açıldı'))
      onClose()
      setSubject('')
      setDescription('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Vaka açılamadı.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!employeeId) return setError(tx('Çalışan seçilmeli.'))
    if (subject.trim().length < 3) return setError(tx('Konu en az 3 karakter olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni İK vakası')}
      note={tx('Vaka açıldığında İK ekibine düşer; İK bir sorumlu atar.')}
      size="lg"
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
            form="new-case"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Vakayı aç')}
          </Button>
        </>
      }
    >
      <form id="new-case" onSubmit={submit} noValidate className="space-y-4">
        {isCaseAdmin ? (
          <EmployeePicker
            id="case-employee"
            value={employeeId}
            onChange={setEmployeeId}
            hint={error?.includes('Çalışan') ? error : undefined}
          />
        ) : (
          <p className="text-[13px] text-muted-foreground">
            {me.notLinked
              ? tx('Hesabınıza bağlı çalışan kaydı bulunamadı; vaka açamazsınız.')
              : tx('Vaka sizin adınıza açılacak.')}
          </p>
        )}
        <TextField
          id="case-subject"
          label={tx('Konu')}
          required
          value={subject}
          maxLength={200}
          onChange={(e) => setSubject(e.target.value)}
          error={error?.includes('Konu') ? error : undefined}
        />
        <div className="grid gap-4 sm:grid-cols-2">
          <SelectField
            id="case-category"
            label={tx('Kategori')}
            value={category}
            onChange={(v) => setCategory(v as CaseCategory)}
            options={(Object.keys(caseCategoryLabels) as CaseCategory[]).map((c) => ({
              value: c,
              label: caseCategoryLabels[c],
            }))}
          />
          <SelectField
            id="case-priority"
            label={tx('Öncelik')}
            value={priority}
            onChange={(v) => setPriority(v as CasePriority)}
            options={(Object.keys(casePriorityLabels) as CasePriority[]).map((p) => ({
              value: p,
              label: casePriorityLabels[p],
            }))}
          />
        </div>
        <TextAreaField
          id="case-desc"
          label={tx('Açıklama')}
          rows={4}
          hint={tx('İsteğe bağlı')}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
        <PiiHint text={description} />
      </form>
    </Modal>
  )
}

export function CasesPage() {
  const { can } = useAuth()
  const navigate = useNavigate()
  const [status, setStatus] = useState<string>(ALL)
  const [priority, setPriority] = useState<string>(ALL)
  const [modalOpen, setModalOpen] = useState(false)
  const nameOf = useEmployeeName()

  const cases = useHrCases({
    status: status === ALL ? undefined : (status as CaseStatus),
    priority: priority === ALL ? undefined : (priority as CasePriority),
  })

  // Varsayılan sıra: yeni vaka en üstte (kullanıcı başlığa tıklayınca kendi sıralaması geçerli).
  const rows = useMemo(
    () => cases.data && [...cases.data].sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()),
    [cases.data],
  )

  const filters: TableFilter[] = [
    {
      id: 'status',
      label: tx('Durum'),
      value: status,
      onChange: setStatus,
      options: [
        { value: ALL, label: tx('Tüm durumlar') },
        ...(Object.keys(caseStatusLabels) as CaseStatus[]).map((s) => ({
          value: s,
          label: caseStatusLabels[s],
        })),
      ],
    },
    {
      id: 'priority',
      label: tx('Öncelik'),
      value: priority,
      onChange: setPriority,
      options: [
        { value: ALL, label: tx('Tüm öncelikler') },
        ...(Object.keys(casePriorityLabels) as CasePriority[]).map((p) => ({
          value: p,
          label: casePriorityLabels[p],
        })),
      ],
    },
  ]

  const columns: Array<Column<HrCase>> = [
    {
      id: 'subject',
      header: tx('Konu'),
      searchText: (c) => `${c.subject} ${caseCategoryLabels[c.category]}`,
      // CSV'de konu ve kategori ayrı sütunlarda (kategori aşağıdaki sütundan).
      exportText: (c) => c.subject,
      sortValue: (c) => c.subject,
      cell: (c) => (
        // Boşluksuz uzun konu tabloyu taşırmasın: genişlik sınırlı, en çok iki satır, her yerden kırılır.
        <div className={cn('min-w-0 max-w-[32rem] border-l-2 pl-3', PRIORITY_EDGE[c.priority])}>
          <p className="line-clamp-2 font-medium text-foreground [overflow-wrap:anywhere]" title={c.subject}>{c.subject}</p>
          <p className="mt-0.5 truncate text-[12px] text-muted-foreground">
            {tx('{0}, {1} açıldı', [caseCategoryLabels[c.category], formatRelativeToNow(c.createdAt)])}</p>
        </div>
      ),
    },
    {
      id: 'category',
      header: tx('Kategori'),
      hideBelow: 'lg',
      sortValue: (c) => caseCategoryLabels[c.category] ?? '',
      exportText: (c) => caseCategoryLabels[c.category] ?? '',
      cell: (c) => <span className="text-muted-foreground">{caseCategoryLabels[c.category]}</span>,
    },
    {
      id: 'createdAt',
      header: tx('Açılış'),
      hideBelow: 'lg',
      sortValue: (c) => new Date(c.createdAt).getTime(),
      exportText: (c) => formatDate(c.createdAt),
      cell: (c) => <span className="tabular text-muted-foreground">{formatDate(c.createdAt)}</span>,
    },
    {
      id: 'employee',
      header: tx('Açan'),
      hideBelow: 'md',
      searchText: (c) => nameOf(c.employeeId),
      sortValue: (c) => nameOf(c.employeeId),
      exportText: (c) => nameOf(c.employeeId),
      cell: (c) => <span className="text-muted-foreground">{nameOf(c.employeeId)}</span>,
    },
    {
      id: 'assignee',
      header: tx('Atanan'),
      hideBelow: 'lg',
      sortValue: (c) => (c.assignedToEmployeeId ? nameOf(c.assignedToEmployeeId) : ''),
      exportText: (c) => (c.assignedToEmployeeId ? nameOf(c.assignedToEmployeeId) : '—'),
      cell: (c) => (
        <span className="text-muted-foreground">
          {c.assignedToEmployeeId ? nameOf(c.assignedToEmployeeId) : '—'}
        </span>
      ),
    },
    {
      id: 'priority',
      header: tx('Öncelik'),
      align: 'right',
      hideBelow: 'sm',
      sortValue: (c) => casePriorityLabels[c.priority] ?? '',
      exportText: (c) => casePriorityLabels[c.priority] ?? '',
      cell: (c) => <CasePriorityBadge priority={c.priority} />,
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (c) => caseStatusLabels[c.status] ?? '',
      exportText: (c) => caseStatusLabels[c.status] ?? '',
      cell: (c) => <CaseStatusBadge status={c.status} />,
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('İK vakaları')}
        description={tx('Çalışanlardan gelen talep, soru ve şikâyetlerin izlendiği kayıt defteri.')}
        actions={
          can('case:create') && (
            <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
              <Plus className="size-4" />
              {tx('Yeni vaka')}
            </Button>
          )
        }
      />

      <DataTable
        rows={rows}
        rowKey={(c) => c.id}
        columns={columns}
        filters={filters}
        isLoading={cases.isPending}
        error={cases.error}
        onRetry={() => void cases.refetch()}
        onRowClick={(c) => navigate(`/panel/ik-vakalari/${c.id}`)}
        searchPlaceholder={tx('Konu, kategori veya kişi ara')}
        exportFileName="ik-vakalari"
        viewKey="hr-cases"
        pageSize={12}
        emptyTitle={tx('Vaka yok')}
        emptyDetail={tx('Bu filtreye uyan vaka bulunmuyor.')}
        emptyAction={
          can('case:create') ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
              {tx('Yeni vaka')}
            </Button>
          ) : undefined
        }
      />

      <NewCaseModal open={modalOpen} onClose={() => setModalOpen(false)} />
    </div>
  )
}
