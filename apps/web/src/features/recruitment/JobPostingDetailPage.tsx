import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, LoaderCircle } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { CenteredSpinner, EmptyState, ErrorState } from '@/components/ui/States'
import {
  ApplicationStatusBadge,
  InterviewResultBadge,
  JobPostingStatusBadge,
} from '@/components/ui/ModuleBadges'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { recruitmentApi } from '@/api/recruitment'
import { useCandidates, useJobPosting } from '@/api/queries'
import {
  applicationStatusLabels,
  employmentTypeLabels,
  interviewTypeLabels,
  type Application,
  type ApplicationStatus,
  type Interview,
} from '@/api/types'
import { formatDate, formatDateTime, formatNumber } from '@/lib/format'
import { ApplicationFunnel } from './ApplicationFunnel'
import { OfferModal, OffersPanel, PipelineBoard, ScheduleInterviewModal, ScorecardsModal, ScorecardTemplatePanel } from './RecruitmentPlus'
import { MeetingPanel } from '@/features/shared/Meetings'
import { CareerDetailsPanel, StatusLinksPanel } from './RecruitmentW11'
import { tx } from '@/lib/i18n'
import { FitBadge, FitSummary, useCandidateFit } from './CandidateFit'
import { useConfirm } from '@/components/ui/Confirm'

/** Başvuru durumunu ilerletme — sıradaki mantıklı aşamayı önerir. */
const NEXT_STAGE: Partial<Record<ApplicationStatus, ApplicationStatus>> = {
  Applied: 'Screening',
  Screening: 'Interview',
  Interview: 'Offer',
  Offer: 'Hired',
}

function StatusModal({
  state,
  onClose,
}: {
  state: { application: Application; suggested?: ApplicationStatus } | null
  onClose: () => void
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<ApplicationStatus>('Screening')
  const [notes, setNotes] = useState('')

  // Modal her açılışta önerilen aşamayla başlasın.
  useEffect(() => {
    if (state) {
      setStatus(state.suggested ?? NEXT_STAGE[state.application.status] ?? state.application.status)
      setNotes('')
    }
  }, [state])

  const mutation = useMutation({
    mutationFn: () =>
      recruitmentApi.setApplicationStatus(state!.application.id, status, notes.trim() || undefined),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      toast.ok(tx('Başvuru durumu güncellendi'))
      onClose()
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Durum güncellenemedi.')),
  })

  if (!state) return null

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Başvuru durumu')}
      note={tx('Aday hangi aşamaya geçiyor?')}
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
            className="cursor-pointer"
            disabled={mutation.isPending}
            onClick={() => mutation.mutate()}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Güncelle')}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <SelectField
          id="app-status"
          label={tx('Yeni durum')}
          value={status}
          onChange={(v) => setStatus(v as ApplicationStatus)}
          options={(Object.keys(applicationStatusLabels) as ApplicationStatus[]).map((s) => ({
            value: s,
            label: applicationStatusLabels[s],
          }))}
        />
        <TextAreaField
          id="app-notes"
          label={tx('Not')}
          rows={3}
          value={notes}
          maxLength={1000}
          hint={tx('İsteğe bağlı. Başvuru geçmişinde görünür.')}
          onChange={(e) => setNotes(e.target.value)}
        />
      </div>
    </Modal>
  )
}

