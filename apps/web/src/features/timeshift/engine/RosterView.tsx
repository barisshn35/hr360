/**
 * Vardiya takvimi: seçili ekibin (ya da tüm ekiplerin) seçili aralıkta kimin
 * hangi vardiyada olduğu.
 *
 * - Üyeler sıraya (rank) göre dizilir; 1 en üstte.
 * - İzin / resmî tatil / elle değişiklik (override) desenin üzerine biner ve
 *   çizgili, ayrı renkte gösterilir — backend izin onaylanınca bunu kendisi
 *   üretir, arayüz ayrıca bir şey yapmaz.
 * - Görünümler: Hafta (geniş hücre, saat yazılı) · 2 hafta · Ay · Yıl
 *   (yıl ayrı bileşende: özet tablosu + kişi başına 12 aylık mini takvim).
 * - Kişi + gün bazlı vardiya atamaları (Öneri sekmesinden uygulanan ya da elle atanan) hücrede
 *   çerçeveyle işaretlenir; ayrıntısı ipucunda ve haftalık görünümde yazılıdır.
 * - En altta günlük kapsama satırı: o gün kaç kişi gündüzde, kaç kişi gecede.
 *   "Tüm ekipler" seçiliyken 7/24 boşluk varsa buradan görünür.
 */

import { Fragment, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, CalendarRange } from 'lucide-react'
import { useShiftTeams, useTeamRosters } from '@/api/queries-shift-engine'
import type { RosterDay, TeamRoster } from '@/api/timeshift'
import { opsApi, type RosterAssignment } from '@/api/opsPlus'
import { Panel, PanelHead } from '@/components/ui/Panel'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { SelectField } from '@/components/ui/Field'
import { Button } from '@/components/ui/button'
import { formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Segmented, errorText } from '@/features/performance/components/controls'
import { usePeople } from '@/features/performance/hooks'
import {
  DAY_STYLE,
  DayBlock,
  Legend,
  OVERRIDE_TYPES,
  PATTERN_TYPES,
  addDays,
  dateRange,
  describeRosterDay,
  formatDayMonth,
  formatLongDay,
  formatMonth,
  isOverride,
  isWeekend,
  mondayOf,
  styleOf,
  timeRange,
  monthEnd,
  monthStart,
  todayIso,
  weekdayShort,
} from './shared'
import { YearView } from './YearView'
import { tx } from '@/lib/i18n'
import { useAuth } from '@/auth/useAuth'

export const ALL_TEAMS = 'tumu'

type Span = 'hafta' | 'hafta2' | 'ay' | 'yil'

function rangeOf(span: Span, cursor: string) {
  if (span === 'yil') return { from: `${cursor.slice(0, 4)}-01-01`, to: `${cursor.slice(0, 4)}-12-31` }
  if (span === 'ay') return { from: monthStart(cursor), to: monthEnd(cursor) }
  const from = mondayOf(cursor)
  return { from, to: addDays(from, span === 'hafta' ? 6 : 13) }
}

function shift(span: Span, cursor: string, dir: -1 | 1) {
  if (span === 'hafta') return addDays(cursor, dir * 7)
  if (span === 'hafta2') return addDays(cursor, dir * 14)
  if (span === 'yil') return `${Number(cursor.slice(0, 4)) + dir}-${cursor.slice(5, 7)}-01`
  // Ayın 1'inden bir gün geri → önceki ay; ayın sonundan bir gün ileri → sonraki ay.
  return dir < 0 ? monthStart(addDays(monthStart(cursor), -1)) : addDays(monthEnd(cursor), 1)
}

function rangeTitle(span: Span, from: string, to: string) {
  if (span === 'yil') return from.slice(0, 4)
  if (span === 'ay') return formatMonth(from)
  return `${formatDayMonth(from)} – ${formatDayMonth(to)} ${to.slice(0, 4)}`
}

const SPAN_LABEL: Record<Span, string> = { hafta: tx('Önceki hafta'), hafta2: tx('Önceki 2 hafta'), ay: tx('Önceki ay'), yil: tx('Önceki yıl') }
const SPAN_NEXT: Record<Span, string> = { hafta: tx('Sonraki hafta'), hafta2: tx('Sonraki 2 hafta'), ay: tx('Sonraki ay'), yil: tx('Sonraki yıl') }

