import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Download, EraserIcon, Play, ShieldCheck } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { Tabs, useTabParam } from '@/components/ui/tabs'
import { useToast } from '@/components/ui/toast'
import { useConfirm } from '@/components/ui/confirm'
import { useDirectory } from '@/api/directory'
import { dataRequestLabels, governanceApi, type DataRequest, type RetentionPolicy } from '@/api/governance'
import { formatDate, formatDateTime } from '@/lib/format'
import { PersonSelect, TenantGate, errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { AccessLogPanel, ComplianceOverview, DestructionLogsPanel, ObjectionsPanel, TransfersPanel } from './PrivacyFoundation'
import { ConsentCampaignsPanel, DestructionVerificationPanel, RequestDeadline, RequestPackageButtons, RetentionPreviewButton, VerbisPanel } from './KvkkWave10'
import { AssessmentsPanel, BreachesPanel, ExternalRequestModal, FieldPoliciesPanel, NoticesPanel, ResponseTemplatePicker, VerifyIdentityButton } from './KvkkOps'

function Consents() {
  const q = useQuery({ queryKey: ['privacy', 'consents', 'all'], queryFn: ({ signal }) => governanceApi.consentSummary(signal) })
  if (q.isPending) return <RowsSkeleton />
  const d = q.data!
  return (
    <div className="space-y-5">
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {d.types.map((t, i) => {
          const total = Math.max(1, d.population)
          return (
            <motion.div key={t.type} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="surface rounded-2xl border border-border p-4">
              <p className="text-[13.5px] font-medium">{t.title}</p>
              <p className="text-[11.5px] text-muted-foreground">{tx('sürüm {0}{1}', [t.version, t.required ? tx(' · bilgilendirme (zorunlu)') : tx(' · açık rıza')])}</p>
              <div className="mt-3 flex h-2.5 overflow-hidden rounded-full bg-muted">
                <motion.div initial={{ width: 0 }} animate={{ width: `${(100 * t.granted) / total}%` }} className="bg-[hsl(var(--success))]" />
                <motion.div initial={{ width: 0 }} animate={{ width: `${(100 * t.denied) / total}%` }} className="bg-destructive" />
              </div>
              <p className="mt-2 text-[12px] text-muted-foreground">{tx('{0} onay · {1} ret · {2} bekliyor', [t.granted, t.denied, t.pending])}{t.outdated ? ` · ${tx('{0} eski sürümde', [t.outdated])}` : ''}</p>
            </motion.div>
          )
        })}
      </div>
      <ConsentCampaignsPanel />
      <Panel>
        <PanelHead title={tx('Aydınlatma metnini okumamış çalışanlar')} note={tx('Bu kişilere hatırlatma gönderin; giriş yaptıklarında Profilim → Gizlilik sekmesinden onaylayabilirler.')} />
        <PanelBody className="p-0">
          {d.missingRequired.length === 0 ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Herkes okudu. 🎉')}</p> : (
            <ul className="divide-y divide-border">{d.missingRequired.map((m) => <li key={m.employeeId} className="flex justify-between px-5 py-2.5 text-[13px]"><span>{m.name}</span><span className="text-muted-foreground">{m.department ?? '—'}</span></li>)}</ul>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}

function Requests() {
  const q = useQuery({ queryKey: ['privacy', 'requests'], queryFn: ({ signal }) => governanceApi.dataRequests(signal) })
  const [sel, setSel] = useState<DataRequest | null>(null)
  const [status, setStatus] = useState<DataRequest['status']>('InProgress')
  const [response, setResponse] = useState('')
  const [ext, setExt] = useState(false)
  const toast = useToast()
  const upd = useAction(() => governanceApi.updateDataRequest(sel!.id, status, response), { success: tx('Başvuru güncellendi'), invalidate: [['privacy']], onDone: () => setSel(null) })
  const channelLabel: Record<string, string> = { Panel: tx('Panel'), Email: tx('E-posta'), Kep: tx('KEP'), Mail: tx('Posta'), InPerson: tx('Elden') }
  if (q.isPending) return <RowsSkeleton />
  const head = <div className="mb-3 flex justify-end"><Button variant="outline" onClick={() => setExt(true)}>{tx('Panel dışı başvuru kaydet')}</Button></div>
  if (!q.data?.length) return <>{head}<EmptyState icon={ShieldCheck} title={tx('Başvuru yok')} detail={tx('Çalışanlar KVKK m.11 başvurularını Profilim → Gizlilik ekranından yapar.')} />{ext && <ExternalRequestModal onClose={() => setExt(false)} />}</>
  return (
    <>
      {head}
      <Panel>
        <PanelBody className="p-0">
          <ul className="divide-y divide-border">
            {q.data.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-3 px-5 py-3">
                <div className="min-w-0 flex-1"><p className="text-[13.5px] font-medium">{r.personName} — {dataRequestLabels[r.kind]}</p><p className="truncate text-[12px] text-muted-foreground">{r.details ?? '—'} · {formatDate(r.createdAt)} · {channelLabel[r.channel ?? 'Panel']}{r.contact ? ` · ${r.contact}` : ''}</p></div>
                <RequestDeadline r={r} />
                {r.identityVerified === false && <StatusBadge tone="warning">{tx('Kimlik doğrulanmadı')}</StatusBadge>}
                {r.identityVerified === false && (r.status === 'Received' || r.status === 'InProgress') && <VerifyIdentityButton id={r.id} />}
                <StatusBadge tone={r.status === 'Completed' ? 'success' : r.status === 'Rejected' || r.status === 'Withdrawn' ? 'neutral' : r.overdue ? 'danger' : 'warning'}>
                  {r.status === 'Completed' ? tx('Yanıtlandı') : r.status === 'Rejected' ? tx('Reddedildi') : r.status === 'Withdrawn' ? tx('Geri çekildi') : r.overdue ? tx('Süre aşıldı') : tx('{0} gün kaldı', [r.daysLeft])}
                </StatusBadge>
                {r.employeeId && <Button size="sm" variant="outline" onClick={() => governanceApi.exportPersonalData(r.employeeId!).catch((e) => toast.stop(errMsg(e)))}><Download className="size-4" />{' '}{tx('Veri dökümü')}</Button>}
                <RequestPackageButtons r={r} />
                {(r.status === 'Received' || r.status === 'InProgress') && <Button size="sm" onClick={() => { setSel(r); setStatus('Completed'); setResponse('') }}>{tx('Yanıtla')}</Button>}
              </li>
            ))}
          </ul>
        </PanelBody>
      </Panel>
      {sel && (
        <Modal open onClose={() => setSel(null)} title={`${sel.personName} — ${dataRequestLabels[sel.kind]}`} note={tx('KVKK m.13: başvuru en geç 30 gün içinde ücretsiz sonuçlandırılır; red gerekçeli olmalıdır.')}
          footer={<><Button variant="outline" onClick={() => setSel(null)}>{tx('Vazgeç')}</Button><Button onClick={() => upd.mutate(undefined)} disabled={upd.isPending}>{tx('Kaydet')}</Button></>}>
          <div className="space-y-4">
            <p className="rounded-xl bg-muted/50 p-3 text-[13px]">{sel.details ?? tx('Açıklama yok.')}</p>
            {sel.identityVerified === false && <InfoNote>{tx('Başvurucunun kimliği doğrulanmadı: başvuru “Sonuçlandı” yapılamaz. Kimlik doğrulama isteği şablonunu gönderebilir ya da reddedebilirsiniz.')}</InfoNote>}
            <ResponseTemplatePicker kind={sel.kind} name={sel.personName} date={formatDate(sel.createdAt)} onPick={(text, outcome) => { setResponse(text); if (outcome === 'Completed' || outcome === 'Rejected' || outcome === 'InProgress') setStatus(outcome) }} />
            <SelectField label={tx('Durum')} value={status} onChange={(v) => setStatus(v as DataRequest['status'])} options={[{ value: 'InProgress', label: tx('İnceleniyor') }, { value: 'Completed', label: tx('Sonuçlandı') }, { value: 'Rejected', label: tx('Reddedildi') }]} />
            <TextAreaField label={tx('Başvurucuya yanıt')} rows={4} value={response} onChange={(e) => setResponse(e.target.value)} />
          </div>
        </Modal>
      )}
      {ext && <ExternalRequestModal onClose={() => setExt(false)} />}
    </>
  )
}

function RetentionRow({ p }: { p: RetentionPolicy }) {
  const [months, setMonths] = useState(String(p.retentionMonths))
  const [action, setAction] = useState(p.action)
  const [enabled, setEnabled] = useState(p.isEnabled)
  const save = useAction(() => governanceApi.updateRetention(p.id, { retentionMonths: Number(months), action, isEnabled: enabled }), { success: tx('Politika kaydedildi'), invalidate: [['privacy', 'retention']] })
  const run = useAction(() => governanceApi.runRetention(p.id), { success: (r) => tx('{0} kayıt işlendi', [r.affected]), invalidate: [['privacy', 'retention']] })
  const confirm = useConfirm()
  // Kayıtlı politika çalışır (kaydedilmemiş form değerleri değil); işlem kalıcıdır.
  const runNow = async () => {
    const ok = await confirm({
      title: tx('“{0}” politikası şimdi çalıştırılsın mı?', [p.label]),
      note: tx('Saklama süresi ({0} ay) dolan kayıtlar {1}. Bu işlem geri alınamaz; kaydedilmemiş değişiklikler dikkate alınmaz.', [p.retentionMonths, p.action === 'Anonymize' ? tx('anonimleştirilir') : tx('kalıcı olarak silinir')]),
      action: tx('Şimdi çalıştır'),
    })
    if (ok) run.mutate(undefined)
  }
  return (
    <div className="grid items-end gap-3 rounded-2xl border border-border p-4 md:grid-cols-[1.6fr_110px_150px_auto_auto]">
      <div><p className="text-[13.5px] font-medium">{p.label}</p><p className="text-[11.5px] text-muted-foreground">{tx('Son çalışma: {0}', [p.lastRunAt ? tx('{0} · {1} kayıt', [formatDateTime(p.lastRunAt), p.lastAffected]) : tx('hiç')])}</p></div>
      <TextField label={tx('Süre (ay)')} type="number" min={1} max={240} value={months} onChange={(e) => setMonths(e.target.value)} />
      <SelectField label={tx('İşlem')} value={action} onChange={setAction} options={p.allowedActions.map((a) => ({ value: a, label: a === 'Anonymize' ? tx('Anonimleştir') : tx('Sil') }))} />
      <label className="flex h-9 items-center gap-2 text-[13px]"><Checkbox checked={enabled} onCheckedChange={(v) => setEnabled(v === true)} />{' '}{tx('Otomatik')}</label>
      <div className="flex gap-2"><Button size="sm" onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button><RetentionPreviewButton id={p.id} label={p.label} /><Button size="sm" variant="outline" disabled={run.isPending} onClick={runNow} title={tx('Şimdi çalıştır')} aria-label={tx('Şimdi çalıştır')}><Play className="size-4" /></Button></div>
    </div>
  )
}

function Retention() {
  const q = useQuery({ queryKey: ['privacy', 'retention'], queryFn: ({ signal }) => governanceApi.retention(signal) })
  const [emp, setEmp] = useState('')
  const anon = useAction(() => governanceApi.anonymize(emp), { success: tx('Çalışan anonimleştirildi'), onDone: () => setEmp('') })
  const dir = useDirectory()
  const confirm = useConfirm()
  const anonymize = async () => {
    const name = dir.data?.find((d) => d.id === emp)?.fullName ?? tx('seçilen çalışan')
    const ok = await confirm({
      title: tx('{0} anonimleştirilsin mi?', [name]),
      note: tx('Ad, e-posta, telefon, adres, IBAN ve TCKN kalıcı olarak silinir; yalnızca istatistik alanları kalır. Bu işlem geri alınamaz.'),
      action: tx('Anonimleştir'),
    })
    if (ok) anon.mutate(undefined)
  }
  return (
    <div className="space-y-5">
      <InfoNote>{tx('KVKK m.7 gereği işleme amacı ortadan kalkan veriler silinir veya anonimleştirilir. Otomatik politikalar günde bir kez çalışır; “▶” ile hemen çalıştırabilirsiniz. Bordro/SGK kayıtları için yasal saklama süreleri (ör. 10 yıl) gözetilmelidir.')}</InfoNote>
      {q.isPending ? <RowsSkeleton /> : q.data?.map((p) => <RetentionRow key={p.id} p={p} />)}
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><EraserIcon className="size-4 text-destructive" />{' '}{tx('Tek çalışanı anonimleştir')}</span>} note={tx('Yalnızca işten ayrılmış çalışanlar. Geri alınamaz: ad, e-posta, telefon, adres, IBAN, TCKN silinir; istatistik alanları kalır.')} />
        <PanelBody className="flex flex-wrap items-end gap-3">
          <div className="w-72"><PersonSelect label={tx('Çalışan')} value={emp} onChange={setEmp} /></div>
          <Button variant="destructive" disabled={!emp || anon.isPending} onClick={anonymize}>{tx('Anonimleştir')}</Button>
        </PanelBody>
      </Panel>
      <DestructionLogsPanel />
    </div>
  )
}

