/**
 * Desenler: şirketin kendi vardiya döngülerini tanımladığı yer.
 *
 * Desen, art arda dizilmiş günlerden oluşan bir döngüdür (ör. 3 gece →
 * 3 tatil → 3 gündüz). İzin ve resmî tatil gibi istisnalar desene YAZILMAZ —
 * onlar takvimde override olarak desenin üzerine biner.
 */

import { useMemo, useState } from 'react'
import {
  ArrowLeft,
  ArrowRight,
  Copy,
  LoaderCircle,
  Plus,
  Repeat,
  Trash2,
  TriangleAlert,
} from 'lucide-react'
import {
  useCreateShiftPattern,
  useDeleteShiftPattern,
  useShiftPatterns,
  useShiftTeams,
} from '@/api/queries-shift-engine'
import type { ShiftDayType, ShiftPattern, ShiftPatternDayInput } from '@/api/timeshift'
import { useQuery } from '@tanstack/react-query'
import { opsApi } from '@/api/opsPlus'
import { limitsFrom, patternWarnings } from '@/lib/workRules'
import { useAuth } from '@/auth/useAuth'
import { Modal } from '@/components/ui/Modal'
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { TextField } from '@/components/ui/Field'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/ui/Toast'
import { formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { ApiError } from '@/api/client'
import { Segmented, errorText } from '@/features/performance/components/controls'
import {
  DAY_STYLE,
  DayBlock,
  LaborWarnings,
  Legend,
  PATTERN_TYPES,
  PatternStrip,
  describeDays,
  durationLabel,
  hhmm,
  sortedDays,
  timeRange,
} from './shared'
import { tx, appLocale } from '@/lib/i18n'

/* --------------------------------- Yardımcılar --------------------------------- */

type DraftDay = ShiftPatternDayInput

const DEFAULT_TIMES: Record<Exclude<ShiftDayType, 'Off'>, { start: string; end: string }> = {
  Day: { start: '09:00', end: '21:00' },
  Night: { start: '21:00', end: '09:00' },
}

const MAX_DAYS = 62

const makeDay = (type: ShiftDayType, times = DEFAULT_TIMES): DraftDay =>
  type === 'Off'
    ? { type, startTime: null, endTime: null }
    : { type, startTime: times[type].start, endTime: times[type].end }

const repeat = (type: ShiftDayType, n: number, times = DEFAULT_TIMES) =>
  Array.from({ length: n }, () => makeDay(type, times))

const PRESETS: Array<{ key: string; label: string; days: () => DraftDay[] }> = [
  {
    key: '3-3-3',
    label: tx('3 gece · 3 tatil · 3 gündüz'),
    days: () => [...repeat('Night', 3), ...repeat('Off', 3), ...repeat('Day', 3)],
  },
  {
    key: '2-2-4',
    label: tx('2 gündüz · 2 gece · 4 tatil'),
    days: () => [...repeat('Day', 2), ...repeat('Night', 2), ...repeat('Off', 4)],
  },
  {
    key: '5-2',
    label: tx('5 gündüz · 2 tatil'),
    days: () => [...repeat('Day', 5, { ...DEFAULT_TIMES, Day: { start: '08:30', end: '17:30' } }), ...repeat('Off', 2)],
  },
]

/** "3 Gece / 3 Tatil / 3 Gündüz" */
function suggestName(days: DraftDay[]): string {
  return describeDays(days)
    .split(' → ')
    .map((part) => part.replace(/ (\p{L})/u, (_, c: string) => ` ${c.toLocaleUpperCase(appLocale)}`))
    .join(' / ')
}

/** Haftalık ortalama çalışma saati — döngü haftaya tam bölünmese de karşılaştırılabilir. */
function weeklyHours(days: Array<{ startTime: string | null; endTime: string | null }>): number {
  if (days.length === 0) return 0
  const minutes = days.reduce((sum, d) => {
    if (!d.startTime || !d.endTime) return sum
    const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))
    let m = toMin(d.endTime) - toMin(d.startTime)
    if (m <= 0) m += 24 * 60
    return sum + m
  }, 0)
  return (minutes / 60 / days.length) * 7
}

