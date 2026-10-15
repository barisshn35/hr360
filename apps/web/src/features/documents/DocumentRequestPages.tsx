import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams, useSearchParams } from 'react-router-dom'
import { BadgeCheck, CheckCircle2, FileCheck2, FileSignature, FileText, Printer, ShieldAlert, XCircle } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { governanceApi, type DocRequestStatus } from '@/api/governance'
import { SignDocumentModal } from '@/features/documents/SignDocument'
import { methodLabel } from '@/features/documents/OtpSignPanel'
import { workflowApi } from '@/api/workflows'
import { workflowTypeLabels } from '@/api/types'
import { formatDate, formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { printDocuments } from '@/features/governance/DocTemplatesPage'
import { tx } from '@/lib/i18n'

const STATUS: Record<DocRequestStatus, { label: string; tone: 'warning' | 'success' | 'danger' }> = {
  Pending: { label: tx('İK onayında'), tone: 'warning' },
  Issued: { label: tx('Hazır'), tone: 'success' },
  Rejected: { label: tx('Reddedildi'), tone: 'danger' },
}

/** /panel/belge-talebi — çalışma belgesi, maaş yazısı gibi belgeleri kendim isterim (Y10). */
export function MyDocumentsPage() {
  const templates = useQuery({ queryKey: ['doc-requests', 'templates'], queryFn: ({ signal }) => governanceApi.requestableTemplates(signal) })
  const mine = useQuery({ queryKey: ['doc-requests', 'mine'], queryFn: ({ signal }) => governanceApi.myDocRequests(signal) })
  const [tpl, setTpl] = useState('')
  const [purpose, setPurpose] = useState('')
  const chosen = templates.data?.find((t) => t.id === tpl)
  const create = useAction(() => governanceApi.requestDocument(tpl, purpose || undefined), {
    success: (r) => (r.status === 'Issued' ? tx('Belgeniz hazır') : tx('Talebiniz İK onayına gönderildi')),
    invalidate: [['doc-requests']], onDone: () => setPurpose(''),
  })
  const open = useAction((id: string) => governanceApi.docRequestDocument(id), { onDone: (d) => printDocuments(d.templateName, [{ employeeId: '', name: '', html: d.html }]) })
  const [signing, setSigning] = useState<{ id: string; name: string } | null>(null)
  return (
    <>
      <PageHeader title={tx('Belge talebi')} description={tx('Çalışma belgesi, maaş yazısı gibi belgeleri isteyin; hazır olunca yazdırın ya da PDF olarak kaydedin.')} />
      <div className="grid gap-5 lg:grid-cols-[380px_1fr]">
        <Panel>
          <PanelHead title={tx('Yeni talep')} />
          <PanelBody className="space-y-3">
            {templates.isPending ? <RowsSkeleton rows={2} /> : !templates.data?.length ? <InfoNote>{tx('Şirketiniz henüz talep edilebilir belge tanımlamamış.')}</InfoNote> : (
              <>
                <SelectField label={tx('Belge')} value={tpl} onChange={setTpl} options={templates.data.map((t) => ({ value: t.id, label: t.name }))} />
                <TextField label={tx('Kullanım amacı (isteğe bağlı)')} value={purpose} onChange={(e) => setPurpose(e.target.value)} maxLength={200} placeholder={tx('Ör. banka kredi başvurusu')} hint={tx('Belgenin altına yazılır.')} />
                {chosen && <p className="text-[12.5px] text-muted-foreground">{chosen.requiresApproval ? tx('Bu belge İK onayından sonra düzenlenir.') : tx('Bu belge hemen düzenlenir.')}</p>}
                <Button disabled={!tpl || create.isPending} onClick={() => create.mutate(undefined)}><FileText className="size-4" /> {tx('Talep et')}</Button>
              </>
            )}
            <InfoNote>{tx('Belgede yalnızca gerekli bilgiler yer alır. Her belgenin bir doğrulama kodu vardır; belgeyi verdiğiniz kurum kodu HR360 doğrulama sayfasında kontrol edebilir (kişisel bilgi gösterilmez).')}</InfoNote>
          </PanelBody>
        </Panel>
        <Panel>
          <PanelHead title={tx('Taleplerim')} />
          <PanelBody className="p-0">
            {mine.isPending ? <div className="p-4"><RowsSkeleton rows={3} /></div> : !mine.data?.length ? (
              <EmptyState icon={FileCheck2} title={tx('Henüz belge talebiniz yok')} detail={tx('Soldan bir belge seçerek başlayın.')} />
            ) : (
              <ul className="divide-y divide-border">
                {mine.data.map((r) => (
                  <li key={r.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                    <div className="min-w-0 flex-1">
                      <p className="font-medium">{r.templateName}</p>
                      <p className="text-[12px] text-muted-foreground">{formatDate(r.createdAt)}{r.purpose ? ` · ${r.purpose}` : ''}{r.verificationCode ? ` · ${tx('kod {0}', [r.verificationCode])}` : ''}{r.decisionNote ? ` · ${r.decisionNote}` : ''}</p>
                    </div>
                    <StatusBadge tone={STATUS[r.status].tone}>{STATUS[r.status].label}</StatusBadge>
                    {r.signed && <StatusBadge tone="success">{tx('İmzalı (basit e-imza)')}</StatusBadge>}
                    {r.status === 'Issued' && !r.signed && <Button size="sm" variant="outline" onClick={() => setSigning({ id: r.id, name: r.templateName })}><FileSignature className="size-4" /> {tx('Kodla imzala')}</Button>}
                    {r.status === 'Issued' && <Button size="sm" variant="outline" onClick={() => open.mutate(r.id)}><Printer className="size-4" /> {tx('Yazdır / PDF')}</Button>}
                  </li>
                ))}
              </ul>
            )}
          </PanelBody>
        </Panel>
      </div>
      {signing && <SignDocumentModal docId={signing.id} name={signing.name} onClose={() => setSigning(null)} />}
    </>
  )
}

function PublicCard({ children }: { children: React.ReactNode }) {
  return (
    <div className="grid min-h-dvh place-items-center bg-background px-4 py-10">
      <div className="w-full max-w-md rounded-2xl border border-border bg-card p-6 shadow-sm">{children}</div>
    </div>
  )
}

/** /belge-dogrula/:kod — herkese açık belge doğrulama (kişisel veri göstermez). */
export function VerifyDocumentPage() {
  const { code = '' } = useParams()
  const [input, setInput] = useState(code)
  const [current, setCurrent] = useState(code)
  const q = useQuery({ queryKey: ['doc-verify', current], queryFn: ({ signal }) => governanceApi.verifyDocument(current, signal), enabled: current.length > 0, retry: false })
  return (
    <PublicCard>
      <h1 className="mb-1 text-lg font-semibold">{tx('Belge doğrulama')}</h1>
      <p className="mb-4 text-[13px] text-muted-foreground">{tx('Belgenin altındaki doğrulama kodunu girin.')}</p>
      <form className="mb-4 flex gap-2" onSubmit={(e) => { e.preventDefault(); setCurrent(input.trim().toUpperCase()) }}>
        <TextField label={tx('Doğrulama kodu')} value={input} onChange={(e) => setInput(e.target.value)} placeholder="ABCDE-FGHJK" />
        <Button type="submit" className="self-end">{tx('Doğrula')}</Button>
      </form>
      {q.isFetching ? <RowsSkeleton rows={2} /> : q.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p> : q.data && (
        q.data.valid ? (
          <div className="flex gap-3 rounded-xl border border-emerald-500/40 bg-emerald-500/10 p-4 text-[13px]">
            <BadgeCheck className="size-6 shrink-0 text-emerald-600" />
            <div>
              <p className="font-semibold">{tx('Geçerli belge')}</p>
              <p>{q.data.document} · {formatDateTime(q.data.issuedAt)}</p>
              <p className="text-muted-foreground">{tx('Belge sahibi: {0}', [q.data.holder ?? '—'])}{q.data.company ? ` · ${q.data.company}` : ''}</p>
              {q.data.signature?.signed && (
                <div className="mt-2 space-y-0.5 border-t border-emerald-500/30 pt-2">
                  <p className="flex items-center gap-1.5 font-medium"><FileSignature className="size-4" /> {tx('Çalışan tarafından kodla imzalandı')} · {formatDateTime(q.data.signature.signedAt)}</p>
                  <p className="text-[12px] text-muted-foreground">{tx('Yöntem')}: {methodLabel(q.data.signature.method)}{q.data.signature.matchesDocument ? '' : ` · ${tx('UYARI: belge özeti eşleşmiyor')}`}</p>
                  <p className="break-all font-mono text-[11px] text-muted-foreground">SHA-256 {q.data.signature.documentSha256.slice(0, 32)}…</p>
                  <p className="text-[11.5px] font-medium text-amber-700 dark:text-amber-400">{tx('Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir.')}</p>
                </div>
              )}
            </div>
          </div>
        ) : (
          <div className="flex gap-3 rounded-xl border border-destructive/40 bg-destructive/10 p-4 text-[13px]">
            <XCircle className="size-6 shrink-0 text-destructive" />
            <p>{tx('Bu kodla düzenlenmiş geçerli bir belge bulunamadı.')}</p>
          </div>
        )
      )}
      <p className="mt-4 text-[11.5px] text-muted-foreground">{tx('KVKK: Bu sayfa belge sahibinin kişisel bilgilerini göstermez; yalnızca belgenin bu şirketçe düzenlendiğini doğrular.')}</p>
    </PublicCard>
  )
}

/** /onay-eposta?t=... — e-postadaki tek kullanımlık bağlantıyla karar (G10). */
export function EmailDecisionPage() {
  const [params] = useSearchParams()
  const token = params.get('t') ?? ''
  const q = useQuery({ queryKey: ['email-action', token], queryFn: ({ signal }) => workflowApi.emailActionPreview(token, signal), enabled: !!token, retry: false })
  const [comment, setComment] = useState('')
  const [decision, setDecision] = useState<'Approved' | 'Rejected'>('Approved')
  const act = useAction(() => workflowApi.emailAction(token, decision, comment || undefined), {})
  if (!token) return <PublicCard><p className="text-[13px]">{tx('Bağlantı eksik.')}</p></PublicCard>
  return (
    <PublicCard>
      <h1 className="mb-3 text-lg font-semibold">{tx('Onay kararı')}</h1>
      {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? (
        <div className="flex gap-2 text-[13px]"><ShieldAlert className="size-5 shrink-0 text-destructive" /><span>{errMsg(q.error)}</span></div>
      ) : act.isSuccess ? (
        <div className="flex gap-2 text-[13px]"><CheckCircle2 className="size-5 shrink-0 text-emerald-600" /><span>{decision === 'Approved' ? tx('Onayladınız. Bu bağlantı artık kullanılamaz.') : tx('Reddettiniz. Bu bağlantı artık kullanılamaz.')}</span></div>
      ) : (
        <div className="space-y-4">
          <p className="text-[13px]">{tx('{0} — açılış {1}', [workflowTypeLabels[q.data!.type], formatDate(q.data!.createdAt)])}{q.data!.stepCount > 1 ? ` · ${tx('adım {0}/{1}', [q.data!.stepOrder, q.data!.stepCount])}` : ''}</p>
          <p className="text-[12.5px] text-muted-foreground">{tx('Ayrıntıları görmek için HR360\'a giriş yapın. Karar bu bağlantıyla bir kez verilebilir.')}</p>
          <SelectField label={tx('Karar')} value={decision} onChange={(v) => setDecision(v as typeof decision)} options={[{ value: 'Approved', label: tx('Onayla') }, { value: 'Rejected', label: tx('Reddet') }]} />
          <TextField label={tx('Not (isteğe bağlı)')} value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} />
          <div className="flex gap-2">
            <Button disabled={act.isPending} onClick={() => act.mutate(undefined)}>{tx('Kararı gönder')}</Button>
            <Button variant="outline" asChild><a href={`/panel/onaylar/${q.data!.workflowId}`}>{tx('HR360\'ta aç')}</a></Button>
          </div>
        </div>
      )}
    </PublicCard>
  )
}
