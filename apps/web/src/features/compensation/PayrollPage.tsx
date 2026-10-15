import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { Calculator, Lock, LockOpen, Plus, Printer, ShieldAlert, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { Modal } from '@/components/ui/Modal'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { useAuth } from '@/auth/useAuth'
import { useDirectory } from '@/api/directory'
import { payrollApi, type Payslip, type PayrollPeriod, type PayrollPeriodStatus } from '@/api/payroll'
import { formatDateTime, formatMoney } from '@/lib/format'
import { Metric, PersonSelect, useAction } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'
import { printPayslip } from './payslipPrint'
import { AdvancesAdminPanel, PayrollExportsPanel } from './PayrollExtras'
import { PayrollAnomalyPanel } from './PayrollAnomalyPanel'
import { BandCompliancePanel, EPayslipPanel, PayrollSettingsPanel, RetroPanel, SeverancePanel } from './PayrollTr'
import { PayrollParametersPanel } from './PayrollParametersPanel'
import { mlInsightsApi } from '@/api/mlInsights'
import { worstSeverity } from '@/lib/mlInsights'
import { severityLabel, severityTone } from '@/lib/expenseAudit'

export const monthName = (m: number) => new Date(2026, m - 1, 1).toLocaleDateString(appLocale, { month: 'long' })
const periodLabel = (p: { year: number; month: number }) => `${monthName(p.month)} ${p.year}`

const STATUS: Record<PayrollPeriodStatus, { label: string; tone: 'neutral' | 'info' | 'success' }> = {
  Open: { label: tx('Açık'), tone: 'neutral' },
  Calculated: { label: tx('Hesaplandı'), tone: 'info' },
  Closed: { label: tx('Kapandı'), tone: 'success' },
}

function useIsPayrollAdmin() {
  const { hasRole } = useAuth()
  return hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')
}

/* ------------------------------------------------------------------ dönem listesi */

function NewPeriodModal({ onClose }: { onClose: () => void }) {
  const nav = useNavigate()
  const now = new Date()
  const [year, setYear] = useState(String(now.getFullYear()))
  const [month, setMonth] = useState(String(now.getMonth() + 1))
  const create = useAction(() => payrollApi.createPeriod(Number(year), Number(month)), {
    success: tx('Dönem açıldı'), invalidate: [['payroll']], onDone: (p) => nav(`/panel/bordro/${p.id}`),
  })
  return (
    <Modal open onClose={onClose} title={tx('Yeni bordro dönemi')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => create.mutate(undefined)} disabled={create.isPending}>{tx('Dönemi aç')}</Button></>}>
      <div className="grid gap-3 sm:grid-cols-2">
        <SelectField label={tx('Yıl')} value={year} onChange={setYear} options={[-1, 0, 1].map((d) => ({ value: String(now.getFullYear() + d), label: String(now.getFullYear() + d) }))} />
        <SelectField label={tx('Ay')} value={month} onChange={setMonth} options={Array.from({ length: 12 }, (_, i) => ({ value: String(i + 1), label: monthName(i + 1) }))} />
      </div>
    </Modal>
  )
}

