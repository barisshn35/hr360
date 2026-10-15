import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Lock, Pencil, Plus, Save, ShieldAlert, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { platformGovApi, type CustomField, type CustomFieldInput, type CustomFieldType, type CustomFieldValue, type FieldLevel } from '@/api/platformGov'
import { formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'

const TYPE_LABELS: Record<CustomFieldType, string> = {
  text: tx('Metin'), number: tx('Sayı'), date: tx('Tarih'), select: tx('Seçim listesi'), boolean: tx('Evet / hayır'),
}
const LEVEL_LABELS: Record<FieldLevel, string> = {
  everyone: tx('Tüm çalışanlar'), manager: tx('Yönetici ve İK'), hr: tx('Yalnızca İK'), self: tx('Yalnızca kişinin kendisi'),
}

const EMPTY: CustomFieldInput = {
  key: '', label: '', type: 'text', options: [], required: false, visibility: 'hr', selfEditable: true,
  isSpecialCategory: false, legalBasis: '', purpose: '', retentionMonths: 24,
}

/* ======================================================================= tasarımcı (İK) */

/** /panel/ayarlar/ozel-alanlar — Y24 alan tasarımcısı: KVKK üst verisi zorunlu. */
export function CustomFieldsPage() {
  const meta = useQuery({ queryKey: ['custom-fields', 'meta'], queryFn: ({ signal }) => platformGovApi.fieldMeta(signal), staleTime: Infinity })
  const list = useQuery({ queryKey: ['custom-fields', 'list'], queryFn: ({ signal }) => platformGovApi.fields(signal) })
  const [editing, setEditing] = useState<{ id?: string; f: CustomFieldInput } | null>(null)
  const del = useAction((id: string) => platformGovApi.deleteField(id), { success: tx('Alan ve değerleri silindi (imha tutanağına yazıldı)'), invalidate: [['custom-fields']] })
  const confirm = useConfirm()
  return (
    <>
      <PageHeader title={tx('Özel alanlar')} eyebrow={[tx('KVKK')]}
        description={tx('Çalışan profiline ek bilgi alanları ekleyin. Her alan KVKK işleme envanterinde kendiliğinden görünür.')}
        actions={<Button onClick={() => setEditing({ f: { ...EMPTY } })}><Plus className="size-4" /> {tx('Yeni alan')}</Button>} />
      <div className="mb-5"><InfoNote>{meta.data?.notice ?? tx('Özel nitelikli kişisel veri yalnızca zorunluysa toplanmalıdır; onaylı gizlilik etki değerlendirmesi gerekir.')}</InfoNote></div>
      <Panel>
        <PanelHead title={tx('Tanımlı alanlar')} note={tx('Değerler ayrılıştan sonra alanın saklama süresi dolunca otomatik silinir.')} />
        <PanelBody className="p-0">
          {list.isPending ? <div className="p-4"><RowsSkeleton rows={3} /></div> : !list.data?.length ? (
            <EmptyState title={tx('Henüz özel alan yok')} detail={tx('"Yeni alan" ile başlayın.')} />
          ) : (
            <ul className="divide-y divide-border">
              {list.data.map(({ field: f, values }) => (
                <li key={f.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                  <div className="min-w-0 flex-1">
                    <p className="flex items-center gap-2 font-medium">
                      {f.isSpecialCategory && <Lock className="size-3.5 text-amber-600" />}{f.label}
                      <span className="font-mono text-[11.5px] text-muted-foreground">{f.key}</span>
                    </p>
                    <p className="text-[12px] text-muted-foreground">
                      {TYPE_LABELS[f.type]} · {LEVEL_LABELS[f.visibility]} · {f.legalBasisLabel} · {tx('{0} ay saklama', [f.retentionMonths])} · {tx('{0} değer', [values])}
                    </p>
                    <p className="text-[12px] text-muted-foreground">{tx('Amaç: {0}', [f.purpose])} · {f.createdBy}, {formatDateTime(f.createdAt)}</p>
                  </div>
                  {f.isSpecialCategory && <StatusBadge tone="warning">{tx('Özel nitelikli · şifreli')}</StatusBadge>}
                  {!f.isActive && <StatusBadge tone="neutral">{tx('Pasif')}</StatusBadge>}
                  <Button size="sm" variant="outline" onClick={() => setEditing({ id: f.id, f: toInput(f) })}><Pencil className="size-4" /> {tx('Düzenle')}</Button>
                  <Button size="sm" variant="outline" disabled={del.isPending}
                    onClick={async () => { if (await confirm({ title: tx('“{0}” alanı silinsin mi?', [f.label]), note: tx('Alan ve tüm çalışanlardaki değerleri kalıcı olarak silinir; silme imha tutanağına yazılır.'), action: tx('Sil') })) del.mutate(f.id) }}><Trash2 className="size-4" /></Button>
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      {editing && meta.data && <FieldEditor id={editing.id} initial={editing.f} meta={meta.data} onClose={() => setEditing(null)} />}
    </>
  )
}

function toInput(f: CustomField): CustomFieldInput {
  return {
    key: f.key, label: f.label, type: f.type, options: f.options, required: f.required, visibility: f.visibility, selfEditable: f.selfEditable,
    isSpecialCategory: f.isSpecialCategory, legalBasis: f.legalBasis, purpose: f.purpose, retentionMonths: f.retentionMonths,
    assessmentId: f.assessmentId, sortOrder: f.sortOrder, isActive: f.isActive,
  }
}

function FieldEditor({ id, initial, meta, onClose }: {
  id?: string; initial: CustomFieldInput; meta: NonNullable<Awaited<ReturnType<typeof platformGovApi.fieldMeta>>>; onClose: () => void
}) {
  const [f, setF] = useState<CustomFieldInput>(initial)
  const [optionsText, setOptionsText] = useState((initial.options ?? []).join('\n'))
  const set = <K extends keyof CustomFieldInput>(k: K, v: CustomFieldInput[K]) => setF((x) => ({ ...x, [k]: v }))
  const pias = useQuery({ queryKey: ['privacy', 'assessments'], queryFn: ({ signal }) => platformGovApi.assessments(signal), enabled: f.isSpecialCategory })
  const approved = (pias.data ?? []).filter((a) => a.kind === 'CustomField' && a.status === 'Approved')
  const bases = useMemo(() => meta.legalBases.filter((b) => b.special === f.isSpecialCategory), [meta, f.isSpecialCategory])
  const [error, setError] = useState<string | null>(null)
  const body = (): CustomFieldInput => ({ ...f, options: f.type === 'select' ? optionsText.split('\n').map((o) => o.trim()).filter(Boolean) : [] })
  const save = useAction(() => (id ? platformGovApi.updateField(id, body()) : platformGovApi.createField(body())), {
    success: tx('Alan kaydedildi; KVKK envanterine eklendi'), invalidate: [['custom-fields']], onDone: onClose,
  })
  const submit = () => save.mutateAsync(undefined).catch((e) => setError(errMsg(e)))
  return (
    <Modal open onClose={onClose} size="lg" title={id ? tx('Alanı düzenle') : tx('Yeni özel alan')}
      note={tx('KVKK bilgileri (özel nitelik, hukuki sebep, amaç, saklama süresi) zorunludur.')}
      footer={<Button disabled={save.isPending} onClick={() => void submit()}><Save className="size-4" /> {tx('Kaydet')}</Button>}>
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={tx('Etiket')} value={f.label} onChange={(e) => set('label', e.target.value)} maxLength={120} required />
        <TextField label={tx('Anahtar')} value={f.key} disabled={!!id} onChange={(e) => set('key', e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '_'))}
          hint={tx('Küçük harf, rakam ve _ (ör. tisort_bedeni). Sonradan değişmez.')} required />
        <SelectField label={tx('Tür')} value={f.type} disabled={!!id} onChange={(v) => set('type', v as CustomFieldType)}
          options={meta.types.map((t) => ({ value: t, label: TYPE_LABELS[t] }))} />
        <SelectField label={tx('Kim görebilir?')} value={f.visibility} onChange={(v) => set('visibility', v as FieldLevel)}
          options={meta.levels.map((l) => ({ value: l, label: LEVEL_LABELS[l], disabled: f.isSpecialCategory && !(l === 'hr' || l === 'self') }))}
          hint={tx('Kişi kendi değerini her zaman görür.')} />
        {f.type === 'select' && (
          <div className="sm:col-span-2"><TextAreaField label={tx('Seçenekler (her satıra bir tane)')} rows={4} value={optionsText} onChange={(e) => setOptionsText(e.target.value)} /></div>
        )}
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.required} onCheckedChange={(v) => set('required', v === true)} /> {tx('Zorunlu')}</label>
        <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.selfEditable} onCheckedChange={(v) => set('selfEditable', v === true)} /> {tx('Çalışan kendisi doldurabilir')}</label>
      </div>
      <div className="mt-5 space-y-4 rounded-xl border border-border p-4">
        <p className="flex items-center gap-2 text-[13px] font-semibold"><ShieldAlert className="size-4 text-primary" /> {tx('KVKK bilgileri')}</p>
        <label className="flex items-start gap-2 text-[13px]">
          <Checkbox checked={f.isSpecialCategory} disabled={!!id}
            onCheckedChange={(v) => setF((x) => ({ ...x, isSpecialCategory: v === true, legalBasis: '', visibility: v === true ? 'hr' : x.visibility }))} />
          <span>{tx('Özel nitelikli kişisel veri (sağlık, din, sendika, biyometrik, ceza mahkûmiyeti vb.)')}<br />
            <span className="text-[12px] text-muted-foreground">{tx('Değer şifreli saklanır, yalnızca İK ya da kişi görür; onaylı gizlilik etki değerlendirmesi gerekir.')}</span></span>
        </label>
        <SelectField label={tx('Hukuki sebep')} value={f.legalBasis} onChange={(v) => set('legalBasis', v)} options={bases.map((b) => ({ value: b.value, label: b.label }))} required />
        <TextAreaField label={tx('İşleme amacı')} rows={2} maxLength={500} value={f.purpose} onChange={(e) => set('purpose', e.target.value)} required />
        <TextField label={tx('Saklama süresi (ay, ayrılıştan sonra)')} type="number" min={1} max={240} value={String(f.retentionMonths)}
          onChange={(e) => set('retentionMonths', Number(e.target.value) || 0)} required />
        {f.isSpecialCategory && !id && (
          approved.length ? (
            <SelectField label={tx('Onaylı gizlilik etki değerlendirmesi')} value={f.assessmentId ?? ''} onChange={(v) => set('assessmentId', v || null)}
              options={approved.map((a) => ({ value: a.id, label: `${a.subject} (${a.risk})` }))} />
          ) : (
            <InfoNote>{tx('Onaylı "Özel alan" türünde bir etki değerlendirmesi yok. KVKK › Etki değerlendirmesi ekranından hazırlayıp onaylatın; konu olarak alanın anahtarını yazarsanız otomatik eşleşir.')}</InfoNote>
          )
        )}
      </div>
      {error && <p role="alert" className="mt-3 text-[13px] text-destructive">{error}</p>}
    </Modal>
  )
}

/* ======================================================================= Ek bilgiler paneli */

function display(v: CustomFieldValue) {
  if (v.value === null || v.value === '') return '—'
  if (v.type === 'boolean') return v.value ? tx('Evet') : tx('Hayır')
  return String(v.value)
}

/** Profil / çalışan sayfası "Ek bilgiler": görünürlük düzeyine göre süzülmüş alanlar. */
export function ExtraInfoPanel({ employeeId }: { employeeId?: string }) {
  // Özel alanlar şirkete aittir: kiracısız oturumda (platform yöneticisi) istek atılmaz (400 tenant_missing).
  const { tenantSlug } = useAuth()
  const q = useQuery({
    enabled: Boolean(tenantSlug),
    queryKey: ['custom-fields', 'values', employeeId ?? 'me'],
    queryFn: ({ signal }) => (employeeId ? platformGovApi.fieldValues(employeeId, signal) : platformGovApi.myFieldValues(signal)),
    retry: false,
  })
  const [draft, setDraft] = useState<Record<string, string | number | boolean | null> | null>(null)
  const target = q.data?.employeeId ?? employeeId
  const save = useAction(() => platformGovApi.setFieldValues(target!, draft ?? {}), {
    success: tx('Ek bilgiler kaydedildi'), invalidate: [['custom-fields', 'values']], onDone: () => setDraft(null),
  })
  if (q.isPending) return null
  if (q.isError || !q.data || q.data.fields.length === 0) return null
  const fields = q.data.fields
  const value = (f: CustomFieldValue) => (draft && f.key in draft ? draft[f.key] : f.value)
  const put = (k: string, v: string | number | boolean | null) => setDraft((d) => ({ ...(d ?? {}), [k]: v }))
  return (
    <Panel>
      <PanelHead title={tx('Ek bilgiler')} note={tx('Şirketinizin tanımladığı ek alanlar. Kilitli alanlar özel nitelikli veridir ve şifreli saklanır.')}
        action={draft ? <Button size="sm" disabled={save.isPending} onClick={() => save.mutate(undefined)}><Save className="size-4" /> {tx('Kaydet')}</Button> : undefined} />
      <PanelBody className="grid gap-4 sm:grid-cols-2">
        {fields.map((f) => {
          const label = `${f.label}${f.required ? ' *' : ''}`
          const v = value(f)
          if (!f.editable) {
            return (
              <div key={f.key} className="text-[13px]">
                <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground">{f.isSpecialCategory && <Lock className="size-3" />}{f.label}</p>
                <p className="font-medium">{display(f)}</p>
              </div>
            )
          }
          if (f.type === 'select')
            return <SelectField key={f.key} label={label} value={(v as string) ?? ''} onChange={(x) => put(f.key, x || null)} options={f.options.map((o) => ({ value: o, label: o }))} />
          if (f.type === 'boolean')
            return (
              <label key={f.key} className="flex items-center gap-2 self-end text-[13px]">
                <Checkbox checked={v === true} onCheckedChange={(x) => put(f.key, x === true)} /> {label}
              </label>
            )
          return (
            <TextField key={f.key} label={label} type={f.type === 'number' ? 'number' : f.type === 'date' ? 'date' : 'text'} value={v === null || v === undefined ? '' : String(v)}
              onChange={(e) => put(f.key, e.target.value === '' ? null : e.target.value)} maxLength={f.type === 'text' ? 500 : undefined}
              hint={f.isSpecialCategory ? tx('Özel nitelikli veri — şifreli saklanır') : undefined} />
          )
        })}
      </PanelBody>
    </Panel>
  )
}
