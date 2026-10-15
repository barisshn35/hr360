import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Check, Download, FileDown, Gift, HandCoins, Plus, TrendingUp, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { useDirectory } from '@/api/directory'
import { payrollExtrasApi, type AdvanceStatus, type BenefitOption, type ExportKind, type RaiseCycle, type SalaryAdvance, type WorksheetRow } from '@/api/payrollExtras'
import { formatDate, formatDateTime, formatMoney, formatNumber, parseDecimal } from '@/lib/format'
import { Metric, errMsg, isoDate, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'
import { SgkValidationBox } from './PayrollTr'

function useIsPayrollAdmin() {
  const { hasRole } = useAuth()
  return hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')
}

/* ================================================================== Y1/Y3/Y4 dosyalar */

const exportLabels: Record<ExportKind, string> = {
  SgkAphb: tx('SGK aylık prim ve hizmet belgesi (XML)'),
  SgkHires: tx('SGK işe giriş / işten ayrılış listesi'),
  Bank: tx('Banka toplu maaş ödeme dosyası'),
  Accounting: tx('Muhasebe fişi (masraf merkezi özeti)'),
}

const formatOptions: Partial<Record<ExportKind, Array<{ value: string; label: string }>>> = {
  SgkAphb: [{ value: 'xml', label: 'XML' }, { value: 'txt', label: tx('TXT (noktalı virgüllü)') }],
  Bank: [{ value: 'settings', label: tx('Şirket ayarındaki şablon') }, { value: 'generic', label: tx('HR360 genel CSV') }, { value: 'ornek-a', label: tx('Örnek şablon A (noktalı virgüllü CSV)') },
    { value: 'ornek-b', label: tx('Örnek şablon B (sabit uzunluklu TXT)') }, { value: 'custom', label: tx('Özel düzen') }],
  Accounting: [{ value: 'generic', label: tx('Genel CSV') }, { value: 'logo', label: 'Logo' }, { value: 'mikro', label: 'Mikro' }, { value: 'netsis', label: 'Netsis' }],
}
const defaultFormat: Partial<Record<ExportKind, string>> = { SgkAphb: 'xml', Bank: 'settings', Accounting: 'generic' }

export function PayrollExportsPanel({ periodId, closed, calculated = false }: { periodId: string; closed: boolean; calculated?: boolean }) {
  const q = useQuery({ queryKey: ['payroll', 'exports', periodId], queryFn: ({ signal }) => payrollExtrasApi.exports(periodId, signal), enabled: closed })
  const [kind, setKind] = useState<ExportKind>('SgkAphb')
  const [format, setFormat] = useState('xml')
  const [payDate, setPayDate] = useState(isoDate())
  const [warnings, setWarnings] = useState<string[]>([])
  const toast = useToast()
  const create = useAction(() => payrollExtrasApi.createExport(periodId, { kind, format: format && format !== 'settings' ? format : undefined, payDate: kind === 'Bank' ? payDate : undefined }), {
    success: (r) => tx('{0} hazır ({1} satır)', [r.fileName, r.rowCount]), invalidate: [['payroll', 'exports', periodId]], onDone: (r) => setWarnings(r.warnings),
  })
  const dl = async (id: string, name: string) => {
    try { await payrollExtrasApi.download(id, name); void q.refetch() } catch (e) { toast.stop(errMsg(e)) }
  }
  const opts = formatOptions[kind]
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><FileDown className="size-4 text-primary" />{' '}{tx('Resmî, banka ve muhasebe dosyaları')}</span>}
        note={tx('Yalnızca kapanmış dönem için. Dosyalar şifreli saklanır, her indirme erişim kaydına yazılır; banka dosyası bir kez indirilir, tüm dosyalar 24 saat sonra silinir.')} />
      <PanelBody className="space-y-4">
        {!closed ? (
          <>
            {/* Doğrulama en çok kapatmadan önce işe yarar: hesaplanmış dönemde de gösterilir. */}
            {calculated && <SgkValidationBox periodId={periodId} />}
            <p className="text-[13px] text-muted-foreground">{tx('Dosya üretmek için önce dönemi kapatın.')}</p>
          </>
        ) : (
          <>
            <SgkValidationBox periodId={periodId} />
            <div className="flex flex-wrap items-end gap-3">
              <div className="w-80"><SelectField label={tx('Dosya')} value={kind} onChange={(v) => { setKind(v as ExportKind); setFormat(defaultFormat[v as ExportKind] ?? '') }} options={(Object.keys(exportLabels) as ExportKind[]).map((k) => ({ value: k, label: exportLabels[k] }))} /></div>
              {opts && <div className="w-64"><SelectField label={tx('Biçim')} value={format} onChange={setFormat} options={opts} placeholder={tx('Şirket ayarındaki şablon')} /></div>}
              {kind === 'Bank' && <div className="w-44"><TextField type="date" label={tx('Ödeme tarihi')} value={payDate} onChange={(e) => setPayDate(e.target.value)} /></div>}
              <Button onClick={() => create.mutate(undefined)} disabled={create.isPending}>{tx('Üret')}</Button>
            </div>
            {warnings.length > 0 && <InfoNote>{tx('Uyarılar ve dosyaya alınmayanlar:')} {warnings.map((w) => txServer(w)).join(' · ')}</InfoNote>}
            <InfoNote>{tx('SGK, banka ve muhasebe dosyaları kamuya açık kaynaklardaki yaygın düzenlerle üretilir; HR360 SGK e-Bildirge\'ye, bankaya ya da muhasebe yazılımına bağlanmaz. Yüklemeden önce güncel şablonla karşılaştırın. Banka örnek şablonları hiçbir bankanın resmî biçimi olarak doğrulanmamıştır.')}</InfoNote>
            {q.data && q.data.length > 0 && (
              <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
                {q.data.map((e) => (
                  <li key={e.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                    <span className="min-w-0 flex-1">{e.fileName}<span className="block text-[12px] text-muted-foreground">{tx('{0} satır · {1} · {2}', [e.rowCount, e.createdBy, formatDateTime(e.createdAt)])}{e.downloadedAt ? ` · ${tx('indirildi: {0}', [formatDateTime(e.downloadedAt)])}` : ''}{e.contentSha256 ? ` · SHA-256 ${e.contentSha256.slice(0, 16)}…` : ''}</span></span>
                    {e.singleUse && <StatusBadge tone="warning">{tx('Tek indirme')}</StatusBadge>}
                    {e.available ? <Button size="sm" variant="outline" onClick={() => void dl(e.id, e.fileName)}><Download className="size-4" />{' '}{tx('İndir')}</Button> : <StatusBadge tone="neutral">{tx('Silindi')}</StatusBadge>}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ================================================================== Y11 avans */

const advStatus: Record<AdvanceStatus, { label: string; tone: 'neutral' | 'warning' | 'success' | 'danger' | 'info' }> = {
  Pending: { label: tx('Bekliyor'), tone: 'warning' }, Approved: { label: tx('Ödeniyor'), tone: 'info' },
  Rejected: { label: tx('Reddedildi'), tone: 'danger' }, Closed: { label: tx('Kapandı'), tone: 'success' }, Cancelled: { label: tx('İptal'), tone: 'neutral' },
}

function AdvanceRequestModal({ onClose }: { onClose: () => void }) {
  const [f, setF] = useState({ kind: 'Advance' as 'Advance' | 'Loan', amount: '', installments: '1', reason: '' })
  const save = useAction(() => payrollExtrasApi.requestAdvance({ kind: f.kind, amount: Number(f.amount), installments: Number(f.installments), reason: f.reason || undefined }),
    { success: tx('Talebiniz İK\'ya iletildi'), invalidate: [['advances']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Avans / borç talebi')} note={tx('Onaylanırsa taksitler bir sonraki aydan başlayarak bordronuzdan kesilir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !(Number(f.amount) > 0)}>{tx('Gönder')}</Button></>}>
      <div className="space-y-3">
        <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => setF({ ...f, kind: v as 'Advance' | 'Loan' })} options={[{ value: 'Advance', label: tx('Maaş avansı') }, { value: 'Loan', label: tx('Şirket borcu') }]} />
        <div className="grid grid-cols-2 gap-3">
          <TextField label={tx('Tutar (TL)')} type="number" min={1} value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} />
          <TextField label={tx('Taksit sayısı')} type="number" min={1} max={24} value={f.installments} onChange={(e) => setF({ ...f, installments: e.target.value })} />
        </div>
        <TextAreaField label={tx('Açıklama (isteğe bağlı)')} rows={2} value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} hint={tx('Sağlık gibi özel bilgileri yazmanız gerekmez.')} />
      </div>
    </Modal>
  )
}

export function MyAdvancesPanel() {
  const q = useQuery({ queryKey: ['advances', 'mine'], queryFn: ({ signal }) => payrollExtrasApi.advances(undefined, signal) })
  const [open, setOpen] = useState(false)
  const cancel = useAction((id: string) => payrollExtrasApi.cancelAdvance(id), { success: tx('Talep iptal edildi'), invalidate: [['advances']] })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><HandCoins className="size-4 text-primary" />{' '}{tx('Avans ve borçlarım')}</span>}
        action={<Button size="sm" variant="outline" onClick={() => setOpen(true)}><Plus className="size-4" />{' '}{tx('Talep et')}</Button>} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : !q.data?.length ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Avans ya da borç kaydınız yok.')}</p> : (
          <ul className="divide-y divide-border">
            {q.data.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <span className="min-w-0 flex-1">{a.kind === 'Loan' ? tx('Borç') : tx('Avans')} · {formatMoney(a.amount)}<span className="block text-[12px] text-muted-foreground">{tx('{0} taksit × {1} · kalan {2}', [a.installments, formatMoney(a.installment), formatMoney(a.remaining)])}{a.decisionNote ? ` · ${a.decisionNote}` : ''}</span></span>
                <StatusBadge tone={advStatus[a.status].tone}>{advStatus[a.status].label}</StatusBadge>
                {a.status === 'Pending' && <Button size="sm" variant="ghost" onClick={() => cancel.mutate(a.id)}>{tx('İptal')}</Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {open && <AdvanceRequestModal onClose={() => setOpen(false)} />}
    </Panel>
  )
}

export function AdvancesAdminPanel() {
  const q = useQuery({ queryKey: ['advances', 'all'], queryFn: ({ signal }) => payrollExtrasApi.advances(undefined, signal) })
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const [rej, setRej] = useState<SalaryAdvance | null>(null)
  const [note, setNote] = useState('')
  const approve = useAction((id: string) => payrollExtrasApi.decideAdvance(id, true), { success: tx('Onaylandı; taksitler bordroya eklenecek'), invalidate: [['advances']] })
  const reject = useAction(() => payrollExtrasApi.decideAdvance(rej!.id, false, note), { success: tx('Reddedildi'), invalidate: [['advances']], onDone: () => setRej(null) })
  if (q.isPending) return <RowsSkeleton />
  if (!q.data?.length) return <EmptyState icon={HandCoins} title={tx('Avans talebi yok')} detail={tx('Çalışanlar Bordrolarım ekranından avans ya da borç talep eder.')} />
  return (
    <>
      <InfoNote>{tx('Onaylanan taksitler dönem hesaplanırken kesinti olarak otomatik eklenir; dönem kapanınca ödenmiş sayılır.')}</InfoNote>
      <Panel className="mt-4"><PanelBody className="p-0"><ul className="divide-y divide-border">
        {q.data.map((a) => (
          <li key={a.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
            <span className="min-w-0 flex-1 font-medium">{nameOf(a.employeeId)}<span className="block text-[12px] font-normal text-muted-foreground">
              {a.kind === 'Loan' ? tx('Borç') : tx('Avans')} {formatMoney(a.amount)} · {tx('{0} taksit, {1}/{2} başlangıç', [a.installments, a.startMonth, a.startYear])}{a.reason ? ` · ${a.reason}` : ''}</span></span>
            {a.installmentShare !== null && a.installmentShare > 0.25 && <StatusBadge tone="warning">{tx('Taksit / brüt ücret: %{0}', [Math.round(a.installmentShare * 100)])}</StatusBadge>}
            <StatusBadge tone={advStatus[a.status].tone}>{advStatus[a.status].label}</StatusBadge>
            {a.status === 'Pending' && <><Button size="sm" onClick={() => approve.mutate(a.id)}><Check className="size-4" />{' '}{tx('Onayla')}</Button><Button size="sm" variant="outline" onClick={() => { setRej(a); setNote('') }}><X className="size-4" />{' '}{tx('Reddet')}</Button></>}
          </li>
        ))}
      </ul></PanelBody></Panel>
      {rej && (
        <Modal open onClose={() => setRej(null)} title={tx('Talebi reddet')} footer={<><Button variant="outline" onClick={() => setRej(null)}>{tx('Vazgeç')}</Button><Button variant="destructive" disabled={!note.trim() || reject.isPending} onClick={() => reject.mutate(undefined)}>{tx('Reddet')}</Button></>}>
          <TextAreaField label={tx('Gerekçe')} rows={3} value={note} onChange={(e) => setNote(e.target.value)} />
        </Modal>
      )}
    </>
  )
}

/* ================================================================== Y13 esnek yan haklar */

const benefitCategories: Record<string, string> = {
  Meal: tx('Yemek'), Transport: tx('Ulaşım'), Health: tx('Özel sağlık sigortası'), Wellness: tx('Spor ve iyi yaşam'), Education: tx('Eğitim'), Other: tx('Diğer'),
}

function BenefitsAdmin({ year }: { year: number }) {
  const q = useQuery({ queryKey: ['benefits', year], queryFn: ({ signal }) => payrollExtrasApi.benefits(year, signal) })
  const [plan, setPlan] = useState<{ budget: string; start: string; end: string } | null>(null)
  const [opt, setOpt] = useState({ name: '', category: 'Meal', annualCost: '', description: '' })
  const p = q.data?.plan
  const cur = plan ?? { budget: String(p?.budgetPerEmployee ?? ''), start: p?.windowStart ?? isoDate(), end: p?.windowEnd ?? isoDate(new Date(Date.now() + 30 * 864e5)) }
  const savePlan = useAction(() => payrollExtrasApi.savePlan({ year, budgetPerEmployee: Number(cur.budget), windowStart: cur.start, windowEnd: cur.end }), { success: tx('Plan kaydedildi'), invalidate: [['benefits']], onDone: () => setPlan(null) })
  const add = useAction(() => payrollExtrasApi.addOption(year, { name: opt.name, category: opt.category, annualCost: Number(opt.annualCost), description: opt.description || null, isActive: true }),
    { success: tx('Seçenek eklendi'), invalidate: [['benefits']], onDone: () => setOpt({ name: '', category: 'Meal', annualCost: '', description: '' }) })
  const toggle = useAction((o: BenefitOption) => payrollExtrasApi.updateOption(o.id, { ...o, isActive: !o.isActive }), { invalidate: [['benefits']] })
  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={tx('{0} planı', [year])} note={tx('Çalışan başına yıllık bütçe ve seçim penceresi.')} />
        <PanelBody className="flex flex-wrap items-end gap-3">
          <div className="w-44"><TextField label={tx('Bütçe (TL/yıl)')} type="number" value={cur.budget} onChange={(e) => setPlan({ ...cur, budget: e.target.value })} /></div>
          <div className="w-44"><TextField label={tx('Pencere başı')} type="date" value={cur.start} onChange={(e) => setPlan({ ...cur, start: e.target.value })} /></div>
          <div className="w-44"><TextField label={tx('Pencere sonu')} type="date" value={cur.end} onChange={(e) => setPlan({ ...cur, end: e.target.value })} /></div>
          <Button onClick={() => savePlan.mutate(undefined)} disabled={savePlan.isPending || !cur.budget}>{tx('Kaydet')}</Button>
        </PanelBody>
      </Panel>
      {p && (
        <Panel>
          <PanelHead title={tx('Seçenekler')} note={q.data?.summary ? tx('{0} çalışan seçim yaptı', [q.data.summary.elections]) : undefined} />
          <PanelBody className="space-y-3">
            <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
              {q.data!.options.map((o) => (
                <li key={o.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                  <span className="min-w-0 flex-1">{o.name}<span className="block text-[12px] text-muted-foreground">{benefitCategories[o.category] ?? o.category} · {formatMoney(o.annualCost)}{q.data?.summary ? ` · ${tx('{0} seçim', [q.data.summary.byOption.find((x) => x.id === o.id)?.count ?? 0])}` : ''}</span></span>
                  <Button size="sm" variant="ghost" onClick={() => toggle.mutate(o)}>{o.isActive ? tx('Kapat') : tx('Aç')}</Button>
                </li>
              ))}
            </ul>
            <div className="grid items-end gap-3 md:grid-cols-[1.5fr_1fr_140px_auto]">
              <TextField label={tx('Ad')} value={opt.name} onChange={(e) => setOpt({ ...opt, name: e.target.value })} />
              <SelectField label={tx('Kategori')} value={opt.category} onChange={(v) => setOpt({ ...opt, category: v })} options={Object.entries(benefitCategories).map(([value, label]) => ({ value, label }))} />
              <TextField label={tx('Yıllık tutar')} type="number" value={opt.annualCost} onChange={(e) => setOpt({ ...opt, annualCost: e.target.value })} />
              <Button onClick={() => add.mutate(undefined)} disabled={!opt.name.trim() || !opt.annualCost}><Plus className="size-4" />{' '}{tx('Ekle')}</Button>
            </div>
          </PanelBody>
        </Panel>
      )}
    </div>
  )
}

function BenefitsChooser({ year }: { year: number }) {
  const q = useQuery({ queryKey: ['benefits', year], queryFn: ({ signal }) => payrollExtrasApi.benefits(year, signal) })
  const [sel, setSel] = useState<string[] | null>(null)
  const chosen = sel ?? q.data?.election?.optionIds ?? []
  const options = q.data?.options.filter((o) => o.isActive) ?? []
  const total = options.filter((o) => chosen.includes(o.id)).reduce((a, o) => a + o.annualCost, 0)
  const budget = q.data?.plan?.budgetPerEmployee ?? 0
  const save = useAction(() => payrollExtrasApi.elect(year, chosen), { success: tx('Seçimleriniz kaydedildi'), invalidate: [['benefits']], onDone: () => setSel(null) })
  if (q.isPending) return <RowsSkeleton />
  if (!q.data?.plan) return <EmptyState icon={Gift} title={tx('Bu yıl için esnek yan hak planı yok')} detail={tx('İK planı açtığında seçimlerinizi buradan yapabilirsiniz.')} />
  const open = q.data.plan.open
  const pick = (o: BenefitOption) => {
    const sameCat = options.filter((x) => x.category === o.category).map((x) => x.id)
    setSel(chosen.includes(o.id) ? chosen.filter((x) => x !== o.id) : [...chosen.filter((x) => !sameCat.includes(x)), o.id])
  }
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <Metric label={tx('Bütçe')} value={formatMoney(budget)} />
        <Metric label={tx('Seçilen')} value={formatMoney(total)} tone={total > budget ? 'bad' : undefined} />
        <Metric label={tx('Seçim penceresi')} value={`${formatDate(q.data.plan.windowStart)} – ${formatDate(q.data.plan.windowEnd)}`} hint={open ? tx('Açık') : tx('Kapalı')} />
      </div>
      <InfoNote>{tx('Her kategoriden bir seçenek seçebilirsiniz. Özel sağlık sigortası için sağlık beyanı istenmez.')}</InfoNote>
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {options.map((o) => (
          <label key={o.id} className={`surface flex cursor-pointer gap-3 rounded-2xl border p-4 ${chosen.includes(o.id) ? 'border-primary' : 'border-border'} ${open ? '' : 'pointer-events-none opacity-70'}`}>
            <Checkbox checked={chosen.includes(o.id)} onCheckedChange={() => pick(o)} disabled={!open} />
            <span className="min-w-0"><span className="block text-[13.5px] font-medium">{o.name}</span><span className="block text-[12px] text-muted-foreground">{benefitCategories[o.category] ?? o.category} · {formatMoney(o.annualCost)}</span>{o.description && <span className="mt-1 block text-[12px]">{o.description}</span>}</span>
          </label>
        ))}
      </div>
      {open && <Button onClick={() => save.mutate(undefined)} disabled={save.isPending || total > budget || sel === null}>{tx('Seçimleri kaydet')}</Button>}
    </div>
  )
}

export function BenefitsPage() {
  const admin = useIsPayrollAdmin()
  const year = new Date().getFullYear()
  const [view, setView] = useState<'mine' | 'admin'>(admin ? 'admin' : 'mine')
  return (
    <>
      <PageHeader title={tx('Esnek yan haklar')} description={tx('Bütçeniz içinde size uygun yan hakları seçin.')}
        actions={admin ? <Button variant="outline" onClick={() => setView(view === 'admin' ? 'mine' : 'admin')}>{view === 'admin' ? tx('Kendi seçimlerim') : tx('Plan yönetimi')}</Button> : undefined} />
      {view === 'admin' && admin ? <BenefitsAdmin year={year} /> : <BenefitsChooser year={year} />}
    </>
  )
}

/* ================================================================== Y21 zam dönemi */

function CycleModal({ onClose }: { onClose: () => void }) {
  const y = new Date().getFullYear()
  const [f, setF] = useState({ name: tx('{0} yıllık zam', [y + 1]), year: String(y + 1), budget: '10', effective: `${y + 1}-01-01` })
  const save = useAction(() => payrollExtrasApi.createCycle({ name: f.name, year: Number(f.year), budgetPercent: parseDecimal(f.budget) ?? 0, effectiveDate: f.effective }), { success: tx('Zam dönemi oluşturuldu'), invalidate: [['raise']], onDone: onClose })
  // Sunucuyla aynı aralık: geçen yıl .. iki yıl sonrası; bütçe %0–100.
  const yearNum = Number(f.year)
  const yearErr = !Number.isInteger(yearNum) || yearNum < y - 1 || yearNum > y + 2 ? tx('Yıl {0}–{1} aralığında olmalı.', [y - 1, y + 2]) : undefined
  const budgetNum = parseDecimal(f.budget)
  const budgetErr = budgetNum === null || budgetNum < 0 || budgetNum > 100 ? tx('Bütçe %0–100 arasında olmalı.') : undefined
  const invalid = !f.name.trim() || !!yearErr || !!budgetErr || !f.effective
  return (
    <Modal open onClose={onClose} title={tx('Yeni zam dönemi')} footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || invalid}>{tx('Oluştur')}</Button></>}>
      <div className="space-y-3">
        <TextField label={tx('Ad')} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
        <div className="grid grid-cols-3 gap-3">
          <TextField label={tx('Yıl')} type="number" min={y - 1} max={y + 2} value={f.year} error={yearErr} onChange={(e) => setF({ ...f, year: e.target.value })} />
          <TextField label={tx('Bütçe (%)')} inputMode="decimal" value={f.budget} error={budgetErr} onChange={(e) => setF({ ...f, budget: e.target.value })} />
          <TextField label={tx('Yürürlük')} type="date" value={f.effective} onChange={(e) => setF({ ...f, effective: e.target.value })} />
        </div>
      </div>
    </Modal>
  )
}

function ProposalCell({ cycleId, row, editable }: { cycleId: string; row: WorksheetRow; editable: boolean }) {
  const [pct, setPct] = useState(row.proposal ? String(row.proposal.proposedPercent) : '')
  const save = useAction(() => payrollExtrasApi.propose(cycleId, { employeeId: row.employeeId, proposedPercent: Number(pct) }), { success: tx('Öneri kaydedildi'), invalidate: [['raise']] })
  if (!editable || (row.proposal && row.proposal.status !== 'Proposed' && row.proposal.status !== 'Rejected'))
    return <span>{row.proposal ? `%${formatNumber(row.proposal.proposedPercent)} → ${formatMoney(row.proposal.proposedSalary, row.currency)}` : '—'}</span>
  return (
    <span className="flex items-center gap-2">
      <input aria-label={tx('Zam oranı')} className="h-8 w-20 rounded-md border border-border bg-background px-2 text-right text-[13px]" type="number" min={0} max={100} step={0.5} value={pct} onChange={(e) => setPct(e.target.value)} />
      <Button size="sm" variant="outline" disabled={pct === '' || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
    </span>
  )
}

function Worksheet({ cycle }: { cycle: RaiseCycle }) {
  const admin = useIsPayrollAdmin()
  const q = useQuery({ queryKey: ['raise', 'ws', cycle.id], queryFn: ({ signal }) => payrollExtrasApi.worksheet(cycle.id, signal) })
  const summary = useQuery({ queryKey: ['raise', 'sum', cycle.id], queryFn: ({ signal }) => payrollExtrasApi.cycleSummary(cycle.id, signal), enabled: admin })
  const pending = useMemo(() => (q.data?.rows ?? []).filter((r) => r.proposal?.status === 'Proposed').map((r) => r.proposal!.id), [q.data])
  const decide = useAction((approve: boolean) => payrollExtrasApi.decideProposals(cycle.id, pending, approve), { success: (r) => tx('{0} öneri karara bağlandı', [r.decided]), invalidate: [['raise']] })
  const apply = useAction(() => payrollExtrasApi.applyCycle(cycle.id), { success: (r) => tx('{0} çalışanın ücreti güncellendi', [r.applied]), invalidate: [['raise']] })
  const status = useAction((s: RaiseCycle['status']) => payrollExtrasApi.setCycleStatus(cycle.id, s), { invalidate: [['raise']] })
  const approved = useMemo(() => (q.data?.rows ?? []).filter((r) => r.proposal?.status === 'Approved').length, [q.data])
  const confirm = useConfirm()
  if (q.isPending) return <RowsSkeleton />
  const d = q.data!
  const editable = cycle.status === 'Open'
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <Metric label={tx('Bütçe')} value={formatMoney(d.budget)} hint={tx('bütçe oranı: %{0} (aylık brüt toplam üzerinden)', [formatNumber(cycle.budgetPercent)])} />
        <Metric label={tx('Önerilen artış')} value={formatMoney(d.used)} tone={d.used > d.budget ? 'bad' : 'good'} />
        <Metric label={tx('Kişi')} value={d.rows.length} />
      </div>
      {admin && (
        <div className="flex flex-wrap gap-2">
          {cycle.status === 'Draft' && <Button onClick={() => status.mutate('Open')}>{tx('Önerilere aç')}</Button>}
          {cycle.status === 'Open' && <Button variant="outline" disabled={!pending.length} onClick={() => decide.mutate(true)}><Check className="size-4" />{' '}{tx('Bekleyenleri onayla ({0})', [pending.length])}</Button>}
          {cycle.status === 'Open' && <Button variant="outline" disabled={!pending.length} onClick={() => decide.mutate(false)}><X className="size-4" />{' '}{tx('Bekleyenleri reddet')}</Button>}
          {/* Yalnızca öneriye açık dönemde: onaylı öneriler ücret kaydına yazılır ve dönem kapanır (geri alınamaz). */}
          {cycle.status === 'Open' && <Button disabled={apply.isPending} onClick={async () => {
            if (await confirm({
              title: tx('Onaylı öneriler uygulansın mı?'),
              note: tx('{0} çalışanın ücret kaydı güncellenir ve "{1}" dönemi kapanır. Bu işlem geri alınamaz.', [approved, cycle.name]),
              action: tx('Uygula ve kapat'),
            })) apply.mutate(undefined)
          }}>{tx('Onaylıları uygula ve kapat')}</Button>}
        </div>
      )}
      <InfoNote>{tx('Ücretler yalnızca İK ve ilgili bölüm yöneticisine görünür; bu ekranın her açılışı erişim kaydına yazılır.')}</InfoNote>
      <Panel><PanelBody className="overflow-x-auto p-0">
        <table className="w-full text-[13px]">
          <thead><tr className="border-b border-border text-left text-[12px] text-muted-foreground"><th className="px-4 py-2">{tx('Çalışan')}</th><th className="px-4 py-2">{tx('Kademe')}</th><th className="px-4 py-2 text-right">{tx('Mevcut')}</th><th className="px-4 py-2 text-right">{tx('Bant konumu')}</th><th className="px-4 py-2">{tx('Öneri')}</th><th className="px-4 py-2">{tx('Durum')}</th></tr></thead>
          <tbody className="divide-y divide-border">
            {d.rows.map((r) => (
              <tr key={r.employeeId}>
                <td className="px-4 py-2">{r.name}<span className="block text-[11.5px] text-muted-foreground">{r.department ?? '—'}</span></td>
                <td className="px-4 py-2">{r.grade ?? '—'}</td>
                <td className="px-4 py-2 text-right tabular-nums">{formatMoney(r.currentSalary, r.currency)}</td>
                <td className="px-4 py-2 text-right">{r.compaRatio !== null ? r.compaRatio.toFixed(2) : '—'}{r.outOfBand && <StatusBadge tone="warning" className="ml-2">{tx('Bant dışı')}</StatusBadge>}</td>
                <td className="px-4 py-2"><ProposalCell cycleId={cycle.id} row={r} editable={editable} /></td>
                <td className="px-4 py-2">{r.proposal ? <StatusBadge tone={r.proposal.status === 'Approved' || r.proposal.status === 'Applied' ? 'success' : r.proposal.status === 'Rejected' ? 'danger' : 'warning'}>{{ Proposed: tx('Önerildi'), Approved: tx('Onaylandı'), Rejected: tx('Reddedildi'), Applied: tx('Uygulandı') }[r.proposal.status]}</StatusBadge> : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </PanelBody></Panel>
      {admin && summary.data && (
        <Panel>
          <PanelHead title={tx('Bölüm özeti')} note={tx('5 kişiden az gruplar gizlenir.')} />
          <PanelBody className="p-0"><ul className="divide-y divide-border text-[13px]">
            {summary.data.groups.map((g) => <li key={g.department} className="flex justify-between px-5 py-2"><span>{g.department}</span><span className="text-muted-foreground">{g.hidden ? tx('gizli ({0} kişi)', [g.count]) : tx('ortalama %{0} · {1} kişi', [formatNumber(g.avgPercent ?? 0), g.count])}</span></li>)}
          </ul></PanelBody>
        </Panel>
      )}
    </div>
  )
}

export function RaiseCyclesPage() {
  const admin = useIsPayrollAdmin()
  const { hasRole } = useAuth()
  const allowed = admin || hasRole('manager')
  const q = useQuery({ queryKey: ['raise', 'cycles'], queryFn: ({ signal }) => payrollExtrasApi.cycles(signal), enabled: allowed })
  const [sel, setSel] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const current = q.data?.find((c) => c.id === sel) ?? q.data?.[0]
  const confirm = useConfirm()
  // Yalnızca öneri girilmemiş ve uygulanmamış dönem silinebilir; sunucu öneri varsa 409 döner.
  const remove = useAction((id: string) => payrollExtrasApi.deleteCycle(id), { success: tx('Zam dönemi silindi'), invalidate: [['raise']], onDone: () => setSel(null) })
  if (!allowed) return <EmptyState icon={TrendingUp} title={tx('Zam dönemi')} detail={tx('Bu ekran yalnızca yöneticilere ve İK\'ya açıktır.')} />
  return (
    <>
      <PageHeader title={tx('Zam dönemi')} description={tx('Yöneticiler bölümleri için öneri yapar, İK bütçe ve ücret bantlarına göre onaylar ve uygular.')}
        actions={admin ? <Button onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Yeni dönem')}</Button> : undefined} />
      {q.isPending ? <RowsSkeleton /> : !current ? <EmptyState icon={TrendingUp} title={tx('Zam dönemi yok')} detail={admin ? tx('Yeni bir dönem oluşturun.') : tx('İK bir zam dönemi açtığında burada görünür.')} /> : (
        <>
          <div className="mb-4 flex flex-wrap items-end gap-3">
            <div className="w-80"><SelectField label={tx('Dönem')} value={current.id} onChange={setSel} options={q.data!.map((c) => ({ value: c.id, label: `${c.name} · ${{ Draft: tx('Taslak'), Open: tx('Açık'), Closed: tx('Kapandı') }[c.status]}` }))} /></div>
            {admin && !current.appliedAt && (
              <Button variant="ghost" className="text-destructive" disabled={remove.isPending} onClick={async () => {
                if (await confirm({
                  title: tx('"{0}" dönemi silinsin mi?', [current.name]),
                  note: tx('Yalnızca öneri girilmemiş dönem silinebilir. Öneri varsa dönemi silmek yerine kapatın.'),
                  action: tx('Sil'),
                })) remove.mutate(current.id)
              }}>{tx('Dönemi sil')}</Button>
            )}
          </div>
          <Worksheet key={current.id} cycle={current} />
        </>
      )}
      {creating && <CycleModal onClose={() => setCreating(false)} />}
    </>
  )
}