function PeriodList() {
  const nav = useNavigate()
  const admin = useIsPayrollAdmin()
  const [year, setYear] = useState(String(new Date().getFullYear()))
  const [adding, setAdding] = useState(false)
  const q = useQuery({ queryKey: ['payroll', 'periods', year], queryFn: ({ signal }) => payrollApi.periods(Number(year), signal) })
  const columns: Array<Column<PayrollPeriod>> = [
    { id: 'p', header: tx('Dönem'), cell: (p) => <span className="font-medium">{periodLabel(p)}</span>, sortValue: (p) => p.year * 100 + p.month, searchText: periodLabel },
    { id: 's', header: tx('Durum'), cell: (p) => (
      <span className="flex flex-wrap gap-1.5">
        <StatusBadge tone={STATUS[p.status].tone}>{STATUS[p.status].label}</StatusBadge>
        {p.status !== 'Closed' && p.parametersConfigured === false && <StatusBadge tone="warning">{tx('Parametre girilmedi')}</StatusBadge>}
      </span>
    ) },
    { id: 'n', header: tx('Çalışan'), cell: (p) => p.employeeCount, align: 'right', hideBelow: 'sm' },
    { id: 'g', header: tx('Toplam brüt'), cell: (p) => formatMoney(p.totalGross), align: 'right', hideBelow: 'md' },
    { id: 'net', header: tx('Toplam net'), cell: (p) => formatMoney(p.totalNet), align: 'right' },
    { id: 'c', header: tx('İşveren maliyeti'), cell: (p) => formatMoney(p.totalEmployerCost), align: 'right', hideBelow: 'md' },
  ]
  const now = new Date().getFullYear()
  return (
    <>
      <DataTable rows={q.data} rowKey={(p) => p.id} columns={columns} isLoading={q.isPending} error={q.error} onRetry={() => void q.refetch()}
        onRowClick={(p) => nav(`/panel/bordro/${p.id}`)} initialSort={{ columnId: 'p', dir: 'desc' }}
        emptyTitle={tx('Bu yıl için bordro dönemi yok')} emptyDetail={tx('Yeni dönem açıp hesaplayın; kapattığınızda çalışanlar pusulalarını görür.')}
        filters={[{ id: 'y', label: tx('Yıl'), value: year, onChange: setYear, options: [now - 2, now - 1, now, now + 1].map((y) => ({ value: String(y), label: String(y) })) }]}
        toolbarActions={admin ? <Button size="sm" onClick={() => setAdding(true)}><Plus className="size-4" /> {tx('Yeni dönem')}</Button> : undefined} />
      {adding && <NewPeriodModal onClose={() => setAdding(false)} />}
    </>
  )
}

type PayrollTab = 'periods' | 'params' | 'advances' | 'retro' | 'severance' | 'bands' | 'settings'

export function PayrollPage() {
  const [tab, setTab] = useTabParam<PayrollTab>('bolum', 'periods')
  const tabs: Array<{ key: PayrollTab; label: string }> = [
    { key: 'periods', label: tx('Dönemler') }, { key: 'params', label: tx('Parametreler') }, { key: 'advances', label: tx('Avanslar') },
    { key: 'retro', label: tx('Fark bordrosu') }, { key: 'severance', label: tx('Kıdem ve ihbar') }, { key: 'bands', label: tx('Bant uyumu') },
    { key: 'settings', label: tx('SGK ve dosya ayarları') },
  ]
  return (
    <>
      <PageHeader title={tx('Bordro')} description={tx('Aylık bordro dönemi: hesaplama, ek ödeme ve kesintiler, dönem kapatma ve bordro pusulası.')} />
      <div className="mb-4"><Tabs label={tx('Bölüm')} value={tab} onChange={setTab} tabs={tabs} /></div>
      {tab === 'periods' ? <PeriodList /> : tab === 'params' ? <PayrollParametersPanel /> : tab === 'advances' ? <AdvancesAdminPanel />
        : tab === 'retro' ? <RetroPanel /> : tab === 'severance' ? <SeverancePanel /> : tab === 'bands' ? <BandCompliancePanel /> : <PayrollSettingsPanel />}
      <div className="mt-4"><InfoNote>{tx('KVKK: Bordro pusulasını yalnızca çalışanın kendisi (dönem kapandıktan sonra) ve bordro yetkilisi görür. Bordro listesinin açılması erişim kaydına yazılır; pusulalar 10 yıl saklanıp imha edilir.')}</InfoNote></div>
    </>
  )
}

/* ------------------------------------------------------------------ dönem ayrıntısı */

