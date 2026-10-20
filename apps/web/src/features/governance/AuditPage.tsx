import { useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis } from 'recharts'
import { ChevronDown, Download, Link2, ScrollText, Search } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { AuditIntegrityPanel } from './KvkkOps'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { SelectField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/states'
import { Modal } from '@/components/ui/modal'
import { useToast } from '@/components/ui/toast'
import { governanceApi, type AuditEntry, type AuditFilter } from '@/api/governance'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { TenantGate, errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { auditActionLabels as actionLabel, auditEntityLabel } from './auditLabels'

const ALL = '__all__'
const actionTone = { Created: 'success', Updated: 'info', Deleted: 'danger', SensitiveViewed: 'warning', Revealed: 'warning' } as const

function fmt(v: unknown): string {
  if (v === null || v === undefined) return '∅'
  if (typeof v === 'object') return JSON.stringify(v)
  return String(v)
}

function Changes({ e }: { e: AuditEntry }) {
  const ch = e.changes ?? {}
  const keys = Object.keys(ch)
  if (keys.length === 0) return <p className="text-[12px] text-muted-foreground">{tx('Ayrıntı yok.')}</p>
  return (
    <table className="w-full text-[12px]">
      <tbody>
        {keys.map((k) => {
          const v = ch[k] as unknown
          const diff = v && typeof v === 'object' && 'old' in (v as object) && 'new' in (v as object)
          return (
            <tr key={k} className="border-t border-border/60 align-top">
              <td className="w-40 py-1 pr-3 font-mono text-muted-foreground">{k}</td>
              {diff ? (
                <td className="py-1"><span className="rounded bg-rose-500/15 px-1 line-through">{fmt((v as { old: unknown }).old)}</span> → <span className="rounded bg-emerald-500/15 px-1">{fmt((v as { new: unknown }).new)}</span></td>
              ) : <td className="py-1 break-all">{fmt(v)}</td>}
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

export function AuditPage() {
  const toast = useToast()
  const [f, setF] = useState<AuditFilter>({ page: 1, pageSize: 50 })
  const [q, setQ] = useState('')
  const [open, setOpen] = useState<number | null>(null)
  const [corr, setCorr] = useState<string | null>(null)
  const facets = useQuery({ queryKey: ['audit', 'facets'], queryFn: ({ signal }) => governanceApi.auditFacets(signal) })
  // Ters tarih aralığında sunucuya istek atılmaz; önceki sonuç da gösterilmez (aşağıda uyarı).
  const rangeError = f.from && f.to && f.from > f.to ? tx('Bitiş tarihi başlangıçtan önce olamaz.') : undefined
  const list = useQuery({ queryKey: ['audit', f], queryFn: ({ signal }) => governanceApi.audit(f, signal), placeholderData: keepPreviousData, enabled: !rangeError })
  const chain = useQuery({ queryKey: ['audit', 'corr', corr], enabled: !!corr, queryFn: ({ signal }) => governanceApi.auditCorrelation(corr!, signal) })
  const set = (patch: Partial<AuditFilter>) => setF((x) => ({ ...x, ...patch, page: 1 }))
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.pageSize)) : 1
  // Aynı adı taşıyan farklı kullanıcılar (ör. her kanal için "Sohbet (Slack)" bot kullanıcısı) kimliğin kısa
  // ön ekiyle ayırt edilir; aksi hâlde filtrede aynı etiket defalarca görünür.
  const userOptions = useMemo(() => {
    const users = (facets.data?.users ?? []).filter((u) => u.id)
    const seen = new Map<string, number>()
    users.forEach((u) => { const n = u.name ?? u.id!; seen.set(n, (seen.get(n) ?? 0) + 1) })
    return users.map((u) => {
      const n = u.name ?? u.id!
      return { value: u.id!, label: (seen.get(n) ?? 0) > 1 ? `${n} · ${u.id!.slice(0, 8)} (${u.count})` : `${n} (${u.count})` }
    })
  }, [facets.data])
  return (
    <TenantGate>
      <PageHeader title={tx('Denetim kaydı')} description={tx('Kim, neyi, ne zaman, hangi istekle değiştirdi? Tüm servislerdeki ekleme/güncelleme/silme işlemleri eski → yeni değerleriyle.')} actions={<Button variant="outline" disabled={!!rangeError} onClick={() => governanceApi.auditExport(f).catch((e) => toast.stop(errMsg(e)))}><Download className="size-4" />{' '}{tx('CSV')}</Button>} />
      <div className="mb-6"><AuditIntegrityPanel /></div>
      <div className="grid gap-6 xl:grid-cols-[300px_1fr]">
        <div className="space-y-5">
          <Panel>
            <PanelHead title={tx('Filtre')} />
            <PanelBody className="space-y-3">
              <form onSubmit={(e) => { e.preventDefault(); set({ q: q || undefined }) }} className="relative">
                <Search className="absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Değer, kişi veya kimlik ara')} className="pl-9" />
              </form>
              <SelectField label={tx('Servis')} value={f.service ?? ALL} onChange={(v) => set({ service: v === ALL ? undefined : v })} options={[{ value: ALL, label: tx('Tümü') }, ...(facets.data?.services ?? []).map((s) => ({ value: s.name, label: `${s.name} (${s.count})` }))]} />
              <SelectField label={tx('Varlık')} value={f.entityType ?? ALL} onChange={(v) => set({ entityType: v === ALL ? undefined : v })} options={[{ value: ALL, label: tx('Tümü') }, ...(facets.data?.entityTypes ?? []).map((s) => ({ value: s.name, label: `${auditEntityLabel(s.name)} (${s.count})` }))]} />
              <SelectField label={tx('Kullanıcı')} value={f.userId ?? ALL} onChange={(v) => set({ userId: v === ALL ? undefined : v })} options={[{ value: ALL, label: tx('Tümü') }, ...userOptions]} />
              <SelectField label={tx('İşlem')} value={f.action ?? ALL} onChange={(v) => set({ action: v === ALL ? undefined : v })} options={[{ value: ALL, label: tx('Tümü') }, ...Object.entries(actionLabel).map(([k, v]) => ({ value: k, label: v }))]} />
              <div className="grid grid-cols-2 gap-2">
                <TextField label={tx('Başlangıç')} type="date" value={f.from ?? ''} max={f.to} onChange={(e) => set({ from: e.target.value || undefined })} />
                <TextField label={tx('Bitiş')} type="date" value={f.to ?? ''} min={f.from} onChange={(e) => set({ to: e.target.value || undefined })} error={rangeError} />
              </div>
              <Button variant="ghost" size="sm" onClick={() => { setQ(''); setF({ page: 1, pageSize: 50 }) }}>{tx('Temizle')}</Button>
            </PanelBody>
          </Panel>
          <Panel>
            {/* Grafik her zaman son 90 günün genel dağılımıdır; tarih filtresi seçiliyken bunu açıkça söyle. */}
            <PanelHead title={tx('Son 90 gün')} note={f.from || f.to ? tx('Genel etkinlik; tarih filtresinden bağımsız.') : undefined} />
            <PanelBody className="h-28 p-2">
              <ResponsiveContainer>
                <AreaChart data={facets.data?.daily ?? []}>
                  <XAxis dataKey="day" hide />
                  <Tooltip contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12, fontSize: 12 }} />
                  <Area dataKey="count" name={tx('Kayıt')} stroke="hsl(var(--primary))" fill="hsl(var(--primary)/0.2)" />
                </AreaChart>
              </ResponsiveContainer>
            </PanelBody>
          </Panel>
        </div>
        <Panel>
          <PanelHead title={rangeError ? tx('Tarih aralığı geçersiz') : tx('{0} kayıt', [list.data?.total ?? 0])} note={tx('Satıra tıklayın: alan bazında eski → yeni değer. Hassas alanlar *** olarak tutulur.')} />
          <PanelBody className="p-0">
            {rangeError ? <EmptyState icon={ScrollText} title={tx('Tarih aralığı geçersiz')} detail={tx('Bitiş tarihi başlangıçtan önce olamaz. Tarihleri düzeltin ya da filtreyi temizleyin.')} /> : list.isPending ? <div className="p-5"><RowsSkeleton /></div> : list.isError ? <ErrorState message={(list.error as Error).message} /> : list.data.items.length === 0 ? <EmptyState icon={ScrollText} title={tx('Kayıt yok')} /> : (
              <ul className="divide-y divide-border">
                {list.data.items.map((e) => (
                  <li key={e.id}>
                    <button onClick={() => setOpen(open === e.id ? null : e.id)} className="flex w-full cursor-pointer flex-wrap items-center gap-3 px-5 py-2.5 text-left text-[13px] hover:bg-accent/30">
                      <span className="tabular w-36 shrink-0 text-[12px] text-muted-foreground">{formatDateTime(e.occurredAt)}</span>
                      <StatusBadge tone={actionTone[e.action as keyof typeof actionTone] ?? 'neutral'}>{actionLabel[e.action] ?? e.action}</StatusBadge>
                      <span className="font-medium" title={e.entityType}>{auditEntityLabel(e.entityType)}</span>
                      <span className="truncate font-mono text-[11.5px] text-muted-foreground">{e.entityId?.slice(0, 8)}</span>
                      <span className="ml-auto text-[12.5px]">{e.userName ?? e.userId}</span>
                      <span className="hidden text-[11.5px] text-muted-foreground md:inline">{e.service}</span>
                      <ChevronDown className={cn('size-4 text-muted-foreground transition', open === e.id && 'rotate-180')} />
                    </button>
                    <AnimatePresence>
                      {open === e.id && (
                        <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} className="overflow-hidden bg-muted/20 px-5">
                          <div className="space-y-2 py-3">
                            <Changes e={e} />
                            <div className="flex flex-wrap gap-3 text-[11.5px] text-muted-foreground">
                              <span>{tx('IP: {0}', [e.ipAddress ?? '—'])}</span>
                              {e.correlationId && <button onClick={() => setCorr(e.correlationId)} className="flex cursor-pointer items-center gap-1 text-primary hover:underline"><Link2 className="size-3" />{' '}{tx('Aynı istekteki tüm değişiklikler ({0}…)', [e.correlationId.slice(0, 10)])}</button>}
                            </div>
                          </div>
                        </motion.div>
                      )}
                    </AnimatePresence>
                  </li>
                ))}
              </ul>
            )}
            <div className="flex items-center justify-between border-t border-border px-5 py-2.5 text-[12.5px]">
              <span className="text-muted-foreground">{tx('Sayfa {0} / {1}', [f.page, pages])}</span>
              <div className="flex gap-2">
                <Button size="sm" variant="outline" disabled={(f.page ?? 1) <= 1} onClick={() => setF((x) => ({ ...x, page: (x.page ?? 1) - 1 }))}>{tx('Önceki')}</Button>
                <Button size="sm" variant="outline" disabled={(f.page ?? 1) >= pages} onClick={() => setF((x) => ({ ...x, page: (x.page ?? 1) + 1 }))}>{tx('Sonraki')}</Button>
              </div>
            </div>
          </PanelBody>
        </Panel>
      </div>
      {corr && (
        <Modal open onClose={() => setCorr(null)} size="lg" title={tx('İstek zinciri')} note={`X-Correlation-Id: ${corr}`}>
          {chain.isPending ? <RowsSkeleton rows={3} /> : (
            <ol className="relative space-y-4 border-l border-border pl-5">
              {chain.data?.map((e) => (
                <li key={e.id}><span className="absolute -left-1.5 mt-1 size-3 rounded-full bg-primary" /><p className="text-[13px]"><b>{e.service}</b> · {auditEntityLabel(e.entityType)} {actionLabel[e.action] ?? e.action}</p><Changes e={e} /></li>
              ))}
            </ol>
          )}
        </Modal>
      )}
    </TenantGate>
  )
}
