import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { ArrowRight, Bell, FlaskConical, MessageSquare, Pencil, Plus, Sparkles, Trash2, Webhook, Workflow, Zap } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { Tabs, useTabParam } from '@/components/ui/tabs'
import { governanceApi, type Rule, type RuleAction, type RuleCatalog, type RuleCondition } from '@/api/governance'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { TenantGate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/confirm'
import { ruleRecipes, type RuleDraft } from './ruleRecipes'

const ACTION_ICON: Record<string, React.ElementType> = { notify: Bell, slack: MessageSquare, teams: MessageSquare, webhook: Webhook }
type Draft = RuleDraft

function RuleEditor({ rule, initial, catalog, onClose }: { rule?: Rule; initial?: Draft; catalog: RuleCatalog; onClose: () => void }) {
  const [d, setD] = useState<Draft>(rule ? { name: rule.name, description: rule.description, trigger: rule.trigger, conditions: rule.conditions, actions: rule.actions, isEnabled: rule.isEnabled }
    : initial ?? { name: '', description: '', trigger: 'leave.approved', conditions: [], actions: [{ type: 'notify', target: 'employee', message: '{{ozet}}' }], isEnabled: true })
  const fields = catalog.events.find((e) => e.type === d.trigger)?.fields ?? []
  const [sample, setSample] = useState('')
  const samplePayload = useMemo(() => {
    try { return sample ? JSON.parse(sample) : Object.fromEntries(fields.map((f) => [f, f === 'Days' ? 7 : f.endsWith('Id') ? '00000000-0000-0000-0000-000000000000' : tx('örnek {0}', [f])])) } catch { return null }
  }, [sample, fields])
  const [test, setTest] = useState<{ matched: boolean; reason: string; actions: Array<{ type: string; message: string }> | null } | null>(null)
  const save = useAction(() => (rule ? governanceApi.updateRule(rule.id, d) : governanceApi.createRule(d)), { success: tx('Kural kaydedildi'), invalidate: [['rules']], onDone: onClose })
  const runTest = useAction(() => governanceApi.testRule({ conditions: d.conditions, actions: d.actions, payload: samplePayload ?? {} }), { onDone: setTest })
  const updC = (i: number, p: Partial<RuleCondition>) => setD({ ...d, conditions: d.conditions.map((c, j) => (j === i ? { ...c, ...p } : c)) })
  const updA = (i: number, p: Partial<RuleAction>) => setD({ ...d, actions: d.actions.map((a, j) => (j === i ? { ...a, ...p } : a)) })
  return (
    <Modal open onClose={onClose} size="xl" title={rule ? tx('Kuralı düzenle') : tx('Yeni kural')} note={tx('Olay → koşullar (hepsi sağlanmalı) → eylemler. Mesajlarda {{Alan}} ve {{ozet}} kullanılabilir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button variant="outline" disabled={!samplePayload || runTest.isPending} title={!samplePayload ? tx('Deneme yükü geçerli JSON değil') : undefined} onClick={() => samplePayload && runTest.mutate(undefined)}><FlaskConical className="size-4" />{' '}{tx('Dene')}</Button><Button disabled={!d.name.trim() || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="grid gap-6 lg:grid-cols-[1.3fr_1fr]">
        <div className="space-y-5">
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Kural adı')} value={d.name} onChange={(e) => setD({ ...d, name: e.target.value })} />
            <SelectField label={tx('Olduğunda (tetikleyici)')} value={d.trigger} onChange={(v) => setD({ ...d, trigger: v, conditions: [] })} options={catalog.events.map((e) => ({ value: e.type, label: e.label }))} />
          </div>
          <div>
            <div className="mb-2 flex items-center justify-between"><p className="text-[13.5px] font-medium">{tx('Koşullar')}</p><Button size="xs" variant="outline" onClick={() => setD({ ...d, conditions: [...d.conditions, { field: fields[0] ?? '', op: 'eq', value: '' }] })}><Plus className="size-3" />{' '}{tx('Koşul')}</Button></div>
            {d.conditions.length === 0 && <p className="text-[12.5px] text-muted-foreground">{tx('Koşul yok — her olayda çalışır.')}</p>}
            {d.conditions.map((c, i) => (
              <div key={i} className="mb-2 grid grid-cols-[1fr_130px_1fr_auto] items-end gap-2">
                {fields.length ? <SelectField label={tx('Alan')} value={c.field} onChange={(v) => updC(i, { field: v })} options={fields.map((f) => ({ value: f, label: f }))} /> : <TextField label={tx('Alan')} value={c.field} onChange={(e) => updC(i, { field: e.target.value })} />}
                <SelectField label={tx('İşlem')} value={c.op} onChange={(v) => updC(i, { op: v })} options={catalog.operators.map((o) => ({ value: o.op, label: o.label }))} />
                <TextField label={tx('Karşılaştırılan değer')} aria-label={tx('Koşul {0} değeri', [i + 1])} placeholder={tx('ör. 5')} value={c.value} onChange={(e) => updC(i, { value: e.target.value })} />
                <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => setD({ ...d, conditions: d.conditions.filter((_, j) => j !== i) })}><Trash2 className="size-4" /></Button>
              </div>
            ))}
          </div>
          <div>
            <div className="mb-2 flex items-center justify-between"><p className="text-[13.5px] font-medium">{tx('Eylemler')}</p><Button size="xs" variant="outline" onClick={() => setD({ ...d, actions: [...d.actions, { type: 'slack', message: '{{ozet}}' }] })}><Plus className="size-3" />{' '}{tx('Eylem')}</Button></div>
            {d.actions.map((a, i) => (
              <div key={i} className="mb-3 space-y-2 rounded-xl border border-border p-3">
                <div className="grid grid-cols-[150px_1fr_auto] items-end gap-2">
                  <SelectField label={tx('Tür')} value={a.type} onChange={(v) => updA(i, { type: v as RuleAction['type'], target: v === 'notify' ? 'employee' : '' })} options={catalog.actions.map((x) => ({ value: x.type, label: x.label }))} />
                  {a.type === 'notify' ? (
                    <SelectField label={tx('Alıcı')} value={a.target ?? 'employee'} onChange={(v) => updA(i, { target: v })} options={[{ value: 'employee', label: tx('Olaydaki çalışan') }, { value: 'requester', label: tx('Talep sahibi') }, { value: 'approver', label: tx('Onaylayan') }]} />
                  ) : <TextField label={a.type === 'webhook' ? tx('URL') : tx('Gelen webhook URL (boş: tanımlı kanallar)')} value={a.target ?? ''} onChange={(e) => updA(i, { target: e.target.value })} />}
                  <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => setD({ ...d, actions: d.actions.filter((_, j) => j !== i) })} disabled={d.actions.length === 1}><Trash2 className="size-4" /></Button>
                </div>
                <TextField label={tx('Mesaj')} value={a.message} onChange={(e) => updA(i, { message: e.target.value })} />
              </div>
            ))}
          </div>
          <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={d.isEnabled} onCheckedChange={(v) => setD({ ...d, isEnabled: v === true })} />{' '}{tx('Etkin')}</label>
        </div>
        <div className="space-y-3">
          <p className="text-[13.5px] font-medium">{tx('Deneme yükü')}</p>
          <TextAreaField label="" aria-label={tx('Örnek olay JSON')} rows={9} className="font-mono text-[11.5px]" value={sample || JSON.stringify(samplePayload ?? {}, null, 2)} onChange={(e) => setSample(e.target.value)} />
          {!samplePayload && <p className="text-[12px] text-destructive">{tx('Geçersiz JSON')}</p>}
          {test && (
            <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className={cn('rounded-xl border p-3 text-[13px]', test.matched ? 'border-[hsl(var(--success))]/40 bg-[hsl(var(--success))]/10' : 'border-border bg-muted/40')}>
              <p className="font-medium">{test.matched ? tx('✓ Kural tetiklenir') : tx('✗ Tetiklenmez')}</p>
              <p className="text-muted-foreground">{test.reason}</p>
              {test.actions?.map((a, i) => <p key={i} className="mt-1">→ <b>{catalog.actions.find((x) => x.type === a.type)?.label ?? a.type}</b>: {a.message}</p>)}
            </motion.div>
          )}
        </div>
      </div>
    </Modal>
  )
}

const NOTIFY_TARGET: Record<string, string> = { employee: tx('olaydaki çalışan'), requester: tx('talep sahibi'), approver: tx('onaylayan') }

/** Kart üzerindeki akış: teknik adlar (document.signed, eq, notify) katalogdaki Türkçe etiketlerle gösterilir. */
function Flow({ r, catalog }: { r: Rule; catalog?: RuleCatalog }) {
  const eventLabel = catalog?.events.find((e) => e.type === r.trigger)?.label ?? r.trigger
  const opLabel = (op: string) => catalog?.operators.find((o) => o.op === op)?.label ?? op
  const actionLabel = (a: RuleAction) => {
    const label = catalog?.actions.find((x) => x.type === a.type)?.label ?? a.type
    return a.type === 'notify' && a.target ? `${label} → ${NOTIFY_TARGET[a.target] ?? a.target}` : label
  }
  const steps = [
    { icon: Zap, text: eventLabel },
    { icon: Workflow, text: r.conditions.length ? r.conditions.map((c) => `${c.field} ${opLabel(c.op)} ${c.value}`).join(tx(' ve ')) : tx('koşulsuz') },
    ...r.actions.map((a) => ({ icon: ACTION_ICON[a.type] ?? Bell, text: actionLabel(a) })),
  ]
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {steps.map((s, i) => (
        <motion.div key={i} initial={{ opacity: 0, x: -6 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: i * 0.08 }} className="flex items-center gap-1.5">
          {i > 0 && <ArrowRight className="size-3.5 text-muted-foreground" />}
          <span className="inline-flex max-w-56 items-center gap-1 truncate rounded-full border border-border bg-background/60 px-2.5 py-1 text-[11.5px]" title={s.text}><s.icon className="size-3 shrink-0 text-primary" /> {s.text}</span>
        </motion.div>
      ))}
    </div>
  )
}

