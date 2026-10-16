import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { ChevronUp, EyeOff, Lightbulb, MessageCircle, Plus, Send, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useAction } from '@/features/shared/kit'
import { useAuth } from '@/auth/useAuth'
import { ideasApi, ideaStatusLabels, type Idea, type IdeaStatus } from '@/api/ideas'
import { formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const KEY = ['ideas'] as const
/** Kategori adları sunucudan Türkçe gelir; arayüz dilinde gösterim. */
const categoryLabel = (c: string) => ({
  'İş süreçleri': tx('İş süreçleri'), 'Çalışma ortamı': tx('Çalışma ortamı'), Müşteri: tx('Müşteri'), Teknoloji: tx('Teknoloji'),
  Sosyal: tx('Sosyal'), Tasarruf: tx('Tasarruf'), Diğer: tx('Diğer'),
} as Record<string, string>)[c] ?? c
const TONE: Record<IdeaStatus, string> = {
  New: 'bg-muted text-muted-foreground', UnderReview: 'bg-sky-500/15 text-sky-600 dark:text-sky-300',
  Planned: 'bg-violet-500/15 text-violet-600 dark:text-violet-300', Done: 'bg-primary/15 text-primary',
  Declined: 'bg-rose-500/10 text-rose-600 dark:text-rose-300',
}

function NewIdeaModal({ open, onClose, categories }: { open: boolean; onClose: () => void; categories: string[] }) {
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [category, setCategory] = useState('Diğer')
  const [anonymous, setAnonymous] = useState(false)
  const create = useAction(() => ideasApi.create({ title: title.trim(), body: body.trim(), category, anonymous }), {
    success: tx('Fikriniz paylaşıldı.'), invalidate: [KEY], onDone: () => { setTitle(''); setBody(''); setAnonymous(false); onClose() },
  })
  const ok = title.trim().length >= 5 && body.trim().length >= 10
  return (
    <Modal open={open} onClose={onClose} title={tx('Fikir paylaş')} note={tx('İyi bir fikir sorunu ve önerinizi kısaca anlatır. Herkes destekleyip yorum yapabilir; İK yanıtlar.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ok || create.isPending} onClick={() => create.mutate(undefined)}><Send className="size-4" />{tx('Paylaş')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Başlık')} value={title} maxLength={120} onChange={(e) => setTitle(e.target.value)} placeholder={tx('ör. Toplantısız cuma öğleden sonraları')} />
        <TextAreaField label={tx('Açıklama')} value={body} maxLength={2000} rows={5} onChange={(e) => setBody(e.target.value)} hint={tx('Sorun ne, öneriniz ne, kime faydası olur?')} />
        <SelectField label={tx('Kategori')} value={category} onChange={setCategory} options={categories.map((c) => ({ value: c, label: categoryLabel(c) }))} />
        <label className="flex cursor-pointer items-start gap-2 text-[13px]">
          <Checkbox checked={anonymous} onCheckedChange={(v) => setAnonymous(v === true)} className="mt-0.5" />
          <span>{tx('Adım görünmesin')}<span className="block text-[12px] text-muted-foreground">{tx('Adınız İK dahil kimseye gösterilmez ve kaydedilmez. Bu durumda yorum ve durum bildirimleri size gelmez.')}</span></span>
        </label>
      </div>
    </Modal>
  )
}

function IdeaDetail({ id, onClose }: { id: string; onClose: () => void }) {
  const { roles } = useAuth()
  const isHr = roles.some((r) => r === 'hr-admin' || r === 'tenant-admin')
  const confirm = useConfirm()
  const q = useQuery({ queryKey: [...KEY, id], queryFn: ({ signal }) => ideasApi.get(id, signal) })
  const [text, setText] = useState('')
  const [status, setStatus] = useState<IdeaStatus | ''>('')
  const [response, setResponse] = useState<string | null>(null)
  const comment = useAction(() => ideasApi.comment(id, text.trim()), { invalidate: [KEY], onDone: () => setText('') })
  const delComment = useAction((cid: string) => ideasApi.deleteComment(cid), { invalidate: [KEY] })
  const save = useAction(() => ideasApi.setStatus(id, (status || q.data!.idea.status) as IdeaStatus, (response ?? q.data!.idea.response ?? '').trim() || null), { success: tx('Durum güncellendi.'), invalidate: [KEY] })
  const remove = useAction(() => ideasApi.remove(id), { success: tx('Fikir kaldırıldı.'), invalidate: [KEY], onDone: onClose })
  const d = q.data
  const labels = ideaStatusLabels()
  return (
    <Modal open onClose={onClose} size="lg" title={d?.idea.title ?? tx('Fikir')}
      note={d ? `${d.idea.anonymous ? tx('Anonim') : d.idea.authorName ?? ''} · ${categoryLabel(d.idea.category)} · ${formatRelativeToNow(d.idea.createdAt)}` : undefined}>
      {q.isPending || !d ? <RowsSkeleton rows={3} /> : (
        <div className="space-y-5">
          <p className="whitespace-pre-wrap text-[14px] leading-relaxed">{d.idea.body}</p>
          {d.idea.response && (
            <div className="rounded-xl border border-border bg-muted/40 p-3 text-[13px]">
              <p className="mb-1 flex items-center gap-2 text-[12px] text-muted-foreground"><span className={cn('rounded-full px-2 py-0.5', TONE[d.idea.status])}>{labels[d.idea.status]}</span>{d.idea.respondedByName}</p>
              {d.idea.response}
            </div>
          )}
          {isHr && (
            <div className="space-y-2 rounded-xl border border-dashed border-border p-3">
              <p className="text-[12.5px] font-medium">{tx('İK yanıtı')}</p>
              <div className="flex flex-wrap gap-1.5">
                {(Object.keys(labels) as IdeaStatus[]).map((s) => (
                  <button key={s} type="button" onClick={() => setStatus(s)} aria-pressed={(status || d.idea.status) === s}
                    className={cn('cursor-pointer rounded-full border px-2.5 py-1 text-[12px]', (status || d.idea.status) === s ? 'border-primary bg-primary/10 text-primary' : 'border-border')}>{labels[s]}</button>
                ))}
              </div>
              <TextAreaField label={tx('Yanıt (fikir sahibine ve herkese görünür)')} rows={2} maxLength={2000} value={response ?? d.idea.response ?? ''} onChange={(e) => setResponse(e.target.value)} />
              <div className="flex justify-between gap-2">
                <Button size="sm" variant="ghost" onClick={async () => { if (await confirm({ title: tx('Fikir kaldırılsın mı?'), action: tx('Kaldır') })) remove.mutate(undefined) }}><Trash2 className="size-4" />{tx('Kaldır')}</Button>
                <Button size="sm" disabled={save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
              </div>
            </div>
          )}
          <div className="space-y-3">
            <p className="flex items-center gap-1.5 text-[12.5px] font-medium text-muted-foreground"><MessageCircle className="size-4" />{tx('Yorumlar ({0})', [d.comments.length])}</p>
            {d.comments.map((c) => (
              <div key={c.id} className="flex gap-2.5">
                <EmployeeAvatar id={c.authorEmployeeId} name={c.authorName} size={28} />
                <div className="min-w-0 flex-1 rounded-xl bg-muted/40 px-3 py-2 text-[13px]">
                  <p className="flex items-center justify-between gap-2 text-[12px] text-muted-foreground"><span className="font-medium text-foreground">{c.authorName}</span>{formatRelativeToNow(c.createdAt)}</p>
                  <p className="whitespace-pre-wrap">{c.body}</p>
                </div>
                {(c.mine || isHr) && <button type="button" aria-label={tx('Yorumu sil')} className="cursor-pointer self-start p-1 text-muted-foreground hover:text-destructive" onClick={() => delComment.mutate(c.id)}><Trash2 className="size-3.5" /></button>}
              </div>
            ))}
            <TextAreaField label={tx('Yorum yaz')} rows={2} maxLength={1000} value={text} onChange={(e) => setText(e.target.value)} />
            <div className="flex justify-end"><Button size="sm" disabled={!text.trim() || comment.isPending} onClick={() => comment.mutate(undefined)}>{tx('Gönder')}</Button></div>
          </div>
        </div>
      )}
    </Modal>
  )
}

function IdeaCard({ idea, onOpen }: { idea: Idea; onOpen: () => void }) {
  const vote = useAction(() => ideasApi.vote(idea.id), { invalidate: [KEY] })
  return (
    <motion.article layout initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }}
      className="flex gap-4 rounded-2xl border border-border bg-card p-4 transition-colors hover:border-primary/30">
      <button type="button" onClick={() => vote.mutate(undefined)} disabled={vote.isPending} aria-pressed={idea.votedByMe}
        aria-label={idea.votedByMe ? tx('Desteği geri al') : tx('Destekle')}
        className={cn('flex h-14 w-12 shrink-0 cursor-pointer flex-col items-center justify-center rounded-xl border text-[13px] font-semibold tabular-nums transition-colors',
          idea.votedByMe ? 'border-primary bg-primary text-primary-foreground' : 'border-border hover:border-primary/50 hover:text-primary')}>
        <ChevronUp className="size-4" />{idea.votes}
      </button>
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 cursor-pointer text-left">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="text-[14.5px] font-semibold">{idea.title}</h3>
          <span className={cn('rounded-full px-2 py-0.5 text-[11px]', TONE[idea.status])}>{ideaStatusLabels()[idea.status]}</span>
        </div>
        <p className="mt-1 line-clamp-2 text-[13px] text-muted-foreground">{idea.body}</p>
        <p className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
          <span>{categoryLabel(idea.category)}</span>
          <span className="flex items-center gap-1">{idea.anonymous ? <><EyeOff className="size-3.5" />{tx('Anonim')}</> : idea.authorName}{idea.mine && ` · ${tx('sizin')}`}</span>
          <span>{formatRelativeToNow(idea.createdAt)}</span>
          <span className="flex items-center gap-1"><MessageCircle className="size-3.5" />{idea.commentCount}</span>
        </p>
      </button>
    </motion.article>
  )
}

