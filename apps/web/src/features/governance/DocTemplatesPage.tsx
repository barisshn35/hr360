import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { FileStack, FileText, Plus, Printer, Save, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Modal } from '@/components/ui/modal'
import { TextField } from '@/components/ui/field'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useToast } from '@/components/ui/toast'
import { governanceApi, type DocTemplate, type RenderedDoc } from '@/api/governance'
import { useDirectory } from '@/api/directory'
import { cn } from '@/lib/utils'
import { TenantGate, errMsg, useAction } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/confirm'
import { Callout } from '@/components/ui/callout'
import { requestTrace, WATERMARK_CSS, watermarkHtml } from '@/lib/watermark'

/** Belgeleri sayfa sonlarıyla tek pencerede açar ve yazdırma diyaloğunu tetikler (PDF olarak kaydet). */
export function printDocuments(title: string, docs: RenderedDoc[]) {
  const w = window.open('', '_blank')
  if (!w) throw new Error(tx('Açılır pencere engellendi; tarayıcıda izin verin.'))
  w.document.write(`<!doctype html><meta charset="utf-8"><title>…</title><p style="font-family:Arial,sans-serif">${tx('Hazırlanıyor…')}</p>`)
  // Güvenlik dalgası 2B: indirene ait filigran + iz kodu (denetim kaydına bağlı).
  void requestTrace('document', { subject: title.slice(0, 100), format: 'print', rows: docs.length }).then((trace) => writeDocuments(w, title, docs, trace?.text))
}

function writeDocuments(w: Window, title: string, docs: RenderedDoc[], watermark?: string) {
  w.document.open()
  const pages = docs.map((d, i) => `<section class="page"${i < docs.length - 1 ? ' style="page-break-after:always"' : ''}>${d.html}</section>`).join('')
  w.document.write(`<!doctype html><html lang="tr"><head><meta charset="utf-8"><title>${title}</title>
    <style>@page{size:A4;margin:22mm 20mm}body{font-family:Inter,'Segoe UI',Arial,sans-serif;color:#111;font-size:12pt;line-height:1.6}
    .page{max-width:170mm;margin:0 auto}h1,h2{letter-spacing:-.01em}@media screen{body{background:#f4f4f5}.page{background:#fff;padding:24mm 20mm;margin:16px auto;box-shadow:0 2px 12px #0002}}${WATERMARK_CSS}</style>
    </head><body>${watermarkHtml(watermark)}${pages}<script>window.onload=()=>setTimeout(()=>window.print(),300)<\/script></body></html>`)
  w.document.close()
}

const SAMPLE: Record<string, string> = {
  'calisan.adSoyad': tx('Ayşe Yılmaz'), 'calisan.ad': tx('Ayşe'), 'calisan.soyad': tx('Yılmaz'), 'calisan.eposta': 'ayse@ornek.com', 'calisan.telefon': '+90 555 000 00 00',
  'calisan.pozisyon': tx('Yazılım Mühendisi'), 'calisan.departman': tx('Mühendislik'), 'calisan.iseGiris': '01.03.2022', 'calisan.kidemYil': '4,6', 'calisan.yoneticisi': tx('Mehmet Demir'),
  'izin.kalanYillik': '9', 'ucret.brut': '75.000,00 TRY', 'sirket.ad': tx('Örnek A.Ş.'), 'sirket.vergiNo': '1234567890', bugun: new Date().toLocaleDateString(appLocale), 'belge.no': '261002-001',
}

function BulkModal({ t, onClose }: { t: DocTemplate; onClose: () => void }) {
  const toast = useToast()
  const dir = useDirectory()
  const [sel, setSel] = useState<string[]>([])
  const [filter, setFilter] = useState('')
  const people = (dir.data ?? []).filter((d) => d.fullName.toLocaleLowerCase(appLocale).includes(filter.toLocaleLowerCase(appLocale)))
  const render = useAction(() => governanceApi.renderTemplate(t.id, sel), {
    onDone: (r) => { try { printDocuments(r.template, r.documents); toast.ok(tx('{0} belge hazırlandı', [r.documents.length])) } catch (e) { toast.stop(errMsg(e)) } },
  })
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Toplu üret — {0}', [t.name])} note={tx('Seçilen her çalışan için ayrı sayfa; açılan pencerede “PDF olarak kaydet”i seçin.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!sel.length || render.isPending} onClick={() => render.mutate(undefined)}><Printer className="size-4" /> {tx('{0} belge üret', [sel.length])}</Button></>}>
      <div className="space-y-3">
        <div className="flex gap-2"><Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder={tx('Ara')} /><Button variant="outline" onClick={() => setSel(people.map((p) => p.id))}>{tx('Tümünü seç')}</Button><Button variant="ghost" onClick={() => setSel([])}>{tx('Temizle')}</Button></div>
        <ul className="max-h-80 space-y-1 overflow-y-auto">
          {people.map((p) => (
            <li key={p.id}><label className="flex cursor-pointer items-center gap-2.5 rounded-lg px-2 py-1.5 text-[13px] hover:bg-accent/40"><Checkbox checked={sel.includes(p.id)} onCheckedChange={(v) => setSel((s) => (v === true ? [...s, p.id] : s.filter((x) => x !== p.id)))} /> {p.fullName}</label></li>
          ))}
        </ul>
      </div>
    </Modal>
  )
}