const hoursText = (h: number) => `${h.toLocaleString(appLocale, { maximumFractionDigits: 1 })} sa`

/* ------------------------------- Desen oluşturucu ------------------------------- */

function PatternBuilderDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast()
  const create = useCreateShiftPattern()

  const [days, setDays] = useState<DraftDay[]>(() => PRESETS[0].days())
  const ruleSettings = useQuery({ queryKey: ['timeshift', 'rules'], queryFn: ({ signal }) => opsApi.timesheetSettings(signal), staleTime: 5 * 60_000 })
  const ruleWarnings = useMemo(() => patternWarnings(days, limitsFrom(ruleSettings.data)), [days, ruleSettings.data])
  const [selected, setSelected] = useState<number | null>(null)
  const [name, setName] = useState('')
  const [nameTouched, setNameTouched] = useState(false)
  const [submitted, setSubmitted] = useState(false)

  // "Blok ekle" satırı
  const [blockType, setBlockType] = useState<ShiftDayType>('Day')
  const [blockCount, setBlockCount] = useState(3)
  const [times, setTimes] = useState(DEFAULT_TIMES)

  const effectiveName = nameTouched ? name : suggestName(days)

  const dayErrors = days.map((d) => {
    if (d.type === 'Off') return null
    if (!d.startTime || !d.endTime) return tx('Başlangıç ve bitiş saati gerekli.')
    if (d.startTime === d.endTime) return tx('Başlangıç ve bitiş aynı olamaz.')
    return null
  })
  const firstBadDay = dayErrors.findIndex(Boolean)
  const errors = {
    name: !effectiveName.trim() ? tx('Desene bir ad verin.') : undefined,
    days:
      days.length === 0
        ? tx('En az bir gün ekleyin.')
        : days.every((d) => d.type === 'Off')
          ? tx('Desende en az bir çalışma günü (gündüz ya da gece) olmalı.')
          : firstBadDay >= 0
            ? tx('{0}. gün: {1}', [firstBadDay + 1, dayErrors[firstBadDay]])
            : undefined,
  }
  const valid = !errors.name && !errors.days

  const update = (i: number, patch: Partial<DraftDay>) =>
    setDays((prev) => prev.map((d, j) => (j === i ? { ...d, ...patch } : d)))

  const setType = (i: number, type: ShiftDayType) =>
    setDays((prev) =>
      prev.map((d, j) => {
        if (j !== i) return d
        if (type === 'Off') return makeDay('Off')
        // Tip değişince o tipin son kullanılan saatleri gelsin; aynı tipse saat korunur.
        return d.type === type ? d : makeDay(type, times)
      }),
    )

  const move = (i: number, dir: -1 | 1) => {
    const j = i + dir
    if (j < 0 || j >= days.length) return
    setDays((prev) => {
      const next = [...prev]
      ;[next[i], next[j]] = [next[j], next[i]]
      return next
    })
    setSelected(j)
  }

  const duplicate = (i: number) => {
    if (days.length >= MAX_DAYS) return
    setDays((prev) => [...prev.slice(0, i + 1), { ...prev[i] }, ...prev.slice(i + 1)])
    setSelected(i + 1)
  }

  const remove = (i: number) => {
    setDays((prev) => prev.filter((_, j) => j !== i))
    setSelected(null)
  }

  const addBlock = () => {
    const room = MAX_DAYS - days.length
    const n = Math.max(0, Math.min(blockCount, room))
    if (n === 0) return
    setDays((prev) => [...prev, ...repeat(blockType, n, times)])
    setSelected(null)
  }

  const submit = () => {
    setSubmitted(true)
    if (!valid) return
    create.mutate(
      {
        name: effectiveName.trim(),
        days: days.map((d) =>
          d.type === 'Off'
            ? { type: 'Off', startTime: null, endTime: null }
            : { type: d.type, startTime: hhmm(d.startTime), endTime: hhmm(d.endTime) },
        ),
      },
      {
        onSuccess: (p) => {
          toast.ok(tx('"{0}" deseni oluşturuldu.', [p?.name ?? effectiveName.trim()]))
          onClose()
        },
      },
    )
  }

  const sel = selected !== null ? days[selected] : null
  const weekly = weeklyHours(days)

  return (
    <Modal
      open
      onClose={onClose}
      size="xl"
      title={tx('Yeni vardiya deseni')}
      note={tx('Döngüyü gün gün kurun. Ekip, başlangıç tarihinden itibaren bu diziyi sırayla takip eder ve sona gelince başa döner.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={create.isPending}>
            {create.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Deseni kaydet')}
          </Button>
        </>
      }
    >
      <div className="space-y-6">
        {create.isError && (
          <p role="alert" className="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-[13px] text-destructive">
            <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
            {errorText(create.error, tx('Desen kaydedilemedi.'))}
          </p>
        )}

        {/* Hazır başlangıçlar */}
        <div>
          <p className="mb-2 text-[13px] font-medium">{tx('Hazır bir döngüden başlayın')}</p>
          <div className="flex flex-wrap gap-2">
            {PRESETS.map((p) => (
              <Button
                key={p.key}
                type="button"
                size="sm"
                variant="outline"
                onClick={() => {
                  setDays(p.days())
                  setSelected(null)
                }}
              >
                {p.label}
              </Button>
            ))}
            <Button
              type="button"
              size="sm"
              variant="ghost"
              onClick={() => {
                setDays([])
                setSelected(null)
              }}
            >
              {tx('Boş başla')}
            </Button>
          </div>
        </div>

        {/* Şerit */}
        <div>
          <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2">
            <p className="text-[13px] font-medium">
              {tx('Döngü')}
              <span className="ml-2 font-normal text-muted-foreground tabular">
                {days.length > 0 ? tx('{0} gün', [formatNumber(days.length)]) : tx('boş')}
              </span>
            </p>
            {days.length > 0 && (
              <p className="text-[12px] text-muted-foreground">
                {tx('{0} · haftalık ort. {1}', [describeDays(days), hoursText(weekly)])}
              </p>
            )}
          </div>

          <div className="rounded-lg border border-border bg-muted/30 p-3">
            {days.length === 0 ? (
              <p className="py-4 text-center text-[13px] text-muted-foreground">
                {tx('Henüz gün yok. Aşağıdan blok ekleyin ya da hazır bir döngü seçin.')}
              </p>
            ) : (
              <ol className="flex flex-wrap gap-1.5" aria-label={tx('Desen günleri')}>
                {days.map((d, i) => {
                  const bad = submitted && Boolean(dayErrors[i])
                  return (
                    <li key={i}>
                      <button
                        type="button"
                        onClick={() => setSelected(selected === i ? null : i)}
                        aria-pressed={selected === i}
                        aria-label={tx('{0}. gün, {1}{2}', [i + 1, DAY_STYLE[d.type].label, d.startTime ? ` ${timeRange(d.startTime, d.endTime)}` : ''])}
                        className={cn(
                          'group flex cursor-pointer flex-col items-center gap-1 rounded-md p-1 transition-colors outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                          selected === i ? 'bg-background shadow-sm ring-2 ring-primary' : 'hover:bg-background',
                          bad && 'ring-2 ring-destructive',
                        )}
                      >
                        <DayBlock type={d.type} size="md" />
                        <span className="tabular text-[10px] leading-none text-muted-foreground">{i + 1}</span>
                      </button>
                    </li>
                  )
                })}
              </ol>
            )}
          </div>
          {submitted && errors.days && (
            <p role="alert" className="mt-1.5 text-[12px] text-destructive">
              {errors.days}
            </p>
          )}
          <LaborWarnings days={days} className="mt-2" />
        </div>

        {/* Seçili gün düzenleyici */}
        {sel && selected !== null && (
          <div className="rounded-lg border border-primary/30 bg-primary/5 p-3">
            <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
              <p className="text-[13px] font-semibold">{tx('{0}. gün', [selected + 1])}</p>
              <div className="flex flex-wrap gap-1">
                <Button type="button" size="sm" variant="ghost" onClick={() => move(selected, -1)} disabled={selected === 0} aria-label={tx('Sola taşı')}>
                  <ArrowLeft className="size-4" />
                </Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => move(selected, 1)} disabled={selected === days.length - 1} aria-label={tx('Sağa taşı')}>
                  <ArrowRight className="size-4" />
                </Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => duplicate(selected)} disabled={days.length >= MAX_DAYS}>
                  <Copy className="size-4" />{' '}{tx('Çoğalt')}
                </Button>
                <Button type="button" size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => remove(selected)}>
                  <Trash2 className="size-4" />{' '}{tx('Sil')}
                </Button>
              </div>
            </div>
            <div className="flex flex-wrap items-end gap-4">
              <div className="flex flex-col gap-1.5">
                <span className="text-[13px] font-medium">{tx('Tip')}</span>
                <Segmented
                  ariaLabel={tx('Gün tipi')}
                  value={sel.type}
                  onChange={(t) => setType(selected, t)}
                  options={PATTERN_TYPES.map((t) => ({ value: t, label: DAY_STYLE[t].label }))}
                />
              </div>
              {sel.type !== 'Off' && (
                <>
                  <TextField
                    label={tx('Başlangıç')}
                    type="time"
                    className="w-32"
                    value={hhmm(sel.startTime)}
                    onChange={(e) => update(selected, { startTime: e.target.value || null })}
                  />
                  <TextField
                    label={tx('Bitiş')}
                    type="time"
                    className="w-32"
                    value={hhmm(sel.endTime)}
                    onChange={(e) => update(selected, { endTime: e.target.value || null })}
                  />
                  <p className="pb-2 text-[12px] text-muted-foreground">
                    {durationLabel(sel.startTime, sel.endTime)}
                    {sel.startTime && sel.endTime && hhmm(sel.endTime) <= hhmm(sel.startTime) && tx(' · ertesi gün biter')}
                  </p>
                </>
              )}
            </div>
            {dayErrors[selected] && <p className="mt-2 text-[12px] text-destructive">{dayErrors[selected]}</p>}
          </div>
        )}

        {/* Madde 66: çalışma süresi kuralları (canlı uyarı; kaydı engellemez). */}
        {ruleWarnings.length > 0 && (
          <div role="status" className="rounded-lg border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/8 px-3 py-2.5 text-[12.5px]">
            <p className="mb-1 font-medium">{tx('Çalışma süresi kuralı uyarıları')}</p>
            <ul className="list-disc space-y-0.5 pl-5">{ruleWarnings.map((w, i) => <li key={`${w.code}-${i}`}>{w.message}</li>)}</ul>
          </div>
        )}

        {/* Blok ekle */}
        <div className="rounded-lg border border-border p-3">
          <p className="mb-3 text-[13px] font-medium">{tx('Sona blok ekle')}</p>
          <div className="flex flex-wrap items-end gap-4">
            <div className="flex flex-col gap-1.5">
              <span className="text-[13px]">{tx('Tip')}</span>
              <Segmented
                ariaLabel={tx('Eklenecek gün tipi')}
                value={blockType}
                onChange={setBlockType}
                options={PATTERN_TYPES.map((t) => ({ value: t, label: DAY_STYLE[t].label }))}
              />
            </div>
            <TextField
              label={tx('Gün sayısı')}
              type="number"
              min={1}
              max={31}
              className="tabular w-24"
              value={blockCount}
              onChange={(e) => setBlockCount(Math.max(1, Math.min(31, Number(e.target.value) || 1)))}
            />
            {blockType !== 'Off' && (
              <>
                <TextField
                  label={tx('Başlangıç')}
                  type="time"
                  className="w-32"
                  value={times[blockType].start}
                  onChange={(e) => setTimes((t) => ({ ...t, [blockType]: { ...t[blockType], start: e.target.value } }))}
                />
                <TextField
                  label={tx('Bitiş')}
                  type="time"
                  className="w-32"
                  value={times[blockType].end}
                  onChange={(e) => setTimes((t) => ({ ...t, [blockType]: { ...t[blockType], end: e.target.value } }))}
                />
              </>
            )}
            <Button type="button" variant="secondary" onClick={addBlock} disabled={days.length >= MAX_DAYS}>
              <Plus className="size-4" />
              {tx('{0} gün ekle', [blockCount])}</Button>
          </div>
          {days.length >= MAX_DAYS && (
            <p className="mt-2 text-[12px] text-muted-foreground">{tx('Bir desen en fazla {0} gün olabilir.', [MAX_DAYS])}</p>
          )}
        </div>

        <TextField
          label={tx('Desen adı')}
          value={effectiveName}
          onChange={(e) => {
            setNameTouched(true)
            setName(e.target.value)
          }}
          hint={nameTouched ? undefined : tx('Döngüden otomatik önerildi; dilediğiniz gibi değiştirebilirsiniz.')}
          error={submitted ? errors.name : undefined}
          maxLength={120}
        />
      </div>
    </Modal>
  )
}

