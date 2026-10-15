import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { Archive, BarChart3, BookOpenText, CheckCircle2, ExternalLink, FilePlus2, Plus, Search } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { complianceApi, type DocAudience, type LibraryDoc, type LibraryVersion } from '@/api/compliance'
import { formatDate, formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { AckStatsView, DepartmentChecklist, RichText, Snippet } from './shared'
import { SemanticResults } from './SemanticResults'

const audienceLabels: Record<DocAudience, string> = {
  All: tx('Tüm çalışanlar'), Managers: tx('Yöneticiler'), Hr: tx('Yalnızca İK'), Departments: tx('Seçili departmanlar'),
}

/** Yeni sürümde önceki sürümün metni ve dosya bağlantısı hazır gelir; yalnızca değişen kısım düzenlenir. */
function DocEditor({ docId, current, onClose }: { docId?: string; current?: LibraryVersion | null; onClose: () => void }) {
  const meta = useQuery({ queryKey: ['library', 'meta'], queryFn: ({ signal }) => complianceApi.libraryMeta(signal) })
  const [f, setF] = useState({ title: '', category: 'Policy', audience: 'All' as DocAudience, departmentIds: [] as string[], requiresAck: false, body: current?.body ?? '', externalUrl: current?.externalUrl ?? '', storageKey: current?.storageKey ?? '', changeNote: '' })
  const isNew = !docId
  const save = useAction(
    () => isNew
      ? complianceApi.createDoc({ ...f, externalUrl: f.externalUrl || null, storageKey: f.storageKey || null, changeNote: f.changeNote || null })
      : complianceApi.newVersion(docId!, { title: f.title || undefined, body: f.body, externalUrl: f.externalUrl || null, storageKey: f.storageKey || null, changeNote: f.changeNote || null }),
    { success: (r) => tx('Sürüm {0} yayımlandı', [r.version]), invalidate: [['library']], onDone: onClose },
  )
  return (
    <Modal open size="xl" onClose={onClose} title={isNew ? tx('Yeni belge') : tx('Yeni sürüm yayımla')}
      note={isNew ? undefined : tx('Eski sürümler saklanır. Onay gerektiren belgede herkesin yeni sürümü yeniden onaylaması gerekir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || (isNew && f.title.trim().length < 3) || (!f.body.trim() && !f.externalUrl.trim() && !f.storageKey.trim())}>{tx('Yayımla')}</Button></>}>
      <div className="space-y-3">
        <TextField label={isNew ? tx('Başlık') : tx('Başlık (boşsa aynı kalır)')} value={f.title} maxLength={200} onChange={(e) => setF({ ...f, title: e.target.value })} />
        {isNew && (
          <div className="grid gap-3 md:grid-cols-2">
            <SelectField label={tx('Kategori')} value={f.category} onChange={(v) => setF({ ...f, category: v })} options={meta.data?.categories ?? []} />
            <SelectField label={tx('Kimler görebilir')} value={f.audience} onChange={(v) => setF({ ...f, audience: v as DocAudience })}
              options={(Object.keys(audienceLabels) as DocAudience[]).map((k) => ({ value: k, label: audienceLabels[k] }))} />
          </div>
        )}
        {isNew && f.audience === 'Departments' && <DepartmentChecklist value={f.departmentIds} onChange={(ids) => setF({ ...f, departmentIds: ids })} />}
        <TextAreaField label={tx('Belge metni (aranabilir)')} rows={10} value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} hint={tx('Tam metin aramada bu metin kullanılır. Dosya varsa bağlantısını aşağıya ekleyin.')} />
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label={tx('Dosya bağlantısı (isteğe bağlı)')} placeholder="https://" value={f.externalUrl} onChange={(e) => setF({ ...f, externalUrl: e.target.value })} />
          <TextField label={tx('Depolama anahtarı (isteğe bağlı)')} value={f.storageKey} onChange={(e) => setF({ ...f, storageKey: e.target.value })} />
        </div>
        <TextField label={tx('Değişiklik notu')} value={f.changeNote} onChange={(e) => setF({ ...f, changeNote: e.target.value })} />
        {isNew && (
          <label className="flex items-center gap-2 text-[13px]">
            <Checkbox checked={f.requiresAck} onCheckedChange={(v) => setF({ ...f, requiresAck: v === true })} />
            {tx('Politika kabulü gereksin ("Okudum, kabul ediyorum")')}
          </label>
        )}
      </div>
    </Modal>
  )
}

function DocViewer({ id, hr, onClose }: { id: string; hr: boolean; onClose: () => void }) {
  const q = useQuery({ queryKey: ['library', 'doc', id], queryFn: ({ signal }) => complianceApi.libraryDoc(id, signal) })
  const stats = useQuery({ queryKey: ['library', 'stats', id], queryFn: ({ signal }) => complianceApi.docStats(id, signal), enabled: hr })
  const [editing, setEditing] = useState(false)
  const ack = useAction((v: number) => complianceApi.ackDoc(id, v), { success: tx('Kabulünüz kaydedildi'), invalidate: [['library']] })
  const archive = useAction((archived: boolean) => complianceApi.updateDocMeta(id, { archived }), { success: tx('Kaydedildi'), invalidate: [['library']] })
  const d = q.data
  return (
    <Modal open size="xl" onClose={onClose} title={d?.title ?? tx('Belge')}
      note={d ? `${d.categoryLabel} · ${tx('Sürüm {0}', [d.version])}${d.current ? ` · ${formatDate(d.current.publishedAt)}` : ''}` : undefined}
      footer={d && (
        <>
          {hr && <Button variant="outline" onClick={() => archive.mutate(!d.archived)}><Archive className="size-4" />{' '}{d.archived ? tx('Arşivden çıkar') : tx('Arşivle')}</Button>}
          {hr && <Button variant="outline" onClick={() => setEditing(true)}><FilePlus2 className="size-4" />{' '}{tx('Yeni sürüm')}</Button>}
          {d.requiresAck && (d.needsAck
            ? <Button onClick={() => ack.mutate(d.version)} disabled={ack.isPending}>{tx('Okudum, kabul ediyorum')}</Button>
            : <StatusBadge tone="success"><CheckCircle2 className="size-3.5" />{' '}{tx('Sürüm {0} kabul edildi', [d.acknowledgedVersion ?? d.version])}</StatusBadge>)}
        </>
      )}>
      {q.isPending || !d ? <RowsSkeleton /> : (
        <div className="space-y-4">
          {d.requiresAck && d.needsAck && d.acknowledgedVersion && (
            <InfoNote>{tx('Daha önce sürüm {0}’i kabul ettiniz; belge güncellendiği için yeniden onayınız gerekiyor.', [d.acknowledgedVersion])}</InfoNote>
          )}
          {d.current?.externalUrl && (
            <a href={d.current.externalUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1.5 text-[13px] text-primary underline-offset-4 hover:underline">
              <ExternalLink className="size-4" />{tx('Dosyayı aç')}
            </a>
          )}
          {d.current?.body ? <RichText text={d.current.body} /> : <p className="text-[13px] text-muted-foreground">{tx('Bu sürümün metni yok; dosya bağlantısını kullanın.')}</p>}
          <div>
            <p className="mb-1 text-[12.5px] font-medium">{tx('Sürüm geçmişi')}</p>
            <ul className="space-y-0.5 text-[12.5px] text-muted-foreground">
              {d.history.map((h) => <li key={h.versionNo}>{tx('Sürüm {0}', [h.versionNo])} · {formatDateTime(h.publishedAt)} · {h.publishedBy}{h.changeNote ? ` — ${h.changeNote}` : ''}</li>)}
            </ul>
          </div>
          {hr && stats.data && (
            <div className="rounded-xl border border-border p-3">
              <p className="mb-2 flex items-center gap-1.5 text-[12.5px] font-medium"><BarChart3 className="size-4" />{tx('Sürüm {0} kabul durumu', [stats.data.version])}</p>
              <AckStatsView stats={stats.data.stats} listMissing={d.requiresAck} />
            </div>
          )}
        </div>
      )}
      {editing && <DocEditor docId={id} current={d?.current} onClose={() => setEditing(false)} />}
    </Modal>
  )
}

/** /panel/belgeler-kutuphanesi — G19 sürümlü doküman kütüphanesi ve tam metin arama. */
export function LibraryPage() {
  const { roles } = useAuth()
  const hr = isHr(roles)
  const [category, setCategory] = useState('')
  // ⌘K paletinden "?ara=...&anlamsal=1" ile gelinebilir.
  const [params] = useSearchParams()
  const [term, setTerm] = useState(() => params.get('ara') ?? '')
  const [debounced, setDebounced] = useState(() => (params.get('ara') ?? '').trim())
  const [semantic, setSemantic] = useState(() => params.get('anlamsal') === '1')
  const [open, setOpen] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  useEffect(() => { const t = setTimeout(() => setDebounced(term.trim()), 300); return () => clearTimeout(t) }, [term])
  const meta = useQuery({ queryKey: ['library', 'meta'], queryFn: ({ signal }) => complianceApi.libraryMeta(signal) })
  const list = useQuery({ queryKey: ['library', 'list', category], queryFn: ({ signal }) => complianceApi.library(category || undefined, signal) })
  const search = useQuery({ queryKey: ['library', 'search', debounced], queryFn: ({ signal }) => complianceApi.librarySearch(debounced, signal), enabled: debounced.length >= 2 && !semantic })
  const pending = (list.data ?? []).filter((d) => d.needsAck)
  return (
    <>
      <PageHeader title={tx('Doküman kütüphanesi')} description={tx('Politikalar, el kitapları ve prosedürler — sürümlü, aranabilir.')}
        actions={hr ? <Button onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Yeni belge')}</Button> : undefined} />
      <div className="space-y-5">
        {pending.length > 0 && <InfoNote>{tx('{0} belge okuyup kabul etmenizi bekliyor: {1}', [pending.length, pending.map((d) => d.title).join(', ')])}</InfoNote>}
        <Panel>
          <PanelBody className="flex flex-wrap items-end gap-3">
            <div className="relative min-w-60 flex-1">
              <Search className="pointer-events-none absolute top-[34px] left-2.5 size-4 text-muted-foreground" />
              <TextField label={tx('Tam metin ara')} className="pl-8" placeholder={tx('ör. fazla mesai -gece')} value={term} onChange={(e) => setTerm(e.target.value)}
                hint={tx('Yalnızca görme yetkiniz olan belgelerde arar. "tırnak" tam ifade, -kelime hariç tutar.')} />
            </div>
            <label className="flex items-center gap-2 pb-2 text-[13px]" title={tx('Kelimesi kelimesine değil, anlamca yakın sonuçlar (bilgi bankası ve duyurular dahil)')}>
              <Checkbox checked={semantic} onCheckedChange={(v) => setSemantic(v === true)} />{' '}{tx('Anlamsal arama')}
            </label>
            <div className="w-52"><SelectField label={tx('Kategori')} value={category || 'all'} onChange={(v) => setCategory(v === 'all' ? '' : v)} options={[{ value: 'all', label: tx('Tümü') }, ...(meta.data?.categories ?? [])]} /></div>
          </PanelBody>
        </Panel>
        {debounced.length >= 2 && semantic ? (
          <SemanticResults query={debounced} onOpenDoc={setOpen} />
        ) : debounced.length >= 2 ? (
          <Panel>
            <PanelHead title={tx('Arama sonuçları')} note={search.data ? tx('{0} belge', [search.data.length]) : undefined} />
            <PanelBody className="p-0">
              {search.isPending ? <div className="p-5"><RowsSkeleton rows={3} /></div> : !search.data?.length ? <EmptyState icon={Search} title={tx('Sonuç yok')} /> : (
                <ul className="divide-y divide-border">
                  {search.data.map((h) => (
                    <li key={h.id}>
                      <button type="button" className="w-full px-5 py-3 text-left text-[13px] hover:bg-muted/40" onClick={() => setOpen(h.id)}>
                        <span className="font-medium">{h.title}</span> <span className="text-[12px] text-muted-foreground">· {h.categoryLabel} · {tx('Sürüm {0}', [h.version])}</span>
                        <span className="mt-1 block text-[12.5px] text-muted-foreground"><Snippet text={h.snippet} /></span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </PanelBody>
          </Panel>
        ) : (
          <Panel>
            <PanelBody className="p-0">
              {list.isPending ? <div className="p-5"><RowsSkeleton /></div> : !list.data?.length ? (
                <EmptyState icon={BookOpenText} title={tx('Belge yok')} detail={hr ? tx('İlk politikayı ekleyin; çalışanlar burada okuyup kabul eder.') : tx('Size açık bir belge bulunmuyor.')} />
              ) : (
                <ul className="divide-y divide-border">
                  {list.data.map((d: LibraryDoc) => (
                    <li key={d.id}>
                      <button type="button" className="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left text-[13px] hover:bg-muted/40" onClick={() => setOpen(d.id)}>
                        <span className="min-w-0 flex-1">
                          <span className="font-medium">{d.title}</span>
                          <span className="block text-[12px] text-muted-foreground">{d.categoryLabel} · {tx('Sürüm {0}', [d.version])} · {formatDate(d.publishedAt)}{hr ? ` · ${audienceLabels[d.audience]}` : ''}</span>
                        </span>
                        {d.archived && <StatusBadge>{tx('Arşiv')}</StatusBadge>}
                        {d.requiresAck && (d.needsAck ? <StatusBadge tone="warning">{tx('Onayınız bekleniyor')}</StatusBadge> : <StatusBadge tone="success">{tx('Kabul edildi')}</StatusBadge>)}
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </PanelBody>
          </Panel>
        )}
      </div>
      {open && <DocViewer id={open} hr={hr} onClose={() => setOpen(null)} />}
      {creating && <DocEditor onClose={() => setCreating(false)} />}
    </>
  )
}
