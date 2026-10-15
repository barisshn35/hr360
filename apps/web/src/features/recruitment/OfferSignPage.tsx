/**
 * Dalga 11 (madde 76): adayın iş teklifini oturumsuz, jetonlu bağlantıdan basit elektronik
 * imzayla kabul ettiği sayfa (/kariyer/:tenant/teklif/:token). Kod adayın e-postasına gider;
 * kod, imza ve kanıt governance'taki tek imza motorundadır (ortak OtpSignPanel bileşeni).
 */
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Briefcase, CheckCircle2, Download, Printer } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import { careerApi, offerStatusLabels, type OfferSignResult } from '@/api/recruitment'
import { formatDate, formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { OtpSignPanel, SimpleSignatureDisclaimer } from '@/features/documents/OtpSignPanel'
import { printLetter } from './RecruitmentPlus'
import { downloadSignedLetter } from './OfferSignatureHr'
import { tx, txServer } from '@/lib/i18n'

function Shell({ company, children }: { company?: string; children: React.ReactNode }) {
  return (
    <div className="min-h-dvh bg-background px-4 py-8">
      <div className="mx-auto w-full max-w-3xl">
        <header className="mb-6 flex items-center gap-2">
          <Briefcase className="size-5 text-primary" />
          <span className="text-[15px] font-semibold">{company ?? tx('Kariyer')}</span>
          <span className="text-[13px] text-muted-foreground">· {tx('İş teklifi')}</span>
        </header>
        {children}
        <p className="mt-10 text-center text-[11.5px] text-muted-foreground">HR360</p>
      </div>
    </div>
  )
}

function Card({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <section className={`rounded-2xl border border-border bg-card p-5 shadow-sm ${className}`}>{children}</section>
}

/** /kariyer/:tenant/teklif/:token */
export function OfferSignPage() {
  const { tenant = '', token = '' } = useParams()
  const qc = useQueryClient()
  const key = ['career-offer-sign', tenant, token]
  const q = useQuery({ queryKey: key, queryFn: ({ signal }) => careerApi.offerSign(tenant, token, signal), retry: false })
  const [read, setRead] = useState(false)
  const [confirmDecline, setConfirmDecline] = useState(false)
  const decline = useAction(() => careerApi.offerSignDecline(tenant, token), { invalidate: [key], success: tx('Teklifi reddettiniz') })
  const download = useAction(() => careerApi.offerSignedDocument(tenant, token), { onDone: downloadSignedLetter })

  return (
    <Shell company={q.data?.company}>
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <Card><p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p></Card> : (
        <div className="space-y-4">
          <Card className="space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <h1 className="text-lg font-semibold">{q.data.positionTitle}</h1>
                <p className="text-[12.5px] text-muted-foreground">
                  {tx('Başlangıç {0} · Son yanıt tarihi {1}', [formatDate(q.data.startDate), formatDate(q.data.expiresAt)])}
                </p>
              </div>
              <StatusBadge tone={q.data.status === 'Accepted' ? 'success' : q.data.status === 'Sent' ? 'info' : 'neutral'}>
                {q.data.signed ? tx('İmzalandı') : offerStatusLabels[q.data.status]}
              </StatusBadge>
            </div>
            <pre className="max-h-[28rem] overflow-auto rounded-xl bg-muted/30 p-3 font-sans text-[12.5px] leading-relaxed whitespace-pre-wrap">{q.data.letterText}</pre>
            <div className="flex flex-wrap gap-2">
              <Button variant="ghost" onClick={() => printLetter(tx('Teklif mektubu'), q.data.letterText)}><Printer className="size-4" /> {tx('Yazdır / PDF')}</Button>
              {q.data.signed && (
                <Button variant="outline" disabled={download.isPending} onClick={() => download.mutate(undefined)}>
                  <Download className="size-4" /> {tx('İmzalı belgeyi indir')}
                </Button>
              )}
            </div>
            <p className="break-all text-[11.5px] text-muted-foreground">{tx('Belge özeti (SHA-256)')}: <span className="font-mono">{q.data.letterSha256}</span></p>
          </Card>

          {q.data.signed ? (
            <Card>
              <p className="flex items-center gap-2 text-[14px]"><CheckCircle2 className="size-5 text-emerald-600" />
                {tx('Teklifi {0} tarihinde elektronik olarak imzalayıp kabul ettiniz.', [formatDateTime(q.data.signed.signedAt)])}</p>
            </Card>
          ) : q.data.canSign ? (
            <Card className="space-y-3">
              <h2 className="text-[15px] font-semibold">{tx('Elektronik imza ile kabul et')}</h2>
              <SimpleSignatureDisclaimer />
              <label className="flex items-start gap-2.5 text-[13px]">
                <Checkbox className="mt-0.5" checked={read} onCheckedChange={(v) => setRead(v === true)} />
                <span>{tx('Teklif mektubunu okudum; bu teklifi kabul ediyor ve elektronik olarak imzalıyorum.')}</span>
              </label>
              <OtpSignPanel<OfferSignResult>
                disabled={!read}
                disabledHint={tx('Kod istemek için önce mektubu okuduğunuzu onaylayın.')}
                sentNote={tx('Kod {0} adresine gönderildi.', [q.data.emailMasked])}
                onRequest={() => careerApi.offerSignOtp(tenant, token)}
                onSign={(c, code) => careerApi.offerSignSubmit(tenant, token, c.otpId, code)}
                toEvidence={(r) => ({ signedAt: r.signedAt, method: r.method, documentSha256: r.documentSha256, evidenceSha256: r.evidenceSha256, ipPrefix: r.ipPrefix, integrityOk: r.integrityOk })}
                onSigned={() => void qc.invalidateQueries({ queryKey: key })}
              />
              {q.data.canDecline && (
                <div className="border-t border-border pt-3">
                  {!confirmDecline ? (
                    <Button variant="outline" onClick={() => setConfirmDecline(true)}>{tx('Teklifi reddet')}</Button>
                  ) : (
                    <div className="flex flex-wrap items-center gap-2 text-[13px]">
                      <span>{tx('Teklifi reddetmek istediğinize emin misiniz?')}</span>
                      <Button variant="destructive" disabled={decline.isPending} onClick={() => decline.mutate(undefined)}>{tx('Evet, reddet')}</Button>
                      <Button variant="ghost" onClick={() => setConfirmDecline(false)}>{tx('Vazgeç')}</Button>
                    </div>
                  )}
                </div>
              )}
            </Card>
          ) : (
            <Card><p className="text-[13px] text-muted-foreground">{q.data.reason ? txServer(q.data.reason) : offerStatusLabels[q.data.status]}</p></Card>
          )}
          <p className="text-center"><Link className="text-[12.5px] text-primary underline" to={`/kariyer/${tenant}`}>{tx('Diğer açık pozisyonlar')}</Link></p>
        </div>
      )}
    </Shell>
  )
}
