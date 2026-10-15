import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, LoaderCircle, Plus, Users } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { JobPostingStatusBadge } from '@/components/ui/ModuleBadges'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { Modal, ErrorSummary, type SummaryItem } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { recruitmentApi } from '@/api/recruitment'
import { useCompanies, useJobPostings } from '@/api/queries'
import {
  employmentTypeLabels,
  jobPostingStatusLabels,
  type EmploymentType,
  type JobPosting,
  type JobPostingStatus,
} from '@/api/types'
import { formatDate, formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'

type TabKey = JobPostingStatus | 'all'

const TABS: Array<TabDef<TabKey>> = [
  { key: 'Published', label: tx('Yayında') },
  { key: 'Draft', label: tx('Taslak') },
  { key: 'OnHold', label: tx('Beklemede') },
  { key: 'Closed', label: tx('Kapandı') },
  { key: 'all', label: tx('Tümü') },
]

interface Errors {
  title?: string
  departmentId?: string
  headcount?: string
}

function NewPostingModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated?: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const companies = useCompanies()

  const [title, setTitle] = useState('')
  const [departmentId, setDepartmentId] = useState('')
  const [description, setDescription] = useState('')
  const [employmentType, setEmploymentType] = useState<EmploymentType>('FullTime')
  const [headcount, setHeadcount] = useState('1')
  const [errors, setErrors] = useState<Errors>({})
  const [submitted, setSubmitted] = useState(false)

  const departments = useMemo(
    () =>
      (companies.data ?? []).flatMap((c) =>
        (c.departments ?? []).map((d) => ({ value: d.id, label: `${c.name} — ${d.name}` })),
      ),
    [companies.data],
  )

  function validate(overrideDepartmentId?: string): Errors {
    const next: Errors = {}
    if (title.trim().length < 3) next.title = tx('Başlık en az 3 karakter olmalı.')
    if (!(overrideDepartmentId ?? departmentId)) next.departmentId = tx('Departman seçilmeli.')
    // GUVENLIK/VERI BUTUNLUGU: onceki hali "Number(headcount) || 1" idi -
    // bu, "0" veya bos degeri sessizce 1'e ceviriyordu (kullaniciya hicbir
    // bildirim yapmadan) ama NEGATIF degerleri (orn. "-5") YAKALAMIYORDU,
    // cunku Number('-5') JavaScript'te "truthy" - "-5 || 1" hala "-5"
    // doner. Simdi acik bir hata gosteriliyor, sessiz "duzeltme" yok.
    const hc = Number(headcount)
    if (!headcount || Number.isNaN(hc) || hc < 1)
      next.headcount = tx('Kişi sayısı en az 1 olmalı.')
    return next
  }

  const mutation = useMutation({
    mutationFn: () =>
      recruitmentApi.createPosting({
        title: title.trim(),
        departmentId,
        description: description.trim() || undefined,
        employmentType,
        headcount: Number(headcount),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      // Yeni ilan taslak olarak açılır; kullanıcı onu Taslak sekmesinde bulur.
      toast.ok(tx('İlan taslak olarak oluşturuldu; Taslak sekmesinde görebilirsiniz.'))
      onCreated?.()
      onClose()
      setTitle('')
      setDescription('')
      setHeadcount('1')
      setErrors({})
      setSubmitted(false)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İlan oluşturulamadı.')),
  })

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitted(true)
    const next = validate()
    setErrors(next)
    if (Object.keys(next).length === 0) mutation.mutate()
  }

  const summary: SummaryItem[] = submitted
    ? ([
        errors.title && { fieldId: 'posting-title', message: errors.title },
        errors.departmentId && { fieldId: 'posting-department', message: errors.departmentId },
        errors.headcount && { fieldId: 'posting-headcount', message: errors.headcount },
      ].filter(Boolean) as SummaryItem[])
    : []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni ilan')}
      note={tx('İlan taslak olarak açılır, yayına almak ayrı bir adım.')}
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
            form="new-posting"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('İlanı oluştur')}
          </Button>
        </>
      }
    >
      <form id="new-posting" onSubmit={handleSubmit} noValidate className="space-y-4">
        <ErrorSummary items={summary} />

        <TextField
          id="posting-title"
          label={tx('Pozisyon başlığı')}
          required
          value={title}
          maxLength={200}
          onChange={(e) => setTitle(e.target.value)}
          onBlur={() => submitted && setErrors(validate())}
          error={errors.title}
        />

        <SelectField
          id="posting-department"
          label={tx('Departman')}
          required
          value={departmentId}
          onChange={(v) => {
            setDepartmentId(v)
            if (submitted) setErrors(validate(v))
          }}
          options={departments}
          placeholder={tx('Departman seçin')}
          hint={
            departments.length === 0 && !companies.isPending
              ? tx('Önce Organizasyon bölümünden departman tanımlamalısınız.')
              : undefined
          }
          error={errors.departmentId}
        />

        <div className="grid gap-4 sm:grid-cols-2">
          <SelectField
            id="posting-type"
            label={tx('Çalışma şekli')}
            value={employmentType}
            onChange={(v) => setEmploymentType(v as EmploymentType)}
            options={(Object.keys(employmentTypeLabels) as EmploymentType[]).map((t) => ({
              value: t,
              label: employmentTypeLabels[t],
            }))}
          />

          <TextField
            id="posting-headcount"
            label={tx('Kişi sayısı')}
            type="number"
            min={1}
            className="tabular"
            value={headcount}
            onChange={(e) => setHeadcount(e.target.value)}
            onBlur={() => submitted && setErrors(validate())}
            error={errors.headcount}
          />
        </div>

        <TextAreaField
          id="posting-description"
          label={tx('Açıklama')}
          rows={4}
          value={description}
          maxLength={4000}
          hint={tx('İsteğe bağlı.')}
          onChange={(e) => setDescription(e.target.value)}
        />
      </form>
    </Modal>
  )
}

