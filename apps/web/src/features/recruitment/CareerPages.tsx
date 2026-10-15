/**
 * Y16: herkese açık kariyer sayfası (/kariyer/:tenant) ve aday öz-hizmeti
 * (/kariyer/:tenant/basvuru/:token). Oturum gerekmez; /panel ağacının dışındadır.
 *
 * KVKK: aydınlatma metni BİLGİ olarak gösterilir (onay kutusu değildir); aday havuzunda
 * saklama için AYRI ve isteğe bağlı açık rıza kutusu vardır. Kişisel bağlantı yalnızca
 * başvurudan hemen sonra bir kez gösterilir (sunucuda yalnızca özeti tutulur).
 */
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Briefcase, CalendarClock, CheckCircle2, Copy, ShieldCheck, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import { careerApi, employmentTypeLabels, interviewTypeLabels, type PublicApplyResult, type PublicJob, type SelfService } from '@/api/recruitment'
import { formatDate, formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { printLetter } from './RecruitmentPlus'
import { tx, txServer } from '@/lib/i18n'
import { useFormToken } from '@/lib/formGuard'

export function Shell({ company, children }: { company?: string; children: React.ReactNode }) {
  return (
    <div className="min-h-dvh bg-background px-4 py-8">
      <div className="mx-auto w-full max-w-3xl">
        <header className="mb-6 flex items-center gap-2">
          <Briefcase className="size-5 text-primary" />
          <span className="text-[15px] font-semibold">{company ?? tx('Kariyer')}</span>
          <span className="text-[13px] text-muted-foreground">· {tx('Kariyer')}</span>
        </header>
        {children}
        <p className="mt-10 text-center text-[11.5px] text-muted-foreground">HR360</p>
      </div>
    </div>
  )
}

export function Card({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <section className={`rounded-2xl border border-border bg-card p-5 shadow-sm ${className}`}>{children}</section>
}

export function PrivacyNotice({ text }: { text: string }) {
  return (
    <details className="rounded-xl border border-border bg-muted/30 px-3.5 py-2.5 text-[12.5px]">
      <summary className="cursor-pointer font-medium"><ShieldCheck className="mr-1.5 inline size-4 text-primary" />{tx('KVKK aydınlatma metni (bilgilendirme)')}</summary>
      <p className="mt-2 leading-relaxed whitespace-pre-wrap text-muted-foreground">{text}</p>
    </details>
  )
}

export function ApplyForm({ tenant, job, notice, version, poolMonths, onDone }: {
  tenant: string; job: PublicJob; notice: string; version: string; poolMonths: number; onDone: (r: PublicApplyResult) => void
}) {
  const [f, setF] = useState({ firstName: '', lastName: '', email: '', phone: '', coverNote: '', resumeText: '', consent: false, website: '' })
  const [error, setError] = useState<string>()
  const guard = useFormToken(() => careerApi.formToken(tenant), tenant)
  const apply = useAction(async () => careerApi.apply(tenant, job.id, {
    firstName: f.firstName.trim(), lastName: f.lastName.trim(), email: f.email.trim(), phone: f.phone.trim() || undefined,
    coverNote: f.coverNote.trim() || undefined, resumeText: f.resumeText.trim() || undefined,
    talentPoolConsent: f.consent, privacyNoticeVersion: version, website: f.website || undefined,
    formToken: await guard.take(),
  }), { onDone })
  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (f.firstName.trim().length < 2 || f.lastName.trim().length < 2) return setError(tx('Ad ve soyad en az 2 karakter olmalı.'))
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(f.email.trim())) return setError(tx('Geçerli bir e-posta adresi girin.'))
    setError(undefined)
    apply.mutate(undefined, { onError: (x) => setError(errMsg(x)) })
  }
  return (
    <form onSubmit={submit} noValidate className="space-y-3">
      <div className="grid gap-3 sm:grid-cols-2">
        <TextField label={tx('Ad')} required autoComplete="given-name" maxLength={100} value={f.firstName} onChange={(e) => setF({ ...f, firstName: e.target.value })} />
        <TextField label={tx('Soyad')} required autoComplete="family-name" maxLength={100} value={f.lastName} onChange={(e) => setF({ ...f, lastName: e.target.value })} />
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        <TextField label={tx('E-posta')} type="email" required autoComplete="email" maxLength={200} value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} />
        <TextField label={tx('Telefon (isteğe bağlı)')} type="tel" autoComplete="tel" maxLength={30} value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} />
      </div>
      <TextAreaField label={tx('Ön yazı (isteğe bağlı)')} rows={4} maxLength={3000} value={f.coverNote} onChange={(e) => setF({ ...f, coverNote: e.target.value })} />
      <TextAreaField label={tx('Özgeçmiş (metin olarak yapıştırın, isteğe bağlı)')} rows={6} maxLength={20000} value={f.resumeText}
        onChange={(e) => setF({ ...f, resumeText: e.target.value })} hint={tx('Fotoğraf, kimlik numarası, sağlık veya din gibi bilgileri eklemeyin.')} />
      {/* Bot tuzağı: görünmez alan. */}
      <input type="text" name="website" tabIndex={-1} autoComplete="off" aria-hidden="true" className="hidden" value={f.website} onChange={(e) => setF({ ...f, website: e.target.value })} />
      <PrivacyNotice text={notice} />
      <label className="flex items-start gap-2.5 rounded-xl border border-dashed border-border p-3 text-[13px]">
        <Checkbox className="mt-0.5" checked={f.consent} onCheckedChange={(v) => setF({ ...f, consent: v === true })} />
        <span>
          <strong>{tx('İsteğe bağlı açık rıza:')}</strong>{' '}
          {tx('Bilgilerimin başka pozisyonlarda değerlendirilmek üzere {0} ay süreyle CV havuzunda saklanmasına açık rıza veriyorum.', [poolMonths])}
          <span className="block text-[12px] text-muted-foreground">{tx('Bu kutuyu işaretlemeseniz de başvurunuz değerlendirilir. Rızanızı istediğiniz an geri alabilirsiniz.')}</span>
        </span>
      </label>
      {error && <p role="alert" className="text-[13px] text-destructive">{error}</p>}
      <Button type="submit" disabled={apply.isPending}>{tx('Başvur')}</Button>
    </form>
  )
}