/** Fikir kutusu (yenilik haritası #24). */
export function IdeasPage() {
  const [status, setStatus] = useState('')
  const [sort, setSort] = useState<'top' | 'new'>('top')
  const [open, setOpen] = useState(false)
  const [detail, setDetail] = useState<string | null>(null)
  const meta = useQuery({ queryKey: [...KEY, 'meta'], queryFn: ({ signal }) => ideasApi.meta(signal), staleTime: Infinity })
  const q = useQuery({ queryKey: [...KEY, status, sort], queryFn: ({ signal }) => ideasApi.list({ status: status || undefined, sort }, signal) })
  const labels = ideaStatusLabels()
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Fikir kutusu')} eyebrow={[tx('Topluluk')]} description={tx('İşi kolaylaştıracak bir fikriniz mi var? Paylaşın; en çok desteklenenler İK gündemine taşınır.')}
        actions={<Button onClick={() => setOpen(true)}><Plus className="size-4" />{tx('Fikir paylaş')}</Button>} />
      <div className="flex flex-wrap items-center gap-2">
        {['', ...(meta.data?.statuses ?? [])].map((s) => (
          <button key={s || 'all'} type="button" onClick={() => setStatus(s)} aria-pressed={status === s}
            className={cn('cursor-pointer rounded-full border px-3 py-1 text-[12.5px]', status === s ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:text-foreground')}>
            {s ? labels[s as IdeaStatus] : tx('Tümü')}
          </button>
        ))}
        <div className="ml-auto inline-flex rounded-lg border border-border p-0.5 text-[12.5px]">
          {([['top', tx('En çok desteklenen')], ['new', tx('En yeni')]] as const).map(([k, l]) => (
            <button key={k} type="button" onClick={() => setSort(k)} aria-pressed={sort === k}
              className={cn('cursor-pointer rounded-md px-2.5 py-1', sort === k ? 'bg-primary text-primary-foreground' : 'text-muted-foreground')}>{l}</button>
          ))}
        </div>
      </div>
      {q.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : !q.data.length ? (
        <EmptyState icon={Lightbulb} title={status ? tx('Bu durumda fikir yok') : tx('İlk fikri siz paylaşın')} detail={tx('Küçük bir iyileştirme de olur: süreç, ortam, müşteri, teknoloji.')}
          action={<Button onClick={() => setOpen(true)}>{tx('Fikir paylaş')}</Button>} />
      ) : (
        <div className="grid gap-3">{q.data.map((i) => <IdeaCard key={i.id} idea={i} onOpen={() => setDetail(i.id)} />)}</div>
      )}
      <NewIdeaModal open={open} onClose={() => setOpen(false)} categories={meta.data?.categories ?? ['Diğer']} />
      {detail && <IdeaDetail id={detail} onClose={() => setDetail(null)} />}
    </div>
  )
}
