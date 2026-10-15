/**
 * Yıllık görünüm.
 *
 * 365 sütunluk bir tablo okunmaz; bunun yerine iki parça:
 *  1. Yıl özeti — her üye için gündüz / gece / tatil / izin / resmî tatil
 *     sayıları ve toplam vardiya saati (iş yükü dengesini görmek için).
 *  2. Seçili üyenin 12 aylık mini takvimi — özet tablosunda satıra tıklanınca
 *     değişir.
 */

import { Fragment, useState } from 'react'
import { useTeamYearRosters } from '@/api/queries-shift-engine'
import type { RosterDay, RosterDayType, TeamRoster } from '@/api/timeshift'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { errorText } from '@/features/performance/components/controls'
import { usePeople } from '@/features/performance/hooks'
import {
  DAY_STYLE,
  DayBlock,
  Legend,
  OVERRIDE_TYPES,
  PATTERN_TYPES,
  dateRange,
  describeRosterDay,
  formatLongDay,
  hoursLabel,
  monthEnd,
  shiftMinutes,
  styleOf,
  todayIso,
  weekdayOf,
} from './shared'
import { tx } from '@/lib/i18n'

const COUNTED: RosterDayType[] = ['Day', 'Night', 'Off', 'Leave', 'Holiday', 'Manual']
const MONTHS = [tx('Ocak'), tx('Şubat'), tx('Mart'), tx('Nisan'), tx('Mayıs'), tx('Haziran'), tx('Temmuz'), tx('Ağustos'), tx('Eylül'), tx('Ekim'), tx('Kasım'), tx('Aralık')]
const WEEK_HEAD = [tx('Pt'), tx('Sa'), tx('Ça'), tx('Pe'), tx('Cu'), tx('Ct'), tx('Pa')]

interface YearRow {
  key: string
  team: string
  employeeId: string
  rank: number
  tag: string | null
  byDate: Map<string, RosterDay>
  counts: Record<RosterDayType, number>
  minutes: number
}

/** Bir ekibin 12 aylık parçasını üye bazında tek bir yıl satırına birleştirir. */
function mergeTeam(chunks: TeamRoster[]): { team: string; patternName: string; rows: YearRow[] } {
  const rows = new Map<string, YearRow>()
  const last = chunks[chunks.length - 1]
  for (const chunk of chunks) {
    for (const m of chunk.members ?? []) {
      const key = `${chunk.team}-${m.employeeId}`
      const row =
        rows.get(key) ??
        ({
          key,
          team: chunk.team,
          employeeId: m.employeeId,
          rank: m.rank,
          tag: m.tag,
          byDate: new Map(),
          counts: { Day: 0, Night: 0, Off: 0, Leave: 0, Holiday: 0, Manual: 0 },
          minutes: 0,
        } satisfies YearRow)
      // Sıra ve etiket en güncel aydan gelsin.
      row.rank = m.rank
      row.tag = m.tag
      for (const d of m.schedule ?? []) row.byDate.set(d.date.slice(0, 10), d)
      rows.set(key, row)
    }
  }
  for (const row of rows.values()) {
    for (const d of row.byDate.values()) {
      if (d.type !== null && d.type in row.counts) row.counts[d.type as RosterDayType]++
      if (d.startTime && d.endTime) row.minutes += shiftMinutes(d.startTime, d.endTime)
    }
  }
  return {
    team: last?.team ?? '—',
    patternName: last?.patternName ?? '—',
    rows: [...rows.values()].sort((a, b) => a.rank - b.rank),
  }
}

function MiniMonth({ year, month, row }: { year: number; month: number; row: YearRow }) {
  const from = `${year}-${String(month + 1).padStart(2, '0')}-01`
  const days = dateRange(from, monthEnd(from))
  const lead = (weekdayOf(from) + 6) % 7 // pazartesi başlangıçlı
  const today = todayIso()
  let worked = 0
  let away = 0
  for (const d of days) {
    const t = row.byDate.get(d)?.type
    if (t === 'Day' || t === 'Night') worked++
    else if (t === 'Leave' || t === 'Holiday' || t === 'Manual') away++
  }

  return (
    <div className="rounded-lg border border-border p-2.5">
      <div className="mb-1.5 flex items-baseline justify-between gap-2">
        <p className="text-[13px] font-semibold">{MONTHS[month]}</p>
        <p className="text-[11px] text-muted-foreground tabular">
          {tx('{0} vardiya{1}', [worked, away ? tx(' · {0} istisna', [away]) : ''])}
        </p>
      </div>
      <div className="grid grid-cols-7 gap-0.5 text-center text-[10px]">
        {WEEK_HEAD.map((w) => (
          <span key={w} className="pb-0.5 text-muted-foreground/70">
            {w}
          </span>
        ))}
        {Array.from({ length: lead }, (_, i) => (
          <span key={`b${i}`} />
        ))}
        {days.map((d) => {
          const day = row.byDate.get(d)
          const n = Number(d.slice(8))
          return day ? (
            <span
              key={d}
              title={`${formatLongDay(d)}\n${describeRosterDay(day)}`}
              className={cn(
                'tabular flex aspect-square items-center justify-center rounded-[4px] ring-1 ring-inset',
                styleOf(day.type).block,
                d === today && 'ring-2 ring-foreground/80',
              )}
            >
              {n}
            </span>
          ) : (
            <span
              key={d}
              title={tx('{0}\nKayıt yok', [formatLongDay(d)])}
              className={cn('tabular flex aspect-square items-center justify-center text-muted-foreground/50', d === today && 'rounded-[4px] ring-2 ring-foreground/80')}
            >
              {n}
            </span>
          )
        })}
      </div>
    </div>
  )
}