/** ISO zaman → datetime-local alanının beklediği yerel "YYYY-MM-DDTHH:mm". */
function toLocalInput(iso: string): string {
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** Planlanan mülakatı yeniden planlama: yeni zaman/süre; görüşmecilere bildirim sunucudan gider. */
function RescheduleInterviewModal({ target, onClose }: { target: { applicationId: string; interview: Interview } | null; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [at, setAt] = useState('')
  const [duration, setDuration] = useState('60')
  const [notify, setNotify] = useState(true)
  useEffect(() => {
    if (target) {
      setAt(toLocalInput(target.interview.scheduledAt))
      setDuration(String(target.interview.durationMinutes ?? 60))
      setNotify(true)
    }
  }, [target])
  const past = !at || Number.isNaN(new Date(at).getTime()) || new Date(at).getTime() < Date.now() - 5 * 60_000
  const dur = Number(duration)
  const durErr = !Number.isInteger(dur) || dur < 15 || dur > 480 ? tx('Süre 15-480 dakika olmalı') : undefined
  const mutation = useMutation({
    mutationFn: () =>
      recruitmentApi.rescheduleInterview(target!.applicationId, target!.interview.id, {
        scheduledAt: new Date(at).toISOString(),
        durationMinutes: dur,
        notifyCandidate: notify,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      toast.ok(tx('Mülakat yeniden planlandı; görüşmecilere bildirim gitti'))
      onClose()
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Mülakat yeniden planlanamadı.')),
  })
  if (!target) return null
  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Mülakatı yeniden planla')}
      note={tx('{0} mülakatı · şu an: {1}', [interviewTypeLabels[target.interview.type], formatDateTime(target.interview.scheduledAt)])}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={mutation.isPending}>{tx('Vazgeç')}</Button>
          <Button disabled={past || !!durErr || mutation.isPending} onClick={() => mutation.mutate()}>
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={tx('Yeni tarih ve saat')} type="datetime-local" value={at} error={at && past ? tx('Geçmiş bir zamana mülakat planlanamaz') : undefined} onChange={(e) => setAt(e.target.value)} />
        <TextField label={tx('Süre (dk)')} type="number" min={15} max={480} value={duration} error={durErr} onChange={(e) => setDuration(e.target.value)} />
        <label className="flex cursor-pointer items-center gap-2 text-[13px] sm:col-span-2">
          <input type="checkbox" checked={notify} onChange={(e) => setNotify(e.target.checked)} />
          {tx('Adaya yeni zamanı e-postayla bildir')}
        </label>
      </div>
    </Modal>
  )
}

export function JobPostingDetailPage() {
  const { postingId } = useParams<{ postingId: string }>()
  const { can } = useAuth()
  const toast = useToast()
  const queryClient = useQueryClient()
  const posting = useJobPosting(postingId)
  const candidates = useCandidates(undefined, can('recruitment:candidates'))

  const [statusFor, setStatusFor] = useState<{
    application: Application
    suggested?: ApplicationStatus
  } | null>(null)
  const [interviewFor, setInterviewFor] = useState<{ applicationId: string; candidateName: string } | null>(null)
  const [offerFor, setOfferFor] = useState<{ applicationId: string; candidateName: string; postingTitle: string } | null>(null)
  const [scorecardsFor, setScorecardsFor] = useState<string | null>(null)
  const [rescheduleFor, setRescheduleFor] = useState<{ applicationId: string; interview: Interview } | null>(null)
  // Dalga 10: aday–ilan uygunluğu (yardımcı; otomatik ret yok). İstek üzerine hesaplanır, denetim kaydına yazılır.
  const [showFit, setShowFit] = useState(false)
  const fit = useCandidateFit(postingId, showFit && can('recruitment:candidates'))
  const fitByApp = useMemo(() => new Map((fit.data?.results ?? []).map((r) => [r.applicationId, r])), [fit.data])

  const cancelInterview = useMutation({
    mutationFn: (v: { applicationId: string; interviewId: string }) =>
      recruitmentApi.cancelInterview(v.applicationId, v.interviewId, { notifyCandidate: true }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      toast.ok(tx('Mülakat iptal edildi; görüşmecilere bildirim gitti'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Mülakat iptal edilemedi.')),
  })

  const candidateName = useMemo(() => {
    const map = new Map<string, string>()
    for (const c of candidates.data ?? []) map.set(c.id, `${c.firstName} ${c.lastName}`)
    return map
  }, [candidates.data])

  const candidateByApplication = useMemo(() => {
    const map = new Map<string, string>()
    for (const a of posting.data?.applications ?? []) map.set(a.id, candidateName.get(a.candidateId) ?? '')
    return map
  }, [posting.data, candidateName])

  const publish = useMutation({
    mutationFn: () => recruitmentApi.publishPosting(postingId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      toast.ok(tx('İlan yayına alındı'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İlan yayınlanamadı.')),
  })

  const close = useMutation({
    mutationFn: () => recruitmentApi.closePosting(postingId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recruitment'] })
      toast.ok(tx('İlan kapatıldı'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İlan kapatılamadı.')),
  })
  const confirm = useConfirm()
  const askCancelInterview = async (applicationId: string, iv: Interview) => {
    if (await confirm({
      title: tx('Mülakat iptal edilsin mi?'),
      note: tx('{0} tarihindeki mülakat iptal edilir; görüşmecilere bildirim gider. Aday e-postayla davet edildiyse ona da iptal e-postası gönderilir.', [formatDateTime(iv.scheduledAt)]),
      action: tx('Mülakatı iptal et'),
    })) cancelInterview.mutate({ applicationId, interviewId: iv.id })
  }
  const askClose = async () => {
    // Kapanışta beklemedeki (gelecekteki) mülakatlar kendiliğinden iptal edilmez; kullanıcı uyarılır.
    const openInterviews = (posting.data?.applications ?? [])
      .flatMap((a) => a.interviews ?? [])
      .filter((iv) => iv.result === 'Pending' && new Date(iv.scheduledAt).getTime() > Date.now()).length
    if (await confirm({
      title: tx('İlan kapatılsın mı?'),
      note:
        tx('Kapatılan ilan yeni başvuru almaz ve yeniden yayına alınamaz; gerekirse yeni ilan açmanız gerekir. Mevcut başvurular korunur.') +
        (openInterviews > 0
          ? ' ' + tx('Bu ilanda planlanmış {0} mülakat var; kapanış onları iptal etmez. Gerekiyorsa mülakatları ayrıca iptal edin.', [formatNumber(openInterviews)])
          : ''),
      action: tx('İlanı kapat'),
    })) close.mutate()
  }

  if (posting.isPending) return <CenteredSpinner label={tx('İlan yükleniyor')} />

  if (posting.isError || !posting.data) {
    return (
      <Panel>
        <ErrorState
          title={tx('İlan bulunamadı')}
          message={posting.error instanceof Error ? posting.error.message : undefined}
          onRetry={() => void posting.refetch()}
        />
      </Panel>
    )
  }

  const data = posting.data
  const applications = data.applications ?? []
  const canManage = can('recruitment:candidates')
  const canPublish = can('recruitment:publish')

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2 cursor-pointer" asChild>
        <Link to="/panel/ise-alim">
          <ArrowLeft className="size-4" />
          {tx('İşe alım')}
        </Link>
      </Button>

      <PageHeader
        title={data.title}
        description={tx('{0}, {1} kişi. {2} başvuru.', [employmentTypeLabels[data.employmentType], formatNumber(data.headcount), formatNumber(applications.length)])}
        actions={
          <>
            <JobPostingStatusBadge status={data.status} />
            {canPublish && data.status === 'Draft' && (
              <Button
                className="cursor-pointer"
                disabled={publish.isPending}
                onClick={() => publish.mutate()}
              >
                {publish.isPending && <LoaderCircle className="size-4 animate-spin" />}
                {tx('Yayına al')}
              </Button>
            )}
            {canPublish && data.status === 'Published' && (
              <Button
                variant="outline"
                className="cursor-pointer"
                disabled={close.isPending}
                onClick={askClose}
              >
                {close.isPending && <LoaderCircle className="size-4 animate-spin" />}
                {tx('İlanı kapat')}
              </Button>
            )}
          </>
        }
      />

      {canManage && (
        <Panel>
          <PanelHead title={tx('Aday panosu')} note={tx('Kartları sürükleyip bırakarak aşama değiştirin (ya da kart üzerindeki "Taşı" listesini kullanın).')} />
          <PanelBody>
            <PipelineBoard
              postingId={data.id}
              canManage={canManage}
              canOffer={canPublish}
              onSchedule={(c) => setInterviewFor({ applicationId: c.id, candidateName: c.candidateName ?? '' })}
              onOffer={(c) => setOfferFor({ applicationId: c.id, candidateName: c.candidateName ?? '', postingTitle: data.title })}
            />
          </PanelBody>
        </Panel>
      )}

      <div className="grid gap-4 xl:grid-cols-[1fr_1.15fr]">
        <Panel>
          <PanelHead title={tx('Başvuru hunisi')} note={tx('Aşamalar arası geçiş oranıyla')} />
          <PanelBody>
            {applications.length === 0 ? (
              <EmptyState
                title={tx('Henüz başvuru yok')}
                detail={tx('İlan yayına alındığında başvurular burada aşamalarına göre görünür.')}
              />
            ) : (
              <ApplicationFunnel applications={applications} />
            )}
          </PanelBody>
        </Panel>

        <Panel>
          <PanelHead
            title={tx('Başvurular')}
            action={
              <span className="flex items-center gap-3">
                {can('recruitment:candidates') && applications.length > 0 && (
                  <Button size="sm" variant={showFit ? 'default' : 'outline'} className="h-7 cursor-pointer text-[12px]" onClick={() => setShowFit((v) => !v)}>
                    {showFit ? tx('Uygunluğu gizle') : tx('Uygunluk puanı (yardımcı)')}
                  </Button>
                )}
                <span className="tabular text-[12px] text-muted-foreground">
                  {tx('{0} kayıt', [formatNumber(applications.length)])}</span>
              </span>
            }
          />
          {showFit && <FitSummary q={fit} />}
          {applications.length === 0 ? (
            <EmptyState title={tx('Başvuru yok')} detail={tx('Aday eklendikçe bu listede görünür.')} />
          ) : (
            <ul className="divide-y divide-border">
              {applications.map((a) => {
                const next = NEXT_STAGE[a.status]
                return (
                  <li key={a.id} className="px-4 py-3.5">
                    <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1.5">
                      <span className="min-w-0">
                        <span className="block truncate text-[14px] font-medium">
                          {candidateName.get(a.candidateId) ?? `${a.candidateId.slice(0, 8)}…`}
                        </span>
                        <span className="tabular mt-0.5 block text-[12px] text-muted-foreground">
                          {tx('{0} tarihinde başvurdu', [formatDate(a.appliedAt)])}</span>
                      </span>
                      <ApplicationStatusBadge status={a.status} />
                    </div>

                    {showFit && <FitBadge fit={fitByApp.get(a.id)} />}

                    {a.notes && (
                      <p className="mt-1.5 border-l-2 border-border pl-3 text-[13px] leading-relaxed text-muted-foreground">
                        {a.notes}
                      </p>
                    )}

                    {(a.interviews?.length ?? 0) > 0 && (
                      <ul className="mt-2 space-y-1">
                        {a.interviews!.map((iv) => (
                          <li
                            key={iv.id}
                            className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-[12px]"
                          >
                            <span className="text-muted-foreground">
                              {interviewTypeLabels[iv.type]}
                            </span>
                            <span className="tabular">{formatDateTime(iv.scheduledAt)}</span>
                            <InterviewResultBadge result={iv.result} />
                            {canManage && (
                              <Button size="sm" variant="ghost" className="h-6 px-2 text-[11.5px]" onClick={() => setScorecardsFor(iv.id)}>
                                {tx('Puan kartları')}
                              </Button>
                            )}
                            {canManage && iv.result === 'Pending' && new Date(iv.scheduledAt) > new Date() && (
                              <>
                                <Button size="sm" variant="ghost" className="h-6 px-2 text-[11.5px]" onClick={() => setRescheduleFor({ applicationId: a.id, interview: iv })}>
                                  {tx('Yeniden planla')}
                                </Button>
                                <Button size="sm" variant="ghost" className="h-6 px-2 text-[11.5px] text-destructive" disabled={cancelInterview.isPending} onClick={() => void askCancelInterview(a.id, iv)}>
                                  {tx('İptal et')}
                                </Button>
                              </>
                            )}
                            {iv.result === 'Pending' && new Date(iv.scheduledAt) > new Date() && (
                              <div className="w-full pt-1">
                                <MeetingPanel sourceType="interview" sourceId={iv.id} canCreate={canManage} candidate />
                              </div>
                            )}
                          </li>
                        ))}
                      </ul>
                    )}

                    {canManage && (
                      <div className="mt-3 flex flex-wrap gap-2">
                        {next && (
                          <Button
                            size="sm"
                            className="cursor-pointer"
                            onClick={() => setStatusFor({ application: a, suggested: next })}
                          >
                            {tx('{0} aşamasına al', [applicationStatusLabels[next]])}</Button>
                        )}
                        <Button
                          size="sm"
                          variant="outline"
                          className="cursor-pointer"
                          onClick={() => setStatusFor({ application: a })}
                        >
                          {tx('Durum değiştir')}
                        </Button>
                        <Button
                          size="sm"
                          variant="ghost"
                          className="cursor-pointer"
                          onClick={() => setInterviewFor({ applicationId: a.id, candidateName: candidateName.get(a.candidateId) ?? '' })}
                        >
                          {tx('Mülakat planla')}
                        </Button>
                      </div>
                    )}
                  </li>
                )
              })}
            </ul>
          )}
        </Panel>
      </div>

      {data.description && (
        <Panel>
          <PanelHead title={tx('İlan metni')} />
          <PanelBody>
            <p className="text-[14px] leading-relaxed whitespace-pre-wrap text-muted-foreground">
              {data.description}
            </p>
          </PanelBody>
        </Panel>
      )}

      {canManage && (
        <div className="grid gap-4 xl:grid-cols-[1.4fr_1fr]">
          <OffersPanel postingId={data.id} isHr={canPublish} names={candidateByApplication} />
          <ScorecardTemplatePanel postingId={data.id} canEdit={canManage} />
        </div>
      )}

      {/* Dalga 11: aday durum bağlantısı (74) ve Google for Jobs alanları (75) */}
      {canManage && (
        <div className="grid gap-4 xl:grid-cols-2">
          <StatusLinksPanel applications={data.applications ?? []} names={candidateByApplication} />
          {canPublish && <CareerDetailsPanel posting={data} />}
        </div>
      )}

      <StatusModal state={statusFor} onClose={() => setStatusFor(null)} />
      <ScheduleInterviewModal target={interviewFor} onClose={() => setInterviewFor(null)} />
      <OfferModal target={offerFor} onClose={() => setOfferFor(null)} />
      <ScorecardsModal interviewId={scorecardsFor} onClose={() => setScorecardsFor(null)} />
      <RescheduleInterviewModal target={rescheduleFor} onClose={() => setRescheduleFor(null)} />
    </div>
  )
}
