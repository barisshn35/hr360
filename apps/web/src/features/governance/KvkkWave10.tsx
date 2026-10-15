import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, Download, Eye, FileArchive, Pencil, Plus, Printer, RefreshCw, Send, ShieldCheck } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { ProgressBar } from '@/components/ui/Progress'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { useToast } from '@/components/ui/Toast'
import { kvkk10Api, type StorageStatus, type VerbisInput, type VerbisItem, type VerbisState } from '@/api/wave10'
import type { DataRequest } from '@/api/governance'
import { downloadWorkbook } from '@/lib/spreadsheet'
import { formatDate, formatDateTime, formatNumber } from '@/lib/format'
import { ChipInput, errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/* ================================================================== 53) VERBİS envanteri */

const EMPTY: VerbisInput = {
  module: '', activity: '', subjects: [], dataCategories: [], purpose: '', legalBasis: '', special: false, retention: '',
  retentionCategory: null, recipients: [], transferProviders: [], measures: '', isActive: true,
}

function VerbisEditor({ item, state, onClose }: { item: VerbisItem | null; state: VerbisState; onClose: () => void }) {
  const [f, setF] = useState<VerbisInput>(() => (item ? {
    module: item.module, activity: item.activity, subjects: item.subjects, dataCategories: item.dataCategories, purpose: item.purpose,
    legalBasis: item.legalBasis, special: item.special, retention: item.retention, retentionCategory: item.retentionCategory,
    recipients: item.recipients, transferProviders: item.transferProviders, measures: item.measures, isActive: item.isActive,
  } : EMPTY))
  const set = <K extends keyof VerbisInput>(k: K, v: VerbisInput[K]) => setF((x) => ({ ...x, [k]: v }))
  const save = useAction(() => (item ? kvkk10Api.updateVerbis(item.id, f) : kvkk10Api.createVerbis(f)),
    { success: tx('Envanter kaydedildi'), invalidate: [['privacy', 'verbis']], onDone: onClose })
  const remove = useAction(() => kvkk10Api.deleteVerbis(item!.id), { success: tx('Faaliyet silindi'), invalidate: [['privacy', 'verbis']], onDone: onClose })
  const confirm = useConfirm()
  const askDelete = async () => {
    if (await confirm({ title: tx('Faaliyet silinsin mi?'), note: tx('Şirketin eklediği faaliyet envanterden kalıcı olarak silinir; işlem denetim kaydına yazılır.'), action: tx('Sil') })) remove.mutate(undefined)
  }
  const valid = f.module.trim() && f.activity.trim() && f.purpose.trim() && f.legalBasis.trim() && f.retention.trim() && f.dataCategories.length > 0 && f.subjects.length > 0
  return (
    <Modal open onClose={onClose} size="lg" title={item ? `${item.module} — ${item.activity}` : tx('Yeni işleme faaliyeti')}
      note={tx('VERBİS alanları: veri kategorisi, işleme amacı, hukuki sebep, ilgili kişi grubu, alıcı grubu, saklama süresi, yurt dışına aktarım ve tedbirler. Her değişiklik önceki/sonraki değerleriyle denetim kaydına yazılır.')}
      footer={<>
        {item?.source === 'Custom' && <Button variant="destructive" className="mr-auto" disabled={remove.isPending} onClick={() => void askDelete()}>{tx('Sil')}</Button>}
        <Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
        <Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !valid}>{tx('Kaydet')}</Button>
      </>}>
      <div className="grid gap-4 md:grid-cols-2">
        <TextField label={tx('Modül')} value={f.module} maxLength={120} onChange={(e) => set('module', e.target.value)} />
        <TextField label={tx('İşleme faaliyeti')} value={f.activity} maxLength={300} onChange={(e) => set('activity', e.target.value)} />
        <div className="md:col-span-2"><ChipInput label={tx('Veri kategorileri')} value={f.dataCategories} onChange={(v) => set('dataCategories', v)} placeholder={tx('Ör. Kimlik (ad, soyad) — Enter')} /></div>
        <ChipInput label={tx('İlgili kişi grupları')} value={f.subjects} onChange={(v) => set('subjects', v)} suggestions={['Çalışanlar', 'Çalışan adayları', 'Ayrılmış çalışanlar', 'Kullanıcılar', 'Çalışanların yakınları']} />
        <ChipInput label={tx('Alıcı grupları')} value={f.recipients} onChange={(v) => set('recipients', v)} suggestions={['SGK', 'Gelir İdaresi Başkanlığı', 'Bankalar', 'Muhasebe']} />
        <div className="md:col-span-2"><TextAreaField label={tx('İşleme amacı')} rows={2} value={f.purpose} onChange={(e) => set('purpose', e.target.value)} /></div>
        <div className="md:col-span-2"><TextAreaField label={tx('Hukuki sebep (KVKK m.5 / m.6)')} rows={2} value={f.legalBasis} onChange={(e) => set('legalBasis', e.target.value)} /></div>
        <TextField label={tx('Saklama süresi')} value={f.retention} onChange={(e) => set('retention', e.target.value)} />
        <SelectField label={tx('Bağlı saklama politikası')} value={f.retentionCategory ?? ''} onChange={(v) => set('retentionCategory', v || null)}
          options={[{ value: '', label: tx('Yok') }, ...state.retentionCategories.map((c) => ({ value: c.value, label: c.label }))]} />
        <div className="md:col-span-2 space-y-1.5">
          <p className="text-[12.5px] font-medium">{tx('Yurt dışına aktarım yapan hizmetler')}</p>
          <div className="flex flex-wrap gap-3">
            {state.providers.map((p) => (
              <label key={p.key} className="flex items-center gap-2 text-[12.5px]">
                <Checkbox checked={f.transferProviders.includes(p.key)}
                  onCheckedChange={(v) => set('transferProviders', v === true ? [...f.transferProviders, p.key] : f.transferProviders.filter((k) => k !== p.key))} />
                {p.name}
              </label>
            ))}
          </div>
        </div>
        <div className="md:col-span-2"><TextAreaField label={tx('İdari ve teknik tedbirler')} rows={2} value={f.measures} onChange={(e) => set('measures', e.target.value)} /></div>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.special} onCheckedChange={(v) => set('special', v === true)} />{' '}{tx('Özel nitelikli veri içerir (m.6)')}</label>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.isActive} onCheckedChange={(v) => set('isActive', v === true)} />{' '}{tx('Etkin (dışa aktarıma girer)')}</label>
      </div>
    </Modal>
  )
}