function AdjustmentsPanel({ period, editable }: { period: PayrollPeriod; editable: boolean }) {
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['payroll', 'adj', period.id], queryFn: ({ signal }) => payrollApi.adjustments(period.id, signal) })
  const [emp, setEmp] = useState('')
  const [kind, setKind] = useState<'Addition' | 'Deduction'>('Addition')
  const [amount, setAmount] = useState('')
  const [desc, setDesc] = useState('')
  const add = useAction(() => payrollApi.addAdjustment(period.id, { employeeId: emp, kind, amount: Number(amount.replace(',', '.')), description: desc }), {
    // Ek ödeme değişince dönem "Açık"a döner (yeniden hesaplanmalı); dönem listesi de tazelenir.
    success: tx('Eklendi; dönemi yeniden hesaplayın'), invalidate: [['payroll']], onDone: () => { setAmount(''); setDesc('') },
  })
  const del = useAction((id: string) => payrollApi.deleteAdjustment(period.id, id), { success: tx('Silindi; dönemi yeniden hesaplayın'), invalidate: [['payroll']] })
  const confirm = useConfirm()
  return (
    <Panel>
      <PanelHead title={tx('Ek ödeme ve kesintiler')} note={tx('Prim, ikramiye (brüte eklenir, vergilendirilir) ya da avans taksiti gibi netten kesintiler.')} />
      <PanelBody className="space-y-4">
        {editable && (
          <div className="grid gap-3 md:grid-cols-[1.4fr_1fr_0.8fr_1.4fr_auto] md:items-end">
            <PersonSelect value={emp} onChange={setEmp} />
            <SelectField label={tx('Tür')} value={kind} onChange={(v) => setKind(v as typeof kind)} options={[{ value: 'Addition', label: tx('Ek ödeme') }, { value: 'Deduction', label: tx('Kesinti') }]} />
            <TextField label={tx('Tutar')} inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
            <TextField label={tx('Açıklama')} value={desc} onChange={(e) => setDesc(e.target.value)} maxLength={200} />
            <Button disabled={!emp || !amount || !desc.trim() || add.isPending} onClick={() => add.mutate(undefined)}>{tx('Ekle')}</Button>
          </div>
        )}
        {!q.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Bu dönemde ek ödeme ya da kesinti yok.')}</p> : (
          <ul className="divide-y divide-border text-[13px]">
            {q.data.map((a) => (
              <li key={a.id} className="flex items-center gap-3 py-2">
                <span className="min-w-0 flex-1">{nameOf(a.employeeId)} · {a.description}</span>
                <StatusBadge tone={a.kind === 'Addition' ? 'success' : 'warning'}>{a.kind === 'Addition' ? tx('Ek ödeme') : tx('Kesinti')}</StatusBadge>
                <span className="w-28 text-right tabular-nums">{formatMoney(a.amount)}</span>
                {editable && <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={async () => {
                  if (await confirm({ title: tx('Kayıt silinsin mi?'), note: `${nameOf(a.employeeId)} · ${a.description} · ${formatMoney(a.amount)}`, action: tx('Sil') })) del.mutate(a.id)
                }}><Trash2 className="size-4" /></Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

export function PayslipBreakdown({ s }: { s: Payslip }) {
  const m = (v: number) => formatMoney(v, s.currency)
  const rows: Array<[string, string, boolean?]> = [
    [tx('Aylık brüt ücret'), m(s.monthlyBaseGross)],
    [tx('Ödenen gün / eksik gün'), `${s.paidDays} / ${s.unpaidDays}`],
    [tx('Dönem ücreti'), m(s.baseGross)],
    ...(s.overtimePay ? [[tx('Fazla mesai ({0} saat)', [s.overtimeHours]), m(s.overtimePay)] as [string, string]] : []),
    ...(s.additions ? [[tx('Ek ödemeler'), m(s.additions)] as [string, string]] : []),
    [tx('Toplam brüt'), m(s.gross), true],
    [tx('SGK işçi payı'), `− ${m(s.sgkEmployee)}`],
    [tx('İşsizlik sigortası işçi payı'), `− ${m(s.unemploymentEmployee)}`],
    [tx('Gelir vergisi matrahı'), m(s.taxBase)],
    [tx('Kümülatif matrah'), m(s.cumulativeTaxBase)],
    [tx('Gelir vergisi'), `− ${m(s.incomeTax - s.incomeTaxExemption)}`],
    [tx('  (hesaplanan {0}, asgari ücret istisnası {1})', [m(s.incomeTax), m(s.incomeTaxExemption)]), ''],
    [tx('Damga vergisi'), `− ${m(s.stampTax - s.stampTaxExemption)}`],
    ...(s.deductions ? [[tx('Kesintiler'), `− ${m(s.deductions)}`] as [string, string]] : []),
    [tx('Net ödenecek'), m(s.net), true],
  ]
  return (
    <dl className="grid grid-cols-[1fr_auto] gap-x-6 gap-y-1.5 text-[13px]">
      {rows.map(([k, v, strong], i) => (
        <div key={i} className="contents">
          <dt className={strong ? 'font-semibold' : k.startsWith('  ') ? 'pl-3 text-[12px] text-muted-foreground' : 'text-muted-foreground'}>{k.trim()}</dt>
          <dd className={strong ? 'text-right font-semibold tabular-nums' : 'text-right tabular-nums'}>{v}</dd>
        </div>
      ))}
    </dl>
  )
}

export function PayrollPeriodPage() {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const admin = useIsPayrollAdmin()
  const { hasRole } = useAuth()
  const dir = useDirectory()
  const nameOf = (eid: string) => dir.data?.find((d) => d.id === eid)?.fullName ?? tx('(ayrılmış çalışan)')
  const periods = useQuery({ queryKey: ['payroll', 'periods', 'all'], queryFn: ({ signal }) => payrollApi.periods(undefined, signal) })
  const period = periods.data?.find((p) => p.id === id)
  const slips = useQuery({ queryKey: ['payroll', 'slips', id], queryFn: ({ signal }) => payrollApi.payslips(id, signal), enabled: !!period && period.status !== 'Open' })
  // Ürün kararı: parametreleri bordro yetkilisi girer. Dönemin ayı için satır yoksa hesaplama yerine uyarı gösterilir.
  const params = useQuery({
    queryKey: ['payroll', 'params', period?.year, 'month', period?.month],
    queryFn: ({ signal }) => payrollApi.parameters(period!.year, period!.month, signal),
    enabled: !!period && period.status !== 'Closed', retry: false,
  })
  const paramsMissing = params.data ? !params.data.configured : false
  const [open, setOpen] = useState<Payslip | null>(null)
  const [reopening, setReopening] = useState(false)
  const [reason, setReason] = useState('')
  const inv = [['payroll']]
  const calc = useAction(() => payrollApi.calculate(id), {
    success: (r) => (r.anomalyFlags ? tx('{0} çalışan için hesaplandı; bordro denetimi {1} işaret üretti', [r.employeeCount, r.anomalyFlags]) : tx('{0} çalışan için hesaplandı', [r.employeeCount]))
      // Madde 72: puantaj dönemi kapatılmadıysa fazla mesai sonradan değişebilir.
      + (r.timesheetLocked === false ? ` · ${tx('Uyarı: bu ayın puantaj dönemi kapatılmadı; fazla mesai değişebilir')}` : ''),
    invalidate: inv,
  })
  // ML dalgası 2: bordro denetim işaretleri (yalnızca bordro yetkilisi; kapatmayı engellemez).
  const anomalies = useQuery({ queryKey: ['payroll', 'anomalies', id], queryFn: ({ signal }) => mlInsightsApi.payrollAnomalies(id, signal), enabled: !!period && period.status !== 'Open', retry: false })
  const flagsBySlip = useMemo(() => new Map((anomalies.data?.items ?? []).map((i) => [i.payslipId, i.flags])), [anomalies.data])
  const flaggedCount = anomalies.data?.items.length ?? 0
  const close = useAction(() => payrollApi.close(id), { success: tx('Dönem kapatıldı; pusulalar çalışanlara açıldı'), invalidate: inv })
  const reopen = useAction(() => payrollApi.reopen(id, reason), { success: tx('Dönem yeniden açıldı'), invalidate: inv, onDone: () => setReopening(false) })
  const remove = useAction(() => payrollApi.deletePeriod(id), { success: tx('Dönem silindi'), invalidate: inv, onDone: () => nav('/panel/bordro') })
  const confirm = useConfirm()
  const totals = useMemo(() => (slips.data ?? []).reduce((a, s) => ({ gross: a.gross + s.gross, net: a.net + s.net, cost: a.cost + s.employerCost, tax: a.tax + s.incomeTax - s.incomeTaxExemption }), { gross: 0, net: 0, cost: 0, tax: 0 }), [slips.data])

  if (periods.isPending) return <RowsSkeleton rows={4} />
  if (!period) return <ErrorState title={tx('Dönem bulunamadı')} message={tx('Dönem silinmiş olabilir.')} />
  const closed = period.status === 'Closed'
  const columns: Array<Column<Payslip>> = [
    { id: 'e', header: tx('Çalışan'), cell: (s) => <span className="font-medium">{nameOf(s.employeeId)}</span>, searchText: (s) => nameOf(s.employeeId), sortValue: (s) => nameOf(s.employeeId) },
    { id: 'd', header: tx('Gün'), cell: (s) => s.paidDays, align: 'right', hideBelow: 'md' },
    { id: 'g', header: tx('Brüt'), cell: (s) => formatMoney(s.gross), align: 'right', sortValue: (s) => s.gross },
    { id: 'k', header: tx('Kesintiler'), cell: (s) => formatMoney(s.gross - s.net), align: 'right', hideBelow: 'sm' },
    { id: 'n', header: tx('Net'), cell: (s) => formatMoney(s.net), align: 'right', sortValue: (s) => s.net },
    { id: 'c', header: tx('İşveren maliyeti'), cell: (s) => formatMoney(s.employerCost), align: 'right', hideBelow: 'lg' },
    {
      id: 'a', header: tx('Denetim'), align: 'right', hideBelow: 'sm',
      sortValue: (s) => flagsBySlip.get(s.id)?.length ?? 0,
      cell: (s) => {
        const fl = flagsBySlip.get(s.id)
        const w = fl ? worstSeverity(fl) : null
        return fl && w ? <StatusBadge tone={severityTone[w]}><ShieldAlert className="size-3.5" /> {fl.length} · {severityLabel(w)}</StatusBadge> : null
      },
    },
  ]
  return (
    <>
      <PageHeader title={tx('Bordro — {0}', [periodLabel(period)])}
        description={closed ? tx('Kapandı: {0} ({1})', [formatDateTime(period.closedAt), period.closedBy ?? '—'])
          : period.calculatedBy ? tx('Hazırlayan: {0}. Kapanan dönem değiştirilemez.', [period.calculatedBy])
          : tx('Hesapla → kontrol et → kapat. Kapanan dönem değiştirilemez.')}
        actions={
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" asChild><Link to="/panel/bordro">{tx('Dönemler')}</Link></Button>
            {admin && !closed && <Button onClick={() => calc.mutate(undefined)} disabled={calc.isPending || paramsMissing}><Calculator className="size-4" /> {period.status === 'Open' ? tx('Hesapla') : tx('Yeniden hesapla')}</Button>}
            {admin && period.status === 'Calculated' && <Button variant="outline" disabled={close.isPending} onClick={async () => {
              if (await confirm({ title: tx('Dönem kapatılsın mı?'), note: tx('{0} bordrosu kesinleşir; pusulalar çalışanlara açılır ve dönem bir daha değiştirilemez (yalnızca şirket yöneticisi gerekçeyle yeniden açabilir).', [periodLabel(period)]) + (flaggedCount ? ' ' + tx('Bordro denetimi {0} çalışan için işaret üretti; kapatmadan önce incelediğinizden emin olun.', [flaggedCount]) : ''), action: tx('Dönemi kapat') })) close.mutate(undefined)
            }}><Lock className="size-4" /> {tx('Dönemi kapat')}</Button>}
            {closed && (hasRole('tenant-admin') || hasRole('platform-admin')) && <Button variant="outline" onClick={() => setReopening(true)}><LockOpen className="size-4" /> {tx('Yeniden aç')}</Button>}
            {admin && !closed && <Button variant="ghost" onClick={async () => {
              if (await confirm({ title: tx('Dönem silinsin mi?'), note: tx('{0} dönemi, hesaplanmış pusulaları, ek ödeme/kesintileri, e-bordro yayın kayıtları ve dışa aktarım dosyalarıyla birlikte silinir. Çalışanların teslim aldığı e-bordro varsa dönem silinemez.', [periodLabel(period)]), action: tx('Sil') })) remove.mutate(undefined)
            }}><Trash2 className="size-4" /> {tx('Sil')}</Button>}
          </div>
        } />
      {!closed && paramsMissing && (
        <div role="alert" className="mb-5 rounded-xl border border-[hsl(var(--warning))]/35 bg-[hsl(var(--warning))]/[0.08] px-4 py-3 text-[13px] leading-relaxed">
          <p className="font-semibold">{tx('{0} için bordro parametreleri girilmemiş', [periodLabel(period)])}</p>
          <p className="mt-0.5 text-muted-foreground">{tx('Dönem, bu ay için geçerli parametreler (asgari ücret, oranlar, vergi dilimleri) girilene kadar hesaplanamaz. Değerleri resmî kaynaklardan alıp Bordro › Parametreler sekmesinde girin.')}</p>
          {admin && <Button size="sm" variant="outline" className="mt-2" asChild><Link to={`/panel/bordro?bolum=params&yil=${period.year}`}>{tx('Parametreleri gir')}</Link></Button>}
        </div>
      )}
      {!closed && params.data?.configured && params.data.seed && (
        <div className="mb-5"><InfoNote>{tx('Bu dönemin parametreleri önceki sürümün kurulumda eklediği örnek değerlerdir ve resmî kaynakla doğrulanmadı. Hesaplamadan önce Bordro › Parametreler sekmesinde kontrol edip kaydedin.')}</InfoNote></div>
      )}
      <div className="mb-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Metric label={tx('Durum')} value={STATUS[period.status].label} />
        <Metric label={tx('Toplam brüt')} value={formatMoney(totals.gross)} />
        <Metric label={tx('Toplam net')} value={formatMoney(totals.net)} />
        <Metric label={tx('İşveren maliyeti')} value={formatMoney(totals.cost)} hint={tx('Ödenecek gelir vergisi {0}', [formatMoney(totals.tax)])} />
      </div>
      <div className="space-y-5">
        {period.status !== 'Open' && <PayrollAnomalyPanel query={anomalies} nameOf={nameOf} closed={closed} />}
        <AdjustmentsPanel period={period} editable={admin && !closed} />
        {admin && <PayrollExportsPanel periodId={period.id} closed={closed} calculated={period.status === 'Calculated'} />}
        {closed && <EPayslipPanel periodId={period.id} closed={closed} />}
        {period.status === 'Open'
          ? <InfoNote>{tx('Dönem henüz hesaplanmadı. Hesaplamaya onaylı ücretsiz izin günleri (eksik gün), onaylı fazla mesai ve yukarıdaki ek ödeme/kesintiler girer.')}</InfoNote>
          : <DataTable rows={slips.data} rowKey={(s) => s.id} columns={columns} isLoading={slips.isPending} error={slips.error} onRowClick={setOpen} exportFileName={`bordro-${period.year}-${period.month}`} />}
      </div>
      {open && (
        <Modal open onClose={() => setOpen(null)} title={tx('Bordro pusulası — {0}', [nameOf(open.employeeId)])} note={periodLabel(open)}
          footer={<Button variant="outline" onClick={() => printPayslip(open, nameOf(open.employeeId))}><Printer className="size-4" /> {tx('Yazdır / PDF')}</Button>}>
          <PayslipBreakdown s={open} />
        </Modal>
      )}
      {reopening && (
        <Modal open onClose={() => setReopening(false)} title={tx('Dönemi yeniden aç')} note={tx('Gerekçe denetim kaydına yazılır.')}
          footer={<><Button variant="outline" onClick={() => setReopening(false)}>{tx('Vazgeç')}</Button><Button disabled={reason.trim().length < 10 || reopen.isPending} onClick={() => reopen.mutate(undefined)}>{tx('Yeniden aç')}</Button></>}>
          <TextField label={tx('Gerekçe (en az 10 karakter)')} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Modal>
      )}
    </>
  )
}