export function YearView({ teamIds, year, showTeams }: { teamIds: string[]; year: number; showTeams: boolean }) {
  const people = usePeople()
  const results = useTeamYearRosters(teamIds, year)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)

  const loaded = results.filter((r) => r.data).length
  const pending = results.some((r) => r.isPending)
  const failed = results.find((r) => r.isError)

  const groups = teamIds
    .map((_, ti) =>
      results
        .slice(ti * 12, ti * 12 + 12)
        .map((r) => r.data)
        .filter((r): r is TeamRoster => Boolean(r)),
    )
    .filter((chunks) => chunks.length > 0)
    .map(mergeTeam)

  const allRows = groups.flatMap((g) => g.rows)
  const selected = allRows.find((r) => r.key === selectedKey) ?? allRows[0] ?? null

  if (failed) {
    return (
      <Panel>
        <ErrorState
          message={errorText(failed.error)}
          onRetry={() => results.filter((r) => r.isError).forEach((r) => void r.refetch())}
        />
      </Panel>
    )
  }
  if (pending && allRows.length === 0) {
    return (
      <Panel>
        <PanelHead title={String(year)} note={tx('Takvim yükleniyor · {0}/{1} ay', [loaded, results.length])} />
        <RowsSkeleton rows={6} columns={8} />
      </Panel>
    )
  }
  if (allRows.length === 0) {
    return (
      <Panel>
        <EmptyState title={tx('Bu yıl için kayıt yok')} detail={tx('Ekipte üye yok ya da seçili yılda takvim hesaplanmadı.')} />
      </Panel>
    )
  }

  const selectedName = selected ? people.nameOf(selected.employeeId) : ''

  return (
    <div className="space-y-4">
      <Panel>
        <PanelHead
          title={tx('{0} özeti', [year])}
          note={
            pending
              ? tx('Yükleniyor · {0}/{1} ay', [loaded, results.length])
              : tx('Satıra tıklayın; aşağıda o kişinin yıllık takvimi açılır.')
          }
          action={<Legend types={[...PATTERN_TYPES, ...OVERRIDE_TYPES]} />}
        />
        <div className="overflow-x-auto">
          <table className="w-full min-w-[640px] text-[13px]">
            <thead>
              <tr className="border-b border-border text-[12px] text-muted-foreground">
                <th scope="col" className="px-4 py-2 text-left font-medium">
                  {tx('Çalışan')}
                </th>
                {COUNTED.map((t) => (
                  <th key={t} scope="col" className="px-2 py-2 text-right font-medium">
                    <span className="inline-flex items-center gap-1.5">
                      <DayBlock type={t} size="xs" />
                      {DAY_STYLE[t].label}
                    </span>
                  </th>
                ))}
                <th scope="col" className="px-4 py-2 text-right font-medium">
                  {tx('Vardiya saati')}
                </th>
              </tr>
            </thead>
            <tbody>
              {groups.map((g) => (
                <Fragment key={g.team}>
                  {showTeams && (
                    <tr>
                      <th colSpan={COUNTED.length + 2} scope="colgroup" className="border-b border-border bg-muted/50 px-4 py-1.5 text-left text-[12px] font-semibold">
                        {g.team}
                        <span className="ml-2 font-normal text-muted-foreground">{g.patternName}</span>
                      </th>
                    </tr>
                  )}
                  {g.rows.map((row) => {
                    const on = selected?.key === row.key
                    return (
                      <tr key={row.key} className={cn('border-b border-border', on ? 'bg-primary/8' : 'hover:bg-muted/50')}>
                        <th scope="row" className="px-4 py-1.5 text-left font-normal">
                          <button
                            type="button"
                            aria-pressed={on}
                            onClick={() => setSelectedKey(row.key)}
                            className="flex cursor-pointer items-center gap-2 text-left"
                          >
                            <span className="tabular flex size-5 shrink-0 items-center justify-center rounded bg-muted text-[10px] font-semibold text-muted-foreground">
                              {row.rank}
                            </span>
                            <span className={cn('truncate', on ? 'font-semibold' : 'font-medium')}>
                              {people.nameOf(row.employeeId)}
                            </span>
                            {row.tag && <span className="truncate text-[11px] text-muted-foreground">{row.tag}</span>}
                          </button>
                        </th>
                        {COUNTED.map((t) => (
                          <td key={t} className={cn('tabular px-2 text-right', row.counts[t] ? '' : 'text-muted-foreground/50')}>
                            {row.counts[t] || '—'}
                          </td>
                        ))}
                        <td className="tabular px-4 text-right font-medium">{hoursLabel(row.minutes)}</td>
                      </tr>
                    )
                  })}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      </Panel>

      {selected && (
        <Panel>
          <PanelHead
            title={tx('{0} · {1} takvimi', [selectedName, year])}
            note={tx('{0} · {1} vardiya, {2}', [selected.team, formatNumber(selected.counts.Day + selected.counts.Night), hoursLabel(selected.minutes)])}
          />
          <PanelBody>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
              {MONTHS.map((_, m) => (
                <MiniMonth key={m} year={year} month={m} row={selected} />
              ))}
            </div>
          </PanelBody>
        </Panel>
      )}
    </div>
  )
}