/* ---------------------------------- Silme onayı --------------------------------- */

function DeletePatternDialog({
  pattern,
  usedBy,
  onClose,
  onOpenTeam,
}: {
  pattern: ShiftPattern
  usedBy: Array<{ id: string; name: string }>
  onClose: () => void
  onOpenTeam?: (teamId: string) => void
}) {
  const toast = useToast()
  const del = useDeleteShiftPattern()
  const conflict = del.error instanceof ApiError && del.error.status === 409

  // role="alertdialog": dışarı tıklamak kapatmaz, odak "Vazgeç"te başlar, Esc = Vazgeç.
  // "Sil" AlertDialogAction değildir: hata (409) iletisi pencerede kalsın diye kapanmayı onSuccess yönetir.
  return (
    <AlertDialog open onOpenChange={(o) => { if (!o) onClose() }}>
      <AlertDialogContent className="sm:max-w-lg">
        <AlertDialogHeader>
          <AlertDialogTitle>{tx('Desen silinsin mi?')}</AlertDialogTitle>
          <AlertDialogDescription>{tx('"{0}" kalıcı olarak silinir.', [pattern.name])}</AlertDialogDescription>
        </AlertDialogHeader>
        <div className="space-y-3 text-[13px]">
          <PatternStrip days={sortedDays(pattern)} size="sm" />
          {usedBy.length > 0 ? (
            <div className="space-y-2 rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2.5">
              <p>{tx('Bu deseni kullanan ekip var. Silmek için önce her ekibi düzenleyip başka bir desene taşıyın (Ekipler › ekip › Düzenle › Desen) ya da üyesi kalmayan ekibi silin.')}</p>
              <ul className="flex flex-wrap gap-2">
                {usedBy.map((t) => (
                  <li key={t.id}>
                    {onOpenTeam ? (
                      <Button size="sm" variant="outline" onClick={() => { onClose(); onOpenTeam(t.id) }}>
                        {tx('{0} ekibini aç', [t.name])}
                      </Button>
                    ) : (
                      <strong>{t.name}</strong>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          ) : (
            <p className="text-muted-foreground">{tx('Deseni kullanan ekip yok; silmek mevcut takvimleri etkilemez.')}</p>
          )}
          {del.isError && (
            <p role="alert" className="text-destructive">
              {conflict
                ? tx('Bu desen hâlâ bir ekip tarafından kullanılıyor, silinemedi.')
                : errorText(del.error, tx('Desen silinemedi.'))}
            </p>
          )}
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel>{tx('Vazgeç')}</AlertDialogCancel>
          <Button
            variant="destructive"
            disabled={del.isPending || usedBy.length > 0}
            onClick={() =>
              del.mutate(pattern.id, {
                onSuccess: () => {
                  toast.ok(tx('Desen silindi.'))
                  onClose()
                },
              })
            }
          >
            {del.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Sil')}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

/* ------------------------------------ Liste ------------------------------------ */

export function PatternsView({ onOpenTeam }: { onOpenTeam?: (teamId: string) => void } = {}) {
  const { can } = useAuth()
  const manage = can('timeshift:manage')
  const patterns = useShiftPatterns()
  const teams = useShiftTeams()
  const [building, setBuilding] = useState(false)
  const [deleting, setDeleting] = useState<ShiftPattern | null>(null)

  const usage = useMemo(() => {
    const map = new Map<string, Array<{ id: string; name: string }>>()
    for (const t of teams.data ?? []) map.set(t.shiftPatternId, [...(map.get(t.shiftPatternId) ?? []), { id: t.id, name: t.name }])
    return map
  }, [teams.data])

  const list = useMemo(
    () =>
      [...(patterns.data ?? [])].sort(
        (a, b) => Number(b.isActive) - Number(a.isActive) || a.name.localeCompare(b.name, 'tr-TR'),
      ),
    [patterns.data],
  )

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Legend types={PATTERN_TYPES} />
        {manage && (
          <Button onClick={() => setBuilding(true)}>
            <Plus className="size-4" />{' '}{tx('Yeni desen')}
          </Button>
        )}
      </div>

      <InfoNote>
        {tx('Desen yalnızca olağan döngüyü tanımlar. Onaylanan izinler ve resmî tatiller takvimde desenin üzerine kendiliğinden işlenir; bunları desene eklemeniz gerekmez.')}
      </InfoNote>

      {patterns.isPending ? (
        <Panel>
          <RowsSkeleton rows={3} columns={3} />
        </Panel>
      ) : patterns.isError ? (
        <Panel>
          <ErrorState message={errorText(patterns.error)} onRetry={() => void patterns.refetch()} />
        </Panel>
      ) : list.length === 0 ? (
        <Panel>
          <EmptyState
            icon={Repeat}
            title={tx('Henüz vardiya deseni yok')}
            detail={
              manage
                ? tx('Şirketiniz vardiyalı çalışıyorsa döngüyü burada tanımlayın. Vardiya kullanmıyorsanız bu bölümü boş bırakabilirsiniz.')
                : tx('Vardiya desenlerini İK yöneticisi tanımlar.')
            }
            action={
              manage && (
                <Button onClick={() => setBuilding(true)}>
                  <Plus className="size-4" />{' '}{tx('İlk deseni oluştur')}
                </Button>
              )
            }
          />
        </Panel>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          {list.map((p) => {
            const days = sortedDays(p)
            const usedBy = usage.get(p.id) ?? []
            return (
              <Panel key={p.id} className={cn(!p.isActive && 'opacity-70')}>
                <PanelHead
                  title={
                    <span className="flex flex-wrap items-center gap-2">
                      {p.name}
                      {!p.isActive && <StatusBadge>{tx('Pasif')}</StatusBadge>}
                    </span>
                  }
                  note={tx('{0} günlük döngü · {1}', [formatNumber(days.length), describeDays(days)])}
                  action={
                    manage && (
                      <Button size="sm" variant="ghost" aria-label={tx('{0} desenini sil', [p.name])} onClick={() => setDeleting(p)}>
                        <Trash2 className="size-4" />
                      </Button>
                    )
                  }
                />
                <PanelBody className="space-y-3">
                  <PatternStrip days={days} size="sm" />
                  <LaborWarnings days={days} />
                  <dl className="flex flex-wrap gap-x-6 gap-y-1 text-[12px] text-muted-foreground">
                    {(['Day', 'Night'] as const).map((t) => {
                      const d = days.find((x) => x.type === t)
                      return d ? (
                        <div key={t} className="flex gap-1.5">
                          <dt>{DAY_STYLE[t].label}</dt>
                          <dd className="tabular text-foreground">{timeRange(d.startTime, d.endTime)}</dd>
                        </div>
                      ) : null
                    })}
                    <div className="flex gap-1.5">
                      <dt>{tx('Haftalık ort.')}</dt>
                      <dd className="tabular text-foreground">{hoursText(weeklyHours(days))}</dd>
                    </div>
                    <div className="flex gap-1.5">
                      <dt>{tx('Kullanan ekip')}</dt>
                      <dd className="text-foreground">{usedBy.length ? usedBy.map((t) => t.name).join(', ') : tx('yok')}</dd>
                    </div>
                  </dl>
                </PanelBody>
              </Panel>
            )
          })}
        </div>
      )}

      {building && <PatternBuilderDialog onClose={() => setBuilding(false)} />}
      {deleting && (
        <DeletePatternDialog pattern={deleting} usedBy={usage.get(deleting.id) ?? []} onClose={() => setDeleting(null)} onOpenTeam={onOpenTeam} />
      )}
    </div>
  )
}