export function VerbisPanel() {
  const q = useQuery({ queryKey: ['privacy', 'verbis'], queryFn: ({ signal }) => kvkk10Api.verbis(signal) })
  const [edit, setEdit] = useState<VerbisItem | 'new' | null>(null)
  const toast = useToast()
  const sync = useAction(() => kvkk10Api.verbisSync(), { success: (r) => tx('{0} faaliyet eklendi', [r.added]), invalidate: [['privacy', 'verbis']] })
  if (q.isPending) return <RowsSkeleton />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const d = q.data
  const exportXlsx = async () => {
    try {
      const r = await kvkk10Api.verbisRows()
      await downloadWorkbook('verbis-envanter.xlsx', {
        [tx('VERBİS envanteri')]: r.rows.map((row) => Object.fromEntries(r.columns.map((c, i) => [c, row[i]]))),
        [tx('Bilgi')]: [{ [tx('Son güncelleme')]: r.lastUpdatedAt ? formatDateTime(r.lastUpdatedAt) : '—', [tx('Güncelleyen')]: r.lastUpdatedBy ?? '—', [tx('Oluşturma')]: formatDateTime(new Date().toISOString()) }],
      })
    } catch (e) { toast.stop(errMsg(e)) }
  }
  const download = (format: 'csv' | 'html') => kvkk10Api.downloadVerbis(format).catch((e) => toast.stop(errMsg(e)))
  return (
    <div className="space-y-4">
      <InfoNote>{tx('Kişisel veri işleme envanteri (KVKK m.16, VERBİS). Ürünün veri modelinden ve saklama politikalarından tohumlanır; İK/KVKK sorumlusu düzenler. Dışa aktarım her faaliyetin her veri kategorisini ayrı satırda, yurt dışı aktarım dayanağıyla birlikte verir.')}</InfoNote>
      <div className="flex flex-wrap items-center gap-2">
        <span className="mr-auto text-[12.5px] text-muted-foreground">
          {d.lastUpdatedAt ? tx('Son güncelleme: {0} · {1}', [formatDateTime(d.lastUpdatedAt), d.lastUpdatedBy ?? '—']) : tx('Henüz güncellenmedi')}
          {' · '}{tx('{0} faaliyet', [d.items.filter((i) => i.isActive).length])}
        </span>
        {d.catalogMissing.length > 0 && (
          <Button size="sm" variant="outline" disabled={sync.isPending} onClick={() => sync.mutate(undefined)}><RefreshCw className="size-4" />{' '}{tx('Katalogdan eksikleri ekle ({0})', [d.catalogMissing.length])}</Button>
        )}
        <Button size="sm" variant="outline" onClick={() => setEdit('new')}><Plus className="size-4" />{' '}{tx('Faaliyet ekle')}</Button>
        <Button size="sm" variant="outline" onClick={() => void exportXlsx()}><Download className="size-4" />{' '}{tx('Excel')}</Button>
        <Button size="sm" variant="outline" onClick={() => void download('csv')}><Download className="size-4" />{' '}{tx('CSV')}</Button>
        <Button size="sm" variant="outline" onClick={() => void download('html')}><Printer className="size-4" />{' '}{tx('Yazdırılabilir')}</Button>
      </div>
      <div className="space-y-3">
        {d.items.map((a) => (
          <details key={a.id} className={`surface rounded-2xl border border-border p-4 ${a.isActive ? '' : 'opacity-60'}`}>
            <summary className="flex cursor-pointer flex-wrap items-center gap-2">
              <span className="text-[13.5px] font-medium">{a.module} — {a.activity}</span>
              {a.special && <StatusBadge tone="warning">{tx('Özel nitelikli')}</StatusBadge>}
              {a.transferProviders.length > 0 && <StatusBadge tone="neutral">{tx('Yurt dışı aktarım')}</StatusBadge>}
              {a.source === 'Custom' && <StatusBadge tone="info">{tx('Şirketin eklediği')}</StatusBadge>}
              {!a.isActive && <StatusBadge>{tx('Pasif')}</StatusBadge>}
              {a.retentionPolicy && !a.retentionPolicy.isEnabled && <StatusBadge tone="warning">{tx('İmha otomatik değil')}</StatusBadge>}
              <Button size="sm" variant="ghost" className="ml-auto h-7" onClick={(e) => { e.preventDefault(); setEdit(a) }}><Pencil className="size-3.5" />{' '}{tx('Düzenle')}</Button>
            </summary>
            <dl className="mt-3 grid gap-x-6 gap-y-2 text-[12.5px] md:grid-cols-2">
              <div><dt className="text-muted-foreground">{tx('İlgili kişi grubu')}</dt><dd>{a.subjects.join(', ')}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Veri kategorileri')}</dt><dd>{a.dataCategories.join('; ')}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Amaç')}</dt><dd>{a.purpose}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Hukuki sebep')}</dt><dd>{a.legalBasis}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Saklama süresi')}</dt><dd>{a.retention}{a.retentionPolicy ? ` · ${a.retentionPolicy.retentionMonths} ${tx('ay')}` : ''}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Alıcı grupları')}</dt><dd>{a.recipients.length ? a.recipients.join(', ') : '—'}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Yurt dışı aktarım')}</dt><dd>{a.transferProviders.length ? a.transferProviders.map((k) => d.providers.find((p) => p.key === k)?.name ?? k).join(', ') : '—'}</dd></div>
              <div><dt className="text-muted-foreground">{tx('Güvenlik önlemleri')}</dt><dd>{a.measures || '—'}</dd></div>
              <div className="md:col-span-2 text-[11.5px] text-muted-foreground">{tx('Son değişiklik: {0} · {1}', [formatDateTime(a.updatedAt), a.updatedByName ?? '—'])}</div>
            </dl>
          </details>
        ))}
      </div>
      {edit && <VerbisEditor item={edit === 'new' ? null : edit} state={d} onClose={() => setEdit(null)} />}
    </div>
  )
}

