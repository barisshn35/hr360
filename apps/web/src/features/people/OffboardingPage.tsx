import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { motion } from 'motion/react'
import { Calculator, Check, ClipboardCheck, LogOut, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { ProgressBar } from '@/components/ui/Progress'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { engagementApi, offboardingReasonLabels, type ExitInterview, type OffboardingReason } from '@/api/engagement'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useEmployee } from '@/api/queries'
import { formatDate, formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Initials, PersonSelect, TenantGate, isoDate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { useModules } from '@/lib/modules'
import { AccountAndRetention, AssetReturnChecklist } from './OffboardingExtras'

function StartModal({ onClose }: { onClose: () => void }) {
  const [emp, setEmp] = useState('')
  const [day, setDay] = useState(isoDate(new Date(Date.now() + 14 * 86400000)))
  const [reason, setReason] = useState<OffboardingReason>('Resignation')
  const start = useAction(() => engagementApi.startOffboarding({ employeeId: emp, lastWorkingDay: day, reason }), { success: tx('Ayrılış süreci başlatıldı'), invalidate: [['offboarding']], onDone: onClose })
  // Sunucuyla aynı kural: son iş günü ≥ işe giriş, ≤ bugün + 1 yıl (geçmiş tarih imha planını da geçmişe çeker).
  const hire = useEmployee(emp || undefined).data?.hireDate?.slice(0, 10)
  const maxDay = isoDate(new Date(new Date().setFullYear(new Date().getFullYear() + 1)))
  const dayError = !day
    ? tx('Son iş günü zorunlu.')
    : hire && day < hire
      ? tx('Son iş günü işe giriş tarihinden önce olamaz.')
      : day > maxDay
        ? tx('Son iş günü en fazla bir yıl sonrası olabilir.')
        : undefined
  return (
    <Modal open onClose={onClose} title={tx('Ayrılış süreci başlat')} note={tx('Açık zimmetler iade listesine otomatik eklenir; süreç tamamlanınca giriş hesabı kapatılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!emp || !!dayError || start.isPending} onClick={() => start.mutate(undefined)}>{tx('Başlat')}</Button></>}>
      <div className="space-y-4">
        <PersonSelect label={tx('Çalışan')} value={emp} onChange={setEmp} />
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField label={tx('Son iş günü')} type="date" min={hire} max={maxDay} value={day} onChange={(e) => setDay(e.target.value)} error={dayError} />
          <SelectField label={tx('Ayrılış nedeni')} value={reason} onChange={(v) => setReason(v as OffboardingReason)} options={(Object.keys(offboardingReasonLabels) as OffboardingReason[]).map((k) => ({ value: k, label: offboardingReasonLabels[k] }))} />
        </div>
      </div>
    </Modal>
  )
}

function Settlement({ id }: { id: string }) {
  const { roles } = useAuth()
  const compensation = useModules().isEnabled('compensation')
  const canPayroll = compensation && roles.some((r) => ['hr-admin', 'tenant-admin', 'platform-admin'].includes(r))
  const q = useQuery({ queryKey: ['offboarding', id, 'settlement'], queryFn: ({ signal }) => engagementApi.settlement(id, signal), retry: false })
  if (q.isPending) return <RowsSkeleton rows={2} />
  if (q.isError) return <p className="text-[13px] text-muted-foreground">{tx('Hak ediş hesabı için ücret görme yetkisi gerekir.')}</p>
  const s = q.data
  return (
    <div className="sensitive-scope space-y-3">
      {s.severanceCeilingMissing && s.severance.eligible && <InfoNote>{tx('Ayrılış tarihinin yarıyılı için kıdem tazminatı tavanı bordro parametrelerinde girilmemiş; kıdem tutarı hesaplanmadı ve toplama dahil değil. Bordro › Parametreler sekmesinde tavanı girin.')}</InfoNote>}
      {!s.hasSalary && <InfoNote>{tx('Bu çalışan için ücret kaydı yok; tutarlar 0 görünür. Ücret ekranından brüt ücret girin.')}</InfoNote>}
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('Kıdem tazminatı {0}', [s.severance.eligible ? '' : tx('(hak yok)')])}</p><p className="tabular text-[18px] font-semibold">{formatMoney(s.severance.gross)}</p><p className="text-[11.5px] text-muted-foreground">{s.severanceCeilingMissing ? tx('Kıdem tavanı girilmemiş (Bordro › Parametreler)') : tx('net {0} · tavan {1}', [formatMoney(s.severance.net), formatMoney(s.severanceCeiling)])}</p></div>
        <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('İhbar ({0} hafta) {1}', [s.notice.weeks, s.notice.applies ? '' : tx('(uygulanmaz)')])}</p><p className="tabular text-[18px] font-semibold">{formatMoney(s.notice.gross)}</p><p className="text-[11.5px] text-muted-foreground">{tx('brüt')}</p></div>
        <div className="rounded-xl border border-border p-3"><p className="text-[12px] text-muted-foreground">{tx('Kullanılmayan izin ({0} gün)', [s.unusedLeave.days])}</p><p className="tabular text-[18px] font-semibold">{formatMoney(s.unusedLeave.gross)}</p><p className="text-[11.5px] text-muted-foreground">{tx('brüt')}</p></div>
      </div>
      <p className="text-[13px]">{tx('Kıdem:')}{' '}<b>{tx('{0} yıl', [s.tenureYears])}</b>{' '}{tx('· Toplam brüt:')}{' '}<b>{formatMoney(s.totalGross)}</b></p>
      <details className="text-[12px] text-muted-foreground"><summary className="cursor-pointer">{tx('Yasal dayanaklar')}</summary><ul className="mt-1 list-disc space-y-1 pl-5"><li>{s.severance.basis}</li><li>{s.notice.basis}</li><li>{s.unusedLeave.basis}</li></ul></details>
      <p className="text-[11.5px] text-muted-foreground">{s.disclaimer}</p>
      {/* Bordro dalgası 8: kesin hesap (giydirilmiş ücret, ihbar gelir vergisi, İK onayı, ibraname) bordro ekranında. */}
      {canPayroll && <Button size="sm" variant="outline" asChild><Link to={`/panel/bordro?bolum=severance&case=${id}`}><Calculator className="size-4" />{' '}{tx('Bordroda kesin hesap ve ibraname')}</Link></Button>}
    </div>
  )
}

function CaseModal({ id, onClose }: { id: string; onClose: () => void }) {
  const { roles } = useAuth()
  const q = useQuery({ queryKey: ['offboarding', id], queryFn: ({ signal }) => engagementApi.offboarding(id, signal) })
  // Ayrıntı ucu zimmetleri onboarding kayıtlarından yeniler (iade edilen "Returned" olur);
  // listedeki "n zimmet bekliyor" rozeti de güncellensin diye liste sorgusu tazelenir.
  const qc = useQueryClient()
  useEffect(() => {
    if (q.dataUpdatedAt) void qc.invalidateQueries({ queryKey: ['offboarding'], exact: true })
  }, [q.dataUpdatedAt, qc])
  const toggle = useAction(({ key, done }: { key: string; done: boolean }) => engagementApi.toggleChecklist(id, key, done), { invalidate: [['offboarding']] })
  const [iv, setIv] = useState<ExitInterview>({})
  const [rehire, setRehire] = useState<boolean | null>(null)
  const saveIv = useAction(() => engagementApi.saveExitInterview(id, iv, rehire), { success: tx('Çıkış görüşmesi kaydedildi'), invalidate: [['offboarding']] })
  const complete = useAction(() => engagementApi.completeOffboarding(id, true), { success: (r) => r.warning ?? tx('Süreç tamamlandı; çalışan “Ayrıldı” durumuna alındı, giriş hesabı kapatıldı'), invalidate: [['offboarding']], onDone: onClose })
  const c = q.data
  const showMoney = roles.some((r) => ['hr-admin', 'tenant-admin', 'platform-admin', 'ext-compensation-view'].includes(r))
  // Süreci yönetmek (tamamlama, çıkış görüşmesi, İK adımları) yalnızca İK; yönetici ekibinin
  // sürecini görür ve yalnızca sorumlusu "Yönetici" olan adımları işaretler.
  const hr = isHr(roles, 'ext-engagement-manage')
  const canTick = (owner: string, key: string) => key !== 'exit-interview' && (hr || owner === 'Yönetici')
  const Score = ({ k, label }: { k: keyof ExitInterview; label: string }) => (
    <div className="flex items-center justify-between gap-2 text-[13px]"><span>{label}</span><div className="flex gap-1">{[1, 2, 3, 4, 5].map((n) => (
      <button key={n} type="button" onClick={() => setIv({ ...iv, [k]: n })} className={cn('size-7 cursor-pointer rounded-md border text-[12px]', iv[k] === n ? 'border-primary bg-primary/15' : 'border-border')}>{n}</button>))}</div></div>
  )
  return (
    <Modal open onClose={onClose} size="xl" title={c ? tx('{0} — ayrılış', [c.employeeName]) : tx('Ayrılış')} note={c ? tx('{0} · son iş günü {1}', [offboardingReasonLabels[c.reason], formatDate(c.lastWorkingDay)]) : undefined}
      footer={c?.status === 'Open' && hr && <><Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button><Button disabled={c.checklist.some((i) => !i.done) || (c.assetChecks ?? []).some((a) => a.resolution === 'Open') || complete.isPending} onClick={() => complete.mutate(undefined)}><LogOut className="size-4" />{' '}{tx('Süreci tamamla')}</Button></>}>
      {!c ? <RowsSkeleton /> : (
        <div className="grid gap-6 lg:grid-cols-2">
          <div>
            <p className="mb-2 flex items-center gap-2 text-[14px] font-semibold"><ClipboardCheck className="size-4 text-primary" />{' '}{tx('Kontrol listesi')}</p>
            <ul className="space-y-1.5">
              {c.checklist.map((it) => (
                <li key={it.key} className="flex items-start gap-2.5 rounded-xl border border-border p-2.5">
                  {/* Kutu metne bağlı: erişilebilir adı başlıktır ve metne tıklamak da işaretler. */}
                  <Checkbox id={`ofb-${c.id}-${it.key}`} aria-describedby={`ofb-${c.id}-${it.key}-d`} checked={it.done} disabled={c.status !== 'Open' || !canTick(it.owner, it.key)} onCheckedChange={(v) => toggle.mutate({ key: it.key, done: v === true })} className="mt-0.5" />
                  <div className="min-w-0 flex-1">
                    <label htmlFor={`ofb-${c.id}-${it.key}`} className={cn('block text-[13px]', c.status === 'Open' && canTick(it.owner, it.key) && 'cursor-pointer', it.done && 'text-muted-foreground line-through')}>{it.title}</label>
                    <p id={`ofb-${c.id}-${it.key}-d`} className="text-[11.5px] text-muted-foreground">{it.owner}{it.doneBy ? ` · ${it.doneBy}, ${formatDate(it.doneAt)}` : ''}{it.hint ? ` · ${it.hint}` : ''}</p>
                  </div>
                </li>
              ))}
            </ul>
            <div className="mt-6"><AssetReturnChecklist c={c} /></div>
          </div>
          <div className="space-y-6">
            <AccountAndRetention c={c} />
            <div>
              <p className="mb-2 text-[14px] font-semibold">{tx('Çıkış görüşmesi')}</p>
              {c.exitInterview ? (
                <div className="space-y-1 rounded-xl bg-muted/40 p-3 text-[13px]">
                  <p>{tx('Ana neden:')}{' '}<b>{c.exitInterview.primaryReason ?? '—'}</b></p>
                  <p>{tx('Yönetici {0}/5 · Kültür {1}/5 · Gelişim {2}/5 · Ücret {3}/5', [c.exitInterview.managerScore ?? '—', c.exitInterview.cultureScore ?? '—', c.exitInterview.growthScore ?? '—', c.exitInterview.compensationScore ?? '—'])}</p>
                  <p>{tx('Tavsiye eder mi: {0} · Yeniden işe alınabilir: {1}', [c.exitInterview.wouldRecommend == null ? '—' : c.exitInterview.wouldRecommend ? tx('Evet') : tx('Hayır'), c.rehireEligible == null ? '—' : c.rehireEligible ? tx('Evet') : tx('Hayır')])}</p>
                  {c.exitInterview.comments && <p className="text-muted-foreground">“{c.exitInterview.comments}”</p>}
                </div>
              ) : c.status === 'Open' && hr ? (
                <div className="space-y-3">
                  <SelectField label={tx('Ana ayrılış nedeni')} value={iv.primaryReason ?? ''} onChange={(v) => setIv({ ...iv, primaryReason: v })} options={[tx('Kariyer fırsatı'), tx('Ücret'), tx('Yönetici ilişkisi'), tx('İş yükü'), tx('Taşınma'), tx('Kişisel'), tx('Diğer')].map((x) => ({ value: x, label: x }))} />
                  <Score k="managerScore" label={tx('Yöneticiyle ilişki')} /><Score k="cultureScore" label={tx('Şirket kültürü')} /><Score k="growthScore" label={tx('Gelişim fırsatı')} /><Score k="compensationScore" label={tx('Ücret ve yan haklar')} />
                  <div className="flex gap-4 text-[13px]">
                    <label className="flex items-center gap-2"><Checkbox checked={iv.wouldRecommend === true} onCheckedChange={(v) => setIv({ ...iv, wouldRecommend: v === true })} />{' '}{tx('Şirketi tavsiye eder')}</label>
                    <label className="flex items-center gap-2"><Checkbox checked={rehire === true} onCheckedChange={(v) => setRehire(v === true)} />{' '}{tx('Yeniden işe alınabilir')}</label>
                  </div>
                  <TextAreaField label={tx('Yorumlar')} rows={2} value={iv.comments ?? ''} onChange={(e) => setIv({ ...iv, comments: e.target.value })} />
                  <Button size="sm" onClick={() => saveIv.mutate(undefined)} disabled={!iv.primaryReason}>{tx('Görüşmeyi kaydet')}</Button>
                </div>
              ) : <p className="text-[13px] text-muted-foreground">{c.status === 'Open' ? tx('Çıkış görüşmesini İK yapar.') : tx('Görüşme yapılmadı.')}</p>}
            </div>
            {showMoney && (
              <div>
                <p className="mb-2 flex items-center gap-2 text-[14px] font-semibold"><Calculator className="size-4 text-primary" />{' '}{tx('Tahmini hak ediş')}</p>
                <Settlement id={c.id} />
              </div>
            )}
          </div>
        </div>
      )}
    </Modal>
  )
}

export function OffboardingPage() {
  // Zimmet iadesi başka ekrandan yapılır; sayfaya dönüşte liste her zaman tazelenir.
  const q = useQuery({ queryKey: ['offboarding'], queryFn: ({ signal }) => engagementApi.offboardings(signal), refetchOnMount: 'always' })
  const { roles } = useAuth()
  // Süreç başlatma yalnızca İK; yönetici ekibindeki süreçleri izler.
  const hr = isHr(roles, 'ext-engagement-manage')
  const [starting, setStarting] = useState(false)
  const [open, setOpen] = useState<string | null>(null)
  return (
    <TenantGate>
      <PageHeader title={tx('İşten ayrılış')} description={tx('Zimmet iadesi, erişim kapatma, SGK bildirimi, çıkış görüşmesi ve hak ediş tahmini — eksiksiz ve izlenebilir bir veda.')} actions={hr ? <Button onClick={() => setStarting(true)}><Plus className="size-4" />{' '}{tx('Süreç başlat')}</Button> : undefined} />
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState message={(q.error as Error).message} onRetry={() => q.refetch()} /> : q.data.length === 0 ? (
        <EmptyState icon={LogOut} title={tx('Açık ayrılış süreci yok')} detail={hr ? tx('Bir çalışan ayrılacağında süreci başlatın; kontrol listesi otomatik oluşur.') : tx('Ekibinizden biri için İK ayrılış süreci başlattığında burada görünür.')} action={hr ? <Button onClick={() => setStarting(true)}>{tx('Süreç başlat')}</Button> : undefined} />
      ) : (
        <Panel>
          <PanelHead title={tx('Süreçler')} />
          <PanelBody className="p-0">
            <ul className="divide-y divide-border">
              {q.data.map((c, i) => (
                <motion.li key={c.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.04 }}>
                  <button className="flex w-full cursor-pointer flex-wrap items-center gap-4 px-5 py-3.5 text-left hover:bg-accent/30" onClick={() => setOpen(c.id)}>
                    <Initials name={c.employeeName} size={36} />
                    <div className="min-w-40 flex-1"><p className="text-[14px] font-medium">{c.employeeName}</p><p className="text-[12px] text-muted-foreground">{tx('{0} · son gün {1}', [offboardingReasonLabels[c.reason], formatDate(c.lastWorkingDay)])}</p></div>
                    <div className="w-48"><ProgressBar value={c.progress} tone={c.progress === 100 ? 'success' : 'info'} label={tx('{0}/{1} adım', [c.done, c.total])} /></div>
                    {c.hasInterview && <StatusBadge tone="info"><Check className="size-3" />{' '}{tx('görüşme')}</StatusBadge>}
                    {(c.openAssets ?? 0) > 0 && <StatusBadge tone="warning">{tx('{0} zimmet bekliyor', [c.openAssets])}</StatusBadge>}
                    {c.accountStatus === 'Disabled' && <StatusBadge tone="neutral">{tx('hesap kapalı')}</StatusBadge>}
                    {c.status === 'Completed' && c.plannedAnonymizationOn && <span className="text-[12px] text-muted-foreground">{tx('imha: {0}', [formatDate(c.plannedAnonymizationOn)])}</span>}
                    <StatusBadge tone={c.status === 'Completed' ? 'success' : c.status === 'Open' ? 'warning' : 'neutral'}>{c.status === 'Completed' ? tx('Tamamlandı') : c.status === 'Open' ? tx('Açık') : tx('İptal')}</StatusBadge>
                  </button>
                </motion.li>
              ))}
            </ul>
          </PanelBody>
        </Panel>
      )}
      {starting && hr && <StartModal onClose={() => setStarting(false)} />}
      {open && <CaseModal id={open} onClose={() => setOpen(null)} />}
    </TenantGate>
  )
}
