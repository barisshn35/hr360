import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { Bar, BarChart, CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { BookmarkPlus, Code2, CornerDownLeft, Sparkles } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { InfoNote } from '@/components/ui/states'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextField } from '@/components/ui/field'
import { governanceApi } from '@/api/governance'
import { platformGovApi, type NlReportPlus, type ReportFilters } from '@/api/platformGov'
import { SaveReportModal, SavedReportsPanel } from './SavedReports'
import { ShinyText } from '@/components/fx/shiny-text'
import { TenantGate, errMsg } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'

const TT = { background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12, fontSize: 12 }

type NlReport = NlReportPlus

export function ReportView({ r }: { r: NlReport }) {
  const [showSql, setShowSql] = useState(false)
  // İşe alım hunisi (tek satır): her aşama bir çubuk.
  const funnel = r.chart === 'funnel' && r.rows.length === 1
  // KVKK: küçük grupların değeri sunucuda null'a çevrilir; 0 sayılmaz, "gizli" olarak işaretlenir ve grafikte çizilmez.
  const num = (c: string | number | null | undefined) => (c === null || c === undefined || c === '' ? null : Number(c))
  const hiddenLabel = tx('gizli')
  const raw = funnel
    ? r.columns.slice(1).map((c, i) => ({ k: c, v: num(r.rows[0]![i + 1]) }))
    : r.rows.map((row) => ({ k: String(row[0] ?? '—'), v: num(row[1]) }))
  const data = raw.map((d) => (d.v === null ? { k: `${d.k} (${hiddenLabel})`, v: null } : d))
  const cell = (c: string | number | null, j: number) => (c !== null ? c : j > 0 && (r.suppressed ?? 0) > 0 ? hiddenLabel : '—')
  return (
    <motion.div initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} className="space-y-4">
      <p className="text-[15px] font-semibold">{r.interpretation}</p>
      {!r.understood ? null : !funnel && (r.chart === 'number' || data.length === 1) ? (
        <motion.p initial={{ scale: 0.8, opacity: 0 }} animate={{ scale: 1, opacity: 1 }} className="tabular text-[56px] font-semibold tracking-tight text-primary">{data[0]?.v === null || data[0]?.v === undefined ? <span className="text-[28px] text-muted-foreground">{data[0] ? tx('Gizli (KVKK)') : '—'}</span> : data[0].v.toLocaleString(appLocale)}</motion.p>
      ) : data.length === 0 ? (
        <p className="text-[13px] text-muted-foreground">{tx('Bu aralıkta kayıt yok.')}</p>
      ) : (
        <div className="h-72">
          <ResponsiveContainer>
            {r.chart === 'line' ? (
              <LineChart data={data}>
                <CartesianGrid stroke="hsl(var(--border))" strokeDasharray="3 3" vertical={false} />
                <XAxis dataKey="k" fontSize={11} tickLine={false} axisLine={false} />
                <YAxis fontSize={11} width={36} tickLine={false} axisLine={false} />
                <Tooltip contentStyle={TT} />
                <Line dataKey="v" name={r.columns[1]} stroke="hsl(var(--primary))" strokeWidth={2.5} dot={{ r: 3 }} animationDuration={1200} />
              </LineChart>
            ) : (
              <BarChart data={data}>
                <CartesianGrid stroke="hsl(var(--border))" strokeDasharray="3 3" vertical={false} />
                <XAxis dataKey="k" fontSize={11} tickLine={false} axisLine={false} />
                <YAxis fontSize={11} width={36} tickLine={false} axisLine={false} />
                <Tooltip contentStyle={TT} cursor={{ fill: 'hsl(var(--muted)/0.4)' }} />
                <Bar dataKey="v" name={r.columns[1]} fill="hsl(var(--primary))" radius={[8, 8, 0, 0]} animationDuration={900} />
              </BarChart>
            )}
          </ResponsiveContainer>
        </div>
      )}
      {r.understood && data.length > 0 && (
        <table className="w-full max-w-lg text-[13px]">
          <thead><tr className="text-left text-muted-foreground">{r.columns.map((c) => <th key={c} className="py-1.5 font-medium">{c}</th>)}</tr></thead>
          <tbody>{r.rows.map((row, i) => <tr key={i} className="border-t border-border">{row.map((c, j) => <td key={j} className={c === null && j > 0 ? 'py-1.5 text-muted-foreground italic' : 'tabular py-1.5'}>{cell(c, j)}</td>)}</tr>)}</tbody>
        </table>
      )}
      {r.understood && r.note && <p className="text-[12px] text-muted-foreground">{r.note}</p>}
      {r.understood && (
        <div>
          <button onClick={() => setShowSql((s) => !s)} className="flex cursor-pointer items-center gap-1.5 text-[12px] text-muted-foreground hover:text-foreground"><Code2 className="size-3.5" /> {showSql ? tx('Sorguyu gizle') : tx('Çalıştırılan sorgu')}</button>
          {showSql && <pre className="mt-2 overflow-x-auto rounded-xl bg-muted/50 p-3 font-mono text-[11.5px] whitespace-pre-wrap">{r.sql}</pre>}
        </div>
      )}
    </motion.div>
  )
}