/* ================================================================== 54) başvuru süre sayacı ve veri paketi */

/** "Gün 12/30" sayacı ve gönderilen hatırlatmalar (İK listesi). */
export function RequestDeadline({ r }: { r: DataRequest }) {
  if (r.day == null) return null
  const tone: StatusTone = r.overdue ? 'danger' : r.day >= 27 ? 'danger' : r.day >= 20 ? 'warning' : 'info'
  const sent = [r.reminder20At && tx('20. gün hatırlatması'), r.reminder27At && tx('27. gün hatırlatması'), r.overdueAlertAt && tx('süre aşımı uyarısı')].filter(Boolean).join(', ')
  return (
    <span className="flex min-w-36 flex-col gap-1" title={sent ? tx('Gönderilen: {0}', [sent]) : undefined}>
      <span className="text-[11.5px] text-muted-foreground">{tx('Gün {0}/{1}', [Math.min(r.day, 99), r.legalDays ?? 30])}{sent ? ' · ✉' : ''}</span>
      <ProgressBar value={Math.min(r.day, r.legalDays ?? 30)} max={r.legalDays ?? 30} tone={tone} label={tx('Yasal süre')} />
    </span>
  )
}

/** Erişim başvurusunda veri paketi: İK hazırlar/yeniler ve indirir. */
export function RequestPackageButtons({ r }: { r: DataRequest }) {
  const toast = useToast()
  const create = useAction(() => kvkk10Api.createPackage(r.id), {
    success: (p) => tx('Veri paketi hazırlandı ({0} KB)', [formatNumber(Math.round(p.sizeBytes / 1024))]), invalidate: [['privacy', 'requests']],
  })
  if (r.kind !== 'Access' || !r.employeeId) return null
  const open = r.status === 'Received' || r.status === 'InProgress'
  return (
    <>
      {r.package && (
        <Button size="sm" variant="outline" title={tx('SHA-256: {0}', [r.package.sha256 ?? '—'])} onClick={() => kvkk10Api.downloadPackage(r.id).catch((e) => toast.stop(errMsg(e)))}>
          <FileArchive className="size-4" />{' '}{tx('Veri paketi')}
        </Button>
      )}
      {open && r.identityVerified !== false && (
        <Button size="sm" variant="ghost" disabled={create.isPending} onClick={() => create.mutate(undefined)}>{r.package ? tx('Paketi yenile') : tx('Paket hazırla')}</Button>
      )}
    </>
  )
}

