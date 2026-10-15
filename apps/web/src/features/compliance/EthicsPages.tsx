import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Copy, Eye, Inbox, KeyRound, ShieldCheck, Trash2, UserPlus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { complianceApi, type EthicsMessage, type EthicsPublicStatus, type EthicsStatus } from '@/api/compliance'
import { formatDate } from '@/lib/format'
import { errMsg, PersonSelect, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'
import { cn } from '@/lib/utils'
import { useFormToken } from '@/lib/formGuard'

const statusView: Record<EthicsStatus, { label: string; tone: 'warning' | 'info' | 'neutral' }> = {
  Received: { label: tx('Alındı'), tone: 'warning' }, InReview: { label: tx('İnceleniyor'), tone: 'info' }, Closed: { label: tx('Kapatıldı'), tone: 'neutral' },
}

function Thread({ messages }: { messages: EthicsMessage[] }) {
  if (!messages.length) return <p className="text-[12.5px] text-muted-foreground">{tx('Henüz yazışma yok.')}</p>
  return (
    <ul className="space-y-2">
      {messages.map((m, i) => (
        <li key={i} className={cn('max-w-[85%] rounded-xl border px-3 py-2 text-[13px]', m.fromReporter ? 'border-border bg-muted/40' : 'ml-auto border-primary/30 bg-primary/5')}>
          <p className="mb-0.5 text-[11.5px] text-muted-foreground">{m.fromReporter ? tx('Bildirimde bulunan') : m.author ?? tx('Etik Kurulu')} · {formatDate(m.createdOn)}</p>
          <p className="whitespace-pre-wrap">{m.body}</p>
        </li>
      ))}
    </ul>
  )
}

function ReportModal({ id, onClose }: { id: string; onClose: () => void }) {
  const toast = useToast()
  const q = useQuery({ queryKey: ['ethics', 'report', id], queryFn: ({ signal }) => complianceApi.ethicsReport(id, signal) })
  const [reply, setReply] = useState('')
  const [outcome, setOutcome] = useState('')
  const inv = [['ethics']]
  const send = useAction(() => complianceApi.ethicsReply(id, reply), { success: tx('Yanıt gönderildi'), invalidate: inv, onDone: () => setReply('') })
  const setStatus = useAction((s: EthicsStatus) => complianceApi.ethicsSetStatus(id, s, outcome || undefined), { success: tx('Durum güncellendi'), invalidate: inv })
  const reveal = async () => {
    try { const r = await complianceApi.ethicsContact(id); toast.ok(tx('İletişim: {0}', [r.contact])) } catch (e) { toast.stop(errMsg(e)) }
  }
  const r = q.data
  return (
    <Modal open size="lg" onClose={onClose} title={r ? r.categoryLabel : tx('Bildirim')} note={r ? tx('Alındı: {0} · kimlik bilgisi tutulmaz', [formatDate(r.receivedOn)]) : undefined}>
      {q.isPending || !r ? <RowsSkeleton /> : (
        <div className="space-y-4 text-[13px]">
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge tone={statusView[r.status].tone}>{statusView[r.status].label}</StatusBadge>
            {r.hasContact && <Button size="sm" variant="outline" onClick={() => void reveal()}><Eye className="size-4" />{' '}{tx('İletişim bilgisini göster (kayda geçer)')}</Button>}
          </div>
          <p className="whitespace-pre-wrap rounded-xl border border-border p-3">{r.description}</p>
          <Thread messages={r.messages} />
          {r.status !== 'Closed' && (
            <div className="space-y-2">
              <TextAreaField label={tx('Bildirimde bulunana yanıt ("Etik Kurulu" imzasıyla)')} rows={3} value={reply} onChange={(e) => setReply(e.target.value)} />
              <PiiHint text={reply} />
              <Button size="sm" onClick={() => send.mutate(undefined)} disabled={!reply.trim() || send.isPending}>{tx('Gönder')}</Button>
            </div>
          )}
          <div className="space-y-2 border-t border-border pt-3">
            <TextField label={tx('Sonuç notu (bildirimde bulunan görür)')} value={outcome} onChange={(e) => setOutcome(e.target.value)} placeholder={r.outcome ?? ''} />
            <div className="flex flex-wrap gap-2">
              {r.status !== 'InReview' && <Button size="sm" variant="outline" onClick={() => setStatus.mutate('InReview')}>{tx('İncelemeye al')}</Button>}
              {r.status !== 'Closed' && <Button size="sm" variant="outline" onClick={() => setStatus.mutate('Closed')}>{tx('Kapat')}</Button>}
              {r.status === 'Closed' && <Button size="sm" variant="outline" onClick={() => setStatus.mutate('InReview')}>{tx('Yeniden aç')}</Button>}
            </div>
          </div>
        </div>
      )}
    </Modal>
  )
}

function CommitteePanel() {
  const q = useQuery({ queryKey: ['ethics', 'committee'], queryFn: ({ signal }) => complianceApi.ethicsCommittee(signal) })
  const [pick, setPick] = useState('')
  const add = useAction(() => complianceApi.addCommitteeMember(pick), { success: tx('Kurula eklendi'), invalidate: [['ethics']], onDone: () => setPick('') })
  const remove = useAction((id: string) => complianceApi.removeCommitteeMember(id), { success: tx('Kuruldan çıkarıldı'), invalidate: [['ethics']] })
  return (
    <Panel>
      <PanelHead title={tx('Etik kurulu')} note={tx('Bildirimleri yalnızca bu kişiler görür. İK otomatik olarak üye değildir.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : !q.data?.length ? <InfoNote>{tx('Kurul boş: üye eklenene kadar etik hattı bildirim kabul etmez.')}</InfoNote> : (
          <ul className="divide-y divide-border text-[13px]">
            {q.data.map((m) => (
              <li key={m.id} className="flex items-center gap-3 py-2">
                <span className="flex-1">{m.name}<span className="block text-[12px] text-muted-foreground">{tx('{0} ekledi · {1}', [m.addedBy, formatDate(m.addedAt)])}</span></span>
                <Button size="sm" variant="ghost" aria-label={tx('Kuruldan çıkar')} onClick={() => remove.mutate(m.id)}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        )}
        <div className="flex flex-wrap items-end gap-2">
          <div className="min-w-60 flex-1"><PersonSelect value={pick} onChange={setPick} label={tx('Üye ekle')} exclude={(q.data ?? []).map((m) => m.employeeId ?? '')} /></div>
          <Button onClick={() => add.mutate(undefined)} disabled={!pick || add.isPending}><UserPlus className="size-4" />{' '}{tx('Ekle')}</Button>
        </div>
      </PanelBody>
    </Panel>
  )
}

/** /panel/etik — etik kurulu gelen kutusu; şirket yöneticisi kurulu düzenler. */
export function EthicsInboxPage() {
  const toast = useToast()
  const me = useQuery({ queryKey: ['ethics', 'me'], queryFn: ({ signal }) => complianceApi.ethicsMe(signal) })
  const [status, setStatus] = useState('open')
  const reports = useQuery({
    queryKey: ['ethics', 'reports', status],
    queryFn: ({ signal }) => complianceApi.ethicsReports(status === 'open' ? undefined : status, signal),
    enabled: !!me.data?.isMember,
  })
  const [open, setOpen] = useState<string | null>(null)
  const link = me.data ? `${window.location.origin}/etik/${me.data.tenant}` : ''
  const rows = (reports.data ?? []).filter((r) => status !== 'open' || r.status !== 'Closed')
  return (
    <>
      <PageHeader title={tx('Etik hattı')} description={tx('Anonim etik ve uyum bildirimleri.')} />
      <div className="space-y-5">
        <Panel>
          <PanelHead title={tx('Bildirim yapmak için')} note={tx('Sayfa oturum istemez; IP, kullanıcı ve cihaz bilgisi kaydedilmez. En iyi anonimlik için kişisel cihazınızı kullanın.')} />
          <PanelBody className="flex flex-wrap items-center gap-3 text-[13px]">
            <code className="rounded-md bg-muted px-2 py-1">{link}</code>
            <Button size="sm" variant="outline" onClick={() => { void navigator.clipboard?.writeText(link); toast.ok(tx('Bağlantı kopyalandı')) }}><Copy className="size-4" />{' '}{tx('Kopyala')}</Button>
          </PanelBody>
        </Panel>
        {me.data?.canManage && <CommitteePanel />}
        {me.isPending ? <RowsSkeleton /> : me.data?.isMember ? (
          <Panel>
            <PanelHead title={tx('Gelen bildirimler')} action={<div className="w-44"><SelectField label={tx('Durum')} value={status} onChange={setStatus}
              options={[{ value: 'open', label: tx('Açık olanlar') }, { value: 'Received', label: tx('Alındı') }, { value: 'InReview', label: tx('İnceleniyor') }, { value: 'Closed', label: tx('Kapatıldı') }]} /></div>} />
            <PanelBody className="p-0">
              {reports.isPending ? <div className="p-5"><RowsSkeleton /></div> : !rows.length ? <EmptyState icon={Inbox} title={tx('Bildirim yok')} /> : (
                <ul className="divide-y divide-border">
                  {rows.map((r) => (
                    <li key={r.id}>
                      <button type="button" className="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left text-[13px] hover:bg-muted/40" onClick={() => setOpen(r.id)}>
                        <span className="min-w-0 flex-1"><span className="font-medium">{r.categoryLabel}</span>
                          <span className="block text-[12px] text-muted-foreground">{formatDate(r.receivedOn)} · {tx('{0} mesaj', [r.messages])}</span></span>
                        {r.awaitingReply && <StatusBadge tone="warning">{tx('Yanıt bekliyor')}</StatusBadge>}
                        <StatusBadge tone={statusView[r.status].tone}>{statusView[r.status].label}</StatusBadge>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </PanelBody>
          </Panel>
        ) : (
          <InfoNote>{tx('Etik bildirimlerini yalnızca şirket yöneticisinin atadığı etik kurulu üyeleri görebilir.')}</InfoNote>
        )}
      </div>
      {open && <ReportModal id={open} onClose={() => setOpen(null)} />}
    </>
  )
}

/* ====================================================================== herkese açık sayfa */

function PublicShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="grid min-h-dvh place-items-start bg-background px-4 py-10 sm:place-items-center">
      <div className="mx-auto w-full max-w-xl rounded-2xl border border-border bg-card p-6 shadow-sm">{children}</div>
    </div>
  )
}

function FollowUp({ tenant }: { tenant: string }) {
  const [code, setCode] = useState('')
  const [data, setData] = useState<EthicsPublicStatus | null>(null)
  const [msg, setMsg] = useState('')
  const [error, setError] = useState<string | null>(null)
  const load = useAction(() => complianceApi.ethicsStatus(tenant, code), { onDone: (r) => { setData(r); setError(null) } })
  const send = useAction(() => complianceApi.ethicsFollowUp(tenant, code, msg), {
    success: tx('Mesajınız iletildi'),
    onDone: () => { setMsg(''); void complianceApi.ethicsStatus(tenant, code).then(setData).catch((e) => setError(errMsg(e))) },
  })
  return (
    <div className="space-y-3">
      <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); load.mutate(undefined) }}>
        <TextField label={tx('Takip kodu')} value={code} onChange={(e) => setCode(e.target.value)} placeholder="XXXX-XXXX-XXXX-XXXX" autoComplete="off" />
        <Button type="submit" className="self-end" disabled={code.trim().length < 16 || load.isPending}>{tx('Sorgula')}</Button>
      </form>
      {error && <p role="alert" className="text-[13px] text-destructive">{error}</p>}
      {data && (
        <div className="space-y-3 text-[13px]">
          <p>{data.categoryLabel} · {formatDate(data.receivedOn)} · <StatusBadge tone={statusView[data.status].tone}>{statusView[data.status].label}</StatusBadge></p>
          {data.outcome && <InfoNote>{data.outcome}</InfoNote>}
          <Thread messages={data.messages} />
          {data.status !== 'Closed' && (
            <>
              <TextAreaField label={tx('Ek bilgi / yanıt')} rows={3} value={msg} onChange={(e) => setMsg(e.target.value)} />
              <PiiHint text={msg} />
              <Button size="sm" onClick={() => send.mutate(undefined)} disabled={!msg.trim() || send.isPending}>{tx('Gönder')}</Button>
            </>
          )}
        </div>
      )}
    </div>
  )
}

/** /etik/:tenant — oturumsuz anonim etik bildirimi ve takip (Y15). */
export function PublicEthicsPage() {
  const { tenant = '' } = useParams()
  const info = useQuery({ queryKey: ['ethics-public', tenant], queryFn: ({ signal }) => complianceApi.ethicsPublicInfo(tenant, signal), retry: false })
  const [mode, setMode] = useState<'new' | 'follow'>('new')
  const [f, setF] = useState({ category: '', description: '', contact: '', website: '' })
  const [issued, setIssued] = useState<string | null>(null)
  const guard = useFormToken(() => complianceApi.ethicsFormToken(tenant), tenant)
  const submit = useAction(async () => complianceApi.ethicsSubmit(tenant, {
    category: f.category, description: f.description, contact: f.contact || null, website: f.website || undefined, formToken: await guard.take(),
  }), {
    onDone: (r) => { setIssued(r.followUpCode); setF({ category: '', description: '', contact: '', website: '' }) },
  })
  return (
    <PublicShell>
      <div className="mb-4 flex items-center gap-2">
        <ShieldCheck className="size-6 text-primary" />
        <div>
          <h1 className="text-lg font-semibold">{tx('Etik hattı')}</h1>
          {info.data && <p className="text-[12.5px] text-muted-foreground">{info.data.company}</p>}
        </div>
      </div>
      {info.isPending ? <RowsSkeleton rows={3} /> : info.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(info.error)}</p> : !info.data?.enabled ? (
        <InfoNote>{tx('Bu şirketin etik hattı henüz yapılandırılmamış.')}</InfoNote>
      ) : issued ? (
        <div className="space-y-3 text-[13px]">
          <p className="font-medium">{tx('Bildiriminiz alındı.')}</p>
          <div className="flex items-center gap-2 rounded-xl border border-primary/40 bg-primary/5 p-3">
            <KeyRound className="size-5 text-primary" />
            <code className="text-[15px] font-semibold tracking-wider">{issued}</code>
            <Button size="sm" variant="ghost" aria-label={tx('Kopyala')} onClick={() => void navigator.clipboard?.writeText(issued)}><Copy className="size-4" /></Button>
          </div>
          <p className="text-destructive">{tx('Bu takip kodunu şimdi güvenli bir yere kaydedin. Yalnızca bir kez gösterilir; kaybolursa yeniden üretilemez ve bildiriminizi takip edemezsiniz.')}</p>
          <Button variant="outline" onClick={() => { setIssued(null); setMode('follow') }}>{tx('Kodu kaydettim')}</Button>
        </div>
      ) : (
        <>
          <div className="mb-4 flex gap-2">
            <Button size="sm" variant={mode === 'new' ? 'default' : 'outline'} onClick={() => setMode('new')}>{tx('Yeni bildirim')}</Button>
            <Button size="sm" variant={mode === 'follow' ? 'default' : 'outline'} onClick={() => setMode('follow')}>{tx('Bildirimimi takip et')}</Button>
          </div>
          {mode === 'follow' ? <FollowUp tenant={tenant} /> : (
            <div className="space-y-3">
              <SelectField label={tx('Konu')} value={f.category} onChange={(v) => setF({ ...f, category: v })} options={info.data.categories} />
              <TextAreaField label={tx('Ne oldu?')} rows={7} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })}
                hint={tx('Ne, nerede, ne zaman? Kendinizi tanıtacak ayrıntılardan kaçının. En az 20 karakter.')} />
              <PiiHint text={f.description} />
              <TextField label={tx('İletişim (isteğe bağlı)')} value={f.contact} onChange={(e) => setF({ ...f, contact: e.target.value })}
                hint={tx('Yalnızca sizinle iletişime geçilmesini istiyorsanız. Şifreli saklanır ve yalnızca etik kurulu görebilir.')} />
              {/* Bot tuzağı: görünmez alan (insanlar boş bırakır). */}
              <input type="text" name="website" tabIndex={-1} autoComplete="off" aria-hidden="true" className="hidden" value={f.website} onChange={(e) => setF({ ...f, website: e.target.value })} />
              <Button onClick={() => submit.mutate(undefined)} disabled={!f.category || f.description.trim().length < 20 || submit.isPending}>{tx('Gönder')}</Button>
            </div>
          )}
          <p className="mt-5 text-[11.5px] text-muted-foreground">{tx('Anonimlik: Bu sayfa oturum açmanızı istemez; IP adresiniz, kullanıcı hesabınız ve tarayıcı bilginiz kaydedilmez. Bildirimler yalnızca şirketin etik kurulu tarafından görülür. Takip kodunuzun yalnızca özeti saklanır.')}</p>
        </>
      )}
    </PublicShell>
  )
}
