import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { LoaderCircle, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { StatCard } from '@/components/ui/StatCard'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { compensationApi } from '@/api/compensation'
import { useCompensationBands, useCompensationRecords, useEmployees } from '@/api/queries'
import {
  compensationReasonLabels,
  type CompensationBand,
  type CompensationChangeReason,
  type SimulationResult,
} from '@/api/types'
import { formatDate, formatMoney, formatNumber, formatPercent, fullName, normalizeSearch, parseDecimal } from '@/lib/format'
import { useConfirm } from '@/components/ui/Confirm'
import { useEmployeeName } from '@/lib/useEmployeeName'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { PayEquityPanel } from './PayEquityPanel'

/** Bant ve ücret kaydı yazmak yalnızca İK'ya açık; "ücret görüntüleme" ek izni salt okumadır. */
function useCanWriteCompensation() {
  const { roles } = useAuth()
  return isHr(roles)
}
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

type TabKey = 'bantlar' | 'gecmis' | 'simulasyon' | 'adalet'

/* ------------------------------------------------------------------ bantlar */

/** Yeni bant ya da (band verilirse) mevcut bandı düzenleme penceresi. */
function NewBandModal({
  open,
  onClose,
  year,
  band,
}: {
  open: boolean
  onClose: () => void
  year: number
  band?: CompensationBand
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [grade, setGrade] = useState(band?.grade ?? '')
  const [title, setTitle] = useState(band?.title ?? '')
  const [minAmount, setMin] = useState(band ? String(band.minAmount) : '')
  const [midAmount, setMid] = useState(band ? String(band.midAmount) : '')
  const [maxAmount, setMax] = useState(band ? String(band.maxAmount) : '')
  const [effectiveFrom, setEffectiveFrom] = useState(band?.effectiveFrom ?? '')
  const [error, setError] = useState<string | undefined>()
  // Tutar alanlarının hataları alan yanında gösterilir (0 < alt ≤ orta ≤ üst).
  const [amountErr, setAmountErr] = useState<{ min?: string; mid?: string; max?: string }>({})

  const mutation = useMutation({
    mutationFn: () => {
      const input = {
        grade: grade.trim(),
        title: title.trim() || undefined,
        minAmount: parseDecimal(minAmount) ?? NaN,
        midAmount: parseDecimal(midAmount) ?? NaN,
        maxAmount: parseDecimal(maxAmount) ?? NaN,
        currency: band?.currency ?? 'TRY',
        year: band?.year ?? year,
        effectiveFrom: effectiveFrom || null,
      }
      return band ? compensationApi.updateBand(band.id, input) : compensationApi.createBand(input)
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['compensation'] })
      toast.ok(band ? tx('Ücret bandı güncellendi') : tx('Ücret bandı eklendi'))
      onClose()
      setGrade('')
      setTitle('')
      setMin('')
      setMid('')
      setMax('')
      setAmountErr({})
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : band ? tx('Bant güncellenemedi.') : tx('Bant eklenemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    const val = (s: string) => parseDecimal(s)
    const [lo, mid, hi] = [val(minAmount), val(midAmount), val(maxAmount)]
    const need = tx('Sıfırdan büyük bir tutar girin.')
    const bad = (n: number | null) => n === null || !Number.isFinite(n) || n <= 0
    const errs: { min?: string; mid?: string; max?: string } = {
      min: bad(lo) ? need : undefined,
      mid: bad(mid) ? need : !bad(lo) && mid! < lo! ? tx('Orta tutar alt tutardan küçük olamaz.') : undefined,
      max: bad(hi) ? need : !bad(mid) && hi! < mid! ? tx('Üst tutar orta tutardan küçük olamaz.') : !bad(lo) && hi! < lo! ? tx('Üst tutar alt tutardan küçük olamaz.') : undefined,
    }
    setAmountErr(errs)
    setError(grade.trim() ? undefined : tx('Kademe zorunlu.'))
    if (!grade.trim() || errs.min || errs.mid || errs.max) return
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={band ? tx('Ücret bandını düzenle') : tx('Yeni ücret bandı')}
      note={tx('{0} yılı', [band?.year ?? year])}
      size="lg"
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="new-band"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {band ? tx('Kaydet') : tx('Bandı ekle')}
          </Button>
        </>
      }
    >
      <form id="new-band" onSubmit={submit} noValidate className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="band-grade"
            label={tx('Kademe')}
            required
            value={grade}
            onChange={(e) => setGrade(e.target.value)}
            error={error}
          />
          <TextField
            id="band-title"
            label={tx('Unvan')}
            hint={tx('İsteğe bağlı')}
            value={title}
            onChange={(e) => setTitle(e.target.value)}
          />
        </div>
        <div className="grid gap-4 sm:grid-cols-3">
          <TextField
            id="band-min"
            label={tx('Alt')}
            inputMode="decimal"
            required
            className="tabular"
            value={minAmount}
            error={amountErr.min}
            onChange={(e) => setMin(e.target.value)}
          />
          <TextField
            id="band-mid"
            label={tx('Orta')}
            inputMode="decimal"
            required
            className="tabular"
            value={midAmount}
            error={amountErr.mid}
            onChange={(e) => setMid(e.target.value)}
          />
          <TextField
            id="band-max"
            label={tx('Üst')}
            inputMode="decimal"
            required
            className="tabular"
            value={maxAmount}
            error={amountErr.max}
            onChange={(e) => setMax(e.target.value)}
          />
        </div>
        <TextField
          id="band-effective"
          type="date"
          label={tx('Yürürlük tarihi')}
          hint={tx('İsteğe bağlı; boşsa yılın başı. Aynı kademede yıl içinde yeni bant tanımlamak için kullanın.')}
          value={effectiveFrom}
          onChange={(e) => setEffectiveFrom(e.target.value)}
        />
      </form>
    </Modal>
  )
}

/**
 * Bant çizgisi: alt–üst aralığı yatay bir şerit, orta nokta işaretli.
 * Sayı sütunları zaten var; bu şerit bantların birbirine göre yerini
 * tek bakışta okutmak için.
 */
function BandSpan({
  band,
  floor,
  ceiling,
}: {
  band: CompensationBand
  floor: number
  ceiling: number
}) {
  const reduced = useReducedMotion()
  const span = Math.max(1, ceiling - floor)
  const left = ((band.minAmount - floor) / span) * 100
  const width = Math.max(2, ((band.maxAmount - band.minAmount) / span) * 100)
  const midPoint =
    ((band.midAmount - band.minAmount) / Math.max(1, band.maxAmount - band.minAmount)) * 100

  return (
    <div className="relative h-1.5 w-full min-w-24 overflow-hidden rounded-full bg-muted">
      <motion.div
        className="absolute inset-y-0 rounded-full bg-primary/70"
        style={{ left: `${left}%` }}
        initial={reduced ? false : { width: 0 }}
        animate={{ width: `${width}%` }}
        transition={{ duration: 0.5, ease: 'easeOut' }}
      >
        <span
          aria-hidden="true"
          className="absolute inset-y-0 w-px bg-foreground"
          style={{ left: `${midPoint}%` }}
        />
      </motion.div>
    </div>
  )
}

function BandsTab({ year, onYearChange }: { year: number; onYearChange: (y: number) => void }) {
  const canWrite = useCanWriteCompensation()
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<CompensationBand | null>(null)
  const bands = useCompensationBands(year)
  const confirm = useConfirm()
  const toast = useToast()
  const queryClient = useQueryClient()
  // Kullanımdaki bant (geçerli ücret kaydı ya da uygulanmamış zam dönemi) sunucuda 409 ile reddedilir.
  const remove = useMutation({
    mutationFn: (id: string) => compensationApi.deleteBand(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['compensation'] })
      toast.ok(tx('Ücret bandı silindi'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Bant silinemedi.')),
  })
  async function askDelete(b: CompensationBand) {
    if (
      await confirm({
        title: tx('"{0}" bandı silinsin mi?', [b.grade]),
        note: tx('Bu kademede geçerli ücret kaydı ya da bandı kullanan açık bir zam dönemi varsa silme reddedilir.'),
        action: tx('Sil'),
      })
    )
      remove.mutate(b.id)
  }

  const { floor, ceiling } = useMemo(() => {
    const list = bands.data ?? []
    if (list.length === 0) return { floor: 0, ceiling: 1 }
    return {
      floor: Math.min(...list.map((b) => b.minAmount)),
      ceiling: Math.max(...list.map((b) => b.maxAmount)),
    }
  }, [bands.data])

  const columns: Array<Column<CompensationBand>> = [
    {
      id: 'grade',
      header: tx('Kademe'),
      searchText: (b) => `${b.grade} ${b.title ?? ''}`,
      sortValue: (b) => b.grade,
      exportText: (b) => b.grade,
      cell: (b) => (
        <div className="min-w-0">
          <p className="font-semibold">{b.grade}</p>
          {b.title && <p className="text-[12px] text-muted-foreground">{b.title}</p>}
        </div>
      ),
    },
    {
      id: 'span',
      header: tx('Aralık'),
      hideBelow: 'md',
      cell: (b) => <BandSpan band={b} floor={floor} ceiling={ceiling} />,
    },
    {
      id: 'min',
      header: tx('Alt'),
      align: 'right',
      sortValue: (b) => b.minAmount,
      exportText: (b) => formatMoney(b.minAmount, b.currency),
      cell: (b) => (
        <span className="text-muted-foreground">{formatMoney(b.minAmount, b.currency)}</span>
      ),
    },
    {
      id: 'mid',
      header: tx('Orta'),
      align: 'right',
      sortValue: (b) => b.midAmount,
      exportText: (b) => formatMoney(b.midAmount, b.currency),
      cell: (b) => <span className="font-semibold">{formatMoney(b.midAmount, b.currency)}</span>,
    },
    {
      id: 'max',
      header: tx('Üst'),
      align: 'right',
      hideBelow: 'sm',
      sortValue: (b) => b.maxAmount,
      exportText: (b) => formatMoney(b.maxAmount, b.currency),
      cell: (b) => (
        <span className="text-muted-foreground">{formatMoney(b.maxAmount, b.currency)}</span>
      ),
    },
    ...(canWrite
      ? [
          {
            id: 'actions',
            header: '',
            align: 'right' as const,
            cell: (b: CompensationBand) => (
              <span className="flex justify-end gap-1">
                <Button size="sm" variant="ghost" className="cursor-pointer" onClick={() => setEditing(b)}>
                  {tx('Düzenle')}
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  className="cursor-pointer text-destructive"
                  disabled={remove.isPending}
                  onClick={() => void askDelete(b)}
                >
                  {tx('Sil')}
                </Button>
              </span>
            ),
          },
        ]
      : []),
  ]

  return (
    <>
      <div className="flex flex-wrap items-end justify-between gap-4">
        <TextField
          id="band-year"
          label={tx('Yıl')}
          type="number"
          min={2020}
          max={2100}
          className="tabular w-28"
          value={year}
          onChange={(e) => onYearChange(Number(e.target.value))}
        />
        {canWrite && (
          <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
            <Plus className="size-4" />
            {tx('Yeni bant')}
          </Button>
        )}
      </div>

      <DataTable
        rows={bands.data}
        rowKey={(b) => b.id}
        columns={columns}
        isLoading={bands.isPending}
        error={bands.error}
        onRetry={() => void bands.refetch()}
        searchPlaceholder={tx('Kademe veya unvan ara')}
        exportFileName={`ucret-bantlari-${year}`}
        initialSort={{ columnId: 'min', dir: 'asc' }}
        emptyTitle={tx('Bu yıl için bant yok')}
        emptyDetail={tx('Bantlar tanımlanmadan zam simülasyonu bant dışı satırları işaretleyemez.')}
        emptyAction={
          canWrite ? (
            <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
              {tx('Yeni bant')}
            </Button>
          ) : undefined
        }
      />

      <NewBandModal open={modalOpen} onClose={() => setModalOpen(false)} year={year} />
      {editing && (
        <NewBandModal key={editing.id} open onClose={() => setEditing(null)} year={year} band={editing} />
      )}
    </>
  )
}

/* ------------------------------------------------------------------ geçmiş */

function NewRecordModal({
  open,
  onClose,
  employeeId,
}: {
  open: boolean
  onClose: () => void
  employeeId: string
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [baseSalary, setSalary] = useState('')
  const [grade, setGrade] = useState('')
  const [reason, setReason] = useState<CompensationChangeReason>('AnnualIncrease')
  const [effectiveFrom, setFrom] = useState('')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      compensationApi.createRecord({
        employeeId,
        baseSalary: Number(baseSalary),
        currency: 'TRY',
        grade: grade.trim() || undefined,
        reason,
        effectiveFrom,
        note: note.trim() || undefined,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['compensation'] })
      toast.ok(tx('Ücret kaydı eklendi'))
      onClose()
      setSalary('')
      setNote('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kayıt eklenemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!Number(baseSalary)) return setError(tx('Taban ücret zorunlu.'))
    if (!effectiveFrom) return setError(tx('Geçerlilik tarihi zorunlu.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni ücret kaydı')}
      note={tx('Önceki kayıt otomatik olarak kapanır.')}
      size="lg"
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="new-record"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      <form id="new-record" onSubmit={submit} noValidate className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="rec-salary"
            label={tx('Taban ücret')}
            type="number"
            min={0}
            required
            className="tabular"
            value={baseSalary}
            onChange={(e) => setSalary(e.target.value)}
            error={error?.includes('Taban') ? error : undefined}
          />
          <TextField
            id="rec-grade"
            label={tx('Kademe')}
            hint={tx('İsteğe bağlı')}
            value={grade}
            onChange={(e) => setGrade(e.target.value)}
          />
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <SelectField
            id="rec-reason"
            label={tx('Gerekçe')}
            value={reason}
            onChange={(v) => setReason(v as CompensationChangeReason)}
            options={(Object.keys(compensationReasonLabels) as CompensationChangeReason[]).map(
              (r) => ({ value: r, label: compensationReasonLabels[r] }),
            )}
          />
          <TextField
            id="rec-from"
            label={tx('Geçerlilik başlangıcı')}
            type="date"
            required
            value={effectiveFrom}
            onChange={(e) => setFrom(e.target.value)}
            error={error?.includes('Geçerlilik') ? error : undefined}
          />
        </div>
        <TextAreaField
          id="rec-note"
          label={tx('Not')}
          rows={2}
          hint={tx('İsteğe bağlı')}
          value={note}
          onChange={(e) => setNote(e.target.value)}
        />
      </form>
    </Modal>
  )
}

function HistoryTab() {
  const canWrite = useCanWriteCompensation()
  const [employeeId, setEmployeeId] = useState('')
  const [modalOpen, setModalOpen] = useState(false)
  const records = useCompensationRecords(employeeId || undefined, Boolean(employeeId))
  const reduced = useReducedMotion()

  const rows = useMemo(
    () => [...(records.data ?? [])].sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : -1)),
    [records.data],
  )

  return (
    <>
      <div className="flex flex-wrap items-end gap-4">
        <div className="w-72">
          <EmployeePicker
            id="comp-employee"
            value={employeeId}
            onChange={setEmployeeId}
            hint={tx('Ücret geçmişini görmek için çalışan seçin.')}
          />
        </div>
        {employeeId && canWrite && (
          <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
            <Plus className="size-4" />
            {tx('Ücret kaydı ekle')}
          </Button>
        )}
      </div>

      <Panel>
        <PanelHead title={tx('Ücret geçmişi')} note={tx('En yeni kayıt en üstte')} />
        {!employeeId ? (
          <EmptyState
            title={tx('Çalışan seçilmedi')}
            detail={tx('Ücret verisi hassastır; yalnızca seçilen çalışanın kaydı getirilir.')}
          />
        ) : records.isPending ? (
          <RowsSkeleton rows={4} columns={3} />
        ) : records.isError ? (
          <ErrorState
            message={records.error instanceof Error ? records.error.message : undefined}
            onRetry={() => void records.refetch()}
          />
        ) : rows.length === 0 ? (
          <EmptyState
            title={tx('Ücret kaydı yok')}
            detail={tx('Bu çalışan için henüz bir ücret kaydı girilmemiş.')}
          />
        ) : (
          <PanelBody>
            <ol className="relative space-y-6 pl-6">
              <span
                aria-hidden="true"
                className="absolute top-1.5 bottom-1.5 left-[5px] w-px bg-border"
              />
              {rows.map((r, i) => {
                const previous = rows[i + 1]
                const delta = previous ? r.baseSalary - previous.baseSalary : null
                const percent =
                  previous && previous.baseSalary > 0 ? (delta! / previous.baseSalary) * 100 : null

                return (
                  <motion.li
                    key={r.id}
                    className="relative"
                    initial={reduced ? false : { opacity: 0, x: -6 }}
                    animate={{ opacity: 1, x: 0 }}
                    transition={{
                      duration: 0.3,
                      ease: 'easeOut',
                      delay: Math.min(i * 0.04, 0.24),
                    }}
                  >
                    <span
                      aria-hidden="true"
                      className={cn(
                        'absolute top-1.5 -left-6 size-[11px] rounded-full border-2 border-card',
                        i === 0 ? 'bg-primary' : 'bg-muted-foreground/50',
                      )}
                    />
                    <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                      <span className="tabular text-[19px] leading-none font-bold">
                        {formatMoney(r.baseSalary, r.currency)}
                      </span>
                      <span className="flex items-center gap-2">
                        {delta !== null && delta !== 0 && (
                          <StatusBadge tone={delta > 0 ? 'success' : 'danger'}>
                            {delta > 0 ? '+' : '−'}
                            {formatMoney(Math.abs(delta), r.currency)}
                            {percent !== null && ` (${formatPercent(Math.abs(percent) / 100, 1)})`}
                          </StatusBadge>
                        )}
                        <StatusBadge tone="neutral">
                          {compensationReasonLabels[r.reason]}
                        </StatusBadge>
                      </span>
                    </div>
                    <p className="tabular mt-1 text-[12px] text-muted-foreground">
                      {formatDate(r.effectiveFrom)}
                      {r.effectiveTo ? ` – ${formatDate(r.effectiveTo)}` : tx(' – sürüyor')}
                      {r.grade ? tx(', kademe {0}', [r.grade]) : ''}
                    </p>
                    {r.note && (
                      <p className="mt-1.5 text-[13px] leading-relaxed text-muted-foreground">
                        {r.note}
                      </p>
                    )}
                  </motion.li>
                )
              })}
            </ol>
          </PanelBody>
        )}
      </Panel>

      <NewRecordModal
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        employeeId={employeeId}
      />
    </>
  )
}

/* -------------------------------------------------------------- simülasyon */

/** İşaretli tutar: +₺17.000 / −₺17.000 (eksi işareti tipografik, "+-" birleşmesi olmaz). */
function signedMoney(v: number): string {
  if (v > 0) return `+${formatMoney(v)}`
  if (v < 0) return `−${formatMoney(-v)}`
  return formatMoney(0)
}

/** Yerel yüzde biçimi (tr: %10,0); işaret tutarla aynı kuralla. */
function signedPercent(v: number): string {
  const p = formatPercent(Math.abs(v) / 100, 1)
  return v > 0 ? `+${p}` : v < 0 ? `−${p}` : p
}

function SimulationTab({ year }: { year: number }) {
  const toast = useToast()
  const employees = useEmployees()
  const [selected, setSelected] = useState<string[]>([])
  const [search, setSearch] = useState('')
  const [mode, setMode] = useState<'percent' | 'flat'>('percent')
  const [amount, setAmount] = useState('10')
  const [result, setResult] = useState<SimulationResult | null>(null)
  const reduced = useReducedMotion()
  const nameOf = useEmployeeName()

  const filtered = useMemo(() => {
    const list = employees.data ?? []
    if (!search.trim()) return list
    const q = normalizeSearch(search)
    return list.filter((e) => normalizeSearch(fullName(e)).includes(q))
  }, [employees.data, search])

  const mutation = useMutation({
    mutationFn: () =>
      compensationApi.simulate({
        employeeIds: selected,
        increasePercent: mode === 'percent' ? (parseDecimal(amount) ?? 0) : undefined,
        flatIncrease: mode === 'flat' ? (parseDecimal(amount) ?? 0) : undefined,
        year,
      }),
    onSuccess: (data) => setResult(data),
    onError: (e: unknown) =>
      toast.stop(e instanceof Error ? e.message : tx('Simülasyon çalıştırılamadı.')),
  })

  function toggle(id: string) {
    setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]))
  }

  type Line = SimulationResult['lines'][number]

  const lineColumns: Array<Column<Line>> = [
    {
      id: 'employee',
      header: tx('Çalışan'),
      searchText: (l) => nameOf(l.employeeId),
      sortValue: (l) => nameOf(l.employeeId),
      exportText: (l) => nameOf(l.employeeId),
      cell: (l) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">{nameOf(l.employeeId)}</p>
          {l.grade && <p className="text-[12px] text-muted-foreground">{tx('Kademe {0}', [l.grade])}</p>}
        </div>
      ),
    },
    {
      id: 'current',
      header: tx('Mevcut'),
      align: 'right',
      hideBelow: 'sm',
      sortValue: (l) => l.currentSalary,
      exportText: (l) => formatMoney(l.currentSalary),
      cell: (l) => <span className="text-muted-foreground">{formatMoney(l.currentSalary)}</span>,
    },
    {
      id: 'proposed',
      header: tx('Önerilen'),
      align: 'right',
      sortValue: (l) => l.proposedSalary,
      exportText: (l) => formatMoney(l.proposedSalary),
      cell: (l) => (
        <span className={cn('font-semibold', l.withinBand === false && 'text-destructive')}>
          {formatMoney(l.proposedSalary)}
        </span>
      ),
    },
    {
      id: 'increase',
      header: tx('Artış'),
      align: 'right',
      hideBelow: 'md',
      sortValue: (l) => l.increaseAmount,
      exportText: (l) => `${signedMoney(l.increaseAmount)} (${signedPercent(l.increasePercent)})`,
      cell: (l) => (
        <span>
          {signedMoney(l.increaseAmount)}
          <span className="block text-[11px] text-muted-foreground">
            {signedPercent(l.increasePercent)}
          </span>
        </span>
      ),
    },
    {
      id: 'band',
      header: tx('Bant'),
      align: 'right',
      sortValue: (l) => (l.withinBand === null ? 2 : l.withinBand ? 1 : 0),
      exportText: (l) => (l.withinBand === null ? tx('Bant yok') : l.withinBand ? tx('Bant içi') : tx('Bant dışı')),
      cell: (l) =>
        l.withinBand === null ? (
          <StatusBadge tone="neutral">{tx('Bant yok')}</StatusBadge>
        ) : l.withinBand ? (
          <StatusBadge tone="success">{tx('Bant içi')}</StatusBadge>
        ) : (
          <StatusBadge tone="danger">{tx('Bant dışı{0}', [l.bandMax ? tx(', üst {0}', [formatMoney(l.bandMax)]) : ''])}
          </StatusBadge>
        ),
    },
  ]

  return (
    <>
      <Panel>
        <PanelHead
          title={tx('Zam simülasyonu')}
          note={tx('Kaydedilmez; yalnızca bütçe etkisini hesaplar.')}
          action={
            selected.length > 0 ? (
              <Button
                variant="ghost"
                size="sm"
                className="cursor-pointer"
                onClick={() => setSelected([])}
              >
                {tx('Seçimi temizle')}
              </Button>
            ) : undefined
          }
        />
        <PanelBody className="space-y-5">
          <div className="grid gap-4 sm:grid-cols-[1fr_auto_auto]">
            <TextField
              id="sim-search"
              label={tx('Çalışan ara')}
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              hint={tx('{0} çalışan seçildi', [formatNumber(selected.length)])}
            />
            <SelectField
              id="sim-mode"
              label={tx('Artış türü')}
              value={mode}
              onChange={(v) => setMode(v as 'percent' | 'flat')}
              options={[
                { value: 'percent', label: tx('Yüzde') },
                { value: 'flat', label: tx('Sabit tutar') },
              ]}
            />
            <TextField
              id="sim-amount"
              label={mode === 'percent' ? tx('Yüzde (%)') : tx('Tutar')}
              inputMode="decimal"
              className="tabular"
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
            />
          </div>

          {employees.isPending ? (
            <RowsSkeleton rows={4} columns={2} />
          ) : (
            <div className="max-h-64 overflow-y-auto rounded-md border border-border">
              <ul className="divide-y divide-border">
                {filtered.map((e) => (
                  <li key={e.id}>
                    <label className="flex min-h-11 cursor-pointer items-center gap-3 px-3 py-2 transition-colors hover:bg-muted/50">
                      <Checkbox
                        checked={selected.includes(e.id)}
                        onCheckedChange={() => toggle(e.id)}
                      />
                      <span className="min-w-0 truncate text-[14px]">{fullName(e)}</span>
                    </label>
                  </li>
                ))}
                {filtered.length === 0 && (
                  <li className="px-3 py-4 text-[13px] text-muted-foreground">
                    {tx('Aramaya uyan çalışan yok.')}
                  </li>
                )}
              </ul>
            </div>
          )}

          <div>
            <Button
              className="cursor-pointer"
              disabled={mutation.isPending || selected.length === 0 || !parseDecimal(amount)}
              onClick={() => mutation.mutate()}
            >
              {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
              {tx('Simülasyonu çalıştır')}
            </Button>
          </div>
        </PanelBody>
      </Panel>

      <AnimatePresence mode="wait">
        {result && (
          <motion.div
            key={`${result.employeeCount}-${result.proposedTotal}`}
            initial={reduced ? false : { opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            exit={reduced ? undefined : { opacity: 0, y: -6 }}
            transition={{ duration: 0.32, ease: 'easeOut' }}
            className="space-y-5"
          >
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <StatCard
                label={tx('Kapsanan çalışan')}
                value={formatNumber(result.employeeCount)}
                trend={tx('Simülasyonda')}
                trendDirection="flat"
                trendSense="neutral"
              />
              <StatCard
                label={tx('Mevcut toplam')}
                value={formatMoney(result.currentTotal)}
                trend={tx('Bugünkü bordro')}
                trendDirection="flat"
                trendSense="neutral"
              />
              <StatCard
                label={tx('Bütçe etkisi')}
                value={signedMoney(result.budgetImpact)}
                trend={tx('yeni toplam {0}', [formatMoney(result.proposedTotal)])}
                trendDirection="up"
                trendSense="negative"
              />
              <StatCard
                label={tx('Bant dışı')}
                value={formatNumber(result.outOfBandCount)}
                trend={
                  result.outOfBandCount > 0
                    ? tx('kademe bant sınırlarının dışında')
                    : result.noBandCount
                      ? tx('{0} çalışana bant atanmamış', [formatNumber(result.noBandCount)])
                      : tx('tümü bant içinde')
                }
                trendDirection={result.outOfBandCount > 0 ? 'up' : 'flat'}
                trendSense="negative"
              />
            </div>

            {result.lines.length === 0 ? (
              <Panel>
                <PanelHead title={tx('Satır bazında etki')} note={tx('{0} bantlarına göre', [year])} />
                <EmptyState
                  title={tx('Satır yok')}
                  detail={tx('Seçilen çalışanların yürürlükteki ücret kaydı bulunamadı.')}
                />
              </Panel>
            ) : (
              <DataTable
                rows={result.lines}
                rowKey={(l) => l.employeeId}
                columns={lineColumns}
                searchPlaceholder={tx('Çalışan ara')}
                exportFileName={`zam-simulasyonu-${year}`}
                pageSize={15}
                rowClassName={(l) => (l.withinBand === false ? 'bg-destructive/5' : undefined)}
                emptyTitle={tx('Satır yok')}
              />
            )}
          </motion.div>
        )}
      </AnimatePresence>
    </>
  )
}

/* ------------------------------------------------------------------- sayfa */

export function CompensationPage() {
  const [tab, setTab] = useTabParam<TabKey>('gorunum', 'bantlar')
  const [year, setYear] = useState(new Date().getFullYear())
  const canAnalyze = useCanWriteCompensation()

  const TABS: Array<TabDef<TabKey>> = [
    { key: 'bantlar', label: tx('Ücret bantları') },
    { key: 'gecmis', label: tx('Çalışan geçmişi') },
    { key: 'simulasyon', label: tx('Zam simülasyonu') },
    // Ücret adaleti yalnızca İK / şirket yöneticisi ("ücret görüntüleme" ek izni kapsamaz).
    ...(canAnalyze ? [{ key: 'adalet' as TabKey, label: tx('Ücret adaleti') }] : []),
  ]

  return (
    <div className="sensitive-scope space-y-5">
      <PageHeader
        title={tx('Ücret')}
        description={tx('Ücret bantları, çalışan ücret geçmişi ve zam simülasyonu. Bu sayfa yalnızca İK yönetimine açıktır.')}
      />

      <Tabs tabs={TABS} value={tab} onChange={setTab} label={tx('Ücret görünümü')} />

      {tab === 'bantlar' && <BandsTab year={year} onYearChange={setYear} />}
      {tab === 'gecmis' && <HistoryTab />}
      {tab === 'simulasyon' && <SimulationTab year={year} />}
      {tab === 'adalet' && canAnalyze && <PayEquityPanel />}
    </div>
  )
}