/** İK: çalışanların belge talepleri (onay bekleyenler ve son düzenlenenler). */
function DocRequestsPanel() {
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['doc-requests', 'all'], queryFn: ({ signal }) => governanceApi.docRequests(undefined, signal) })
  const decide = useAction(({ id, ok }: { id: string; ok: boolean }) => governanceApi.decideDocRequest(id, ok), { success: tx('Karar kaydedildi; çalışana bildirildi'), invalidate: [['doc-requests']] })
  const open = useAction((id: string) => governanceApi.docRequestDocument(id), { onDone: (d) => printDocuments(d.templateName, [{ employeeId: '', name: '', html: d.html }]) })
  const pending = (q.data ?? []).filter((r) => r.status === 'Pending')
  const recent = (q.data ?? []).filter((r) => r.status !== 'Pending').slice(0, 10)
  return (
    <Panel>
      <PanelHead title={tx('Belge talepleri')} note={tx('Çalışanlar "çalışan talep edebilir" işaretli şablonları kendileri ister. Belge şifreli saklanır ve doğrulama koduyla doğrulanır; açtığınız belge erişim kaydına yazılır.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : !q.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Henüz belge talebi yok.')}</p> : (
          <>
            {pending.length > 0 && (
              <ul className="divide-y divide-border text-[13px]">
                {pending.map((r) => (
                  <li key={r.id} className="flex flex-wrap items-center gap-2 py-2">
                    <span className="min-w-0 flex-1">{nameOf(r.employeeId)} · {r.templateName}{r.purpose ? ` · ${r.purpose}` : ''}</span>
                    <Button size="sm" onClick={() => decide.mutate({ id: r.id, ok: true })}>{tx('Düzenle')}</Button>
                    <Button size="sm" variant="outline" onClick={() => decide.mutate({ id: r.id, ok: false })}>{tx('Reddet')}</Button>
                  </li>
                ))}
              </ul>
            )}
            {recent.length > 0 && (
              <ul className="divide-y divide-border text-[12.5px] text-muted-foreground">
                {recent.map((r) => (
                  <li key={r.id} className="flex items-center gap-2 py-1.5">
                    <span className="min-w-0 flex-1 truncate">{nameOf(r.employeeId)} · {r.templateName} · {r.status === 'Issued' ? tx('Düzenlendi') : tx('Reddedildi')}</span>
                    {r.status === 'Issued' && <Button size="xs" variant="ghost" onClick={() => open.mutate(r.id)}><Printer className="size-3.5" /></Button>}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

export function DocTemplatesPage() {
  const list = useQuery({ queryKey: ['doc-templates'], queryFn: ({ signal }) => governanceApi.templates(signal) })
  const ph = useQuery({ queryKey: ['doc-placeholders'], queryFn: ({ signal }) => governanceApi.placeholders(signal), staleTime: Infinity })
  const [selId, setSelId] = useState<string | null>(null)
  const sel = list.data?.find((t) => t.id === selId) ?? null
  const [f, setF] = useState({ name: '', category: 'Genel', body: '', selfService: false, requiresApproval: true })
  const [bulk, setBulk] = useState<DocTemplate | null>(null)
  const area = useRef<HTMLTextAreaElement>(null)
  useEffect(() => { if (sel) setF({ name: sel.name, category: sel.category, body: sel.body, selfService: sel.selfService, requiresApproval: sel.requiresApproval }) }, [sel])
  const samples = useAction(() => governanceApi.sampleTemplates(), { success: (r) => tx('{0} hazır şablon eklendi', [r.added]), invalidate: [['doc-templates']] })
  const save = useAction(() => (sel ? governanceApi.updateTemplate(sel.id, f) : governanceApi.createTemplate(f)), { success: tx('Şablon kaydedildi'), invalidate: [['doc-templates']], onDone: (t) => setSelId(t.id) })
  const del = useAction(() => governanceApi.deleteTemplate(sel!.id), { success: tx('Silindi'), invalidate: [['doc-templates']], onDone: () => { setSelId(null); setF({ name: '', category: 'Genel', body: '', selfService: false, requiresApproval: true }) } })
  const insert = (key: string) => {
    const el = area.current
    const token = `{{${key}}}`
    if (!el) return setF((x) => ({ ...x, body: x.body + token }))
    const s = el.selectionStart, e = el.selectionEnd
    setF((x) => ({ ...x, body: x.body.slice(0, s) + token + x.body.slice(e) }))
    requestAnimationFrame(() => { el.focus(); el.selectionStart = el.selectionEnd = s + token.length })
  }
  // Tanımsız yer tutucular ({{yok.alan}}) belgede olduğu gibi kalır; önizlemede ve kaydetmede uyarılır.
  const unknownKeys = useMemo(() => {
    if (!ph.data) return []
    const known = new Set(ph.data.map((p) => p.key))
    return [...new Set([...f.body.matchAll(/\{\{\s*([\w.]+)\s*\}\}/g)].map((m) => m[1]))].filter((k) => !known.has(k))
  }, [f.body, ph.data])
  const confirm = useConfirm()
  const askSave = async () => {
    if (unknownKeys.length > 0 && !(await confirm({
      title: tx('Tanımsız yer tutucu var'),
      note: tx('{0} tanımlı değil; üretilen belgelerde olduğu gibi görünür. Yine de kaydedilsin mi?', [unknownKeys.map((k) => `{{${k}}}`).join(', ')]),
      action: tx('Yine de kaydet'),
      destructive: false,
    }))) return
    save.mutate(undefined)
  }
  const askDelete = async () => {
    if (sel && await confirm({ title: tx('“{0}” şablonu silinsin mi?', [sel.name]), note: tx('Şablon kalıcı olarak silinir; çalışanlar bu şablonla yeni belge talep edemez.'), action: tx('Sil') })) del.mutate(undefined)
  }
  const preview = useMemo(() => f.body.replace(/<\s*(script|iframe|object|embed|style)[^>]*>[\s\S]*?<\s*\/\s*\1\s*>/gi, '').replace(/\son\w+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)/gi, '').replace(/\{\{\s*([\w.]+)\s*\}\}/g, (_, k: string) => unknownKeys.includes(k)
    ? `<mark style="background:hsl(0 80% 55%/.2);color:inherit;border-radius:3px;padding:0 2px;text-decoration:underline wavy hsl(0 80% 50%)">{{${k}}}</mark>`
    : `<mark style="background:hsl(160 80% 40%/.18);color:inherit;border-radius:3px;padding:0 2px">${SAMPLE[k] ?? `{{${k}}}`}</mark>`), [f.body, unknownKeys])
  const groups = useMemo(() => [...new Set((list.data ?? []).map((t) => t.category))], [list.data])

  return (
    <TenantGate>
      <PageHeader title={tx('Belge şablonları')} description={tx('Çalışma belgesi, görev değişikliği, ücret yazısı… Yer tutuculu şablonlardan tek tıkla toplu belge ve PDF.')} actions={<Button onClick={() => { setSelId(null); setF({ name: '', category: 'Genel', body: '', selfService: false, requiresApproval: true }) }}><Plus className="size-4" />{' '}{tx('Yeni şablon')}</Button>} />
      <div className="grid gap-6 xl:grid-cols-[280px_1fr]">
        <Panel>
          <PanelHead title={tx('Şablonlar')} action={(list.data?.length ?? 0) < 4 && <Button size="xs" variant="outline" onClick={() => samples.mutate(undefined)}>{tx('Hazır şablonlar')}</Button>} />
          <PanelBody className="space-y-4 p-3">
            {list.isPending ? <RowsSkeleton rows={4} /> : (list.data ?? []).length === 0 ? <EmptyState icon={FileStack} title={tx('Şablon yok')} detail={tx('Hazır şablonlarla başlayın.')} /> : groups.map((g) => (
              <div key={g}>
                <p className="mb-1 px-2 text-[11px] tracking-wider text-muted-foreground uppercase">{g}</p>
                {list.data!.filter((t) => t.category === g).map((t) => (
                  <button key={t.id} onClick={() => setSelId(t.id)} className={cn('flex w-full cursor-pointer items-center gap-2 rounded-xl px-2.5 py-2 text-left text-[13px] transition', sel?.id === t.id ? 'bg-primary/10 ring-1 ring-primary/30' : 'hover:bg-accent/40')}>
                    <FileText className="size-4 text-muted-foreground" /> <span className="flex-1 truncate">{t.name}</span>
                  </button>
                ))}
              </div>
            ))}
          </PanelBody>
        </Panel>
        <div className="space-y-5">
          <Panel>
            <PanelHead title={sel ? tx('Şablonu düzenle') : tx('Yeni şablon')} action={<div className="flex gap-2">
              {sel && <Button size="sm" variant="outline" onClick={() => setBulk(sel)}><Printer className="size-4" />{' '}{tx('Toplu üret / PDF')}</Button>}
              {sel && <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={askDelete}><Trash2 className="size-4" /></Button>}
              <Button size="sm" onClick={askSave} disabled={!f.name.trim() || !f.body.trim() || save.isPending}><Save className="size-4" />{' '}{tx('Kaydet')}</Button>
            </div>} />
            <PanelBody className="space-y-4">
              <div className="grid gap-3 sm:grid-cols-[1fr_200px]"><TextField label={tx('Ad')} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /><TextField label={tx('Kategori')} value={f.category} onChange={(e) => setF({ ...f, category: e.target.value })} /></div>
              <div className="flex flex-wrap gap-5 text-[13px]">
                <label className="flex items-center gap-2"><Checkbox checked={f.selfService} onCheckedChange={(v) => setF({ ...f, selfService: v === true })} /> {tx('Çalışan kendisi talep edebilir')}</label>
                {f.selfService && <label className="flex items-center gap-2"><Checkbox checked={f.requiresApproval} onCheckedChange={(v) => setF({ ...f, requiresApproval: v === true })} /> {tx('İK onayından sonra düzenlensin')}</label>}
              </div>
              <div>
                <p className="mb-1.5 text-[12.5px] text-muted-foreground">{tx('Yer tutucu eklemek için tıklayın:')}</p>
                <div className="flex flex-wrap gap-1">{ph.data?.map((p) => <button key={p.key} onClick={() => insert(p.key)} className="cursor-pointer rounded-md border border-border bg-muted/40 px-2 py-0.5 font-mono text-[11px] hover:border-primary/50" title={p.label}>{`{{${p.key}}}`}</button>)}</div>
              </div>
              <div className="grid gap-4 lg:grid-cols-2">
                <div>
                  <p className="mb-1.5 text-[13px] font-medium">{tx('İçerik (HTML)')}</p>
                  <textarea ref={area} value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} rows={18} className="w-full rounded-xl border border-input bg-background/60 p-3 font-mono text-[12px] outline-none focus:ring-2 focus:ring-primary/30" />
                </div>
                <div>
                  <p className="mb-1.5 text-[13px] font-medium">{tx('Önizleme (örnek verilerle)')}</p>
                  <motion.div key={f.body.length > 0 ? 'p' : 'e'} initial={{ opacity: 0 }} animate={{ opacity: 1 }} className="h-[434px] overflow-y-auto rounded-xl border border-border bg-white p-6 text-[13px] leading-relaxed text-zinc-900" dangerouslySetInnerHTML={{ __html: preview || tx('<p style="color:#888">İçerik yazdıkça burada görünür.</p>') }} />
                </div>
              </div>
              {unknownKeys.length > 0 && (
                <div role="alert">
                  <Callout tone="danger">
                    {tx('Tanımsız yer tutucu: {0}. Yukarıdaki listeden geçerli bir yer tutucu seçin.', [unknownKeys.map((k) => `{{${k}}}`).join(', ')])}
                  </Callout>
                </div>
              )}
              <InfoNote>{tx('Güvenlik: kaydedilirken betik, iframe ve olay öznitelikleri (onclick vb.) sunucuda temizlenir; değerler HTML olarak kaçışlanır.')}</InfoNote>
            </PanelBody>
          </Panel>
          <DocRequestsPanel />
        </div>
      </div>
      {bulk && <BulkModal t={bulk} onClose={() => setBulk(null)} />}
    </TenantGate>
  )
}