interface Row {
  teamName: string
  employeeId: string
  rank: number
  tag: string | null
  byDate: Map<string, RosterDay>
  worked: number
}

function toRows(roster: TeamRoster): Row[] {
  return [...(roster.members ?? [])]
    .sort((a, b) => a.rank - b.rank)
    .map((m) => {
      const byDate = new Map((m.schedule ?? []).map((d) => [d.date.slice(0, 10), d]))
      const worked = [...byDate.values()].filter((d) => d.type === 'Day' || d.type === 'Night').length
      return { teamName: roster.team, employeeId: m.employeeId, rank: m.rank, tag: m.tag, byDate, worked }
    })
}

/** Haftalık görünümün geniş hücresi: tip adı ve saat (ya da override notu) yazılı. */
/** Atanmış vardiyanın kısa açıklaması: "Akşam 16:00–24:00". */
function assignedText(a: RosterAssignment): string {
  return a.shift ? `${a.shift.name} ${timeRange(a.shift.startTime, a.shift.endTime)}` : tx('Atanmış vardiya')
}

function WideCell({ day, date, assigned }: { day: RosterDay | undefined; date: string; assigned?: RosterAssignment }) {
  if (!day && assigned) {
    return (
      <span title={`${formatLongDay(date)}\n${tx('Atanan vardiya: {0}', [assignedText(assigned)])}`}
        className="flex h-11 min-w-24 flex-col justify-center rounded-md px-2 text-left ring-2 ring-primary ring-inset">
        <span className="text-[12px] font-semibold">{assigned.shift?.name ?? tx('Atanmış vardiya')}</span>
        {assigned.shift && <span className="tabular block max-w-28 truncate text-[11px] opacity-80">{timeRange(assigned.shift.startTime, assigned.shift.endTime)}</span>}
      </span>
    )
  }
  if (!day) return <span className="block h-11 min-w-24" title={tx('{0}\nKayıt yok', [formatLongDay(date)])} />
  const s = styleOf(day.type)
  const Icon = s.icon
  const showAssigned = assigned && !isOverride(day.type)
  const detail = showAssigned ? assignedText(assigned) : day.startTime ? timeRange(day.startTime, day.endTime!) : day.note ?? ''
  return (
    <span
      title={`${formatLongDay(date)}
${describeRosterDay(day)}${assigned ? `\n${tx('Atanan vardiya: {0}', [assignedText(assigned)])}` : ''}`}
      className={cn('flex h-11 min-w-24 flex-col justify-center rounded-md px-2 text-left ring-1 ring-inset', s.block, showAssigned && 'ring-2 ring-primary')}
    >
      <span className="flex items-center gap-1 text-[12px] font-semibold">
        <Icon aria-hidden className="size-3.5 shrink-0" strokeWidth={1.75} />
        {s.label}
        {'partial' in day && day.partial && <span className="font-normal">{' '}½</span>}
      </span>
      {detail && <span className="tabular block max-w-28 truncate text-[11px] opacity-80">{detail}</span>}
    </span>
  )
}

function Cell({ day, date, name, assigned }: { day: RosterDay | undefined; date: string; name: string; assigned?: RosterAssignment }) {
  const extra = assigned ? `\n${tx('Atanan vardiya: {0}', [assignedText(assigned)])}` : ''
  if (!day) {
    return assigned
      ? <span className="mx-auto block size-7 rounded-md ring-2 ring-primary ring-inset" title={`${formatLongDay(date)}${extra}`} aria-label={tx('{0}, {1}: {2}', [name, formatLongDay(date), assignedText(assigned)])} />
      : <span className="block size-7" aria-label={tx('{0}, {1}: kayıt yok', [name, formatLongDay(date)])} />
  }
  const label = describeRosterDay(day)
  return (
    <DayBlock
      type={day.type}
      size="sm"
      title={`${formatLongDay(date)}\n${label}${extra}`}
      className={cn('size-7', isOverride(day.type) && 'ring-[1.5px]', 'partial' in day && day.partial && 'opacity-60', assigned && !isOverride(day.type) && 'ring-2 ring-primary')}
    />
  )
}

