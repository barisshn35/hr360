import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { BookmarkPlus, CalendarClock, Pin, PinOff, Play, Trash2 } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { platformGovApi, type NlReportPlus, type ReportFilters, type ReportSchedule, type SavedReport } from '@/api/platformGov'
import { formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { appLocale, tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/Confirm'
import { cn } from '@/lib/utils'

const SCHEDULE_LABELS: Record<ReportSchedule, string> = {
  None: tx('Zamanlanmadı'), Daily: tx('Her gün'), Weekly: tx('Her hafta'), Monthly: tx('Her ay'),
}
const WEEKDAYS = [tx('Pazartesi'), tx('Salı'), tx('Çarşamba'), tx('Perşembe'), tx('Cuma'), tx('Cumartesi'), tx('Pazar')]

function scheduleText(r: SavedReport) {
  if (r.schedule === 'None') return SCHEDULE_LABELS.None
  const day = r.schedule === 'Weekly' ? ` (${WEEKDAYS[(r.day ?? 1) - 1]})` : r.schedule === 'Monthly' ? ` (${tx('ayın {0}. günü', [r.day ?? 1])})` : ''
  return `${SCHEDULE_LABELS[r.schedule]}${day} ${r.time}`
}

/** Zamanlama alanları (saat Europe/Istanbul). Kişi bazındaki raporlar zamanlanamaz. */
function ScheduleFields({ schedule, time, day, disabled, onChange }: {
  schedule: ReportSchedule; time: string; day: number | null; disabled?: boolean
  onChange: (v: { schedule: ReportSchedule; time: string; day: number | null }) => void
}) {
  return (
    <div className="grid gap-4 sm:grid-cols-3">
      <SelectField label={tx('Zamanlanmış teslim')} value={schedule} disabled={disabled}
        onChange={(v) => onChange({ schedule: v as ReportSchedule, time, day: null })}
        options={(Object.keys(SCHEDULE_LABELS) as ReportSchedule[]).map((k) => ({ value: k, label: SCHEDULE_LABELS[k] }))} />
      {schedule !== 'None' && (
        <TextField label={tx('Saat (İstanbul)')} type="time" value={time} onChange={(e) => onChange({ schedule, time: e.target.value, day })} />
      )}
      {schedule === 'Weekly' && (
        <SelectField label={tx('Gün')} value={String(day ?? 1)} onChange={(v) => onChange({ schedule, time, day: Number(v) })}
          options={WEEKDAYS.map((w, i) => ({ value: String(i + 1), label: w }))} />
      )}
      {schedule === 'Monthly' && (
        <SelectField label={tx('Ayın günü')} value={String(day ?? 1)} onChange={(v) => onChange({ schedule, time, day: Number(v) })}
          options={Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: String(i + 1) }))} />
      )}
    </div>
  )
}

