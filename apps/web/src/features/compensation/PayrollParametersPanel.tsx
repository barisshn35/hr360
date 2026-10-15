import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { Calculator, Copy, Pencil, Plus, Trash2 } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { Checkbox } from '@/components/ui/checkbox'
import { useAuth } from '@/auth/useAuth'
import { ApiError } from '@/api/client'
import { payrollApi, type PayrollParameterRow, type PayrollParametersPreview } from '@/api/payroll'
import { formatDateTime, formatMoney } from '@/lib/format'
import { useAction, errMsg } from '@/features/shared/kit'
import { appLocale, tx } from '@/lib/i18n'
import {
  addBracket, emptyForm, formFromRow, parseAmount, prefillSource, removeBracket, rowStatus, serverErrors, sgkFloorCeiling, validateForm,
  type ParamErrors, type ParamForm,
} from '@/lib/payrollParams'

/**
 * Bordro › Parametreler (ürün kararı: yıllık parametreleri bordro yetkilisi girer; uygulama değer varsaymaz).
 * Yıl seçilir → yılın yürürlük satırları (Ocak'tan itibaren, Temmuz'dan itibaren ...) listelenir → satır
 * düzenlenir ya da yeni satır eklenir. Satır yoksa o yılın dönemleri hesaplanamaz (409 payroll_parameters_missing).
 */

const monthName = (m: number) => new Date(2026, m - 1, 1).toLocaleDateString(appLocale, { month: 'long' })
const pct = (v: number) => `%${(v * 100).toLocaleString(appLocale, { maximumFractionDigits: 4 })}`

function useIsPayrollAdmin() {
  const { hasRole } = useAuth()
  return hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')
}

/** Gruplanmış form bölümü (fieldset + legend: ekran okuyucu grubu duyurur). */
function Group({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <fieldset className="min-w-0 rounded-xl border border-border p-4">
      <legend className="px-1 text-[13.5px] font-semibold">{title}</legend>
      {note && <p className="mb-3 text-[12.5px] leading-relaxed text-muted-foreground">{note}</p>}
      {children}
    </fieldset>
  )
}

function RowSummary({ r }: { r: PayrollParameterRow }) {
  return (
    <p className="text-[12.5px] text-muted-foreground">
      {tx('Brüt asgari ücret {0} · SGK işçi {1} · işveren {2} (−{3} puan) · tavan {4} kat', [
        formatMoney(r.minimumWageGross), pct(r.sgkEmployeeRate), pct(r.sgkEmployerRate), r.employerIncentivePoints, r.sgkCeilingMultiplier,
      ])}
    </p>
  )
}

function StatusLine({ r }: { r: PayrollParameterRow }) {
  return rowStatus(r) === 'seed' ? (
    <StatusBadge tone="warning">{tx('Örnek değer — kontrol edip kaydedin')}</StatusBadge>
  ) : (
    <StatusBadge tone="success">{tx('Girildi')}</StatusBadge>
  )
}

/* ------------------------------------------------------------------ düzenleyici */