export function NlReportPage() {
  const ex = useQuery({ queryKey: ['nl-examples'], queryFn: ({ signal }) => governanceApi.examples(signal), staleTime: Infinity })
  const depts = useQuery({ queryKey: ['nl-departments'], queryFn: ({ signal }) => platformGovApi.departments(signal), staleTime: 5 * 60_000 })
  const [q, setQ] = useState('')
  const [filters, setFilters] = useState<ReportFilters>({})
  const [history, setHistory] = useState<Array<{ q: string; r?: NlReport; err?: string; filters: ReportFilters; saved?: string }>>([])
  const [busy, setBusy] = useState(false)
  const [saving, setSaving] = useState<{ q: string; filters: ReportFilters; personLevel?: boolean } | null>(null)
  const [params, setParams] = useSearchParams()
  const clean = (f: ReportFilters): ReportFilters => ({
    departmentId: f.departmentId || undefined, from: f.from || undefined, to: f.to || undefined, compare: f.compare || undefined,
  })
  const ask = async (question: string) => {
    if (!question.trim()) return
    setBusy(true)
    setQ('')
    const f = clean(filters)
    try {
      const r = await platformGovApi.report(question, f)
      setHistory((h) => [{ q: question, r, filters: f }, ...h].slice(0, 8))
    } catch (e) {
      setHistory((h) => [{ q: question, err: errMsg(e), filters: f }, ...h])
    } finally {
      setBusy(false)
    }
  }
  /** Kayıtlı raporu güncel veriyle çalıştırır (bildirimdeki bağlantı ?kayitli=<id> ile gelir). */
  const runSaved = async (id: string) => {
    setBusy(true)
    try {
      const { report, result } = await platformGovApi.runSavedReport(id)
      setHistory((h) => [{ q: report.question, r: result, filters: {}, saved: report.name }, ...h].slice(0, 8))
    } catch (e) {
      setHistory((h) => [{ q: tx('Kayıtlı rapor'), err: errMsg(e), filters: {} }, ...h])
    } finally {
      setBusy(false)
    }
  }
  const savedParam = params.get('kayitli')
  useEffect(() => {
    if (!savedParam) return
    void runSaved(savedParam)
    setParams((p) => { p.delete('kayitli'); return p }, { replace: true })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [savedParam])
  return (
    <TenantGate>
      <PageHeader title={tx('Rapor asistanı')} description={tx('Türkçe sorun; tablo ve grafik gelsin. Rapor yazmak için SQL bilmeye gerek yok.')} />
      <div className="mb-5"><InfoNote><Sparkles className="mr-1 inline size-3.5" />{' '}{tx('Kural tabanlı ayrıştırıcıdır (dil modeli değil): soruyu ölçüt + kırılım + zaman aralığına çevirir, hazır ve güvenli sorgu şablonlarından birini çalıştırır. Nasıl anladığını ve çalıştırdığı sorguyu her zaman gösterir. 5 kişiden küçük grupların değeri gizlenir.')}</InfoNote></div>
      <form onSubmit={(e) => { e.preventDefault(); void ask(q) }} className="surface relative mb-3 flex items-center gap-2 rounded-2xl border border-border p-2 shadow-[0_0_0_1px_hsl(var(--primary)/0.08),0_20px_60px_-30px_hsl(var(--primary)/0.4)]">
        <Sparkles className="ml-2 size-5 text-primary" />
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Örn. son 6 ayda departmanlara göre izin günleri')} className="h-11 flex-1 bg-transparent text-[15px] outline-none placeholder:text-muted-foreground" autoFocus />
        <Button type="submit" disabled={busy || !q.trim()}>{busy ? <ShinyText>{tx('Hesaplanıyor…')}</ShinyText> : <><CornerDownLeft className="size-4" />{' '}{tx('Sor')}</>}</Button>
      </form>
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <SelectField label={tx('Departman')} value={filters.departmentId ?? ''} placeholder={tx('Sorudaki / tüm şirket')}
          onChange={(v) => setFilters((f) => ({ ...f, departmentId: v || null }))}
          options={[{ value: '', label: tx('Sorudaki / tüm şirket') }, ...(depts.data ?? []).map((d) => ({ value: d.id, label: d.name }))]} />
        <TextField label={tx('Başlangıç')} type="date" value={filters.from ?? ''} onChange={(e) => setFilters((f) => ({ ...f, from: e.target.value || null }))} />
        <TextField label={tx('Bitiş')} type="date" value={filters.to ?? ''} onChange={(e) => setFilters((f) => ({ ...f, to: e.target.value || null }))} />
        <label className="flex items-center gap-2 self-end pb-2 text-[13px]">
          <Checkbox checked={filters.compare === true} onCheckedChange={(v) => setFilters((f) => ({ ...f, compare: v === true || null }))} /> {tx('Geçen yılla karşılaştır')}
        </label>
      </div>
      <div className="mb-6 flex flex-wrap gap-1.5">
        {ex.data?.map((e) => <button key={e} onClick={() => void ask(e)} className="cursor-pointer rounded-full border border-border bg-card/50 px-3 py-1 text-[12.5px] text-muted-foreground transition hover:border-primary/50 hover:text-foreground">{e}</button>)}
      </div>
      <div className="space-y-5">
        <AnimatePresence initial={false}>
          {history.map((h, i) => (
            <motion.div key={`${h.q}-${history.length - i}`} initial={{ opacity: 0, y: -10 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }}>
              <Panel>
                <PanelHead title={<span className="text-muted-foreground">{h.saved ? `${h.saved} · ` : ''}“{h.q}”</span>}
                  action={h.r?.understood && !h.saved ? (
                    <Button size="sm" variant="outline" onClick={() => setSaving({ q: h.q, filters: h.filters, personLevel: h.r?.personLevel })}>
                      <BookmarkPlus className="size-4" /> {tx('Kaydet')}
                    </Button>
                  ) : undefined} />
                <PanelBody>{h.err ? <p className="text-[13px] text-destructive">{h.err}</p> : h.r && <ReportView r={h.r} />}</PanelBody>
              </Panel>
            </motion.div>
          ))}
        </AnimatePresence>
        <SavedReportsPanel onRun={(id) => void runSaved(id)} />
      </div>
      {saving && <SaveReportModal question={saving.q} filters={saving.filters} personLevel={saving.personLevel} onClose={() => setSaving(null)} />}
    </TenantGate>
  )
}
