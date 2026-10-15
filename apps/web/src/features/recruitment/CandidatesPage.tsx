import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowLeft, FileUp, LoaderCircle, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { ApplicationStatusBadge } from '@/components/ui/ModuleBadges'
import { Modal } from '@/components/ui/Modal'
import { TextAreaField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { recruitmentApi } from '@/api/recruitment'
import { useCandidates } from '@/api/queries'
import type { Candidate } from '@/api/types'
import { formatDate, formatNumber } from '@/lib/format'
import { ApiError } from '@/api/client'
import { aiApi } from '@/api/ai'
import { ChipInput, errMsg } from '@/features/shared/kit'
import { SkillSuggest } from '@/features/shared/SkillSuggest'
import { tx } from '@/lib/i18n'

interface DuplicateInfo { message: string; existingCandidateId?: string; canForce?: boolean }

function NewCandidateModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [firstName, setFirstName] = useState('')
  const [lastName, setLastName] = useState('')
  const [email, setEmail] = useState('')
  const [phone, setPhone] = useState('')
  const [source, setSource] = useState('')
  const [skills, setSkills] = useState<string[]>([])
  const [resumeText, setResumeText] = useState('')
  const [error, setError] = useState<string | undefined>()
  const [duplicate, setDuplicate] = useState<DuplicateInfo | null>(null)
  const [parsing, setParsing] = useState(false)

  const reset = () => {
    setFirstName(''); setLastName(''); setEmail(''); setPhone(''); setSource(''); setSkills([]); setResumeText(''); setDuplicate(null)
  }

  const mutation = useMutation({
    mutationFn: (force: boolean) =>
      recruitmentApi.createCandidate({
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        email: email.trim(),
        phone: phone.trim() || undefined,
        source: source.trim() || undefined,
        skills: skills.length ? skills : undefined,
        resumeText: resumeText.trim() || undefined,
        force,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      toast.ok(tx('Aday kaydedildi'))
      onClose()
      reset()
    },
    onError: (e: unknown) => {
      // G13: tekrar aday — 409 gövdesinde mevcut aday kimliği ve zorlanabilirlik bilgisi gelir.
      if (e instanceof ApiError && e.status === 409 && e.detail && typeof e.detail === 'object') {
        const d = e.detail as { message?: string; existingCandidateId?: string; canForce?: boolean }
        setDuplicate({ message: errMsg(e), existingCandidateId: d.existingCandidateId, canForce: d.canForce })
        return
      }
      toast.stop(e instanceof Error ? e.message : tx('Aday kaydedilemedi.'))
    },
  })

  /** CV'den ön doldurma (ml-inference CV ayrıştırma): ad, e-posta, telefon, beceriler. Dosya saklanmaz. */
  async function parseCv(file: File) {
    if (file.size > 5 * 1024 * 1024) return toast.stop(tx('Dosya 5 MB\'tan büyük olamaz'))
    setParsing(true)
    try {
      const r = await aiApi.parseCv(file)
      if (r.name && !firstName && !lastName) {
        const parts = r.name.trim().split(/\s+/)
        setLastName(parts.length > 1 ? parts.pop()! : '')
        setFirstName(parts.join(' '))
      }
      if (r.email && !email) setEmail(r.email)
      if (r.phone && !phone) setPhone(r.phone)
      if (r.skills.length) setSkills((s) => Array.from(new Set([...s, ...r.skills])).slice(0, 40))
      if (r.summary && !resumeText) setResumeText(r.summary)
      toast.ok(r.warnings.length ? tx('CV okundu; kontrol edin: {0}', [r.warnings.join(' ')]) : tx('CV okundu, alanlar dolduruldu'))
    } catch (e) {
      toast.stop(errMsg(e, tx('CV okunamadı')))
    } finally {
      setParsing(false)
    }
  }

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (firstName.trim().length < 2 || lastName.trim().length < 2)
      return setError(tx('Ad ve soyad en az 2 karakter olmalı.'))
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(email.trim()))
      return setError(tx('Geçerli bir e-posta adresi girin.'))
    setError(undefined)
    setDuplicate(null)
    mutation.mutate(false)
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni aday')}
      note={tx('Aday havuzuna eklenir; başvuruyu ilan sayfasından bağlarsınız.')}
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
            form="new-candidate"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Adayı kaydet')}
          </Button>
        </>
      }
    >
      <form id="new-candidate" onSubmit={submit} noValidate className="space-y-4">
        <label className="flex cursor-pointer items-center gap-2 rounded-xl border border-dashed border-border px-3 py-2.5 text-[13px] hover:border-primary/50">
          {parsing ? <LoaderCircle className="size-4 animate-spin" /> : <FileUp className="size-4 text-primary" />}
          <span>{tx('CV\'den doldur (PDF, DOCX, TXT · en fazla 5 MB)')}</span>
          <span className="ml-auto text-[11.5px] text-muted-foreground">{tx('Dosya saklanmaz')}</span>
          <input type="file" accept=".pdf,.docx,.txt" className="sr-only" disabled={parsing}
            onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void parseCv(f) }} />
        </label>

        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="cand-first"
            label={tx('Ad')}
            required
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
            error={error?.includes('Ad ve soyad') ? error : undefined}
          />
          <TextField
            id="cand-last"
            label={tx('Soyad')}
            required
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
          />
        </div>

        <TextField
          id="cand-email"
          label="E-posta"
          type="email"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={error?.includes('e-posta') ? error : undefined}
        />

        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="cand-phone"
            label={tx('Telefon')}
            type="tel"
            hint={tx('İsteğe bağlı')}
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
          />
          <TextField
            id="cand-source"
            label={tx('Kaynak')}
            hint={tx('Örn. LinkedIn, referans')}
            value={source}
            onChange={(e) => setSource(e.target.value)}
          />
        </div>

        <ChipInput id="cand-skills" label={tx('Beceriler')} value={skills} onChange={setSkills} />
        {(skills.length > 0 || resumeText.trim().length > 0) && (
          <SkillSuggest paste={false} text={[resumeText, ...skills].join('\n')} existing={skills} onAdd={(s) => setSkills((x) => [...x, s].slice(0, 40))} />
        )}
        <TextAreaField id="cand-summary" label={tx('Özgeçmiş özeti (isteğe bağlı)')} rows={3} maxLength={20000} value={resumeText}
          onChange={(e) => setResumeText(e.target.value)} hint={tx('Özel nitelikli veri (sağlık, din, medeni hal…) eklemeyin.')} />

        {duplicate && (
          <div role="alert" className="space-y-2 rounded-xl border border-amber-500/40 bg-amber-500/10 p-3 text-[13px]">
            <p className="flex items-center gap-1.5 font-medium"><AlertTriangle className="size-4 text-amber-600" />{tx('Olası tekrar aday')}</p>
            <p>{duplicate.message}</p>
            <div className="flex flex-wrap gap-2">
              {duplicate.canForce && (
                <Button type="button" size="sm" variant="outline" disabled={mutation.isPending} onClick={() => mutation.mutate(true)}>
                  {tx('Yine de yeni aday olarak kaydet')}
                </Button>
              )}
              <Button type="button" size="sm" variant="ghost" onClick={() => { setDuplicate(null); onClose() }}>
                {tx('Vazgeç, mevcut adayı kullan')}
              </Button>
            </div>
          </div>
        )}
      </form>
    </Modal>
  )
}