/** Rapor asistanındaki bir soruyu kaydetme (sabitleme + zamanlama). */
export function SaveReportModal({ question, filters, personLevel, onClose }: {
  question: string; filters: ReportFilters; personLevel?: boolean; onClose: () => void
}) {
  const [name, setName] = useState(question.slice(0, 80))
  const [pinned, setPinned] = useState(true)
  const [sched, setSched] = useState<{ schedule: ReportSchedule; time: string; day: number | null }>({ schedule: 'None', time: '07:00', day: null })
  const save = useAction(() => platformGovApi.saveReport({ name: name.trim(), question, ...filters, pinned, ...sched, day: sched.day ?? undefined }), {
    success: tx('Rapor kaydedildi'), invalidate: [['saved-reports']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={tx('Raporu kaydet')}
      note={tx('Kayıtlı rapor her açılışta güncel veriyle ve sizin yetkinizle yeniden çalışır; sonuç saklanmaz.')}
      footer={<Button disabled={save.isPending || !name.trim()} onClick={() => save.mutate(undefined)}><BookmarkPlus className="size-4" /> {tx('Kaydet')}</Button>}>
      <div className="space-y-4">
        <TextField label={tx('Ad')} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} required />
        <p className="text-[12.5px] text-muted-foreground">“{question}”</p>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={pinned} onCheckedChange={(v) => setPinned(v === true)} /> {tx('Panoma sabitle')}</label>
        <ScheduleFields {...sched} disabled={personLevel} onChange={setSched} />
        <InfoNote>{personLevel
          ? tx('Kişi bazındaki raporlar zamanlanamaz; yalnızca toplu (departman/ay/tür) raporlar zamanlanabilir.')
          : tx('Zamanlanmış teslimde size yalnızca rapor bağlantısı gönderilir (uygulama içi + e-posta); veri, rakam ya da ek gönderilmez, görmek için giriş yapmanız gerekir.')}</InfoNote>
      </div>
    </Modal>
  )
}

function EditScheduleModal({ r, onClose }: { r: SavedReport; onClose: () => void }) {
  const [sched, setSched] = useState({ schedule: r.schedule, time: r.time, day: r.day })
  const save = useAction(() => platformGovApi.updateSavedReport(r.id, { ...sched, day: sched.day ?? undefined }), {
    success: tx('Zamanlama güncellendi'), invalidate: [['saved-reports']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={tx('Zamanlanmış teslim')} note={r.name}
      footer={<Button disabled={save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>}>
      <ScheduleFields {...sched} disabled={!r.schedulable} onChange={setSched} />
    </Modal>
  )
}

/** Rapor asistanı sayfasında "Kayıtlı raporlarım" listesi. */
export function SavedReportsPanel({ onRun }: { onRun: (id: string) => void }) {
  const list = useQuery({ queryKey: ['saved-reports', 'list'], queryFn: ({ signal }) => platformGovApi.savedReports(signal) })
  const [editing, setEditing] = useState<SavedReport | null>(null)
  const pin = useAction((r: SavedReport) => platformGovApi.updateSavedReport(r.id, { pinned: !r.pinned }), { invalidate: [['saved-reports']] })
  const del = useAction((id: string) => platformGovApi.deleteSavedReport(id), { success: tx('Kayıtlı rapor silindi'), invalidate: [['saved-reports']] })
  const confirm = useConfirm()
  if (list.isPending) return <Panel><PanelBody><RowsSkeleton rows={2} /></PanelBody></Panel>
  if (!list.data?.length) return null
  return (
    <Panel>
      <PanelHead title={tx('Kayıtlı raporlarım')} note={tx('Sabitlenenler panonuzda görünür.')} />
      <PanelBody className="p-0">
        <ul className="divide-y divide-border">
          {list.data.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center gap-2 px-5 py-3 text-[13px]">
              <div className="min-w-0 flex-1">
                <p className="font-medium">{r.name}</p>
                <p className="truncate text-[12px] text-muted-foreground">“{r.question}” · {scheduleText(r)}
                  {r.lastRunAt ? ` · ${tx('son teslim {0}', [formatDateTime(r.lastRunAt)])}` : ''}</p>
              </div>
              {r.personLevel && <StatusBadge tone="neutral">{tx('Kişi bazında')}</StatusBadge>}
              <Button size="sm" variant="outline" onClick={() => onRun(r.id)}><Play className="size-4" /> {tx('Çalıştır')}</Button>
              <Button size="sm" variant="outline" aria-label={r.pinned ? tx('Sabitlemeyi kaldır') : tx('Panoma sabitle')} disabled={pin.isPending} onClick={() => pin.mutate(r)}>
                {r.pinned ? <PinOff className="size-4" /> : <Pin className="size-4" />}
              </Button>
              <Button size="sm" variant="outline" aria-label={tx('Zamanlanmış teslim')} disabled={!r.schedulable} onClick={() => setEditing(r)}><CalendarClock className="size-4" /></Button>
              <Button size="sm" variant="outline" aria-label={tx('Sil')} disabled={del.isPending}
                onClick={async () => { if (await confirm({ title: tx('“{0}” silinsin mi?', [r.name]), note: tx('Kayıtlı rapor ve zamanlanmış teslimi kalıcı olarak silinir.'), action: tx('Sil') })) del.mutate(r.id) }}><Trash2 className="size-4" /></Button>
            </li>
          ))}
        </ul>
      </PanelBody>
      {editing && <EditScheduleModal r={editing} onClose={() => setEditing(null)} />}
    </Panel>
  )
}

/** Pano bileşeni: sabitlenmiş raporların güncel sonucu (yalnızca yönetici/İK; boşsa görünmez). */
export function PinnedReportsWidget({ className }: { className?: string }) {
  const q = useQuery({ queryKey: ['saved-reports', 'pinned'], queryFn: ({ signal }) => platformGovApi.pinnedReports(signal), retry: false, staleTime: 60_000 })
  if (!q.data?.length) return null
  return (
    <motion.div initial={{ opacity: 0, y: 18 }} animate={{ opacity: 1, y: 0 }} className={cn('min-w-0', className)}>
      <Panel>
        <PanelHead title={tx('Sabitlenmiş raporlar')} note={tx('Rapor asistanından sabitlediğiniz raporlar; güncel veriyle.')}
          action={<Button size="sm" variant="outline" asChild><Link to="/panel/rapor-asistani">{tx('Rapor asistanı')}</Link></Button>} />
        <PanelBody className="grid gap-5 lg:grid-cols-2">
          {q.data.map(({ report, result }) => (
            <div key={report.id} className="min-w-0 rounded-xl border border-border p-4">
              <Link to={report.link} className="mb-2 block text-[13.5px] font-semibold hover:text-primary">{report.name}</Link>
              <CompactReport r={result} />
            </div>
          ))}
        </PanelBody>
      </Panel>
    </motion.div>
  )
}

/** Panoda grafiksiz özet: tek değer ya da ilk 5 satır (küçük grup gizlemesi sunucuda uygulanmıştır). */
function CompactReport({ r }: { r: NlReportPlus }) {
  if (!r.understood) return <p className="text-[12.5px] text-muted-foreground">{r.interpretation}</p>
  const single = r.rows.length === 1 && r.columns.length <= 2
  return (
    <div className="space-y-2">
      <p className="text-[12px] text-muted-foreground">{r.interpretation}</p>
      {r.rows.length === 0 ? (
        <p className="text-[12.5px] text-muted-foreground">{tx('Bu aralıkta kayıt yok.')}</p>
      ) : single ? (
        <p className="tabular text-[32px] font-semibold tracking-tight text-primary">{r.rows[0]![1] === null ? '—' : Number(r.rows[0]![1]).toLocaleString(appLocale)}</p>
      ) : (
        <table className="w-full text-[12.5px]">
          <thead><tr className="text-left text-muted-foreground">{r.columns.map((c) => <th key={c} className="py-1 pr-2 font-medium">{c}</th>)}</tr></thead>
          <tbody>{r.rows.slice(0, 5).map((row, i) => <tr key={i} className="border-t border-border">{row.map((c, j) => <td key={j} className="tabular py-1 pr-2">{c ?? '—'}</td>)}</tr>)}</tbody>
        </table>
      )}
      {r.note && <p className="text-[11.5px] text-muted-foreground">{r.note}</p>}
    </div>
  )
}
