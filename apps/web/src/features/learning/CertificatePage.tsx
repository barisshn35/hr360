/** Y20 yazdırılabilir sertifika (çalışan, departman yöneticisi, İK). */
import { Link, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Award, Printer } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { learningContentApi, type CertificateView } from '@/api/learningContent'
import { errMsg } from '@/features/shared/kit'
import { ApiError } from '@/api/client'
import { formatDate } from '@/lib/format'
import { appLocale, tx } from '@/lib/i18n'
import { requestTrace, WATERMARK_CSS, watermarkHtml } from '@/lib/watermark'

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

/** Sertifikayı yazdırma penceresinde açar ("PDF olarak kaydet" ile PDF alınır). */
function printCertificate(c: CertificateView) {
  const w = window.open('', '_blank')
  if (!w) throw new Error(tx('Açılır pencere engellendi; tarayıcıda izin verin.'))
  w.document.write(`<!doctype html><meta charset="utf-8"><title>…</title><p style="font-family:Arial,sans-serif">${esc(tx('Hazırlanıyor…'))}</p>`)
  // Güvenlik dalgası 2B: indirene ait filigran + iz kodu (denetim kaydına bağlı).
  void requestTrace('certificate', { subject: c.verificationCode ?? c.credentialId ?? undefined, format: 'print' }).then((trace) => writeCertificate(w, c, trace?.text))
}

function writeCertificate(w: Window, c: CertificateView, watermark?: string) {
  w.document.open()
  const title = c.courseTitle ?? c.name
  w.document.write(`<!doctype html><html lang="${appLocale}"><head><meta charset="utf-8"><title>${esc(tx('Sertifika'))} — ${esc(c.employeeName)}</title>
<style>@page{size:A4 landscape;margin:14mm}body{font-family:Georgia,'Times New Roman',serif;color:#1a1a1a;margin:0}
.f{border:3px double #8a6d1f;padding:16mm 18mm;min-height:150mm;display:flex;flex-direction:column;align-items:center;text-align:center}
.k{letter-spacing:.3em;text-transform:uppercase;font-size:11pt;color:#8a6d1f}h1{font-size:30pt;margin:6mm 0 2mm}
.n{font-size:24pt;margin:8mm 0 2mm;border-bottom:1px solid #999;padding:0 12mm 2mm}.t{font-size:15pt;margin:4mm 0}
.m{margin-top:auto;display:flex;justify-content:space-between;width:100%;font-size:10pt;color:#444;font-family:Arial,sans-serif}
.c{font-family:'Courier New',monospace;font-size:12pt;letter-spacing:.08em}${WATERMARK_CSS}</style></head><body>${watermarkHtml(watermark)}<div class="f">
<div class="k">${esc(c.company ?? c.issuer ?? 'HR360')}</div><h1>${esc(tx('Başarı Sertifikası'))}</h1>
<div>${esc(tx('Bu belge,'))}</div><div class="n">${esc(c.employeeName)}</div>
<div class="t">${esc(tx('«{0}» eğitimini başarıyla tamamladığını gösterir.', [title]))}</div>
${c.durationHours ? `<div>${esc(tx('Süre: {0} saat', [c.durationHours]))}</div>` : ''}
<div class="m"><div>${esc(tx('Veriliş'))}: ${esc(formatDate(c.issuedOn))}${c.expiresOn ? `<br>${esc(tx('Geçerlilik bitişi'))}: ${esc(formatDate(c.expiresOn))}` : ''}</div>
<div>${esc(tx('Doğrulama kodu'))}<br><span class="c">${esc(c.verificationCode ?? c.credentialId ?? '—')}</span></div></div></div></body></html>`)
  w.document.close()
  // Satır içi betik CSP'ye takılır; yazdırma üst pencereden tetiklenir.
  setTimeout(() => w.print(), 300)
}

export function CertificatePage() {
  const { id = '' } = useParams()
  const toast = useToast()
  const q = useQuery({ queryKey: ['learning', 'certificate', id], queryFn: ({ signal }) => learningContentApi.certificate(id, signal) })
  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Sertifika')}
        description={tx('Yazdırın ya da tarayıcının "PDF olarak kaydet" seçeneğiyle saklayın.')}
        actions={
          <>
            <Button asChild variant="outline"><Link to="/panel/egitim?gorunum=sertifikalar"><ArrowLeft className="size-4" />{' '}{tx('Sertifikalar')}</Link></Button>
            <Button disabled={!q.data} onClick={() => { try { printCertificate(q.data!) } catch (e) { toast.stop(errMsg(e)) } }}><Printer className="size-4" />{' '}{tx('Yazdır')}</Button>
          </>
        }
      />
      {q.isPending ? <RowsSkeleton rows={4} columns={1} /> : q.error instanceof ApiError && (q.error.status === 404 || q.error.status === 400) ? (
        <EmptyState title={tx('Sertifika bulunamadı')} detail={tx('Bağlantı hatalı olabilir ya da sertifika kaldırılmış.')} />
      ) : q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : (
        <div className="mx-auto max-w-3xl rounded-2xl border-4 border-double border-[hsl(var(--warning))]/60 bg-card px-8 py-10 text-center shadow-sm">
          <Award className="mx-auto size-10 text-[hsl(var(--warning))]" aria-hidden />
          <p className="mt-2 text-[12px] uppercase tracking-[0.3em] text-muted-foreground">{q.data.company ?? q.data.issuer}</p>
          <h2 className="mt-3 font-serif text-[30px] font-semibold">{tx('Başarı Sertifikası')}</h2>
          <p className="mt-6 text-[14px] text-muted-foreground">{tx('Bu belge,')}</p>
          <p className="mx-auto mt-2 inline-block border-b border-border px-8 pb-1 font-serif text-[26px]">{q.data.employeeName}</p>
          <p className="mt-4 text-[16px]">{tx('«{0}» eğitimini başarıyla tamamladığını gösterir.', [q.data.courseTitle ?? q.data.name])}</p>
          <div className="mt-10 flex flex-wrap justify-between gap-4 text-left text-[12.5px] text-muted-foreground">
            <span>{tx('Veriliş')}: {formatDate(q.data.issuedOn)}{q.data.expiresOn && <><br />{tx('Geçerlilik bitişi')}: {formatDate(q.data.expiresOn)}</>}</span>
            <span className="text-right">{tx('Doğrulama kodu')}<br /><span className="font-mono text-[14px] tracking-wider text-foreground">{q.data.verificationCode ?? q.data.credentialId ?? '—'}</span></span>
          </div>
        </div>
      )}
    </div>
  )
}