export function CandidatesPage() {
  const { can } = useAuth()
  const [modalOpen, setModalOpen] = useState(false)
  // Arama DataTable içinde istemci tarafında yapılıyor; havuzun tamamı çekilir.
  const candidates = useCandidates(undefined)

  const canManage = can('recruitment:candidates')

  const columns: Array<Column<Candidate>> = [
    {
      id: 'name',
      header: tx('Aday'),
      searchText: (c) => `${c.firstName} ${c.lastName} ${c.email}`,
      sortValue: (c) => `${c.firstName} ${c.lastName}`,
      exportText: (c) => `${c.firstName} ${c.lastName}`,
      cell: (c) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">
            {c.firstName} {c.lastName}
          </p>
          <p className="mt-0.5 truncate text-[12px] text-muted-foreground">{c.email}</p>
        </div>
      ),
    },
    {
      id: 'phone',
      header: tx('Telefon'),
      hideBelow: 'lg',
      searchText: (c) => c.phone ?? '',
      exportText: (c) => c.phone ?? '—',
      cell: (c) => <span className="tabular text-muted-foreground">{c.phone ?? '—'}</span>,
    },
    {
      id: 'source',
      header: tx('Kaynak'),
      hideBelow: 'md',
      searchText: (c) => c.source ?? '',
      sortValue: (c) => c.source ?? '',
      exportText: (c) => c.source ?? '—',
      cell: (c) => <span className="text-muted-foreground">{c.source ?? '—'}</span>,
    },
    {
      id: 'applications',
      header: tx('Başvuru'),
      sortValue: (c) => c.applications?.length ?? 0,
      exportText: (c) => String(c.applications?.length ?? 0),
      cell: (c) =>
        (c.applications?.length ?? 0) === 0 ? (
          <span className="text-muted-foreground">—</span>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {c.applications!.slice(0, 3).map((a) => (
              <ApplicationStatusBadge key={a.id} status={a.status} />
            ))}
            {c.applications!.length > 3 && (
              <span className="tabular text-[12px] text-muted-foreground">
                +{c.applications!.length - 3}
              </span>
            )}
          </div>
        ),
    },
    {
      id: 'createdAt',
      header: tx('Kayıt'),
      align: 'right',
      hideBelow: 'sm',
      sortValue: (c) => new Date(c.createdAt).getTime(),
      exportText: (c) => formatDate(c.createdAt),
      cell: (c) => <span className="text-muted-foreground">{formatDate(c.createdAt)}</span>,
    },
  ]

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/ise-alim">
          <ArrowLeft className="size-4" />
          {tx('İşe alım')}
        </Link>
      </Button>

      <PageHeader
        title={tx('Adaylar')}
        description={tx('Aday havuzu ve başvuru geçmişleri.{0}', [candidates.data ? tx(' {0} kayıt.', [formatNumber(candidates.data.length)]) : ''])}
        actions={
          canManage && (
            <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
              <Plus className="size-4" />
              {tx('Yeni aday')}
            </Button>
          )
        }
      />

      <DataTable
        rows={candidates.data}
        rowKey={(c) => c.id}
        columns={columns}
        isLoading={candidates.isPending}
        error={candidates.error}
        onRetry={() => void candidates.refetch()}
        searchPlaceholder={tx('Ad, soyad, e-posta veya kaynak')}
        exportFileName="adaylar"
        viewKey="candidates"
        pageSize={12}
        initialSort={{ columnId: 'createdAt', dir: 'desc' }}
        emptyTitle={tx('Aday kaydı yok')}
        emptyDetail={tx('İlk adayı ekleyerek havuzu oluşturmaya başlayın.')}
        emptyAction={
          canManage ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
              {tx('Yeni aday')}
            </Button>
          ) : undefined
        }
      />

      <NewCandidateModal open={modalOpen} onClose={() => setModalOpen(false)} />
    </div>
  )
}
