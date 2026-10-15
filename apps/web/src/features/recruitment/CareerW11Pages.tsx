/**
 * Dalga 11: herkese açık ilan ayrıntısı (/kariyer/:tenant/ilan/:jobId) — Google for Jobs için
 * schema.org JobPosting JSON-LD'si sayfaya <script type="application/ld+json"> olarak eklenir —
 * ve aday durum bağlantısı (/kariyer/:tenant/durum/:token): yalnızca kaba aşama ve sonraki adım.
 *
 * CSP notu: script-src 'self' yalnızca ÇALIŞTIRILAN betikleri sınırlar; application/ld+json bir
 * veri bloğudur, tarayıcı çalıştırmaz ve CSP engellemez (e2e: test_career_jsonld).
 */
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, Copy, Info, MapPin, ShieldOff } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import { careerApi, employmentTypeLabels, type PublicApplyResult } from '@/api/recruitment'
import { careerW11Api, injectJsonLd, type PublicStatus } from '@/api/recruitmentW11'
import { formatDate, formatMoney } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'
import { ApplyForm, Card, PrivacyNotice, Shell } from './CareerPages'

const PERIOD: Record<string, string> = { HOUR: tx('saatlik'), DAY: tx('günlük'), WEEK: tx('haftalık'), MONTH: tx('aylık'), YEAR: tx('yıllık') }

/** /kariyer/:tenant/ilan/:jobId */
export function CareerJobPage() {
  const { tenant = '', jobId = '' } = useParams()
  const q = useQuery({ queryKey: ['career-job', tenant, jobId], queryFn: ({ signal }) => careerW11Api.job(tenant, jobId, signal), retry: false })
  const list = useQuery({ queryKey: ['career', tenant], queryFn: ({ signal }) => careerApi.jobs(tenant, signal), retry: false })
  const [open, setOpen] = useState(false)
  const [done, setDone] = useState<PublicApplyResult | null>(null)
  useEffect(() => injectJsonLd(q.data?.jsonLd), [q.data?.jsonLd])
  useEffect(() => {
    if (q.data) document.title = `${q.data.job.title} · ${q.data.company}`
  }, [q.data])
  const link = done ? `${window.location.origin}${done.selfServicePath}` : ''
  const j = q.data?.job
  return (
    <Shell company={q.data?.company}>
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError || !j ? <Card><p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p></Card> : done ? (
        <Card className="space-y-3">
          <p className="flex items-center gap-2 text-[15px] font-semibold"><CheckCircle2 className="size-5 text-emerald-600" />{txServer(done.message)}</p>
          <p className="text-[13px]">{tx('Başvurunuzun durumunu görmek ve verilerinizin silinmesini istemek için aşağıdaki kişisel bağlantıyı saklayın. Bu bağlantı yalnızca şimdi gösterilir ve e-postayla gönderilmez.')}</p>
          <div className="flex gap-2">
            <input readOnly value={link} aria-label={tx('Kişisel bağlantı')} className="h-9 flex-1 rounded-lg border border-input bg-muted/40 px-2 text-[12.5px]" />
            <Button variant="outline" onClick={() => void navigator.clipboard?.writeText(link)}><Copy className="size-4" /> {tx('Kopyala')}</Button>
          </div>
          <Button asChild variant="ghost"><Link to={done.selfServicePath}>{tx('Başvuru sayfama git')}</Link></Button>
        </Card>
      ) : (
        <div className="space-y-4">
          <Card>
            <h1 className="text-2xl font-semibold tracking-tight">{j.title}</h1>
            <p className="mt-1 flex flex-wrap items-center gap-x-2 text-[12.5px] text-muted-foreground">
              {[j.department, employmentTypeLabels[j.employmentType], j.publishedAt ? formatDate(j.publishedAt) : null].filter(Boolean).join(' · ')}
              {(j.location || j.remoteAllowed) && (
                <span className="inline-flex items-center gap-1"><MapPin className="size-3.5" />{placeText(j.location, j.region, j.remoteAllowed)}</span>
              )}
            </p>
            {j.salary && (
              <p className="mt-2 text-[13px]">
                <strong>{tx('Ücret aralığı:')}</strong>{' '}
                {[j.salary.min, j.salary.max].filter((x): x is number => x != null).map((x) => formatMoney(x, j.salary!.currency)).join(' – ')}
                {' '}({PERIOD[j.salary.period] ?? j.salary.period}, {tx('brüt')})
              </p>
            )}
            {j.validThrough && <p className="mt-1 text-[12.5px] text-muted-foreground">{tx('Son başvuru: {0}', [formatDate(j.validThrough)])}</p>}
            {j.description && <p className="mt-4 text-[13.5px] leading-relaxed whitespace-pre-wrap">{j.description}</p>}
            <div className="mt-4">
              {q.data.expired ? <p className="text-[13px] text-muted-foreground">{tx('Bu ilanın son başvuru tarihi geçti.')}</p>
                : !open && <Button onClick={() => setOpen(true)}>{tx('Başvur')}</Button>}
            </div>
            {open && !q.data.expired && list.data && (
              <div className="mt-4 border-t border-border pt-4">
                <ApplyForm tenant={tenant} job={{ id: j.id, title: j.title, description: j.description, employmentType: j.employmentType, department: j.department, publishedAt: j.publishedAt }}
                  notice={list.data.privacyNotice.text} version={list.data.privacyNotice.version} poolMonths={list.data.poolMonths} onDone={setDone} />
              </div>
            )}
          </Card>
          {list.data && <PrivacyNotice text={list.data.privacyNotice.text} />}
          <p className="text-center"><Link className="text-[12.5px] text-primary underline" to={`/kariyer/${tenant}`}>{tx('Diğer açık pozisyonlar')}</Link></p>
        </div>
      )}
    </Shell>
  )
}