/** /kariyer/:tenant */
export function CareerPage() {
  const { tenant = '' } = useParams()
  const q = useQuery({ queryKey: ['career', tenant], queryFn: ({ signal }) => careerApi.jobs(tenant, signal), retry: false })
  const [open, setOpen] = useState<string | null>(null)
  const [done, setDone] = useState<PublicApplyResult | null>(null)
  const link = done ? `${window.location.origin}${done.selfServicePath}` : ''
  return (
    <Shell company={q.data?.company}>
      {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <Card><p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p></Card> : done ? (
        <Card className="space-y-3">
          <p className="flex items-center gap-2 text-[15px] font-semibold"><CheckCircle2 className="size-5 text-emerald-600" />{done.message}</p>
          <p className="text-[13px]">{tx('Başvurunuzun durumunu görmek ve verilerinizin silinmesini istemek için aşağıdaki kişisel bağlantıyı saklayın. Bu bağlantı yalnızca şimdi gösterilir ve e-postayla gönderilmez.')}</p>
          <div className="flex gap-2">
            <input readOnly value={link} aria-label={tx('Kişisel bağlantı')} className="h-9 flex-1 rounded-lg border border-input bg-muted/40 px-2 text-[12.5px]" />
            <Button variant="outline" onClick={() => void navigator.clipboard?.writeText(link)}><Copy className="size-4" /> {tx('Kopyala')}</Button>
          </div>
          <Button asChild variant="ghost"><Link to={done.selfServicePath}>{tx('Başvuru sayfama git')}</Link></Button>
        </Card>
      ) : (
        <div className="space-y-4">
          <h1 className="text-2xl font-semibold tracking-tight">{tx('Açık pozisyonlar')}</h1>
          {q.data.jobs.length === 0 && <Card><p className="text-[13px] text-muted-foreground">{tx('Şu anda açık pozisyon bulunmuyor.')}</p></Card>}
          {q.data.jobs.map((j) => (
            <Card key={j.id}>
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div>
                  <h2 className="text-[16px] font-semibold"><Link className="hover:underline" to={`/kariyer/${tenant}/ilan/${j.id}`}>{j.title}</Link></h2>
                  <p className="text-[12.5px] text-muted-foreground">{[j.department, employmentTypeLabels[j.employmentType], j.publishedAt ? formatDate(j.publishedAt) : null].filter(Boolean).join(' · ')}</p>
                </div>
                {open !== j.id && <Button size="sm" onClick={() => setOpen(j.id)}>{tx('Başvur')}</Button>}
              </div>
              {j.description && <p className="mt-3 text-[13.5px] leading-relaxed whitespace-pre-wrap text-muted-foreground">{j.description}</p>}
              {open === j.id && (
                <div className="mt-4 border-t border-border pt-4">
                  <ApplyForm tenant={tenant} job={j} notice={q.data.privacyNotice.text} version={q.data.privacyNotice.version} poolMonths={q.data.poolMonths} onDone={setDone} />
                </div>
              )}
            </Card>
          ))}
        </div>
      )}
    </Shell>
  )
}

