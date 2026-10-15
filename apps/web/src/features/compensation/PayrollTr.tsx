import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { BadgeCheck, Calculator, Check, FileSignature, FileText, Mail, Save, Send, ShieldCheck, X } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { useDirectory } from '@/api/directory'
import { payrollApi } from '@/api/payroll'
import { engagementApi } from '@/api/engagement'
import {
  payrollTrApi, type BankSettings, type BankTemplate, type CompaRow, type CoverageGroup, type EmployeeSgk, type ExitReason, type OpeningTaxBase,
  type PayrollSettings, type RetroCandidate, type SeveranceCalc, type SeverancePreview, type SeveranceRequest, type SgkSettings,
} from '@/api/payrollTr'
import { formatDate, formatDateTime, formatMoney, formatNumber, parseDecimal } from '@/lib/format'
import { Metric, PersonSelect, errMsg, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'
import {
  exitReasonLabel, formatCostCenters, integrityLabel, parseCostCenters, positionLabel, positionTone, retroKey, retroTotal, shortHash,
  validOccupationCode, validTrIban,
} from '@/lib/payrollTr'

function useIsPayrollAdmin() {
  const { hasRole } = useAuth()
  return hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')
}

const periodText = (y: number, m: number) => `${y}/${String(m).padStart(2, '0')}`

/* ================================================================== 58/62/64 ayarlar */

const bankTemplateLabels: Record<BankTemplate, string> = {
  generic: tx('HR360 genel CSV'),
  'ornek-a': tx('Örnek şablon A (noktalı virgüllü CSV)'),
  'ornek-b': tx('Örnek şablon B (sabit uzunluklu TXT)'),
  custom: tx('Özel düzen'),
}

const leaveTypeLabels: Record<string, string> = {
  Unpaid: tx('Ücretsiz izin'), Sick: tx('Hastalık (rapor)'), Maternity: tx('Doğum izni'), Annual: tx('Yıllık izin'),
  Paternity: tx('Babalık izni'), Marriage: tx('Evlilik izni'), Bereavement: tx('Ölüm izni'),
}

export function PayrollSettingsPanel() {
  const admin = useIsPayrollAdmin()
  const q = useQuery({ queryKey: ['payroll', 'settings'], queryFn: ({ signal }) => payrollTrApi.settings(signal) })
  const [sgk, setSgk] = useState<SgkSettings | null>(null)
  const [accounts, setAccounts] = useState<PayrollSettings['accounts'] | null>(null)
  const [cc, setCc] = useState('')
  const [bank, setBank] = useState<BankSettings | null>(null)
  const [columns, setColumns] = useState('')
  useEffect(() => {
    if (!q.data) return
    setSgk(q.data.sgk); setAccounts(q.data.accounts); setCc(formatCostCenters(q.data.costCenters)); setBank(q.data.bank)
    setColumns(q.data.bank.custom.columns.join(', '))
  }, [q.data])
  const save = useAction(() => payrollTrApi.saveSettings({
    sgk: sgk!, accounts: accounts!, costCenters: parseCostCenters(cc),
    bank: { ...bank!, debitIban: bank!.debitIban || null, companyCode: bank!.companyCode || null, custom: { ...bank!.custom, columns: columns.split(/[,\s]+/).filter(Boolean) } },
  }), { success: tx('Bordro ayarları kaydedildi'), invalidate: [['payroll', 'settings']] })
  if (q.isPending || !sgk || !accounts || !bank) return q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : <RowsSkeleton rows={6} />
  const setCode = (i: number, patch: Partial<SgkSettings['missingDayCodes'][number]>) =>
    setSgk({ ...sgk, missingDayCodes: sgk.missingDayCodes.map((m, j) => (j === i ? { ...m, ...patch } : m)) })
  const debitBad = !!bank.debitIban && !validTrIban(bank.debitIban)
  const accountFields: Array<[keyof PayrollSettings['accounts'], string]> = [
    ['salary', tx('Ücret giderleri')], ['employerSgk', tx('SGK işveren payı giderleri')], ['netPayable', tx('Personele borçlar')],
    ['incomeTax', tx('Ödenecek gelir vergisi')], ['stampTax', tx('Ödenecek damga vergisi')], ['sgk', tx('Ödenecek SGK primleri')],
    ['advances', tx('Personel avansları ve diğer kesintiler')],
  ]
  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={tx('SGK bildirimi (APHB)')} note={tx('Belge türü, kanun ve eksik gün nedeni kodları e-Bildirge listelerinden alınmıştır; yüklemeden önce SGK\'nın güncel listesiyle karşılaştırın.')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-3">
            <TextField label={tx('Varsayılan belge türü')} disabled={!admin} value={sgk.defaultDocumentType} onChange={(e) => setSgk({ ...sgk, defaultDocumentType: e.target.value })} hint={tx('01 = tüm sigorta kolları')} />
            <TextField label={tx('Varsayılan kanun no')} disabled={!admin} value={sgk.defaultLawNo} onChange={(e) => setSgk({ ...sgk, defaultLawNo: e.target.value })} hint={tx('05510 = teşviksiz')} />
            <TextField label={tx('İşyeri sicil no')} disabled={!admin} value={sgk.workplaceRegistryNo ?? ''} onChange={(e) => setSgk({ ...sgk, workplaceRegistryNo: e.target.value || null })} hint={tx('İsteğe bağlı')} />
          </div>
          <div>
            <p className="mb-2 text-[13px] font-medium">{tx('Eksik gün nedenleri (izin türü → SGK kodu)')}</p>
            <ul className="space-y-2">
              {sgk.missingDayCodes.map((m, i) => (
                <li key={i} className="grid grid-cols-[1.4fr_0.8fr_1.4fr_auto] items-end gap-2">
                  <SelectField label={tx('İzin türü')} disabled={!admin} value={m.leaveType} onChange={(v) => setCode(i, { leaveType: v })}
                    options={Object.keys(leaveTypeLabels).map((k) => ({ value: k, label: leaveTypeLabels[k]! }))} />
                  <TextField label={tx('Kod')} disabled={!admin} value={m.code} maxLength={2} onChange={(e) => setCode(i, { code: e.target.value })} />
                  <label className="flex h-9 items-center gap-2 text-[13px]">
                    <Checkbox disabled={!admin} checked={m.reducesPay} onCheckedChange={(v) => setCode(i, { reducesPay: v === true })} /> {tx('Ücretten düşer (eksik gün)')}
                  </label>
                  {admin && <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => setSgk({ ...sgk, missingDayCodes: sgk.missingDayCodes.filter((_, j) => j !== i) })}><X className="size-4" /></Button>}
                </li>
              ))}
            </ul>
            {admin && <Button className="mt-2" size="sm" variant="outline" onClick={() => setSgk({ ...sgk, missingDayCodes: [...sgk.missingDayCodes, { leaveType: 'Unpaid', code: '13', reducesPay: false }] })}>{tx('Satır ekle')}</Button>}
            <p className="mt-2 text-[12px] text-muted-foreground">{tx('"Ücretten düşer" işaretli izinler bordroda eksik gün sayılır (varsayılan yalnızca ücretsiz izin). Birden fazla neden varsa 12 yazılır. Hastalık kodu SGK bildirimi için yasal zorunluluktur; tanı bilgisi tutulmaz.')}</p>
          </div>
          <div className="grid gap-3 sm:grid-cols-5">
            {(Object.keys(exitReasonLabel) as ExitReason[]).map((k) => (
              <TextField key={k} label={tx('Çıkış kodu: {0}', [exitReasonLabel[k]])} disabled={!admin} maxLength={2} value={sgk.exitReasonCodes[k] ?? ''}
                onChange={(e) => setSgk({ ...sgk, exitReasonCodes: { ...sgk.exitReasonCodes, [k]: e.target.value } })} />
            ))}
          </div>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Muhasebe hesap planı')} note={tx('Fiş borç = alacak denetiminden geçmezse dosya üretilmez. Masraf merkezi eşlemesi yoksa bölüm adı yazılır.')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {accountFields.map(([k, label]) => (
              <TextField key={k} label={label} disabled={!admin} value={accounts[k]} onChange={(e) => setAccounts({ ...accounts, [k]: e.target.value })} />
            ))}
          </div>
          <TextAreaField label={tx('Masraf merkezi kodları (her satıra "Bölüm = KOD")')} rows={4} disabled={!admin} value={cc} onChange={(e) => setCc(e.target.value)} />
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Banka toplu ödeme dosyası')} note={tx('Örnek şablonlar yaygın banka dosyalarına benzer ama hiçbir bankanın resmî biçimi olarak doğrulanmamıştır; bankanızın teknik dokümanıyla karşılaştırın.')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <SelectField label={tx('Şablon')} disabled={!admin} value={bank.template} onChange={(v) => setBank({ ...bank, template: v as BankTemplate })}
              options={(Object.keys(bankTemplateLabels) as BankTemplate[]).map((k) => ({ value: k, label: bankTemplateLabels[k] }))} />
            <TextField label={tx('Açıklama kalıbı')} disabled={!admin} value={bank.description} onChange={(e) => setBank({ ...bank, description: e.target.value })} hint={tx('{yil} ve {ay} yer tutucuları')} />
            <TextField label={tx('Borçlu hesap IBAN')} disabled={!admin} value={bank.debitIban ?? ''} onChange={(e) => setBank({ ...bank, debitIban: e.target.value })} error={debitBad ? tx('IBAN geçersiz') : undefined} />
            <TextField label={tx('Firma/müşteri kodu')} disabled={!admin} value={bank.companyCode ?? ''} onChange={(e) => setBank({ ...bank, companyCode: e.target.value })} />
          </div>
          {bank.template === 'custom' && (
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <SelectField label={tx('Alan ayırıcı')} disabled={!admin} value={bank.custom.delimiter} onChange={(v) => setBank({ ...bank, custom: { ...bank.custom, delimiter: v } })}
                options={[{ value: ';', label: ';' }, { value: ',', label: ',' }, { value: '|', label: '|' }, { value: '\t', label: tx('Sekme') }]} />
              <SelectField label={tx('Ondalık ayırıcı')} disabled={!admin} value={bank.custom.decimalSeparator} onChange={(v) => setBank({ ...bank, custom: { ...bank.custom, decimalSeparator: v } })}
                options={[{ value: '.', label: '.' }, { value: ',', label: ',' }]} />
              <SelectField label={tx('Karakter kodlaması')} disabled={!admin} value={bank.custom.encoding} onChange={(v) => setBank({ ...bank, custom: { ...bank.custom, encoding: v as BankSettings['custom']['encoding'] } })}
                options={[{ value: 'utf-8-bom', label: 'UTF-8 (BOM)' }, { value: 'utf-8', label: 'UTF-8' }, { value: 'iso-8859-9', label: 'ISO-8859-9' }, { value: 'windows-1254', label: 'Windows-1254' }]} />
              <TextField label={tx('Tarih biçimi')} disabled={!admin} value={bank.custom.dateFormat} onChange={(e) => setBank({ ...bank, custom: { ...bank.custom, dateFormat: e.target.value } })} hint="dd.MM.yyyy" />
              <TextField className="lg:col-span-2" label={tx('Sütunlar (virgülle)')} disabled={!admin} value={columns} onChange={(e) => setColumns(e.target.value)}
                hint={tx('Kullanılabilir: {0}. iban ve amount zorunlu.', [(q.data?.bankFields ?? []).join(', ')])} />
              <label className="flex items-center gap-2 text-[13px]"><Checkbox disabled={!admin} checked={bank.custom.header} onCheckedChange={(v) => setBank({ ...bank, custom: { ...bank.custom, header: v === true } })} /> {tx('Başlık satırı')}</label>
              <label className="flex items-center gap-2 text-[13px]"><Checkbox disabled={!admin} checked={bank.custom.totalsLine} onCheckedChange={(v) => setBank({ ...bank, custom: { ...bank.custom, totalsLine: v === true } })} /> {tx('Toplam satırı')}</label>
              <label className="flex items-center gap-2 text-[13px]"><Checkbox disabled={!admin} checked={bank.custom.ascii} onCheckedChange={(v) => setBank({ ...bank, custom: { ...bank.custom, ascii: v === true } })} /> {tx('Türkçe karakterleri ASCII\'ye çevir')}</label>
            </div>
          )}
        </PanelBody>
      </Panel>
      {admin && <Button onClick={() => save.mutate(undefined)} disabled={save.isPending || debitBad}><Save className="size-4" /> {tx('Ayarları kaydet')}</Button>}
      {q.data?.updatedBy && <p className="text-[12px] text-muted-foreground">{tx('Son değişiklik: {0} ({1})', [q.data.updatedBy, formatDateTime(q.data.updatedAt)])}</p>}
      <EmployeeSgkPanel />
      <OpeningTaxBasePanel />
    </div>
  )
}