const TONE: Record<PublicStatus['status'], StatusTone> = {
  Received: 'info', InReview: 'warning', Offer: 'info', Positive: 'success', Negative: 'danger', Withdrawn: 'neutral', Closed: 'neutral',
}

/** /kariyer/:tenant/durum/:token — oturumsuz, yalnızca kaba durum (kişisel veri yok). */
export function CandidateStatusPage() {
  const { tenant = '', token = '' } = useParams()
  const q = useQuery({ queryKey: ['career-status', tenant, token], queryFn: ({ signal }) => careerW11Api.status(tenant, token, signal), retry: false })
  const [revoked, setRevoked] = useState(false)
  const [confirm, setConfirm] = useState(false)
  const revoke = useAction(() => careerW11Api.revokeStatus(tenant, token), { onDone: () => setRevoked(true) })
  if (revoked) return <Shell><Card><p className="flex items-center gap-2 text-[14px]"><ShieldOff className="size-5 text-primary" />{tx('Bağlantı iptal edildi; artık açılmaz.')}</p></Card></Shell>
  return (
    <Shell company={q.data?.company}>
      {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <Card><p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p></Card> : (
        <div className="space-y-4">
          <Card>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <h1 className="text-lg font-semibold">{q.data.posting ?? tx('Başvuru')}</h1>
                <p className="text-[12.5px] text-muted-foreground">{tx('Başvuru tarihi {0}', [formatDate(q.data.appliedAt)])} · {tx('Son güncelleme {0}', [formatDate(q.data.updatedAt)])}</p>
              </div>
              <StatusBadge tone={TONE[q.data.status]}>{txServer(q.data.statusLabel)}</StatusBadge>
            </div>
            <p className="mt-4 flex items-start gap-2 rounded-xl bg-muted/40 p-3 text-[13px]"><Info className="mt-0.5 size-4 shrink-0 text-primary" /><span><strong>{tx('Sonraki adım:')}</strong> {txServer(q.data.nextStep)}</span></p>
          </Card>
          <Card className="space-y-2 text-[12.5px] text-muted-foreground">
            <p>{tx('Bu sayfa yalnızca başvurunuzun genel durumunu gösterir; kişisel verileriniz burada yer almaz.')}</p>
            <p>{tx('Bağlantı {0} tarihine kadar geçerlidir. Başkasıyla paylaştıysanız iptal edebilirsiniz.', [formatDate(q.data.expiresAt)])}</p>
            {!confirm ? <Button variant="outline" size="sm" onClick={() => setConfirm(true)}>{tx('Bağlantıyı iptal et')}</Button> : (
              <div className="flex gap-2">
                <Button variant="destructive" size="sm" disabled={revoke.isPending} onClick={() => revoke.mutate(undefined)}>{tx('Evet, iptal et')}</Button>
                <Button variant="ghost" size="sm" onClick={() => setConfirm(false)}>{tx('Vazgeç')}</Button>
              </div>
            )}
          </Card>
          <p className="text-center"><Link className="text-[12.5px] text-primary underline" to={`/kariyer/${tenant}`}>{tx('Diğer açık pozisyonlar')}</Link></p>
        </div>
      )}
    </Shell>
  )
}

/** "İstanbul, İstanbul" yerine tek "İstanbul": şehir ile bölge aynıysa bölge yazılmaz. */
export function placeText(location?: string | null, region?: string | null, remote?: boolean | null): string {
  const norm = (v?: string | null) => (v ?? '').trim().toLocaleLowerCase('tr-TR')
  const parts = [location?.trim(), norm(region) && norm(region) !== norm(location) ? region!.trim() : null, remote ? tx('Uzaktan çalışılabilir') : null]
  return parts.filter(Boolean).join(', ')
}
