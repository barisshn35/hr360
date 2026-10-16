import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { downloadWorkbook } from '@/lib/spreadsheet'
import { Building2, CalendarDays, Download, FileSpreadsheet, GraduationCap, LoaderCircle, Upload, Users, Wallet } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { InfoNote } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { employeeApi } from '@/api/employees'
import { organizationApi } from '@/api/organization'
import { leaveApi, leaveStatusLabels, leaveTypeLabels } from '@/api/leave'
import { claimStatusLabels, expenseApi } from '@/api/expense'
import { enrollmentStatusLabels, learningApi } from '@/api/learning'
import { employeeStatusLabels } from '@/api/types'
import { useDirectory } from '@/api/directory'
import { ImportEmployeesModal } from '@/features/employees/ImportEmployeesModal'
import { TenantGate, errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { requestTrace } from '@/lib/watermark'

type Dataset = { id: string; title: string; detail: string; icon: React.ElementType; load: () => Promise<Record<string, unknown>[]> }

const download = downloadWorkbook

/**
 * İçe aktarma şablonu (CSV, Türkçe Excel için ';' ayırıcılı, UTF-8 BOM'lu). Başlıklar
 * ImportEmployeesModal'daki takma adlarla eşleşir; dil ne olursa olsun Türkçe kalır.
 */
function downloadImportTemplate() {
  const lines = ['Ad;Soyad;E-posta;Telefon;İşe giriş tarihi;Departman;Unvan', 'Ayşe;Yılmaz;ayse.yilmaz@ornek.com;05551234567;01.09.2026;Mühendislik;Yazılım Mühendisi']
  const blob = new Blob(['\ufeff' + lines.join('\r\n') + '\r\n'], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = 'hr360-calisan-sablonu.csv'
  document.body.appendChild(a)
  a.click()
  a.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export function ImportExportPage() {
  const toast = useToast()
  const dir = useDirectory()
  const name = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? id
  const departments = useQuery({ queryKey: ['organization', 'departments'], queryFn: ({ signal }) => organizationApi.listDepartments(undefined, signal) })
  const deptName = (id?: string | null) => departments.data?.find((d) => d.id === id)?.name ?? ''
  const [busy, setBusy] = useState<string | null>(null)
  const [importing, setImporting] = useState(false)

  const datasets: Dataset[] = [
    { id: 'employees', title: tx('Çalışanlar'), detail: tx('Ad, e-posta, işe giriş, durum, güncel departman ve pozisyon'), icon: Users, load: async () => (await employeeApi.list()).map((e) => {
      const a = [...(e.assignments ?? [])].sort((x, y) => (y.effectiveFrom ?? '').localeCompare(x.effectiveFrom ?? ''))[0]
      return { [tx('Ad')]: e.firstName, [tx('Soyad')]: e.lastName, [tx('E-posta')]: e.email, [tx('Telefon')]: e.phone ?? '', [tx('İşe giriş')]: e.hireDate, [tx('Durum')]: employeeStatusLabels[e.status] ?? String(e.status), [tx('Departman')]: deptName(a?.departmentId), [tx('Pozisyon')]: a?.positionTitle ?? '' }
    }) },
    { id: 'departments', title: tx('Departmanlar'), detail: tx('Ad, üst departman ve departman başı'), icon: Building2, load: async () => (await organizationApi.listDepartments()).map((d) => ({ [tx('Ad')]: d.name, [tx('Üst departman')]: deptName(d.parentDepartmentId), [tx('Departman başı')]: d.headEmployeeId ? name(d.headEmployeeId) : '' })) },
    { id: 'leave', title: tx('İzin talepleri'), detail: tx('Tüm izin talepleri ve durumları'), icon: CalendarDays, load: async () => (await leaveApi.listRequests()).map((l) => ({ [tx('Çalışan')]: name(l.employeeId), [tx('Tür')]: leaveTypeLabels[l.type] ?? l.type, [tx('Başlangıç')]: l.startDate, [tx('Bitiş')]: l.endDate, [tx('Gün')]: l.days, [tx('Durum')]: leaveStatusLabels[l.status] ?? l.status, [tx('Açıklama')]: l.reason ?? '' })) },
    { id: 'expense', title: tx('Masraflar'), detail: tx('Beyanlar, tutar ve durum'), icon: Wallet, load: async () => (await expenseApi.listClaims()).map((c) => ({ [tx('Çalışan')]: name(c.employeeId), [tx('Başlık')]: c.title, [tx('Tutar')]: c.totalAmount, [tx('Para birimi')]: c.currency, [tx('Durum')]: claimStatusLabels[c.status] ?? c.status, [tx('Oluşturma')]: c.createdAt?.slice(0, 10) })) },
    { id: 'learning', title: tx('Eğitim kayıtları'), detail: tx('Kurs bazında katılım ve tamamlama'), icon: GraduationCap, load: async () => (await learningApi.listCourses()).flatMap((c) => (c.enrollments ?? []).map((e) => ({ [tx('Eğitim')]: c.title, [tx('Kategori')]: c.category, [tx('Zorunlu')]: c.isMandatory ? tx('Evet') : tx('Hayır'), [tx('Çalışan')]: name(e.employeeId), [tx('Durum')]: enrollmentStatusLabels[e.status] ?? e.status, [tx('Puan')]: e.score ?? '', [tx('Tamamlama')]: e.completedAt?.slice(0, 10) ?? '' }))) },
  ]

  const exportOne = async (d: Dataset) => {
    setBusy(d.id)
    try {
      const rows = await d.load()
      const trace = await requestTrace(`xlsx:${d.id}`, { format: 'xlsx', rows: rows.length })
      await download(`hr360-${d.id}-${new Date().toISOString().slice(0, 10)}.xlsx`, { [d.title]: rows }, { watermark: trace?.text })
      toast.ok(tx('{0} satır indirildi', [rows.length]))
    } catch (e) { toast.stop(errMsg(e)) } finally { setBusy(null) }
  }
  const exportAll = async () => {
    setBusy('all')
    try {
      const sheets: Record<string, Record<string, unknown>[]> = {}
      for (const d of datasets) {
        try { sheets[d.title] = await d.load() } catch { sheets[d.title] = [] }
      }
      const trace = await requestTrace('xlsx:all', { format: 'xlsx', rows: Object.values(sheets).reduce((n, r) => n + r.length, 0) })
      await download(`hr360-tum-veriler-${new Date().toISOString().slice(0, 10)}.xlsx`, sheets, { watermark: trace?.text })
      toast.ok(tx('Tüm veriler tek dosyada indirildi'))
    } catch (e) { toast.stop(errMsg(e)) } finally { setBusy(null) }
  }

  return (
    <TenantGate>
      <PageHeader title={tx('İçe/dışa aktarım')} description={tx('Excel ile toplu veri: çalışanları içe aktarın, modül verilerini tek tıkla dışa aktarın.')} actions={<Button onClick={exportAll} disabled={busy !== null}>{busy === 'all' ? <LoaderCircle className="size-4 animate-spin" /> : <Download className="size-4" />}{' '}{tx('Hepsini tek dosyada indir')}</Button>} />
      <div className="grid gap-6 xl:grid-cols-[1fr_380px]">
        <Panel>
          <PanelHead title={tx('Dışa aktar')} note={tx('Yetkinizin gördüğü kadar veri indirilir; her sayfa ayrı Excel sekmesidir.')} />
          <PanelBody className="grid gap-3 sm:grid-cols-2">
            {datasets.map((d, i) => (
              <motion.button key={d.id} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} whileHover={{ y: -3 }} onClick={() => void exportOne(d)} disabled={busy !== null}
                className="flex cursor-pointer items-start gap-3 rounded-2xl border border-border bg-card/40 p-4 text-left transition hover:border-primary/40 disabled:opacity-60">
                <span className="grid size-10 shrink-0 place-items-center rounded-xl bg-primary/10 text-primary">{busy === d.id ? <LoaderCircle className="size-5 animate-spin" /> : <d.icon className="size-5" />}</span>
                <div><p className="text-[14px] font-medium">{d.title}</p><p className="text-[12px] text-muted-foreground">{d.detail}</p></div>
              </motion.button>
            ))}
          </PanelBody>
        </Panel>
        <Panel>
          <PanelHead title={tx('İçe aktar')} />
          <PanelBody className="space-y-4">
            <div className="flex items-start gap-3 rounded-2xl border border-dashed border-border p-4">
              <FileSpreadsheet className="mt-0.5 size-5 text-primary" />
              <div className="text-[13px]"><p className="font-medium">{tx('Çalışanlar (Excel)')}</p><p className="text-muted-foreground">{tx('Şablonu indirin, doldurun, yükleyin. Satırlar tek tek doğrulanır; hatalı satırlar atlanır ve raporlanır.')}</p></div>
            </div>
            <Button variant="outline" className="w-full" onClick={downloadImportTemplate}><Download className="size-4" />{' '}{tx('Şablonu indir (CSV)')}</Button>
            <Button className="w-full" onClick={() => setImporting(true)}><Upload className="size-4" />{' '}{tx('Çalışan içe aktar')}</Button>
            <InfoNote>{tx('Her eklenen çalışan için “işe alındı” olayı üretilir (bildirim, kural motoru ve webhook’lar tetiklenir).')}</InfoNote>
          </PanelBody>
        </Panel>
      </div>
      <ImportEmployeesModal open={importing} onClose={() => setImporting(false)} departments={(departments.data ?? []).map((d) => ({ id: d.id, label: d.name }))} />
    </TenantGate>
  )
}