export function RosterView({ teamId, onTeamChange }: { teamId: string | null; onTeamChange: (id: string) => void }) {
  const teams = useShiftTeams()
  const people = usePeople()
  const manage = useAuth().can('timeshift:manage')
  const [span, setSpan] = useState<Span>('hafta2')
  const today = todayIso()
  const [cursor, setCursor] = useState(today)

  const list = useMemo(
    () => [...(teams.data ?? [])].sort((a, b) => a.name.localeCompare(b.name, 'tr-TR')),
    [teams.data],
  )
  const selected = teamId === ALL_TEAMS ? ALL_TEAMS : (list.find((t) => t.id === teamId)?.id ?? list[0]?.id ?? null)
  const ids = selected === ALL_TEAMS ? list.map((t) => t.id) : selected ? [selected] : []

  const { from, to } = rangeOf(span, cursor)
  const dates = useMemo(() => dateRange(from, to), [from, to])
  const isYear = span === 'yil'
  const wide = span === 'hafta'
  // Yıl görünümü kendi (aylık parçalı) isteklerini yapar; burada boşa 365 günlük istek atılmasın.
  const rosters = useTeamRosters(isYear ? [] : ids, from, to)
  // Kişi + gün atamaları (Öneri sekmesinden uygulananlar dahil). 'timeshift' önekiyle: uygulama sonrası yenilenir.
  const assignments = useQuery({
    queryKey: ['timeshift', 'assignments', from, to],
    queryFn: ({ signal }) => opsApi.roster({ from, to }, signal),
    enabled: !isYear && ids.length > 0,
    retry: false,
  })
  const assignedMap = useMemo(
    () => new Map((assignments.data ?? []).map((a) => [`${a.employeeId}|${a.date.slice(0, 10)}`, a])),
    [assignments.data],
  )

  const pending = rosters.some((r) => r.isPending)
  const failed = rosters.find((r) => r.isError)
  const fetching = rosters.some((r) => r.isFetching)
  const groups = rosters
    .map((r) => r.data)
    .filter((r): r is TeamRoster => Boolean(r))
    .map((r) => ({ team: r.team, patternName: r.patternName, rows: toRows(r) }))

  // Tek ekipte "0" olağandır (ekip tatilde); boşluk uyarısı yalnızca tüm ekipler birlikteyken anlamlı.
  const coverage = dates.map((d) => {
    let day = 0
    let night = 0
    let away = 0
    for (const g of groups)
      for (const row of g.rows) {
        const t = row.byDate.get(d)?.type
        if (t === 'Day') day++
        else if (t === 'Night') night++
        else if (t && isOverride(t)) away++
      }
    return { date: d, day, night, away }
  })
  const totalRows = groups.reduce((n, g) => n + g.rows.length, 0)
  const showTeamHeaders = selected === ALL_TEAMS

  if (teams.isPending) {
    return (
      <Panel>
        <RowsSkeleton rows={6} columns={6} />
      </Panel>
    )
  }
  if (teams.isError) {
    return (
      <Panel>
        <ErrorState message={errorText(teams.error)} onRetry={() => void teams.refetch()} />
      </Panel>
    )
  }
  if (list.length === 0) {
    return (
      <Panel>
        <EmptyState
          icon={CalendarRange}
          title={tx('Takvim için önce bir vardiya ekibi gerekli')}
          detail={
            manage
              ? tx('Ekipler sekmesinden bir ekip kurup üye ekleyin; takvim desenden kendiliğinden hesaplanır.')
              : tx('Henüz bir vardiya ekibine eklenmediniz. Vardiya ekiplerini İK yöneticisi kurar.')
          }
        />
      </Panel>
    )
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="w-full sm:w-72">
          <SelectField
            label={tx('Ekip')}
            value={selected ?? ''}
            onChange={onTeamChange}
            options={[
              ...list.map((t) => ({ value: t.id, label: t.name })),
              ...(list.length > 1 ? [{ value: ALL_TEAMS, label: tx('Tüm ekipler (7/24 görünüm)') }] : []),
            ]}
          />
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Segmented
            ariaLabel={tx('Aralık')}
            value={span}
            onChange={setSpan}
            options={[
              { value: 'hafta', label: tx('Hafta') },
              { value: 'hafta2', label: '2 hafta' },
              { value: 'ay', label: tx('Ay') },
              { value: 'yil', label: tx('Yıl') },
            ]}
          />
          <div className="flex items-center gap-1">
            <Button size="sm" variant="outline" aria-label={SPAN_LABEL[span]} onClick={() => setCursor((c) => shift(span, c, -1))}>
              <ChevronLeft className="size-4" />
            </Button>
            <Button size="sm" variant="outline" onClick={() => setCursor(today)}>
              {tx('Bugün')}
            </Button>
            <Button size="sm" variant="outline" aria-label={SPAN_NEXT[span]} onClick={() => setCursor((c) => shift(span, c, 1))}>
              <ChevronRight className="size-4" />
            </Button>
          </div>
        </div>
      </div>

      {isYear ? (
        <YearView teamIds={ids} year={Number(from.slice(0, 4))} showTeams={showTeamHeaders} />
      ) : (
      <Panel>
        <PanelHead
          title={
            <span className="flex items-center gap-2">
              {rangeTitle(span, from, to)}
              {fetching && !pending && <span className="text-[12px] font-normal text-muted-foreground">{tx('güncelleniyor…')}</span>}
            </span>
          }
          note={
            groups.length === 1
              ? tx('{0} · {1} · {2} kişi, sıraya göre', [groups[0].team, groups[0].patternName, formatNumber(totalRows)])
              : groups.length > 1
                ? tx('{0} ekip · {1} kişi', [formatNumber(groups.length), formatNumber(totalRows)])
                : undefined
          }
          action={
            <span className="flex flex-wrap items-center gap-3">
              <Legend types={[...PATTERN_TYPES, ...OVERRIDE_TYPES]} />
              {assignedMap.size > 0 && (
                <span className="flex items-center gap-1.5 text-[11.5px] text-muted-foreground">
                  <span aria-hidden className="inline-block size-3 rounded-sm ring-2 ring-primary ring-inset" />
                  {tx('Atanan vardiya')}
                </span>
              )}
            </span>
          }
        />

        {pending && groups.length === 0 ? (
          <RowsSkeleton rows={6} columns={8} />
        ) : failed ? (
          <ErrorState message={errorText(failed.error)} onRetry={() => rosters.forEach((r) => void r.refetch())} />
        ) : totalRows === 0 ? (
          <EmptyState
            title={tx('Ekipte üye yok')}
            detail={manage ? tx('Takvimde gösterilecek kimse yok. Ekipler sekmesinden üye ekleyin.') : tx('Takvimde gösterilecek kimse yok. Üyeleri İK yöneticisi ekler.')}
          />
        ) : (
          <div className={cn('overflow-x-auto transition-opacity', fetching && !pending && 'opacity-70')}>
            <table className="w-max min-w-full border-separate border-spacing-0 text-[12px]">
              <thead>
                <tr>
                  <th
                    scope="col"
                    className="sticky left-0 z-20 min-w-48 border-b border-border bg-card px-4 py-2 text-left font-medium text-muted-foreground"
                  >
                    {tx('Çalışan')}
                  </th>
                  {dates.map((d) => (
                    <th
                      key={d}
                      scope="col"
                      className={cn(
                        'border-b border-border px-0.5 py-1.5 text-center font-normal',
                        d === today && 'bg-primary/10',
                        isWeekend(d) ? 'text-muted-foreground/70' : 'text-muted-foreground',
                      )}
                    >
                      <span className="block text-[10px] leading-tight">{weekdayShort(d)}</span>
                      <span className={cn('tabular block leading-tight', d === today && 'font-semibold text-primary')}>
                        {wide ? formatDayMonth(d) : Number(d.slice(8))}
                      </span>
                    </th>
                  ))}
                  <th scope="col" className="border-b border-border px-3 py-2 text-right font-medium text-muted-foreground" title={tx('Aralıktaki vardiya günü')}>
                    {tx('Vardiya')}
                  </th>
                </tr>
              </thead>
              <tbody>
                {groups.map((g) => (
                  <Fragment key={g.team}>
                    {showTeamHeaders && (
                      <tr>
                        <th
                          scope="colgroup"
                          colSpan={dates.length + 2}
                          className="sticky left-0 border-b border-border bg-muted/50 px-4 py-1.5 text-left text-[12px] font-semibold"
                        >
                          {g.team}
                          <span className="ml-2 font-normal text-muted-foreground">{g.patternName}</span>
                        </th>
                      </tr>
                    )}
                    {g.rows.map((row) => {
                      const name = people.nameOf(row.employeeId)
                      return (
                        <tr key={`${g.team}-${row.employeeId}`} className="group">
                          <th
                            scope="row"
                            className="sticky left-0 z-10 border-b border-border bg-card px-4 py-1.5 text-left font-normal group-hover:bg-muted"
                          >
                            <span className="flex items-center gap-2">
                              <span className="tabular flex size-5 shrink-0 items-center justify-center rounded bg-muted text-[10px] font-semibold text-muted-foreground" title={tx('Sıra (rank)')}>
                                {row.rank}
                              </span>
                              <span className="min-w-0">
                                <span className="block max-w-40 truncate text-[13px] font-medium">{name}</span>
                                {row.tag && <span className="block max-w-40 truncate text-[11px] text-muted-foreground">{row.tag}</span>}
                              </span>
                            </span>
                          </th>
                          {dates.map((d) => (
                            <td
                              key={d}
                              className={cn('border-b border-border py-1 text-center group-hover:bg-muted/50', wide ? 'px-1' : 'px-0.5', d === today && 'bg-primary/5')}
                            >
                              {wide ? (
                                <WideCell day={row.byDate.get(d)} date={d} assigned={assignedMap.get(`${row.employeeId}|${d}`)} />
                              ) : (
                                <Cell day={row.byDate.get(d)} date={d} name={name} assigned={assignedMap.get(`${row.employeeId}|${d}`)} />
                              )}
                            </td>
                          ))}
                          <td className="tabular border-b border-border px-3 text-right text-[13px] group-hover:bg-muted/50">
                            {row.worked}
                          </td>
                        </tr>
                      )
                    })}
                  </Fragment>
                ))}
              </tbody>
              <tfoot>
                {(['Day', 'Night'] as const).map((t) => (
                  <tr key={t}>
                    <th scope="row" className="sticky left-0 z-10 bg-card px-4 py-1 text-left text-[12px] font-normal text-muted-foreground">
                      <span className="flex items-center gap-1.5">
                        <DayBlock type={t} size="xs" />
                        {tx('{0}de', [DAY_STYLE[t].label])}</span>
                    </th>
                    {coverage.map((c) => {
                      const n = t === 'Day' ? c.day : c.night
                      return (
                        <td
                          key={c.date}
                          className={cn(
                            'tabular px-0.5 py-1 text-center text-[11px]',
                            c.date === today && 'bg-primary/5',
                            n === 0 && showTeamHeaders ? 'font-semibold text-destructive' : 'text-muted-foreground',
                          )}
                          title={n === 0 && showTeamHeaders ? tx('{0}: {1} vardiyasında kimse yok', [formatLongDay(c.date), DAY_STYLE[t].lower]) : undefined}
                        >
                          {n}
                        </td>
                      )
                    })}
                    <td />
                  </tr>
                ))}
                <tr>
                  <th scope="row" className="sticky left-0 z-10 bg-card px-4 pt-1 pb-2 text-left text-[12px] font-normal text-muted-foreground">
                    <span className="flex items-center gap-1.5">
                      <DayBlock type="Leave" size="xs" />
                      {tx('İzin / tatil')}
                    </span>
                  </th>
                  {coverage.map((c) => (
                    <td key={c.date} className={cn('tabular px-0.5 pt-1 pb-2 text-center text-[11px] text-muted-foreground', c.date === today && 'bg-primary/5')}>
                      {c.away || ''}
                    </td>
                  ))}
                  <td />
                </tr>
              </tfoot>
            </table>
          </div>
        )}
      </Panel>
      )}
    </div>
  )
}