/* ================================================================== 58 çalışan SGK bilgileri */

function SgkEditModal({ row, name, onClose }: { row: EmployeeSgk; name: string; onClose: () => void }) {
  const [f, setF] = useState({ occupationCode: row.occupationCode ?? '', documentType: row.documentType ?? '', lawNo: row.lawNo ?? '', sgdp: row.sgdp })
  const bad = !!f.occupationCode && !validOccupationCode(f.occupationCode)
  const save = useAction(() => payrollTrApi.saveSgkEmployee(row.employeeId, {
    occupationCode: f.occupationCode.trim() || null, documentType: f.documentType.trim() || null, lawNo: f.lawNo.trim() || null, sgdp: f.sgdp,
  }), { success: tx('SGK bilgileri kaydedildi'), invalidate: [['payroll', 'sgk-employees']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('SGK bilgileri — {0}', [name])} note={tx('Belge türü ve kanun boşsa şirket varsayılanı kullanılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={bad || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="grid gap-3 sm:grid-cols-3">
        <TextField label={tx('Meslek kodu')} value={f.occupationCode} onChange={(e) => setF({ ...f, occupationCode: e.target.value })} error={bad ? tx('0000.00 biçiminde olmalı') : undefined} hint="2512.01" />
        <TextField label={tx('Belge türü')} value={f.documentType} maxLength={2} onChange={(e) => setF({ ...f, documentType: e.target.value })} />
        <TextField label={tx('Kanun no')} value={f.lawNo} maxLength={5} onChange={(e) => setF({ ...f, lawNo: e.target.value })} />
      </div>
      <label className="mt-3 flex items-center gap-2 text-[13px]"><Checkbox checked={f.sgdp} onCheckedChange={(v) => setF({ ...f, sgdp: v === true })} /> {tx('SGDP kapsamında (emekli olup çalışıyor; belge türü 02)')}</label>
    </Modal>
  )
}

export function EmployeeSgkPanel() {
  const admin = useIsPayrollAdmin()
  const dir = useDirectory()
  const q = useQuery({ queryKey: ['payroll', 'sgk-employees'], queryFn: ({ signal }) => payrollTrApi.sgkEmployees(signal) })
  const [edit, setEdit] = useState<EmployeeSgk | null>(null)
  const byId = useMemo(() => new Map((q.data ?? []).map((x) => [x.employeeId, x])), [q.data])
  type Row = { id: string; name: string; info: EmployeeSgk }
  const rows: Row[] = (dir.data ?? []).map((d) => ({ id: d.id, name: d.fullName, info: byId.get(d.id) ?? { employeeId: d.id, occupationCode: null, documentType: null, lawNo: null, sgdp: false } }))
  const missing = rows.filter((r) => !r.info.occupationCode).length
  const columns: Array<Column<Row>> = [
    { id: 'n', header: tx('Çalışan'), cell: (r) => <span className="font-medium">{r.name}</span>, searchText: (r) => r.name, sortValue: (r) => r.name },
    { id: 'o', header: tx('Meslek kodu'), cell: (r) => r.info.occupationCode ?? <StatusBadge tone="warning">{tx('Eksik')}</StatusBadge>, searchText: (r) => r.info.occupationCode ?? '' },
    { id: 'd', header: tx('Belge / kanun'), cell: (r) => `${r.info.documentType ?? '—'} / ${r.info.lawNo ?? '—'}`, hideBelow: 'sm' },
    { id: 's', header: 'SGDP', cell: (r) => (r.info.sgdp ? <StatusBadge tone="info">{tx('Evet')}</StatusBadge> : ''), hideBelow: 'sm' },
  ]
  return (
    <Panel>
      <PanelHead title={tx('Çalışanların SGK bilgileri')} note={missing ? tx('{0} çalışanın meslek kodu eksik (APHB doğrulamasında uyarı verir).', [missing]) : tx('Tüm çalışanların meslek kodu girilmiş.')} />
      <PanelBody className="p-0">
        <DataTable rows={rows} rowKey={(r) => r.id} columns={columns} isLoading={q.isPending || dir.isPending} error={q.error}
          onRowClick={admin ? (r) => setEdit(r.info) : undefined} emptyTitle={tx('Çalışan yok')} />
      </PanelBody>
      {edit && <SgkEditModal row={edit} name={rows.find((r) => r.id === edit.employeeId)?.name ?? ''} onClose={() => setEdit(null)} />}
    </Panel>
  )
}

/* ================================================================== devreden kümülatif GV matrahı */

function OpeningTaxBaseModal({ row, name, year, onClose }: { row: OpeningTaxBase; name: string; year: number; onClose: () => void }) {
  const [amount, setAmount] = useState(row.amount ? formatNumber(row.amount) : '')
  const [note, setNote] = useState(row.note ?? '')
  const parsed = amount.trim() ? parseDecimal(amount) : 0
  const bad = parsed === null || parsed < 0
  const save = useAction(() => payrollTrApi.saveOpeningTaxBase(row.employeeId, year, { amount: parsed ?? 0, note: note.trim() || null }),
    { success: tx('Devreden matrah kaydedildi'), invalidate: [['payroll', 'opening-tax-bases', year]], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Devreden GV matrahı — {0}', [name])}
      note={tx('{0} yılında HR360 dışında (önceki işveren ya da yıl ortası geçiş öncesi) oluşan kümülatif gelir vergisi matrahı. Açık dönemler yeniden hesaplanınca girer; kapanmış dönemler değişmez. 0 girilirse kayıt silinir.', [year])}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={bad || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <TextField label={tx('Kümülatif GV matrahı (TL)')} inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} error={bad ? tx('Geçerli bir tutar girin') : undefined} />
        <TextField label={tx('Not (isteğe bağlı)')} maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} hint={tx('Ör. önceki işverenin bordrosu')} />
      </div>
    </Modal>
  )
}

export function OpeningTaxBasePanel() {
  const admin = useIsPayrollAdmin()
  const dir = useDirectory()
  const thisYear = new Date().getFullYear()
  const [year, setYear] = useState(thisYear)
  const q = useQuery({ queryKey: ['payroll', 'opening-tax-bases', year], queryFn: ({ signal }) => payrollTrApi.openingTaxBases(year, signal) })
  const [edit, setEdit] = useState<OpeningTaxBase | null>(null)
  const byId = useMemo(() => new Map((q.data ?? []).map((x) => [x.employeeId, x])), [q.data])
  type Row = { id: string; name: string; info: OpeningTaxBase }
  const rows: Row[] = (dir.data ?? []).map((d) => ({ id: d.id, name: d.fullName, info: byId.get(d.id) ?? { employeeId: d.id, year, amount: 0, note: null, updatedBy: null, updatedAt: '' } }))
  const columns: Array<Column<Row>> = [
    { id: 'n', header: tx('Çalışan'), cell: (r) => <span className="font-medium">{r.name}</span>, searchText: (r) => r.name, sortValue: (r) => r.name },
    { id: 'a', header: tx('Devreden matrah'), cell: (r) => (r.info.amount ? formatMoney(r.info.amount) : '—'), align: 'right', sortValue: (r) => r.info.amount },
    { id: 'u', header: tx('Son değişiklik'), cell: (r) => (r.info.updatedBy ? `${r.info.updatedBy} · ${formatDateTime(r.info.updatedAt)}` : ''), hideBelow: 'md' },
  ]
  return (
    <Panel>
      <PanelHead title={tx('Devreden kümülatif GV matrahı')}
        note={tx('Yıl ortasında HR360\'a geçildiyse ya da çalışan yıl içinde başka işverenden geldiyse, o yılın önceki matrahı burada girilir; bordro ve ihbar gelir vergisi dilimleri bu tutardan başlar. Ücret verisidir: görüntüleme ve değişiklik denetim kaydına yazılır.')}
        action={<div className="w-32"><SelectField label={tx('Yıl')} value={String(year)} onChange={(v) => setYear(Number(v))} options={[thisYear - 1, thisYear, thisYear + 1].map((y) => ({ value: String(y), label: String(y) }))} /></div>} />
      <PanelBody className="p-0">
        <DataTable rows={rows} rowKey={(r) => r.id} columns={columns} isLoading={q.isPending || dir.isPending} error={q.error}
          onRowClick={admin ? (r) => setEdit(r.info) : undefined} emptyTitle={tx('Çalışan yok')} />
      </PanelBody>
      {edit && <OpeningTaxBaseModal row={edit} year={year} name={rows.find((r) => r.id === edit.employeeId)?.name ?? ''} onClose={() => setEdit(null)} />}
    </Panel>
  )
}

/* ================================================================== 58 APHB doğrulama */

export function SgkValidationBox({ periodId }: { periodId: string }) {
  const [open, setOpen] = useState(false)
  const q = useQuery({ queryKey: ['payroll', 'sgk-validation', periodId], queryFn: ({ signal }) => payrollTrApi.sgkValidation(periodId, signal), enabled: open, retry: false })
  return (
    <div className="rounded-xl border border-border p-3">
      <div className="flex flex-wrap items-center gap-3">
        <ShieldCheck className="size-4 text-primary" />
        <span className="flex-1 text-[13px]">{tx('SGK bildirimi doğrulaması: eksik TCKN ve meslek kodu, gün > 30, PEK asgari ücretin altında / tavanın üstünde.')}</span>
        <Button size="sm" variant="outline" onClick={() => (open ? void q.refetch() : setOpen(true))} disabled={q.isFetching}>{tx('Doğrula')}</Button>
      </div>
      {open && (q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : (
        <div className="mt-3 space-y-3 text-[13px]">
          <div className="grid gap-3 sm:grid-cols-4">
            <Metric label={tx('Dosyaya girecek')} value={`${q.data.included} / ${q.data.payslips}`} />
            <Metric label={tx('Hata')} value={q.data.errors} tone={q.data.errors ? 'bad' : 'good'} />
            <Metric label={tx('Uyarı')} value={q.data.warnings} tone={q.data.warnings ? 'warn' : 'good'} />
            <Metric label={tx('Toplam PEK')} value={formatMoney(q.data.totals.pek)} hint={tx('{0} prim günü', [q.data.totals.days])} />
          </div>
          {q.data.documents.length > 0 && <p className="text-muted-foreground">{q.data.documents.map((d) => tx('Belge {0} / kanun {1}: {2} kişi', [d.documentType, d.lawNo, d.count])).join(' · ')}</p>}
          {q.data.issues.length === 0 ? <InfoNote>{tx('Sorun bulunmadı.')}</InfoNote> : (
            <ul className="divide-y divide-border rounded-xl border border-border">
              {q.data.issues.map((i, k) => (
                <li key={k} className="flex items-center gap-3 px-3 py-2">
                  <StatusBadge tone={i.level === 'error' ? 'danger' : 'warning'}>{i.level === 'error' ? tx('Hata') : tx('Uyarı')}</StatusBadge>
                  <span className="font-medium">{i.name}</span>
                  <span className="text-muted-foreground">{txServer(i.message)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      ))}
    </div>
  )
}

/* ================================================================== 59 e-bordro */

export function EPayslipPanel({ periodId, closed }: { periodId: string; closed: boolean }) {
  const admin = useIsPayrollAdmin()
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['payroll', 'epayslips', periodId], queryFn: ({ signal }) => payrollTrApi.ePayslipStatus(periodId, signal), enabled: closed })
  const publish = useAction(() => payrollTrApi.publishEPayslips(periodId), {
    success: (r) => tx('e-Bordro: {0} yeni, {1} yeniden yayımlandı, {2} değişmedi', [r.created, r.republished, r.unchanged]),
    invalidate: [['payroll', 'epayslips', periodId]],
  })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Mail className="size-4 text-primary" />{' '}{tx('e-Bordro')}</span>}
        note={tx('Pusulalar uygulamada yayımlanır; çalışana uygulama içi ve e-posta bildirimi gider (tutar yazmaz). Çalışan "Okudum, teslim aldım" ile onaylar; içerik özeti (SHA-256) kayda geçer.')}
        action={admin && closed ? <Button size="sm" onClick={() => publish.mutate(undefined)} disabled={publish.isPending}><Send className="size-4" /> {tx('Yayımla')}</Button> : undefined} />
      <PanelBody>
        {!closed ? <p className="text-[13px] text-muted-foreground">{tx('e-Bordro kapanmış dönem için yayımlanır.')}</p> : q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : (
          <div className="space-y-3">
            <div className="grid gap-3 sm:grid-cols-4">
              <Metric label={tx('Pusula')} value={q.data.payslips} />
              <Metric label={tx('Yayımlandı')} value={q.data.published} />
              <Metric label={tx('Açıldı')} value={q.data.opened} />
              <Metric label={tx('Teslim alındı')} value={q.data.acknowledged} tone={q.data.published && q.data.acknowledged === q.data.published ? 'good' : undefined} />
            </div>
            {q.data.items.length > 0 && (
              <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
                {q.data.items.map((i) => (
                  <li key={i.payslipId} className="flex flex-wrap items-center gap-3 px-3 py-2">
                    <span className="min-w-0 flex-1">{nameOf(i.employeeId)}<span className="block text-[12px] text-muted-foreground">{tx('Yayım: {0}', [formatDateTime(i.publishedAt)])}{i.emailQueued ? ` · ${tx('e-posta kuyruğa alındı')}` : ''}{i.firstOpenedAt ? ` · ${tx('ilk açılış: {0}', [formatDateTime(i.firstOpenedAt)])}` : ''}</span></span>
                    {i.integrity !== 'ok' && <StatusBadge tone="danger">{integrityLabel[i.integrity]}</StatusBadge>}
                    {i.acknowledgedAt ? <StatusBadge tone="success"><BadgeCheck className="size-3.5" /> {formatDateTime(i.acknowledgedAt)}</StatusBadge> : <StatusBadge tone="neutral">{i.firstOpenedAt ? tx('Okundu') : tx('Açılmadı')}</StatusBadge>}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

/** Çalışanın pusulasındaki e-bordro kutusu: yayım, içerik özeti ve "Okudum, teslim aldım". */
export function MyEPayslipBox({ payslipId }: { payslipId: string }) {
  const q = useQuery({ queryKey: ['payroll', 'my-epayslip', payslipId], queryFn: ({ signal }) => payrollTrApi.myEPayslip(payslipId, signal), retry: false })
  const ack = useAction(() => payrollTrApi.acknowledge(payslipId), { success: tx('Pusula teslim alındı olarak kaydedildi'), invalidate: [['payroll', 'my-epayslip', payslipId]] })
  if (q.isPending || q.isError || !q.data.published) return null
  const d = q.data
  return (
    <div className="mt-4 rounded-xl border border-border p-3 text-[13px]">
      <div className="flex flex-wrap items-center gap-3">
        <FileSignature className="size-4 text-primary" />
        <span className="flex-1">{tx('e-Bordro · içerik özeti {0}', [shortHash(d.contentSha256)])}{d.integrity !== 'ok' ? ` · ${integrityLabel[d.integrity]}` : ''}</span>
        {d.acknowledgedAt ? <StatusBadge tone="success"><BadgeCheck className="size-3.5" /> {tx('Teslim alındı: {0}', [formatDateTime(d.acknowledgedAt)])}</StatusBadge>
          : d.canAcknowledge ? <Button size="sm" onClick={() => ack.mutate(undefined)} disabled={ack.isPending}><Check className="size-4" /> {tx('Okudum, teslim aldım')}</Button> : null}
      </div>
    </div>
  )
}

/* ================================================================== 63 fark bordrosu */

export function RetroPanel() {
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? tx('(ayrılmış çalışan)')
  const q = useQuery({ queryKey: ['payroll', 'retro'], queryFn: ({ signal }) => payrollTrApi.retroCandidates(undefined, signal) })
  const periods = useQuery({ queryKey: ['payroll', 'periods', 'all'], queryFn: ({ signal }) => payrollApi.periods(undefined, signal) })
  const openPeriods = (periods.data ?? []).filter((p) => p.status !== 'Closed')
  const [target, setTarget] = useState('')
  const [sel, setSel] = useState<Set<string>>(new Set())
  useEffect(() => { if (!target && openPeriods[0]) setTarget(openPeriods[0].id) }, [openPeriods, target])
  const rows = q.data ?? []
  const apply = useAction(() => payrollTrApi.retroApply(target, rows.filter((r) => r.applicable && sel.has(retroKey(r))).map((r) => ({ employeeId: r.employeeId, sourcePeriodId: r.sourcePeriodId }))), {
    success: (r) => tx('{0} fark ({1}) hedef döneme eklendi; dönemi yeniden hesaplayın', [r.applied, formatMoney(r.total)]),
    invalidate: [['payroll']], onDone: () => setSel(new Set()),
  })
  const toggle = (r: RetroCandidate) => setSel((s) => { const n = new Set(s); const k = retroKey(r); if (n.has(k)) n.delete(k); else n.add(k); return n })
  const columns: Array<Column<RetroCandidate>> = [
    { id: 'x', header: '', cell: (r) => (r.applicable ? <Checkbox checked={sel.has(retroKey(r))} onCheckedChange={() => toggle(r)} aria-label={tx('Seç')} /> : null) },
    { id: 'e', header: tx('Çalışan'), cell: (r) => <span className="font-medium">{nameOf(r.employeeId)}</span>, searchText: (r) => nameOf(r.employeeId), sortValue: (r) => nameOf(r.employeeId) },
    { id: 'p', header: tx('Dönem'), cell: (r) => periodText(r.year, r.month), sortValue: (r) => r.year * 100 + r.month },
    { id: 'b', header: tx('Eski → yeni aylık brüt'), cell: (r) => `${formatMoney(r.oldBase)} → ${formatMoney(r.newBase)}`, hideBelow: 'md' },
    { id: 'p2', header: tx('Önceden ödenen fark'), cell: (r) => (r.alreadyPaid ? formatMoney(r.alreadyPaid) : '—'), align: 'right', hideBelow: 'lg' },
    { id: 'd', header: tx('Brüt fark'), cell: (r) => <span className={r.diffGross < 0 ? 'text-destructive' : ''}>{formatMoney(r.diffGross)}</span>, align: 'right', sortValue: (r) => r.diffGross },
    { id: 'n', header: tx('Tahmini net fark'), cell: (r) => formatMoney(r.estimatedNetDiff), align: 'right', hideBelow: 'sm' },
  ]
  const total = retroTotal(rows, sel)
  return (
    <div className="space-y-4">
      <InfoNote>{tx('Ücret kaydı kapanmış bir döneme geriye dönük değiştiyse eski ve yeni brüt arasındaki fark burada listelenir. Kapanmış dönem açılmaz; onayladığınız farklar seçtiğiniz açık döneme "Fark: YYYY/AA" ek ödemesi olarak girer ve o dönemde vergilendirilir. Negatif farklar yalnızca gösterilir. SGK açısından farkın ait olduğu aya ek belgeyle bildirilmesi gerekebilir; mali müşavirinize danışın.')}</InfoNote>
      <DataTable rows={q.data} rowKey={retroKey} columns={columns} isLoading={q.isPending} error={q.error} onRetry={() => void q.refetch()}
        emptyTitle={tx('Fark yok')} emptyDetail={tx('Son 24 aydaki kapanmış dönemlerde ücreti sonradan değişen çalışan bulunmadı.')} />
      {rows.some((r) => r.applicable) && (
        <div className="flex flex-wrap items-end gap-3">
          <div className="w-64"><SelectField label={tx('Hedef dönem (açık)')} value={target} onChange={setTarget} options={openPeriods.map((p) => ({ value: p.id, label: periodText(p.year, p.month) }))} /></div>
          <Button disabled={!target || sel.size === 0 || apply.isPending} onClick={() => apply.mutate(undefined)}><Check className="size-4" /> {tx('Seçilenleri onayla ({0})', [formatMoney(total)])}</Button>
          {!openPeriods.length && <span className="text-[12.5px] text-muted-foreground">{tx('Önce açık bir bordro dönemi oluşturun.')}</span>}
        </div>
      )}
    </div>
  )
}

/* ================================================================== 61 kıdem ve ihbar */

function Breakdown({ p }: { p: SeverancePreview }) {
  const r = p.result
  const m = (v: number) => formatMoney(v, p.sources?.currency ?? 'TRY')
  const rows: Array<[string, string, boolean?]> = [
    [tx('Hizmet süresi'), tx('{0} gün ({1} yıl)', [r.tenureDays, formatNumber(r.tenureYears)])],
    [tx('Giydirilmiş aylık brüt'), m(r.dressedMonthlyGross)],
    [tx('  aylık brüt {0} + düzenli ek ödemeler {1} + diğer yan haklar {2}', [m(p.input.monthlyBaseGross), m(p.input.regularAdditionsMonthly), m(p.input.otherBenefitsMonthly)]), ''],
    [tx('Kıdem tavanı'), m(p.input.severanceCeiling) + (r.ceilingApplied ? ` · ${tx('uygulandı')}` : '')],
    [tx('Kıdem tazminatı (brüt)'), r.severanceEligible ? m(r.severanceGross) : tx('Hak yok')],
    [tx('  damga vergisi'), `− ${m(r.severanceStampTax)}`],
    [tx('Kıdem tazminatı (net)'), m(r.severanceNet), true],
    [tx('İhbar süresi'), tx('{0} hafta', [r.noticeWeeks])],
    [tx('İhbar tazminatı (brüt)'), r.noticePaid ? m(r.noticeGross) : tx('Ödenmez')],
    [tx('  gelir vergisi'), `− ${m(r.noticeIncomeTax)}`],
    [tx('  damga vergisi'), `− ${m(r.noticeStampTax)}`],
    [tx('İhbar tazminatı (net)'), m(r.noticeNet), true],
    [tx('Kullanılmayan izin ({0} gün, brüt; son bordroda işlenir)', [formatNumber(r.unusedLeaveDays)]), m(r.unusedLeaveGross)],
    [tx('Toplam net (kıdem + ihbar)'), m(r.totalNet), true],
  ]
  return (
    <div className="space-y-3">
      <dl className="grid grid-cols-[1fr_auto] gap-x-6 gap-y-1.5 text-[13px]">
        {rows.map(([k, v, strong], i) => (
          <div key={i} className="contents">
            <dt className={strong ? 'font-semibold' : k.startsWith('  ') ? 'pl-3 text-[12px] text-muted-foreground' : 'text-muted-foreground'}>{k.trim()}</dt>
            <dd className={strong ? 'text-right font-semibold tabular-nums' : 'text-right tabular-nums'}>{v}</dd>
          </div>
        ))}
      </dl>
      <ul className="list-disc space-y-1 pl-5 text-[12px] text-muted-foreground">
        <li>{txServer(p.basis.severance)}</li><li>{txServer(p.basis.notice)}</li><li>{txServer(p.basis.leave)}</li>
      </ul>
      <InfoNote>{txServer(p.disclaimer)}</InfoNote>
    </div>
  )
}

const MANUAL = 'manual'
const triOptions = [{ value: 'auto', label: tx('Ayrılış nedenine göre') }, { value: 'yes', label: tx('Evet (İK kararı)') }, { value: 'no', label: tx('Hayır (İK kararı)') }]
const tri = (v: string) => (v === 'yes' ? true : v === 'no' ? false : null)

function openDocument(html: string, title: string) {
  const w = window.open('', '_blank')
  if (!w) throw new Error(tx('Açılır pencere engellendi; tarayıcıda izin verin.'))
  w.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>${title.replace(/[<>&"]/g, '')}</title><style>@page{size:A4;margin:18mm}body{font-family:Inter,'Segoe UI',Arial,sans-serif;font-size:11pt;color:#111}</style></head><body>${html}<script>window.onload=()=>setTimeout(()=>window.print(),300)<\/script></body></html>`)
  w.document.close()
}

export function SeverancePanel() {
  const admin = useIsPayrollAdmin()
  const toast = useToast()
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const [params] = useSearchParams()
  const offs = useQuery({ queryKey: ['offboarding', 'list'], queryFn: ({ signal }) => engagementApi.offboardings(signal) })
  const list = useQuery({ queryKey: ['payroll', 'severance'], queryFn: ({ signal }) => payrollTrApi.severanceList(undefined, signal) })
  const [f, setF] = useState({ caseId: params.get('case') ?? MANUAL, employeeId: '', lastDay: '', reason: '' as ExitReason | '', additions: '', other: '', leave: '', elig: 'auto', notice: 'auto' })
  const [preview, setPreview] = useState<SeverancePreview | null>(null)
  const [rej, setRej] = useState<SeveranceCalc | null>(null)
  const [note, setNote] = useState('')
  useEffect(() => {
    const c = offs.data?.find((o) => o.id === f.caseId)
    if (c) setF((x) => ({ ...x, employeeId: c.employeeId, lastDay: c.lastWorkingDay, reason: c.reason as ExitReason }))
  }, [f.caseId, offs.data])
  const num = (s: string) => (s.trim() ? parseDecimal(s) : null)
  const body = (): SeveranceRequest => ({
    employeeId: f.employeeId, offboardingCaseId: f.caseId !== MANUAL ? f.caseId : null, lastWorkingDay: f.lastDay || null, reason: f.reason || null,
    regularAdditionsMonthly: num(f.additions), otherBenefitsMonthly: num(f.other), unusedLeaveDays: num(f.leave),
    severanceEligible: tri(f.elig), noticePaid: tri(f.notice),
  })
  const calc = useAction(() => payrollTrApi.severancePreview(body()), { onDone: setPreview })
  const save = useAction(() => payrollTrApi.severanceSave(body()), { success: tx('Hesap kaydedildi; başka bir bordro yetkilisi onaylamalı'), invalidate: [['payroll', 'severance']], onDone: () => setPreview(null) })
  const approve = useAction((id: string) => payrollTrApi.severanceDecide(id, true), { success: tx('Hesap onaylandı'), invalidate: [['payroll', 'severance']] })
  const reject = useAction(() => payrollTrApi.severanceDecide(rej!.id, false, note), { success: tx('Hesap reddedildi'), invalidate: [['payroll', 'severance']], onDone: () => { setRej(null); setNote('') } })
  const doc = async (c: SeveranceCalc) => {
    try { const d = await payrollTrApi.severanceDocument(c.id); openDocument(d.html, tx('İbraname — {0}', [nameOf(c.employeeId)])) } catch (e) { toast.stop(errMsg(e)) }
  }
  const status: Record<SeveranceCalc['status'], { label: string; tone: 'warning' | 'success' | 'danger' }> = {
    Draft: { label: tx('Onay bekliyor'), tone: 'warning' }, Approved: { label: tx('Onaylandı'), tone: 'success' }, Rejected: { label: tx('Reddedildi'), tone: 'danger' },
  }
  return (
    <div className="space-y-5">
      {admin && (
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><Calculator className="size-4 text-primary" />{' '}{tx('Kıdem ve ihbar hesabı')}</span>}
            note={tx('Ayrılış kaydından (offboarding) ya da elle. Giydirilmiş ücrete düzenli ek ödemelerin son 12 kapanmış pusula ortalaması önerilir; değiştirebilirsiniz. Hesabı başka bir bordro yetkilisi onaylar.')} />
          <PanelBody className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <SelectField label={tx('Ayrılış kaydı')} value={f.caseId} onChange={(v) => setF({ ...f, caseId: v })}
                options={[{ value: MANUAL, label: tx('— elle —') }, ...(offs.data ?? []).filter((o) => o.status !== 'Cancelled').map((o) => ({ value: o.id, label: `${o.employeeName} · ${formatDate(o.lastWorkingDay)}` }))]} />
              <PersonSelect value={f.employeeId} onChange={(v) => setF({ ...f, employeeId: v })} />
              <TextField type="date" label={tx('Son iş günü')} value={f.lastDay} onChange={(e) => setF({ ...f, lastDay: e.target.value })} />
              <SelectField label={tx('Ayrılış nedeni')} value={f.reason} onChange={(v) => setF({ ...f, reason: v as ExitReason })}
                options={(Object.keys(exitReasonLabel) as ExitReason[]).map((k) => ({ value: k, label: exitReasonLabel[k] }))} />
              <TextField label={tx('Düzenli ek ödemeler (aylık)')} inputMode="decimal" value={f.additions} onChange={(e) => setF({ ...f, additions: e.target.value })} hint={tx('Boş: öneri kullanılır')} />
              <TextField label={tx('Diğer sürekli yan haklar (aylık brüt)')} inputMode="decimal" value={f.other} onChange={(e) => setF({ ...f, other: e.target.value })} hint={tx('Yemek, yol vb.')} />
              <TextField label={tx('Kullanılmayan izin (gün)')} inputMode="decimal" value={f.leave} onChange={(e) => setF({ ...f, leave: e.target.value })} hint={tx('Boş: izin bakiyesinden')} />
              <div className="grid grid-cols-2 gap-2">
                <SelectField label={tx('Kıdem hakkı')} value={f.elig} onChange={(v) => setF({ ...f, elig: v })} options={triOptions} />
                <SelectField label={tx('İhbar ödenir')} value={f.notice} onChange={(v) => setF({ ...f, notice: v })} options={triOptions} />
              </div>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" disabled={!f.employeeId || calc.isPending} onClick={() => calc.mutate(undefined)}><Calculator className="size-4" /> {tx('Hesapla')}</Button>
              {preview && <Button disabled={save.isPending} onClick={() => save.mutate(undefined)}><Save className="size-4" /> {tx('Onaya gönder')}</Button>}
            </div>
            {preview && <Breakdown p={preview} />}
          </PanelBody>
        </Panel>
      )}
      <Panel>
        <PanelHead title={tx('Kayıtlı hesaplar')} />
        <PanelBody className="p-0">
          {list.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : !list.data?.length ? <div className="p-5"><EmptyState icon={FileText} title={tx('Kayıtlı hesap yok')} /></div> : (
            <ul className="divide-y divide-border text-[13px]">
              {list.data.map((c) => (
                <li key={c.id} className="flex flex-wrap items-center gap-3 px-5 py-3">
                  <span className="min-w-0 flex-1"><span className="font-medium">{nameOf(c.employeeId)}</span> · {exitReasonLabel[c.reason]} · {formatDate(c.lastWorkingDay)}
                    <span className="block text-[12px] text-muted-foreground">{tx('Net {0} · hazırlayan {1}', [formatMoney(c.totalNet), c.preparedByName ?? '—'])}{c.decidedByName ? ` · ${tx('karar: {0}', [c.decidedByName])}` : ''}{c.decisionNote ? ` · ${c.decisionNote}` : ''}</span></span>
                  <StatusBadge tone={status[c.status].tone}>{status[c.status].label}</StatusBadge>
                  {admin && c.status === 'Draft' && <>
                    <Button size="sm" variant="outline" onClick={() => approve.mutate(c.id)}><Check className="size-4" /> {tx('Onayla')}</Button>
                    <Button size="sm" variant="ghost" onClick={() => setRej(c)}><X className="size-4" /> {tx('Reddet')}</Button>
                  </>}
                  {admin && c.status === 'Approved' && <Button size="sm" variant="outline" onClick={() => void doc(c)}><FileText className="size-4" /> {tx('İbraname')}</Button>}
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      <InfoNote>{tx('İbraname metni Belge şablonları ekranındaki "İbraname" şablonundan alınır (yoksa varsayılan metin). Hukuk biriminizce uyarlanmalıdır; ibraname ödemeden en az bir ay sonra ve ödeme banka aracılığıyla yapılmışsa geçerlidir (TBK m.420).')}</InfoNote>
      {rej && (
        <Modal open onClose={() => setRej(null)} title={tx('Hesabı reddet')}
          footer={<><Button variant="outline" onClick={() => setRej(null)}>{tx('Vazgeç')}</Button><Button disabled={!note.trim() || reject.isPending} onClick={() => reject.mutate(undefined)}>{tx('Reddet')}</Button></>}>
          <TextAreaField label={tx('Gerekçe')} rows={3} value={note} onChange={(e) => setNote(e.target.value)} />
        </Modal>
      )}
    </div>
  )
}

/* ================================================================== 65 bant uyumu */

function GroupRow({ g }: { g: CoverageGroup }) {
  return (
    <li className="grid grid-cols-[1.6fr_repeat(5,1fr)] gap-2 px-3 py-2 text-[13px] tabular-nums">
      <span className="truncate font-medium">{g.key}</span>
      <span className="text-right">{g.count}</span>
      {g.hidden ? <span className="col-span-4 text-right text-muted-foreground">{tx('5 kişiden az: gizli')}</span> : <>
        <span className="text-right">{g.below}</span><span className="text-right">{g.within}</span><span className="text-right">{g.above}</span>
        <span className="text-right">{g.avgCompaRatio == null ? '—' : formatNumber(g.avgCompaRatio)}</span>
      </>}
    </li>
  )
}

function GroupList({ title, groups }: { title: string; groups: CoverageGroup[] }) {
  return (
    <Panel>
      <PanelHead title={title} />
      <PanelBody className="p-0">
        <div className="grid grid-cols-[1.6fr_repeat(5,1fr)] gap-2 border-b border-border px-3 py-2 text-[12px] text-muted-foreground">
          <span /><span className="text-right">{tx('Kişi')}</span><span className="text-right">{tx('Altında')}</span><span className="text-right">{tx('İçinde')}</span><span className="text-right">{tx('Üstünde')}</span><span className="text-right">{tx('Ort. compa')}</span>
        </div>
        <ul className="divide-y divide-border">{groups.map((g) => <GroupRow key={g.key} g={g} />)}</ul>
      </PanelBody>
    </Panel>
  )
}

export function BandCompliancePanel() {
  const cov = useQuery({ queryKey: ['bands', 'coverage'], queryFn: ({ signal }) => payrollTrApi.bandCoverage(signal) })
  const [showPeople, setShowPeople] = useState(false)
  const rows = useQuery({ queryKey: ['bands', 'compa'], queryFn: ({ signal }) => payrollTrApi.compaRatios(signal), enabled: showPeople })
  const columns: Array<Column<CompaRow>> = [
    { id: 'n', header: tx('Çalışan'), cell: (r) => <span className="font-medium">{r.name}</span>, searchText: (r) => `${r.name} ${r.department ?? ''}`, sortValue: (r) => r.name },
    { id: 'd', header: tx('Bölüm'), cell: (r) => r.department ?? '—', hideBelow: 'md', sortValue: (r) => r.department ?? '' },
    { id: 'g', header: tx('Kademe'), cell: (r) => r.grade ?? '—', sortValue: (r) => r.grade ?? '' },
    { id: 's', header: tx('Ücret'), cell: (r) => formatMoney(r.salary, r.currency), align: 'right', sortValue: (r) => r.salary, hideBelow: 'sm' },
    { id: 'b', header: tx('Bant (alt–orta–üst)'), cell: (r) => (r.band ? `${formatMoney(r.band.minAmount)} – ${formatMoney(r.band.midAmount)} – ${formatMoney(r.band.maxAmount)}` : '—'), hideBelow: 'lg' },
    { id: 'c', header: tx('Compa-ratio'), cell: (r) => (r.compaRatio == null ? '—' : formatNumber(r.compaRatio)), align: 'right', sortValue: (r) => r.compaRatio ?? -1 },
    { id: 'p', header: tx('Konum'), cell: (r) => <StatusBadge tone={positionTone[r.position]}>{positionLabel[r.position]}</StatusBadge>, sortValue: (r) => r.position },
  ]
  return (
    <div className="space-y-5">
      {cov.isPending ? <RowsSkeleton rows={3} /> : cov.isError ? <ErrorState message={errMsg(cov.error)} /> : (
        <>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Metric label={tx('Ücret kaydı olan çalışan')} value={cov.data.total} />
            <Metric label={tx('Bantsız')} value={cov.data.noBand} tone={cov.data.noBand ? 'warn' : 'good'} hint={tx('Kademe yok ya da bant tanımsız')} />
            <Metric label={tx('Bant dışında')} value={cov.data.outsideBand ?? tx('gizli')} />
            <Metric label={tx('Ortalama compa-ratio')} value={cov.data.overall.avgCompaRatio == null ? '—' : formatNumber(cov.data.overall.avgCompaRatio)} hint={tx('1,00 = bant ortası')} />
          </div>
          <div className="grid gap-5 lg:grid-cols-2">
            <GroupList title={tx('Bant bazında')} groups={cov.data.byBand} />
            <GroupList title={tx('Bölüm bazında')} groups={cov.data.byDepartment} />
          </div>
        </>
      )}
      <InfoNote>{tx('Toplu görünümlerde 5 kişiden az gruplar gizlenir. Kişi bazlı compa-ratio yalnızca İK ve ücret görme izni olanlara açıktır; her görüntüleme erişim kaydına yazılır. Ücret adaleti analizi aynı kademe/unvan bilgisini kullanır.')}</InfoNote>
      {!showPeople ? <Button variant="outline" onClick={() => setShowPeople(true)}>{tx('Kişi bazlı compa-ratio göster')}</Button>
        : <DataTable rows={rows.data} rowKey={(r) => r.employeeId} columns={columns} isLoading={rows.isPending} error={rows.error} />}
    </div>
  )
}