function Editor({ year, validFromMonth, row, rows, previous, onClose }: {
  year: number
  validFromMonth: number
  row: PayrollParameterRow | null
  rows: PayrollParameterRow[]
  previous: PayrollParameterRow | null
  onClose: () => void
}) {
  const [from, setFrom] = useState(validFromMonth)
  const existingMonths = rows.map((r) => r.validFromMonth)
  const prefill = prefillSource(rows, previous, from)
  const [form, setForm] = useState<ParamForm>(() => (row ? formFromRow(row, { keepSource: !row.seed }) : emptyForm()))
  const [errors, setErrors] = useState<ParamErrors>({})
  const [touched, setTouched] = useState(false)
  /** "Önceki yıldan doldur" kullanıldı: değerler kopya — resmî kaynakla karşılaştırma uyarısı belirginleşir. */
  const [prefilledFrom, setPrefilledFrom] = useState<PayrollParameterRow | null>(null)
  const [sampleGross, setSampleGross] = useState('')
  const [preview, setPreview] = useState<PayrollParametersPreview | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const set = <K extends keyof ParamForm>(k: K, v: ParamForm[K]) => { setForm((f) => ({ ...f, [k]: v })); setPreview(null) }
  const setBracket = (i: number, patch: Partial<ParamForm['brackets'][number]>) =>
    set('brackets', form.brackets.map((b, j) => (j === i ? { ...b, ...patch } : b)))

  // İlk kayıt denemesinden sonra hatalar yazdıkça güncellenir (satır içi doğrulama).
  useEffect(() => { if (touched) setErrors(validateForm(form, from).errors) }, [form, from, touched])

  const save = useAction(async () => {
    const v = validateForm(form, from)
    setTouched(true)
    setErrors(v.errors)
    if (!v.input) throw new Error(tx('Formdaki hataları düzeltin.'))
    try {
      return await payrollApi.saveParameters(year, v.input)
    } catch (e) {
      if (e instanceof ApiError) { const se = serverErrors(e.detail); if (Object.keys(se).length) setErrors(se) }
      throw e
    }
  }, { success: tx('Parametreler kaydedildi'), invalidate: [['payroll', 'params']], onDone: onClose })

  const runPreview = async () => {
    const v = validateForm(form, from)
    setTouched(true)
    setErrors(v.errors)
    setPreview(null)
    setPreviewError(null)
    if (!v.input) { setPreviewError(tx('Örnek hesap için önce formdaki hataları düzeltin.')); return }
    const g = parseAmount(sampleGross) ?? v.input.minimumWageGross
    if (Number.isNaN(g) || g <= 0) { setPreviewError(tx('Örnek brüt ücret için sıfırdan büyük bir sayı girin.')); return }
    setPreviewing(true)
    try {
      setPreview(await payrollApi.previewParameters(year, { parameters: v.input, monthlyGross: g, month: from }))
    } catch (e) {
      setPreviewError(errMsg(e))
    } finally {
      setPreviewing(false)
    }
  }

  const fc = sgkFloorCeiling(form)
  const err = (k: string) => errors[k]
  const isNew = !row
  const monthOptions = Array.from({ length: 12 }, (_, i) => i + 1)
    .filter((m) => !isNew || !existingMonths.includes(m))
    .map((m) => ({ value: String(m), label: tx('{0} ayından itibaren', [monthName(m)]) }))
  const prefillLabel = prefill
    ? prefill.year === year ? tx('{0} satırından doldur', [monthName(prefill.validFromMonth)]) : tx('Önceki yıldan doldur ({0} · {1})', [prefill.year, monthName(prefill.validFromMonth)])
    : null
  const netMismatch = preview && preview.minimumWageNetEntered != null && Math.abs(preview.minimumWageNetEntered - preview.minimumWageNetCalculated) >= 0.01

  return (
    <section aria-labelledby="param-editor-title" className="space-y-4 rounded-2xl border border-primary/30 bg-card/40 p-4 sm:p-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="min-w-0">
          <h3 id="param-editor-title" className="text-[14.5px] font-semibold">
            {isNew ? tx('{0} için yeni yürürlük satırı', [year]) : tx('{0} · {1} ayından itibaren', [year, monthName(from)])}
          </h3>
          <p className="text-[12.5px] text-muted-foreground">{tx('Yüzdeleri yüzde olarak yazın (ör. 14 ya da 0,759). Ondalık için virgül kullanabilirsiniz.')}</p>
        </div>
        {isNew && <div className="w-full sm:w-60"><SelectField label={tx('Yürürlük ayı')} value={String(from)} onChange={(v) => setFrom(Number(v))} options={monthOptions} /></div>}
      </div>

      <div role="note" className="rounded-xl border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/[0.07] px-3.5 py-2.5 text-[13px] leading-relaxed">
        {tx('HR360 yıllık bordro parametrelerini kendisi belirlemez. Kaydetmeden önce her değeri resmî kaynaklarla (Resmî Gazete, GİB, SGK genelgeleri) karşılaştırın; kaynağı not edin. Kayıt, adınız ve zamanla denetim kaydına yazılır.')}
      </div>

      {prefill && (
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => { setForm(formFromRow(prefill)); setPreview(null); setPrefilledFrom(prefill) }}>
            <Copy className="size-4" /> {prefillLabel}
          </Button>
          <span className="text-[12px] text-muted-foreground">{tx('Değerler kopyalanır; yeni yılın değerleriyle güncelleyip kontrol edin.')}</span>
        </div>
      )}

      {prefilledFrom && (
        <div role="alert" className="rounded-xl border border-[hsl(var(--warning))]/45 bg-[hsl(var(--warning))]/[0.10] px-3.5 py-2.5 text-[13px] leading-relaxed">
          <p className="font-semibold">{tx('Değerler {0} · {1} satırından kopyalandı', [prefilledFrom.year, monthName(prefilledFrom.validFromMonth)])}</p>
          <p className="text-muted-foreground">{tx('Asgari ücret, vergi dilimleri, SGK tavanı ve kıdem tavanı genellikle her yıl (bazen yıl içinde) değişir. Kaydetmeden önce her alanı resmî kaynaklarla kontrol edin.')}</p>
        </div>
      )}

      <form className="space-y-4" noValidate onSubmit={(e) => { e.preventDefault(); save.mutate(undefined) }}>
        <Group title={tx('Asgari ücret')} note={tx('Aylık tutarlar (TL). SGK prim tabanı brüt asgari ücrettir.')}>
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Brüt asgari ücret (aylık)')} required inputMode="decimal" autoComplete="off" value={form.minimumWageGross}
              onChange={(e) => set('minimumWageGross', e.target.value)} error={err('minimumWageGross')} hint={tx('Biçim: 12.345,67')} />
            <TextField label={tx('Net asgari ücret (bilgi)')} inputMode="decimal" autoComplete="off" value={form.minimumWageNet}
              onChange={(e) => set('minimumWageNet', e.target.value)} error={err('minimumWageNet')} hint={tx('Hesaplamaya girmez; örnek hesapla karşılaştırılır.')} />
          </div>
        </Group>

        <Group title={tx('Gelir vergisi dilimleri (ücret gelirleri)')} note={tx('Kümülatif matrah üst sınırları artan sırada; son dilim "üzeri"dir (sınırsız).')}>
          {err('brackets') && <p role="alert" className="mb-2 text-[12px] text-destructive">{err('brackets')}</p>}
          <ol className="space-y-3">
            {form.brackets.map((b, i) => {
              const last = i === form.brackets.length - 1
              return (
                <li key={i} className="grid grid-cols-[1fr_6.5rem_auto] items-start gap-2 sm:grid-cols-[1fr_9rem_auto]">
                  {last ? (
                    <div className="flex flex-col gap-1.5">
                      <span className="text-[13px] font-medium">{tx('{0}. dilim', [i + 1])}</span>
                      <p className="flex h-9 items-center rounded-md border border-dashed border-border px-3 text-[13px] text-muted-foreground">{tx('Üzeri (sınırsız)')}</p>
                    </div>
                  ) : (
                    <TextField label={tx('{0}. dilim üst sınırı (TL)', [i + 1])} inputMode="decimal" autoComplete="off" value={b.upTo}
                      onChange={(e) => setBracket(i, { upTo: e.target.value })} error={err(`brackets.${i}.upTo`)} />
                  )}
                  <TextField label={tx('{0}. dilim oranı (%)', [i + 1])} inputMode="decimal" autoComplete="off" value={b.ratePct}
                    onChange={(e) => setBracket(i, { ratePct: e.target.value })} error={err(`brackets.${i}.rate`)} />
                  <Button type="button" size="icon" variant="ghost" className="mt-6" aria-label={tx('{0}. dilimi sil', [i + 1])}
                    disabled={form.brackets.length <= 1} onClick={() => set('brackets', removeBracket(form.brackets, i))}>
                    <Trash2 className="size-4" />
                  </Button>
                </li>
              )
            })}
          </ol>
          <Button type="button" variant="outline" size="sm" className="mt-3" disabled={form.brackets.length >= 10} onClick={() => set('brackets', addBracket(form.brackets))}>
            <Plus className="size-4" /> {tx('Dilim ekle')}
          </Button>
        </Group>

        <Group title={tx('SGK ve işsizlik oranları')} note={tx('Yüzde olarak. İşveren SGK oranı teşviksiz yazılır; teşvik ayrı puan olarak düşülür.')}>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <TextField label={tx('SGK işçi payı (%)')} required inputMode="decimal" autoComplete="off" value={form.sgkEmployeePct} onChange={(e) => set('sgkEmployeePct', e.target.value)} error={err('sgkEmployeeRate')} />
            <TextField label={tx('İşsizlik işçi payı (%)')} required inputMode="decimal" autoComplete="off" value={form.unemploymentEmployeePct} onChange={(e) => set('unemploymentEmployeePct', e.target.value)} error={err('unemploymentEmployeeRate')} />
            <TextField label={tx('SGK işveren payı, teşviksiz (%)')} required inputMode="decimal" autoComplete="off" value={form.sgkEmployerPct} onChange={(e) => set('sgkEmployerPct', e.target.value)} error={err('sgkEmployerRate')} />
            <TextField label={tx('Teşvik indirimi (puan)')} required inputMode="decimal" autoComplete="off" value={form.incentivePoints} onChange={(e) => set('incentivePoints', e.target.value)} error={err('employerIncentivePoints')} hint={tx('Teşvik yoksa 0 yazın.')} />
            <TextField label={tx('İşsizlik işveren payı (%)')} required inputMode="decimal" autoComplete="off" value={form.unemploymentEmployerPct} onChange={(e) => set('unemploymentEmployerPct', e.target.value)} error={err('unemploymentEmployerRate')} />
          </div>
        </Group>

        <Group title={tx('SGK prim tabanı ve tavanı')}>
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Tavan (asgari ücretin katı)')} required inputMode="decimal" autoComplete="off" value={form.ceilingMultiplier}
              onChange={(e) => set('ceilingMultiplier', e.target.value)} error={err('sgkCeilingMultiplier')} hint={tx('Tavan tabandan düşük olamaz (en az 1 kat).')} />
            <dl className="grid grid-cols-[auto_1fr] content-center gap-x-4 gap-y-1 rounded-xl bg-muted/40 px-3.5 py-2.5 text-[13px]" aria-live="polite">
              <dt className="text-muted-foreground">{tx('Taban (asgari ücret)')}</dt><dd className="text-right tabular-nums">{fc ? formatMoney(fc.floor) : '—'}</dd>
              <dt className="text-muted-foreground">{tx('Tavan')}</dt><dd className="text-right tabular-nums">{fc ? formatMoney(fc.ceiling) : '—'}</dd>
            </dl>
          </div>
        </Group>

        <Group title={tx('Damga vergisi ve istisnalar')}>
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Damga vergisi oranı (%)')} required inputMode="decimal" autoComplete="off" value={form.stampPct} onChange={(e) => set('stampPct', e.target.value)} error={err('stampTaxRate')} hint={tx('Binde 7,59 = 0,759')} />
            <TextField label={tx('AGİ (aylık, 2022 öncesi — bilgi)')} inputMode="decimal" autoComplete="off" value={form.agiMonthly} onChange={(e) => set('agiMonthly', e.target.value)} error={err('agiMonthly')} hint={tx('Hesaplamaya girmez')} />
          </div>
          <label className="mt-3 flex items-start gap-2 text-[13px]">
            <Checkbox className="mt-0.5" checked={form.minimumWageExemption} onCheckedChange={(v) => set('minimumWageExemption', v === true)} />
            <span>{tx('Asgari ücret gelir vergisi ve damga vergisi istisnası uygulanır')}</span>
          </label>
        </Group>

        <Group title={tx('Kıdem tazminatı tavanı')} note={tx('Bilmiyorsanız boş bırakın; o yarıyılda kıdem hesabı tavan girilene kadar yapılmaz.')}>
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Ocak–Haziran')} inputMode="decimal" autoComplete="off" value={form.severanceH1} onChange={(e) => set('severanceH1', e.target.value)} error={err('severanceCeilingH1')} />
            <TextField label={tx('Temmuz–Aralık')} inputMode="decimal" autoComplete="off" value={form.severanceH2} onChange={(e) => set('severanceH2', e.target.value)} error={err('severanceCeilingH2')} />
          </div>
        </Group>

        <TextField label={tx('Kaynak notu (isteğe bağlı)')} value={form.source} onChange={(e) => set('source', e.target.value)} maxLength={300}
          error={err('source')} hint={tx('Ör. Resmî Gazete tarihi/sayısı, GİB duyurusu')} />

        <Group title={tx('Örnek hesap (kaydetmeden)')} note={tx('Girdiğiniz değerlerle, yıl başından beri aynı brütü alan bir çalışanın {0} bordrosu.', [monthName(from)])}>
          <div className="flex flex-wrap items-end gap-3">
            <div className="w-full sm:w-56">
              <TextField label={tx('Örnek brüt ücret')} inputMode="decimal" autoComplete="off" value={sampleGross} onChange={(e) => setSampleGross(e.target.value)} hint={tx('Boşsa asgari ücret')} />
            </div>
            <Button type="button" variant="outline" onClick={() => void runPreview()} disabled={previewing}><Calculator className="size-4" /> {tx('Hesapla')}</Button>
          </div>
          {previewError && <p role="alert" className="mt-2 text-[12.5px] text-destructive">{previewError}</p>}
          {preview && (
            <div className="mt-3 space-y-2" aria-live="polite">
              <dl className="grid grid-cols-[1fr_auto] gap-x-6 gap-y-1 text-[13px]">
                <dt className="text-muted-foreground">{tx('Brüt')}</dt><dd className="text-right tabular-nums">{formatMoney(preview.sample.gross)}</dd>
                <dt className="text-muted-foreground">{tx('SGK + işsizlik işçi payı')}</dt><dd className="text-right tabular-nums">− {formatMoney(preview.sample.sgkEmployee + preview.sample.unemploymentEmployee)}</dd>
                <dt className="text-muted-foreground">{tx('Gelir vergisi (istisna sonrası)')}</dt><dd className="text-right tabular-nums">− {formatMoney(preview.sample.incomeTax - preview.sample.incomeTaxExemption)}</dd>
                <dt className="text-muted-foreground">{tx('Damga vergisi (istisna sonrası)')}</dt><dd className="text-right tabular-nums">− {formatMoney(preview.sample.stampTax - preview.sample.stampTaxExemption)}</dd>
                <dt className="font-semibold">{tx('Net')}</dt><dd className="text-right font-semibold tabular-nums">{formatMoney(preview.sample.net)}</dd>
                <dt className="text-muted-foreground">{tx('İşveren maliyeti')}</dt><dd className="text-right tabular-nums">{formatMoney(preview.sample.employerCost)}</dd>
              </dl>
              <p className={netMismatch ? 'text-[12.5px] text-[hsl(var(--warning))]' : 'text-[12.5px] text-muted-foreground'}>
                {preview.minimumWageNetEntered == null
                  ? tx('Bu değerlerle asgari ücretin neti: {0}', [formatMoney(preview.minimumWageNetCalculated)])
                  : netMismatch
                    ? tx('Uyarı: bu değerlerle asgari ücretin neti {0}, girdiğiniz net {1}. Oranları ve dilimleri kontrol edin.', [formatMoney(preview.minimumWageNetCalculated), formatMoney(preview.minimumWageNetEntered)])
                    : tx('Bu değerlerle asgari ücretin neti girdiğiniz netle aynı: {0}', [formatMoney(preview.minimumWageNetCalculated)])}
              </p>
            </div>
          )}
        </Group>

        {Object.keys(errors).length > 0 && <p role="alert" className="text-[13px] text-destructive">{tx('Kaydetmeden önce işaretli alanları düzeltin.')}</p>}
        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={save.isPending}>{tx('Kaydet')}</Button>
          <Button type="button" variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
        </div>
      </form>
    </section>
  )
}

