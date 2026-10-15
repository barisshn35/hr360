import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { motion, useReducedMotion } from 'motion/react'
import { LoaderCircle } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { StatCard } from '@/components/ui/StatCard'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState } from '@/components/ui/States'
import { TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { timeshiftApi } from '@/api/timeshift'
import { useMyEmployeeId, useTimeEntries, useTimeSummary } from '@/api/queries'
import type { TimeEntry } from '@/api/types'
import { formatDate, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'
import { OvertimePanel } from './OvertimePanel'
import { AttendanceReportPanel } from './AttendanceReport'
import { TimesheetAnomalyPanel } from './TimesheetAnomalyPanel'
import { TimesheetPeriodsPanel } from './TimesheetPeriodsPanel'
import { isHr } from '@/auth/roles'

/** Dakikayı "7s 30dk" biçimine çevirir. */
function hm(minutes: number): string {
  if (!minutes) return '—'
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return h > 0 ? `${h}s${m ? ` ${m}dk` : ''}` : `${m}dk`
}

function monthRange(year: number, month: number) {
  const from = new Date(Date.UTC(year, month - 1, 1)).toISOString().slice(0, 10)
  const to = new Date(Date.UTC(year, month, 0)).toISOString().slice(0, 10)
  return { from, to }
}

const clockTime = (iso?: string | null) =>
  iso ? new Date(iso).toLocaleTimeString(appLocale, { hour: '2-digit', minute: '2-digit' }) : '—'

/**
 * Bugünün durum kartı ve tek eylem düğmesi.
 *
 * Düğme duruma göre değişir: giriş yapılmamışsa "Giriş yap", yapılmışsa
 * "Çıkış yap", gün kapanmışsa eylemsiz. Böylece kullanıcı ne yapacağını
 * düşünmek zorunda kalmaz.
 */
function TodayCard({ employeeId, today }: { employeeId: string; today?: TimeEntry }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const reduced = useReducedMotion()

  const clockedIn = Boolean(today?.clockIn) && !today?.clockOut
  const closed = Boolean(today?.clockOut)

  const mutation = useMutation({
    mutationFn: (kind: 'in' | 'out') => {
      const payload = { employeeId, at: new Date().toISOString(), source: 'Manual' as const }
      return kind === 'in' ? timeshiftApi.clockIn(payload) : timeshiftApi.clockOut(payload)
    },
    onSuccess: (_, kind) => {
      void queryClient.invalidateQueries({ queryKey: ['timeshift'] })
      toast.ok(kind === 'in' ? tx('Giriş kaydedildi') : tx('Çıkış kaydedildi'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kayıt yapılamadı.')),
  })

  return (
    <Panel>
      <PanelHead
        title={tx('Bugün')}
        note={new Date().toLocaleDateString(appLocale, { dateStyle: 'long' })}
      />
      <PanelBody className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex flex-wrap items-center gap-6">
          {/* Durum işareti: açık gün canlı, kapanmış gün sakin */}
          <span className="relative flex size-3 shrink-0 items-center justify-center">
            <span
              className={cn(
                'size-3 rounded-full',
                clockedIn
                  ? 'bg-primary'
                  : closed
                    ? 'bg-[hsl(var(--success))]'
                    : 'bg-muted-foreground/40',
              )}
            />
            {clockedIn && !reduced && (
              <motion.span
                aria-hidden="true"
                className="absolute inset-0 rounded-full border border-primary"
                animate={{ scale: [1, 2.2], opacity: [0.6, 0] }}
                transition={{ duration: 2.2, repeat: Infinity, ease: 'easeOut' }}
              />
            )}
          </span>

          <div>
            <p className="text-[12px] text-muted-foreground">{tx('Giriş')}</p>
            <p className="tabular text-[20px] leading-none font-bold">
              {clockTime(today?.clockIn)}
            </p>
          </div>
          <div>
            <p className="text-[12px] text-muted-foreground">{tx('Çıkış')}</p>
            <p className="tabular text-[20px] leading-none font-bold">
              {clockTime(today?.clockOut)}
            </p>
          </div>
          <div>
            <p className="text-[12px] text-muted-foreground">{tx('Çalışılan')}</p>
            <motion.p
              key={today?.workedMinutes ?? 0}
              className="tabular text-[20px] leading-none font-bold"
              initial={reduced ? false : { opacity: 0, y: 4 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.25, ease: 'easeOut' }}
            >
              {hm(today?.workedMinutes ?? 0)}
            </motion.p>
          </div>
          {(today?.overtimeMinutes ?? 0) > 0 && (
            <StatusBadge tone="warning">{tx('Fazla mesai {0}', [hm(today!.overtimeMinutes)])}</StatusBadge>
          )}
        </div>

        {closed ? (
          <StatusBadge tone="success">{tx('Gün kapandı')}</StatusBadge>
        ) : (
          <Button
            className="cursor-pointer"
            disabled={mutation.isPending}
            onClick={() => mutation.mutate(clockedIn ? 'out' : 'in')}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {clockedIn ? tx('Çıkış yap') : tx('Giriş yap')}
          </Button>
        )}
      </PanelBody>
    </Panel>
  )
}

export function TimesheetPage() {
  const { can, roles } = useAuth()
  const now = new Date()
  // Başkasının puantajını yalnızca yönetici/İK görür (backend 403 döner);
  // diğerleri için sayfa doğrudan kendi kaydıyla açılır.
  const canManage = can('timeshift:manage')
  const me = useMyEmployeeId(true)
  const [pickedEmployeeId, setEmployeeId] = useState('')
  const employeeId = canManage ? pickedEmployeeId || (me.employeeId ?? '') : (me.employeeId ?? '')
  const [year, setYear] = useState(now.getFullYear())
  const [month, setMonth] = useState(now.getMonth() + 1)

  const { from, to } = monthRange(year, month)
  const entries = useTimeEntries(
    { employeeId: employeeId || undefined, from, to },
    Boolean(employeeId),
  )
  const summary = useTimeSummary(
    employeeId || undefined,
    year,
    month,
    Boolean(employeeId) && (canManage || employeeId === me.employeeId),
  )

  // NOT: toISOString() UTC tarihi verir; 00:00-03:00 arası (UTC+3) "bugün" dünü
  // gösteriyordu. Yerel tarih kullanılır. Gece vardiyasında açık (çıkışı
  // yapılmamış) kayıt dünün tarihini taşır; kart önce onu gösterir.
  const todayKey = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
  const today = useMemo(
    () =>
      // Açık kayıt yalnızca son 16 saat içinde başladıysa "şu an içeride" sayılır
      // (backend'deki MaxShiftHours ile aynı); daha eskisi unutulmuş çıkıştır,
      // yönetici düzeltir - aksi halde "Çıkış yap" düğmesi kalıcı takılıyordu.
      entries.data?.find(
        (e) => Boolean(e.clockIn) && !e.clockOut && Date.now() - new Date(e.clockIn!).getTime() < 16 * 3_600_000,
      ) ??
      entries.data?.find((e) => e.date?.slice(0, 10) === todayKey),
    [entries.data, todayKey],
  )

  const rows = useMemo(
    () => [...(entries.data ?? [])].sort((a, b) => (a.date < b.date ? 1 : -1)),
    [entries.data],
  )

  const columns: Array<Column<TimeEntry>> = [
    {
      id: 'date',
      header: tx('Tarih'),
      searchText: (e) => formatDate(e.date),
      sortValue: (e) => new Date(e.date).getTime(),
      exportText: (e) => formatDate(e.date),
      cell: (e) => <span className="tabular font-medium">{formatDate(e.date)}</span>,
    },
    {
      id: 'in',
      header: tx('Giriş'),
      hideBelow: 'sm',
      exportText: (e) => clockTime(e.clockIn),
      cell: (e) => <span className="tabular text-muted-foreground">{clockTime(e.clockIn)}</span>,
    },
    {
      id: 'out',
      header: tx('Çıkış'),
      hideBelow: 'sm',
      exportText: (e) => clockTime(e.clockOut),
      cell: (e) => <span className="tabular text-muted-foreground">{clockTime(e.clockOut)}</span>,
    },
    {
      id: 'worked',
      header: tx('Çalışılan'),
      align: 'right',
      sortValue: (e) => e.workedMinutes,
      exportText: (e) => hm(e.workedMinutes),
      cell: (e) => hm(e.workedMinutes),
    },
    {
      id: 'overtime',
      header: tx('Fazla mesai'),
      align: 'right',
      hideBelow: 'md',
      sortValue: (e) => e.overtimeMinutes,
      exportText: (e) => hm(e.overtimeMinutes),
      cell: (e) =>
        e.overtimeMinutes > 0 ? (
          <span className="font-semibold text-[hsl(var(--warning))]">{hm(e.overtimeMinutes)}</span>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Puantaj')}
        description={tx('Günlük giriş/çıkış kayıtları ve aylık çalışma özeti. 8 saati aşan süre fazla mesai sayılır.')}
      />

      <div className="grid max-w-2xl gap-4 sm:grid-cols-[1fr_auto_auto]">
        {canManage ? (
          <EmployeePicker
            value={employeeId}
            onChange={setEmployeeId}
            hint={tx('Kayıtları görmek için çalışan seçin.')}
          />
        ) : (
          <p className="self-end pb-2 text-[13px] text-muted-foreground">
            {me.notLinked ? tx('Hesabınıza bağlı çalışan kaydı bulunamadı.') : tx('Kendi puantajınız')}
          </p>
        )}
        <TextField
          id="timesheet-year"
          label={tx('Yıl')}
          type="number"
          min={2020}
          max={2100}
          value={year}
          className="tabular w-24"
          onChange={(e) => setYear(Number(e.target.value))}
        />
        <TextField
          id="timesheet-month"
          label={tx('Ay')}
          type="number"
          min={1}
          max={12}
          value={month}
          className="tabular w-20"
          onChange={(e) => setMonth(Number(e.target.value))}
        />
      </div>

      {/* Giriş/çıkış düğmesi yalnızca kişinin kendi kaydında: başkasının sayfasına
          bakan yönetici yanlışlıkla onun adına giriş yapmasın. */}
      {employeeId && employeeId === me.employeeId && can('timeshift:clock') && (
        <TodayCard employeeId={employeeId} today={today} />
      )}

      {employeeId && summary.data && (
        <div className="grid gap-3 sm:grid-cols-3">
          <StatCard
            label={tx('Çalışılan gün')}
            value={formatNumber(summary.data.daysWorked)}
            trend={`${month}/${year}`}
            trendDirection="flat"
            trendSense="neutral"
          />
          <StatCard
            label={tx('Toplam süre')}
            value={hm(summary.data.totalWorkedMinutes)}
            trend={tx('Ay toplamı')}
            trendDirection="flat"
            trendSense="neutral"
          />
          <StatCard
            label={tx('Fazla mesai')}
            value={hm(summary.data.totalOvertimeMinutes)}
            trend={summary.data.totalOvertimeMinutes > 0 ? tx('Ek ödeme konusu') : tx('Yok')}
            trendDirection={summary.data.totalOvertimeMinutes > 0 ? 'up' : 'flat'}
            trendSense="negative"
          />
        </div>
      )}

      {!employeeId ? (
        <Panel>
          <PanelHead title={tx('Kayıtlar')} />
          <EmptyState
            title={tx('Çalışan seçilmedi')}
            detail={tx('Puantaj kayıtlarını görmek için yukarıdan bir çalışan seçin.')}
          />
        </Panel>
      ) : (
        <DataTable
          rows={rows}
          rowKey={(e) => e.id}
          columns={columns}
          isLoading={entries.isPending}
          error={entries.error}
          onRetry={() => void entries.refetch()}
          searchPlaceholder={tx('Tarih ara')}
          exportFileName={`puantaj-${year}-${String(month).padStart(2, '0')}`}
          pageSize={15}
          emptyTitle={tx('Bu ayda kayıt yok')}
          emptyDetail={tx('Seçili ay için giriş/çıkış kaydı bulunmuyor.')}
        />
      )}

      {can('timeshift:clock') && <OvertimePanel />}

      <AttendanceReportPanel />

      {isHr(roles, 'ext-timeshift-manage') && <TimesheetAnomalyPanel />}
      {isHr(roles, 'ext-timeshift-manage') && <TimesheetPeriodsPanel />}
    </div>
  )
}