const PRIVACY_TABS = ['uyum', 'envanter', 'aktarim', 'metinler', 'riza', 'basvuru', 'ihlal', 'pia', 'alan', 'itiraz', 'erisim', 'saklama', 'imha'] as const
type PrivacyTab = (typeof PRIVACY_TABS)[number]

export function PrivacyAdminPage() {
  const [rawTab, setTab] = useTabParam<PrivacyTab>('sekme', 'uyum')
  // Geçersiz ?sekme= değeri boş sayfa yerine varsayılan sekmeye düşer.
  const tab: PrivacyTab = (PRIVACY_TABS as readonly string[]).includes(rawTab) ? rawTab : 'uyum'
  return (
    <TenantGate>
      <PageHeader title={tx('KVKK')} description={tx('Uyum durumu, VERBİS envanteri, yurt dışı aktarım, aydınlatma ve rıza metinleri, ilgili kişi başvuruları, veri ihlalleri, etki değerlendirmesi, alan yetkileri, itirazlar, erişim kayıtları, saklama, imha ve imha doğrulama.')} />
      <div className="mb-5"><Tabs label={tx('KVKK')} value={tab} onChange={setTab} tabs={[
        { key: 'uyum', label: tx('Uyum durumu') }, { key: 'envanter', label: tx('Envanter') }, { key: 'aktarim', label: tx('Yurt dışı aktarım') },
        { key: 'metinler', label: tx('Metinler') }, { key: 'riza', label: tx('Rıza durumu') }, { key: 'basvuru', label: tx('Başvurular') },
        { key: 'ihlal', label: tx('Veri ihlali') }, { key: 'pia', label: tx('Etki değerlendirmesi') }, { key: 'alan', label: tx('Alan yetkileri') }, { key: 'itiraz', label: tx('İtirazlar') },
        { key: 'erisim', label: tx('Erişim kayıtları') }, { key: 'saklama', label: tx('Saklama & imha') }, { key: 'imha', label: tx('İmha doğrulama') },
      ]} /></div>
      {tab === 'uyum' && <ComplianceOverview onOpen={(t) => setTab(t as PrivacyTab)} />}
      {tab === 'envanter' && <VerbisPanel />}
      {tab === 'aktarim' && <TransfersPanel />}
      {tab === 'metinler' && <NoticesPanel />}
      {tab === 'riza' && <Consents />}
      {tab === 'ihlal' && <BreachesPanel />}
      {tab === 'pia' && <AssessmentsPanel />}
      {tab === 'alan' && <FieldPoliciesPanel />}
      {tab === 'basvuru' && <Requests />}
      {tab === 'itiraz' && <ObjectionsPanel />}
      {tab === 'erisim' && <AccessLogPanel />}
      {tab === 'saklama' && <Retention />}
      {tab === 'imha' && <DestructionVerificationPanel />}
    </TenantGate>
  )
}