export function JobPostingsPage() {
  const { can, tenantSlug } = useAuth()
  const navigate = useNavigate()
  const [tab, setTab] = useTabParam<TabKey>('durum', 'Published')
  const [modalOpen, setModalOpen] = useState(false)
  const postings = useJobPostings(tab === 'all' ? undefined : tab)

  const canManage = can('recruitment:publish')

  const columns: Array<Column<JobPosting>> = [
    {
      id: 'title',
      header: tx('İlan'),
      searchText: (p) => p.title,
      sortValue: (p) => p.title,
      cell: (p) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">{p.title}</p>
          <p className="mt-0.5 truncate text-[12px] text-muted-foreground">
            {employmentTypeLabels[p.employmentType]}
          </p>
        </div>
      ),
    },
    {
      id: 'headcount',
      header: tx('Kişi'),
      align: 'right',
      hideBelow: 'sm',
      sortValue: (p) => p.headcount,
      exportText: (p) => String(p.headcount),
      cell: (p) => formatNumber(p.headcount),
    },
    {
      id: 'applications',
      header: tx('Başvuru'),
      align: 'right',
      hideBelow: 'md',
      sortValue: (p) => p.applicationCount ?? p.applications?.length ?? 0,
      exportText: (p) => String(p.applicationCount ?? p.applications?.length ?? 0),
      cell: (p) => formatNumber(p.applicationCount ?? p.applications?.length ?? 0),
    },
    {
      id: 'createdAt',
      header: tx('Açılış'),
      align: 'right',
      hideBelow: 'lg',
      sortValue: (p) => new Date(p.createdAt).getTime(),
      exportText: (p) => formatDate(p.createdAt),
      cell: (p) => <span className="text-muted-foreground">{formatDate(p.createdAt)}</span>,
    },
    {
      id: 'status',
      header: tx('Durum'),
      align: 'right',
      sortValue: (p) => jobPostingStatusLabels[p.status] ?? '',
      exportText: (p) => jobPostingStatusLabels[p.status] ?? '',
      cell: (p) => <JobPostingStatusBadge status={p.status} />,
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('İşe alım')}
        description={tx('Açık ilanlar, başvurular ve mülakat süreci.')}
        actions={
          <>
            {tenantSlug && (
              <Button variant="ghost" className="cursor-pointer" asChild>
                <a href={`/kariyer/${tenantSlug}`} target="_blank" rel="noreferrer noopener" title={tx('Yayındaki ilanlar oturum gerektirmeyen bu sayfada listelenir')}>
                  <ExternalLink className="size-4" />
                  {tx('Kariyer sayfası')}
                </a>
              </Button>
            )}
            <Button variant="outline" className="cursor-pointer" asChild>
              <Link to="/panel/ise-alim/adaylar">
                <Users className="size-4" />
                {tx('Adaylar')}
              </Link>
            </Button>
            {canManage && (
              <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
                <Plus className="size-4" />
                {tx('Yeni ilan')}
              </Button>
            )}
          </>
        }
      />

      <Tabs tabs={TABS} value={tab} onChange={setTab} label={tx('İlan durumu')} />

      <DataTable
        rows={postings.data}
        rowKey={(p) => p.id}
        columns={columns}
        isLoading={postings.isPending}
        error={postings.error}
        onRetry={() => void postings.refetch()}
        onRowClick={(p) => navigate(`/panel/ise-alim/${p.id}`)}
        searchPlaceholder={tx('İlan başlığı ara')}
        exportFileName="ilanlar"
        emptyTitle={tx('Bu durumda ilan yok')}
        emptyDetail={tx('Başka bir durum sekmesi seçin ya da yeni bir ilan açın.')}
        emptyAction={
          canManage ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
              {tx('Yeni ilan')}
            </Button>
          ) : undefined
        }
      />

      <NewPostingModal open={modalOpen} onClose={() => setModalOpen(false)} onCreated={() => setTab('Draft')} />
    </div>
  )
}