/* ------------------------------------------------------------------ panel */

export function PayrollParametersPanel() {
  const admin = useIsPayrollAdmin()
  const confirm = useConfirm()
  const [searchParams] = useSearchParams()
  const now = new Date().getFullYear()
  const initialYear = Number(searchParams.get('yil')) || now
  const [year, setYear] = useState(initialYear)
  const [editing, setEditing] = useState<{ from: number; row: PayrollParameterRow | null } | null>(null)
  const q = useQuery({ queryKey: ['payroll', 'params', year], queryFn: ({ signal }) => payrollApi.parameters(year, 1, signal) })
  const years = useQuery({ queryKey: ['payroll', 'params', 'years'], queryFn: ({ signal }) => payrollApi.parameterYears(signal) })
  useEffect(() => { setEditing(null) }, [year])
  const reset = useAction((from: number) => payrollApi.resetParameters(year, from), { success: tx('Yürürlük satırı silindi'), invalidate: [['payroll', 'params']] })
  const yearOptions = useMemo(() => {
    const entered = new Set((years.data ?? []).map((y) => y.year))
    const ys = new Set([now - 2, now - 1, now, now + 1, initialYear, ...entered])
    return [...ys].sort((a, b) => a - b).map((y) => ({ value: String(y), label: entered.has(y) ? tx('{0} · girildi', [y]) : tx('{0} · girilmedi', [y]) }))
  }, [now, initialYear, years.data])

  const rows = q.data?.rows ?? []
  const existing = rows.map((r) => r.validFromMonth)
  const firstFree = Array.from({ length: 12 }, (_, i) => i + 1).find((m) => !existing.includes(m)) ?? 1
  const startNew = () => setEditing({ from: rows.length ? (existing.includes(7) ? firstFree : 7) : 1, row: null })
  const janMissing = rows.length > 0 && !existing.includes(1)

  return (
    <Panel className="max-w-4xl">
      <PanelHead title={tx('Bordro parametreleri')}
        note={tx('Yıl ve yürürlük ayına göre bordro yetkilisi girer. Dönem hesaplanırken ayına göre en son yürürlüğe giren satır kullanılır; kapanmış dönemler değişmez. Her değişiklik denetim kaydına yazılır.')}
        action={<div className="w-40"><SelectField label={tx('Yıl')} value={String(year)} onChange={(v) => setYear(Number(v))} options={yearOptions} /></div>} />
      <PanelBody className="space-y-4">
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} /> : (
          <>
            {rows.length === 0 && !editing && (
              <EmptyState title={tx('{0} için parametre girilmedi', [year])}
                detail={tx('Bu yılın bordro dönemleri parametreler girilene kadar hesaplanamaz. Değerleri resmî kaynaklardan (Resmî Gazete, GİB, SGK) alıp girin.')}
                action={admin ? <Button onClick={() => setEditing({ from: 1, row: null })}><Plus className="size-4" /> {tx('Parametreleri gir')}</Button> : undefined} />
            )}
            {rows.length > 0 && (
              <ul className="space-y-2" aria-label={tx('{0} yürürlük satırları', [year])}>
                {rows.map((r) => (
                  <li key={r.id} className="flex flex-col gap-2 rounded-xl border border-border p-3.5 sm:flex-row sm:items-start sm:justify-between">
                    <div className="min-w-0 space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-[14px] font-semibold">{tx('{0} ayından itibaren', [monthName(r.validFromMonth)])}</span>
                        <StatusLine r={r} />
                      </div>
                      <RowSummary r={r} />
                      <p className="text-[12px] text-muted-foreground">
                        {r.seed
                          ? tx('Önceki sürümün kurulumda eklediği değerler; resmî kaynakla doğrulanmadı. Kontrol edip kaydedin.')
                          : tx('Giren: {0} · {1}', [r.updatedBy ?? '—', formatDateTime(r.updatedAt)])}
                        {r.source ? ` · ${tx('Kaynak: {0}', [r.source])}` : ''}
                      </p>
                    </div>
                    {admin && (
                      <div className="flex shrink-0 gap-2">
                        <Button size="sm" variant="outline" onClick={() => setEditing({ from: r.validFromMonth, row: r })}
                          aria-label={tx('{0} satırını düzenle', [monthName(r.validFromMonth)])}><Pencil className="size-4" /> {r.seed ? tx('Kontrol et') : tx('Düzenle')}</Button>
                        <Button size="sm" variant="ghost" aria-label={tx('{0} satırını sil', [monthName(r.validFromMonth)])} disabled={reset.isPending} onClick={async () => {
                          if (await confirm({ title: tx('Yürürlük satırı silinsin mi?'), note: tx('{0} yılı {1} ayından itibaren geçerli satır silinir. O aylar için önceki satır geçerli olur; önceki satır yoksa dönemler hesaplanamaz. Kapanmış dönemler etkilenmez.', [year, monthName(r.validFromMonth)]), action: tx('Sil') })) reset.mutate(r.validFromMonth)
                        }}><Trash2 className="size-4" /></Button>
                      </div>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {rows.length > 0 && (
              <div>
                <p className="mb-1.5 text-[12.5px] font-medium">{tx('Aylara göre durum')}</p>
                <ul className="grid grid-cols-3 gap-1.5 sm:grid-cols-6" aria-label={tx('{0} aylara göre parametre durumu', [year])}>
                  {Array.from({ length: 12 }, (_, i) => i + 1).map((m) => {
                    const applies = rows.filter((r) => r.validFromMonth <= m).sort((a, b) => b.validFromMonth - a.validFromMonth)[0]
                    return (
                      <li key={m} className={applies ? 'rounded-lg border border-border px-2 py-1.5 text-[12px]' : 'rounded-lg border border-[hsl(var(--warning))]/45 bg-[hsl(var(--warning))]/[0.08] px-2 py-1.5 text-[12px]'}>
                        <span className="block font-medium">{monthName(m)}</span>
                        <span className="text-muted-foreground">{applies ? tx('Girildi ({0})', [monthName(applies.validFromMonth)]) : tx('Girilmedi')}</span>
                      </li>
                    )
                  })}
                </ul>
              </div>
            )}
            {janMissing && <InfoNote>{tx('Ocak ayından itibaren geçerli satır yok: {0} ayından önceki dönemler hesaplanamaz.', [monthName(Math.min(...existing))])}</InfoNote>}
            {!admin && <InfoNote>{tx('Parametreleri yalnızca bordro yetkilileri (İK yöneticisi, şirket yöneticisi) girebilir.')}</InfoNote>}
            {admin && rows.length > 0 && !editing && existing.length < 12 && (
              <Button variant="outline" onClick={startNew}><Plus className="size-4" /> {tx('Yıl içi yeni yürürlük satırı')}</Button>
            )}
            {admin && editing && (
              <Editor key={`${year}-${editing.from}-${editing.row?.id ?? 'new'}`} year={year} validFromMonth={editing.from} row={editing.row}
                rows={rows} previous={q.data?.previous ?? null} onClose={() => setEditing(null)} />
            )}
            <p className="text-[12px] text-muted-foreground">{tx('Kanunla sabit değerler (parametre değildir): fazla mesai saatlik ücretin %150\'si, aylık 225 saat.')}</p>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}