export function RulesPage() {
  const [rawTab, setTab] = useTabParam<'kurallar' | 'calismalar'>('sekme', 'kurallar')
  // Geçersiz ?sekme= değeri boş sayfa yerine varsayılan sekmeye düşer.
  const tab = rawTab === 'calismalar' ? 'calismalar' : 'kurallar'
  const catalog = useQuery({ queryKey: ['rules', 'catalog'], queryFn: ({ signal }) => governanceApi.ruleCatalog(signal), staleTime: Infinity })
  const rules = useQuery({ queryKey: ['rules'], queryFn: ({ signal }) => governanceApi.rules(signal) })
  const runs = useQuery({ queryKey: ['rules', 'runs'], queryFn: ({ signal }) => governanceApi.ruleRuns(undefined, signal), enabled: tab === 'calismalar', refetchInterval: 15_000 })
  const [edit, setEdit] = useState<Rule | null | undefined>(undefined)
  const [recipe, setRecipe] = useState<RuleDraft | null>(null)
  const samples = useAction(() => governanceApi.sampleRules(), { success: tx('Örnek kurallar eklendi'), invalidate: [['rules']] })
  const toggle = useAction((r: Rule) => governanceApi.updateRule(r.id, { name: r.name, description: r.description, trigger: r.trigger, conditions: r.conditions, actions: r.actions, isEnabled: !r.isEnabled }), { invalidate: [['rules']] })
  const del = useAction((id: string) => governanceApi.deleteRule(id), { success: tx('Silindi'), invalidate: [['rules']] })
  const confirm = useConfirm()
  const askDelete = async (r: Rule) => {
    if (await confirm({ title: tx('“{0}” kuralı silinsin mi?', [r.name]), note: tx('Kural kalıcı olarak silinir; olaylar artık bu kurala göre değerlendirilmez. Yalnızca durdurmak için “Etkin” işaretini kaldırabilirsiniz.'), action: tx('Sil') })) del.mutate(r.id)
  }
  return (
    <TenantGate>
      <PageHeader title={tx('Kural motoru')} description={tx('“5 günden uzun izin onaylanınca İK kanalına yaz”, “ayrılışta webhook tetikle”… Kod yazmadan olay tabanlı otomasyonlar.')} actions={catalog.data && <Button onClick={() => setEdit(null)}><Plus className="size-4" />{' '}{tx('Yeni kural')}</Button>} />
      <div className="mb-4"><InfoNote>{tx('Kurallar Kafka olay akışında değerlendirilir (governance-service). Her tetiklenme “Çalışmalar” sekmesine sonucuyla yazılır.')}</InfoNote></div>
      <div className="mb-5"><Tabs label={tx('Kural motoru')} value={tab} onChange={setTab} tabs={[{ key: 'kurallar', label: tx('Kurallar'), count: rules.data?.length }, { key: 'calismalar', label: tx('Çalışmalar') }]} /></div>
      {tab === 'kurallar' && catalog.data && (
        <Panel className="mb-5">
          <PanelHead title={<span className="flex items-center gap-2"><Sparkles className="size-4 text-amber-400" />{tx('Hazır tarifler')}</span>} note={tx('Tıklayın, düzenleyicide açılsın; isterseniz değiştirip kaydedin.')} />
          <PanelBody className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
            {ruleRecipes().map((x) => (
              <button key={x.id} type="button" onClick={() => setRecipe(x.draft)}
                className="cursor-pointer rounded-xl border border-border p-3 text-left transition-colors hover:border-primary/40 hover:bg-primary/5">
                <span className="block text-[13px] font-semibold">{x.title}</span>
                <span className="block text-[12px] text-muted-foreground">{x.detail}</span>
              </button>
            ))}
          </PanelBody>
        </Panel>
      )}
      {tab === 'kurallar' ? (
        rules.isPending ? <RowsSkeleton /> : (rules.data ?? []).length === 0 ? (
          <EmptyState icon={Workflow} title={tx('Henüz kural yok')} detail={tx('Örnek kurallarla başlayın veya kendi kuralınızı oluşturun.')} action={<Button onClick={() => samples.mutate(undefined)}>{tx('Örnek kurallar ekle')}</Button>} />
        ) : (
          <div className="grid gap-4 lg:grid-cols-2">
            {rules.data!.map((r, i) => (
              <motion.div key={r.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className={cn('surface rounded-2xl border p-5', r.isEnabled ? 'border-border' : 'border-dashed border-border opacity-70')}>
                <div className="flex items-start justify-between gap-2">
                  <div><h3 className="text-[15px] font-semibold">{r.name}</h3>{r.description && <p className="text-[12.5px] text-muted-foreground">{r.description}</p>}</div>
                  <div className="flex items-center gap-1">
                    <Checkbox checked={r.isEnabled} onCheckedChange={() => toggle.mutate(r)} aria-label={tx('Etkin')} />
                    <Button size="icon" variant="ghost" aria-label={tx('Düzenle')} onClick={() => setEdit(r)}><Pencil className="size-4" /></Button>
                    <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => askDelete(r)}><Trash2 className="size-4" /></Button>
                  </div>
                </div>
                <div className="mt-3"><Flow r={r} catalog={catalog.data} /></div>
                <p className="mt-3 text-[12px] text-muted-foreground">{tx('{0} kez tetiklendi{1}', [r.fireCount, r.lastFiredAt ? tx(' · son {0}', [formatRelativeToNow(r.lastFiredAt)]) : ''])}</p>
              </motion.div>
            ))}
          </div>
        )
      ) : (
        <Panel>
          <PanelHead title={tx('Son 100 çalışma')} />
          <PanelBody className="p-0">
            {runs.isPending ? <div className="p-5"><RowsSkeleton /></div> : (runs.data ?? []).length === 0 ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Henüz tetiklenme yok.')}</p> : (
              <ul className="divide-y divide-border">{runs.data!.map((x) => (
                <li key={x.id} className="flex flex-wrap items-center gap-3 px-5 py-2.5 text-[13px]">
                  <span className="tabular w-36 text-[12px] text-muted-foreground">{formatDateTime(x.occurredAt)}</span>
                  <span className="font-medium">{x.ruleName}</span><span className="text-[11.5px] text-muted-foreground" title={x.eventType}>{catalog.data?.events.find((e) => e.type === x.eventType)?.label ?? x.eventType}</span>
                  <StatusBadge tone={x.result.includes('hata') ? 'danger' : 'success'}>{x.result}</StatusBadge>
                </li>
              ))}</ul>
            )}
          </PanelBody>
        </Panel>
      )}
      {edit !== undefined && catalog.data && <RuleEditor rule={edit ?? undefined} catalog={catalog.data} onClose={() => setEdit(undefined)} />}
      {recipe && catalog.data && <RuleEditor initial={recipe} catalog={catalog.data} onClose={() => setRecipe(null)} />}
    </TenantGate>
  )
}