const STATUS_TONE: Record<SelfService['status'], StatusTone> = {
  Received: 'info', InReview: 'warning', Offer: 'info', Positive: 'success', Negative: 'danger', Withdrawn: 'neutral', Closed: 'neutral',
}

/** /kariyer/:tenant/basvuru/:token — aday öz-hizmeti (KVKK m.11). */
export function CandidateSelfServicePage() {
  const { tenant = '', token = '' } = useParams()
  const q = useQuery({ queryKey: ['career-self', tenant, token], queryFn: ({ signal }) => careerApi.selfService(tenant, token, signal), retry: false })
  const [confirm, setConfirm] = useState(false)
  const [deleted, setDeleted] = useState<string | null>(null)
  const inv = { invalidate: [['career-self', tenant, token]] }
  const consent = useAction((v: boolean) => careerApi.consent(tenant, token, v), { ...inv, success: (r) => (r.talentPoolConsent ? tx('Havuz rızanız kaydedildi') : tx('Havuz rızanız geri alındı')) })
  const respond = useAction((accept: boolean) => careerApi.respondOffer(tenant, token, accept), { ...inv, success: (r) => (r.status === 'Accepted' ? tx('Teklifi kabul ettiniz') : tx('Teklifi reddettiniz')) })
  const remove = useAction(() => careerApi.remove(tenant, token), {
    onDone: (r) => setDeleted(r.scope === 'candidate' ? tx('Tüm verileriniz silindi.') : tx('Bu başvuruya ait verileriniz silindi. Önceki kayıtlarınız için İK ile iletişime geçebilirsiniz.')),
  })
  if (deleted) return <Shell><Card><p className="flex items-center gap-2 text-[14px]"><CheckCircle2 className="size-5 text-emerald-600" />{deleted}</p></Card></Shell>
  return (
    <Shell company={q.data?.company}>
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <Card><p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p></Card> : (
        <div className="space-y-4">
          <Card>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <h1 className="text-lg font-semibold">{q.data.posting}</h1>
                <p className="text-[12.5px] text-muted-foreground">{tx('Başvuru tarihi {0}', [formatDate(q.data.appliedAt)])}</p>
              </div>
              <StatusBadge tone={STATUS_TONE[q.data.status]}>{txServer(q.data.statusLabel)}</StatusBadge>
            </div>
            {q.data.nextStep && <p className="mt-3 text-[13px]"><strong>{tx('Sonraki adım:')}</strong> {txServer(q.data.nextStep)}</p>}
            {q.data.interviews.map((i) => (
              <p key={i.scheduledAt} className="mt-3 flex items-center gap-2 text-[13px]">
                <CalendarClock className="size-4 text-primary" />
                {tx('{0} mülakatı: {1} ({2} dk){3}', [interviewTypeLabels[i.type], formatDateTime(i.scheduledAt), i.durationMinutes, i.location ? ` · ${i.location}` : ''])}
                {i.meetingUrl && <a className="text-primary underline" href={i.meetingUrl} target="_blank" rel="noreferrer noopener">{tx('Bağlantı')}</a>}
              </p>
            ))}
          </Card>

          {q.data.offer && (
            <Card className="space-y-3">
              <h2 className="text-[15px] font-semibold">{tx('İş teklifi')}</h2>
              <pre className="max-h-96 overflow-auto rounded-xl bg-muted/30 p-3 font-sans text-[12.5px] leading-relaxed whitespace-pre-wrap">{q.data.offer.letterText}</pre>
              <div className="flex flex-wrap gap-2">
                {q.data.offer.status === 'Sent' ? <>
                  {/* Dalga 11: kabul, e-postaya gelen kodla basit elektronik imza ile yapılır. */}
                  <Button asChild><Link to={`/kariyer/${tenant}/teklif/${token}`}>{tx('E-imza ile kabul et')}</Link></Button>
                  <Button variant="outline" disabled={respond.isPending} onClick={() => respond.mutate(false)}>{tx('Reddet')}</Button>
                </> : <StatusBadge tone={q.data.offer.status === 'Accepted' ? 'success' : 'neutral'}>{q.data.offer.status === 'Accepted' ? tx('Kabul edildi') : tx('Reddedildi')}</StatusBadge>}
                <Button variant="ghost" onClick={() => printLetter(tx('Teklif mektubu'), q.data.offer!.letterText)}>{tx('Yazdır / PDF')}</Button>
              </div>
              <p className="text-[12px] text-muted-foreground">{tx('Son yanıt tarihi: {0}', [formatDate(q.data.offer.expiresAt)])}</p>
            </Card>
          )}

          <Card className="space-y-3">
            <h2 className="text-[15px] font-semibold">{tx('Hakkınızda tuttuğumuz veriler')}</h2>
            <dl className="grid gap-x-4 gap-y-1.5 text-[13px] sm:grid-cols-[140px_1fr]">
              <dt className="text-muted-foreground">{tx('Ad soyad')}</dt><dd>{q.data.data.firstName} {q.data.data.lastName}</dd>
              <dt className="text-muted-foreground">{tx('E-posta')}</dt><dd>{q.data.data.email}</dd>
              <dt className="text-muted-foreground">{tx('Telefon')}</dt><dd>{q.data.data.phone ?? '—'}</dd>
              <dt className="text-muted-foreground">{tx('Ön yazı')}</dt><dd className="whitespace-pre-wrap">{q.data.data.coverNote ?? '—'}</dd>
              {q.data.data.resumeText && <><dt className="text-muted-foreground">{tx('Özgeçmiş')}</dt><dd className="max-h-40 overflow-auto whitespace-pre-wrap">{q.data.data.resumeText}</dd></>}
            </dl>
            {!q.data.ownsCandidate && <p className="text-[12px] text-muted-foreground">{tx('Bu başvuru daha önceki bir kaydınızla eşleştirildi; güvenliğiniz için önceki kayda ait bilgiler maskelenmiştir.')}</p>}
            <p className="text-[13px]">{q.data.retention}</p>
            <label className="flex items-center gap-2 text-[13px]">
              <Checkbox checked={q.data.talentPoolConsent} disabled={consent.isPending || (!q.data.ownsCandidate && !q.data.talentPoolConsent)}
                onCheckedChange={(v) => consent.mutate(v === true)} />
              {tx('CV havuzunda saklanmasına açık rıza (isteğe bağlı, istediğiniz an geri alınabilir)')}
            </label>
            <div className="border-t border-border pt-3">
              {!confirm ? (
                <Button variant="outline" onClick={() => setConfirm(true)}><Trash2 className="size-4" /> {tx('Verilerimi sil')}</Button>
              ) : (
                <div className="space-y-2 rounded-xl border border-destructive/40 bg-destructive/5 p-3 text-[13px]">
                  <p>{tx('Başvurunuz ve ilgili tüm veriler kalıcı olarak silinecek; süreç sona erer. Emin misiniz?')}</p>
                  <div className="flex gap-2">
                    <Button variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate(undefined)}>{tx('Evet, sil')}</Button>
                    <Button variant="ghost" onClick={() => setConfirm(false)}>{tx('Vazgeç')}</Button>
                  </div>
                </div>
              )}
            </div>
          </Card>
          <PrivacyNotice text={q.data.privacyNotice.text} />
          <p className="text-center"><Link className="text-[12.5px] text-primary underline" to={`/kariyer/${tenant}`}>{tx('Diğer açık pozisyonlar')}</Link></p>
        </div>
      )}
    </Shell>
  )
}