/* ================================================================== 55) yeniden onay kampanyası */

export function ConsentCampaignsPanel() {
  const q = useQuery({ queryKey: ['privacy', 'campaigns'], queryFn: ({ signal }) => kvkk10Api.campaigns(signal) })
  const [type, setType] = useState('')
  const start = useAction((t: string) => kvkk10Api.startCampaign(t), { success: tx('Kampanya başlatıldı'), invalidate: [['privacy', 'campaigns']], onDone: () => setType('') })
  const remind = useAction((id: string) => kvkk10Api.remindCampaign(id), { success: (r) => tx('{0} kişiye hatırlatma gönderildi', [r.sent]), invalidate: [['privacy', 'campaigns']] })
  const close = useAction((id: string) => kvkk10Api.closeCampaign(id), { success: tx('Kampanya kapatıldı'), invalidate: [['privacy', 'campaigns']] })
  if (q.isPending) return <RowsSkeleton rows={2} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const free = q.data.types.filter((t) => !t.hasCampaign)
  return (
    <Panel>
      <PanelHead title={tx('Yeniden onay kampanyaları')}
        note={tx('Bir metnin yeni sürümü yayımlanınca kampanya kendiliğinden açılır. Aydınlatma metninde tüm kullanıcılar, açık rızada eski sürüme onay vermiş olanlar hedeftir; rıza zorlanmaz. Bekleyenler girişte bilgilendirme bandı görür; hatırlatma haftada bir (en çok 3 kez) otomatik gider.')} />
      <PanelBody className="space-y-4">
        {free.length > 0 && (
          <div className="flex flex-wrap items-end gap-3">
            <div className="w-80"><SelectField label={tx('Güncel sürüm için kampanya başlat')} value={type} onChange={setType}
              options={free.map((t) => ({ value: t.type, label: `${t.title} · ${t.version}` }))} /></div>
            <Button disabled={!type || start.isPending} onClick={() => start.mutate(type)}>{tx('Başlat')}</Button>
          </div>
        )}
        {q.data.campaigns.length === 0 ? <EmptyState icon={ShieldCheck} title={tx('Kampanya yok')} detail={tx('Metinler sekmesinden yeni sürüm yayımlandığında burada izlenir.')} /> : (
          <ul className="space-y-3">
            {q.data.campaigns.map((c) => (
              <li key={c.id} className="rounded-2xl border border-border p-4">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-[13.5px] font-medium">{c.title}</span>
                  <span className="text-[12px] text-muted-foreground">{c.previousVersion ? `${c.previousVersion} → ` : ''}{c.version}</span>
                  <StatusBadge tone={c.status === 'Open' ? (c.current ? 'info' : 'neutral') : 'neutral'}>{c.status === 'Open' ? (c.current ? tx('Açık') : tx('Eski sürüm')) : tx('Kapalı')}</StatusBadge>
                  <span className="ml-auto flex gap-2">
                    {c.canRemind && c.pending && c.pending.length > 0 && (
                      <Button size="sm" variant="outline" disabled={remind.isPending} onClick={() => remind.mutate(c.id)}><Send className="size-4" />{' '}{tx('Hatırlat')}</Button>
                    )}
                    {c.status === 'Open' && <Button size="sm" variant="ghost" disabled={close.isPending} onClick={() => close.mutate(c.id)}>{tx('Kapat')}</Button>}
                  </span>
                </div>
                <div className="mt-3 flex items-center gap-3">
                  <div className="flex-1"><ProgressBar value={c.done} max={Math.max(1, c.target)} tone={c.percent === 100 ? 'success' : 'info'} label={tx('İlerleme')} /></div>
                  <span className="tabular text-[12.5px]">{tx('{0}/{1} yanıtladı (%{2})', [c.done, c.target, c.percent])}</span>
                </div>
                <p className="mt-1 text-[11.5px] text-muted-foreground">
                  {tx('Başlatan {0} · {1}', [c.startedBy, formatDate(c.startedAt)])}
                  {c.reminderCount > 0 ? ` · ${tx('{0} hatırlatma, son {1}', [c.reminderCount, c.lastReminderAt ? formatDate(c.lastReminderAt) : '—'])}` : ''}
                </p>
                {c.pending && c.pending.length > 0 && (
                  <details className="mt-2">
                    <summary className="cursor-pointer text-[12.5px] text-muted-foreground">{tx('Bekleyen {0} kişi', [c.pending.length])}</summary>
                    <ul className="mt-2 grid gap-x-6 gap-y-1 text-[12.5px] md:grid-cols-2">
                      {c.pending.map((p) => <li key={p.employeeId} className="flex justify-between gap-3"><span>{p.name}</span><span className="text-muted-foreground">{p.department ?? '—'}</span></li>)}
                    </ul>
                  </details>
                )}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ================================================================== 56) imha önizleme */

export function RetentionPreviewButton({ id, label }: { id: string; label: string }) {
  const [open, setOpen] = useState(false)
  const q = useQuery({ queryKey: ['privacy', 'retention', 'preview', id], queryFn: ({ signal }) => kvkk10Api.retentionPreview(id, signal), enabled: open })
  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)} title={tx('Kuru çalıştırma')} aria-label={tx('Kuru çalıştırma')}><Eye className="size-4" /></Button>
      {open && (
        <Modal open size="lg" onClose={() => setOpen(false)} title={tx('Önizleme: {0}', [label])}
          note={tx('Kayıtlı ayarla (süre, işlem) çalıştırılsaydı hangi tablodan kaç satırın etkileneceği. Hiçbir kayıt değiştirilmez.')}
          footer={<Button variant="outline" onClick={() => setOpen(false)}>{tx('Kapat')}</Button>}>
          {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : (
            <div className="space-y-4 text-[12.5px]">
              <p>
                {q.data.subjects != null ? tx('{0} kişi etkilenir', [q.data.subjects]) + ' · ' : ''}
                {tx('Toplam {0} satır', [q.data.total])} · {q.data.action === 'Anonymize' ? tx('Anonimleştirme') : tx('Silme')} · {tx('{0} ay', [q.data.retentionMonths])}
              </p>
              <table className="w-full">
                <thead><tr className="text-left text-muted-foreground"><th className="py-1.5 pr-3 font-medium">{tx('Tablo')}</th><th className="py-1.5 pr-3 font-medium">{tx('Kapsam')}</th><th className="py-1.5 pr-3 font-medium">{tx('İşlem')}</th><th className="py-1.5 text-right font-medium">{tx('Satır')}</th></tr></thead>
                <tbody className="divide-y divide-border">
                  {q.data.tables.map((t, i) => (
                    <tr key={`${t.table}-${i}`} className={t.rows === 0 ? 'text-muted-foreground' : ''}>
                      <td className="py-1.5 pr-3 font-mono text-[11.5px]">{t.table}</td>
                      <td className="py-1.5 pr-3">{t.label}{t.skipped ? ` (${tx('tablo yok, atlanır')})` : ''}</td>
                      <td className="py-1.5 pr-3">{t.kind === 'Delete' ? tx('Sil') : tx('Anonimleştir')}</td>
                      <td className="tabular py-1.5 text-right">{t.rows}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {q.data.storage.length > 0 && (
                <InfoNote>{q.data.storage.map((s) => tx('{0}.{1}: {2} dosya nesne deposundan silinip doğrulanacak', [s.table, s.column, s.keys])).join(' · ')}</InfoNote>
              )}
              {q.data.retained.length > 0 && (
                <details><summary className="cursor-pointer text-muted-foreground">{tx('Bilerek dokunulmayan kayıtlar ({0})', [q.data.retained.length])}</summary>
                  <ul className="mt-2 list-disc space-y-1 pl-5">{q.data.retained.map((r) => <li key={r.table}><span className="font-mono text-[11.5px]">{r.table}</span> — {r.reason}</li>)}</ul>
                </details>
              )}
            </div>
          )}
        </Modal>
      )}
    </>
  )
}

/* ================================================================== 57) imha doğrulama */

const storageTone: Record<StorageStatus, StatusTone> = { Pending: 'warning', Deleted: 'success', Absent: 'success', NotStored: 'neutral', Failed: 'danger' }
function storageLabel(s: StorageStatus) {
  return s === 'Pending' ? tx('Bekliyor') : s === 'Deleted' ? tx('Silindi, yokluğu doğrulandı') : s === 'Absent' ? tx('Zaten yok') : s === 'NotStored' ? tx('Depoda tutulmuyor') : tx('Başarısız')
}

export function DestructionVerificationPanel() {
  const q = useQuery({ queryKey: ['privacy', 'verification'], queryFn: ({ signal }) => kvkk10Api.verification(signal) })
  const process = useAction(() => kvkk10Api.processStorage(), { success: (r) => tx('{0} dosya işlendi', [r.processed]), invalidate: [['privacy', 'verification']] })
  if (q.isPending) return <RowsSkeleton />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const d = q.data
  const totals = d.storage.reduce<Record<string, number>>((acc, s) => ({ ...acc, [s.status]: (acc[s.status] ?? 0) + s.count }), {})
  const review = d.coverage.filter((c) => c.status === 'review')
  return (
    <div className="space-y-5">
      <InfoNote>{tx('İmha doğrulama raporu: anonimleştirme/silme planının hangi tabloları kapsadığı, bilerek saklanan kayıtlar ve silinen kayıtların başvurduğu dosyaların nesne deposundan (MinIO) silindiğinin doğrulaması. Dosya anahtarları işlendikten sonra yalnızca SHA-256 özeti saklanır.')}</InfoNote>
      <div className="flex flex-wrap items-center gap-3 print:hidden">
        <Button variant="outline" disabled={process.isPending} onClick={() => process.mutate(undefined)}><RefreshCw className="size-4" />{' '}{tx('Bekleyen dosyaları şimdi işle')}</Button>
        <Button variant="outline" onClick={() => window.print()}><Printer className="size-4" />{' '}{tx('Yazdır')}</Button>
      </div>

      <Panel>
        <PanelHead title={tx('Nesne deposu silme doğrulaması')} note={tx('Bekleyen ve başarısız dosyalar bakım turunda (saatte bir, en çok 5 deneme) yeniden işlenir.')} />
        <PanelBody className="space-y-3">
          <div className="flex flex-wrap gap-2">
            {(['Deleted', 'Absent', 'NotStored', 'Pending', 'Failed'] as StorageStatus[]).map((s) => (
              <StatusBadge key={s} tone={storageTone[s]}>{storageLabel(s)}: {totals[s] ?? 0}</StatusBadge>
            ))}
          </div>
          {d.recent.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Henüz dosyası olan bir kayıt imha edilmedi.')}</p> : (
            <div className="overflow-x-auto">
              <table className="w-full text-[12px]">
                <thead><tr className="text-left text-muted-foreground"><th className="py-1.5 pr-3 font-medium">{tx('Tarih')}</th><th className="py-1.5 pr-3 font-medium">{tx('Kaynak')}</th><th className="py-1.5 pr-3 font-medium">{tx('Anahtar özeti')}</th><th className="py-1.5 pr-3 font-medium">{tx('Durum')}</th></tr></thead>
                <tbody className="divide-y divide-border">
                  {d.recent.map((r, i) => (
                    <tr key={i}>
                      <td className="py-1.5 pr-3 whitespace-nowrap">{formatDateTime(r.processedAt ?? r.createdAt)}</td>
                      <td className="py-1.5 pr-3 font-mono text-[11px]">{r.table}.{r.column}</td>
                      <td className="py-1.5 pr-3 font-mono text-[11px]">{r.keyHash}…</td>
                      <td className="py-1.5 pr-3"><StatusBadge tone={storageTone[r.status]}>{storageLabel(r.status)}</StatusBadge>{r.detail ? <span className="ml-2 text-muted-foreground">{r.detail}</span> : null}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead title={tx('Kapsam denetimi (ayrılmış çalışan)')} note={tx('Kişi kimliği taşıyan her tablo ya anonimleştirme planında ya da gerekçeli olarak saklananlar listesinde olmalı.')} />
        <PanelBody className="space-y-3">
          {review.length > 0
            ? <InfoNote>{tx('İncelenmesi gereken tablolar: {0}', [review.map((r) => r.table).join(', ')])}</InfoNote>
            : <p className="flex items-center gap-2 text-[13px]"><CheckCircle2 className="size-4 text-[hsl(var(--success))]" />{' '}{tx('Kişi kimliği taşıyan tüm tablolar plan ya da gerekçeli istisna kapsamında.')}</p>}
          <details>
            <summary className="cursor-pointer text-[12.5px] text-muted-foreground">{tx('Anonimleştirme adımları ({0})', [d.steps.length])}</summary>
            <ul className="mt-2 grid gap-x-6 gap-y-1 text-[12px] md:grid-cols-2">
              {d.steps.map((s, i) => <li key={i}><span className="font-mono text-[11px]">{s.table}</span> — {s.label} ({s.kind === 'Delete' ? tx('sil') : tx('anonimleştir')})</li>)}
            </ul>
          </details>
          <details>
            <summary className="cursor-pointer text-[12.5px] text-muted-foreground">{tx('Bilerek saklananlar ({0})', [d.coverage.filter((c) => c.status === 'retained').length])}</summary>
            <ul className="mt-2 space-y-1 text-[12px]">
              {d.coverage.filter((c) => c.status === 'retained').map((c) => <li key={c.table}><span className="font-mono text-[11px]">{c.table}</span> — {c.reason}</li>)}
            </ul>
          </details>
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead title={tx('Dosya ve ikili veri konumları')} />
        <PanelBody className="overflow-x-auto p-0">
          <table className="w-full text-[12px]">
            <thead><tr className="text-left text-muted-foreground"><th className="px-4 py-2 font-medium">{tx('Konum')}</th><th className="px-4 py-2 font-medium">{tx('Depo')}</th><th className="px-4 py-2 font-medium">{tx('Kişisel veri')}</th><th className="px-4 py-2 font-medium">{tx('İmha yolu')}</th></tr></thead>
            <tbody className="divide-y divide-border">
              {d.storageMap.map((s) => <tr key={s.location}><td className="px-4 py-2 font-mono text-[11px]">{s.location}</td><td className="px-4 py-2">{s.store}</td><td className="px-4 py-2">{s.personal}</td><td className="px-4 py-2">{s.destruction}</td></tr>)}
            </tbody>
          </table>
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead title={tx('Son imha turları (tablo bazında)')} />
        <PanelBody className="space-y-2">
          {d.logs.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('İmha kaydı yok.')}</p> : d.logs.map((l) => (
            <details key={l.id} className="rounded-xl border border-border p-3">
              <summary className="flex cursor-pointer flex-wrap gap-2 text-[12.5px]">
                <span className="font-medium">{l.label}</span><span className="text-muted-foreground">{formatDateTime(l.ranAt)} · {l.actor}</span>
                <span className="ml-auto tabular">{tx('{0} kayıt', [l.affected])}</span>
              </summary>
              {l.details?.tables ? (
                <ul className="mt-2 grid gap-x-6 gap-y-1 text-[12px] md:grid-cols-2">
                  {l.details.tables.filter((t) => t.rows > 0).map((t, i) => <li key={i}><span className="font-mono text-[11px]">{t.table}</span>: {t.rows}</li>)}
                  {l.details.tables.every((t) => t.rows === 0) && <li className="text-muted-foreground">{tx('Değişen satır yok.')}</li>}
                  {(l.details.storageQueued ?? 0) > 0 && <li>{tx('{0} dosya silme kuyruğuna alındı', [l.details.storageQueued])}</li>}
                </ul>
              ) : <p className="mt-2 text-[12px] text-muted-foreground">{tx('Bu tur için tablo dökümü yok (eski kayıt ya da başka servis).')}</p>}
            </details>
          ))}
        </PanelBody>
      </Panel>
    </div>
  )
}
