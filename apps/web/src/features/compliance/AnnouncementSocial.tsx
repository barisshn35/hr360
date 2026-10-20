import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { MessageCircle, Send, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useConfirm } from '@/components/ui/confirm'
import { PiiHint } from '@/components/PiiHint'
import { useAction } from '@/features/shared/kit'
import { complianceApi, type Announcement, type ReactionKind } from '@/api/compliance'
import { formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const REACTIONS: { kind: ReactionKind; emoji: string; label: () => string }[] = [
  { kind: 'like', emoji: '👍', label: () => tx('Beğen') },
  { kind: 'love', emoji: '❤️', label: () => tx('Sevdim') },
  { kind: 'celebrate', emoji: '🎉', label: () => tx('Kutla') },
  { kind: 'clap', emoji: '👏', label: () => tx('Alkış') },
  { kind: 'insightful', emoji: '💡', label: () => tx('Faydalı') },
  { kind: 'funny', emoji: '😄', label: () => tx('Eğlenceli') },
]

function Comments({ a }: { a: Announcement }) {
  const key = ['announcements', 'comments', a.id]
  const q = useQuery({ queryKey: key, queryFn: ({ signal }) => complianceApi.announcementComments(a.id, signal) })
  const [text, setText] = useState('')
  const confirm = useConfirm()
  const add = useAction(() => complianceApi.addAnnouncementComment(a.id, text.trim()), { invalidate: [key, ['announcements', 'mine']], onDone: () => setText('') })
  const del = useAction((id: string) => complianceApi.deleteAnnouncementComment(a.id, id), { success: tx('Yorum silindi'), invalidate: [key, ['announcements', 'mine']] })
  return (
    <div className="mt-3 space-y-3 border-t border-border pt-3">
      {(q.data ?? []).map((c) => (
        <div key={c.id} className="group flex items-start gap-2.5">
          {c.authorEmployeeId ? <EmployeeAvatar id={c.authorEmployeeId} name={c.authorName} size={28} /> : <span className="size-7 shrink-0 rounded-full bg-muted" />}
          <div className="min-w-0 flex-1 rounded-xl bg-muted/50 px-3 py-2">
            <p className="text-[12px]"><span className="font-semibold">{c.authorName}</span> <span className="text-muted-foreground">· {formatRelativeToNow(c.createdAt)}</span></p>
            <p className="text-[13px] whitespace-pre-line">{c.body}</p>
          </div>
          {c.canDelete && (
            <button type="button" aria-label={tx('Yorumu sil')} className="cursor-pointer rounded-lg p-1.5 text-muted-foreground opacity-60 hover:text-destructive group-hover:opacity-100"
              onClick={async () => { if (await confirm({ title: tx('Yorum silinsin mi?'), note: c.body.slice(0, 120), action: tx('Sil') })) del.mutate(c.id) }}>
              <Trash2 className="size-3.5" />
            </button>
          )}
        </div>
      ))}
      {a.allowComments ? (
        <div>
          <div className="flex items-end gap-2">
            <textarea value={text} onChange={(e) => setText(e.target.value)} maxLength={1000} rows={1} placeholder={tx('Yorum yazın…')} aria-label={tx('Yorum yazın…')}
              onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey && text.trim()) { e.preventDefault(); add.mutate(undefined) } }}
              className="min-h-9 flex-1 resize-none rounded-xl border border-border bg-transparent px-3 py-2 text-[13px] outline-none focus:border-primary" />
            <Button size="sm" disabled={!text.trim() || add.isPending} onClick={() => add.mutate(undefined)} aria-label={tx('Gönder')}><Send className="size-4" /></Button>
          </div>
          <PiiHint text={text} />
        </div>
      ) : <p className="text-[12px] text-muted-foreground">{tx('Bu duyuruda yorumlar kapalı.')}</p>}
    </div>
  )
}

/** Duyuru altındaki tepki ve yorum şeridi (yenilik haritası #31). */
export function AnnouncementSocial({ a }: { a: Announcement }) {
  const [open, setOpen] = useState(false)
  const [picker, setPicker] = useState(false)
  const react = useAction((k: ReactionKind) => complianceApi.reactAnnouncement(a.id, k), { invalidate: [['announcements', 'mine']], onDone: () => setPicker(false) })
  const counts = REACTIONS.filter((r) => (a.reactions[r.kind] ?? 0) > 0)
  const mine = REACTIONS.find((r) => r.kind === a.myReaction)
  return (
    <div className="mt-4">
      <div className="flex flex-wrap items-center gap-1.5">
        {counts.map((r) => (
          <button key={r.kind} type="button" onClick={() => react.mutate(r.kind)} aria-pressed={a.myReaction === r.kind} title={r.label()}
            className={cn('flex cursor-pointer items-center gap-1 rounded-full border px-2 py-0.5 text-[12.5px] tabular-nums transition', a.myReaction === r.kind ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:text-foreground')}>
            <span>{r.emoji}</span>{a.reactions[r.kind]}
          </button>
        ))}
        <div className="relative">
          <Button size="sm" variant="ghost" onClick={() => setPicker((v) => !v)} aria-expanded={picker}>
            {mine ? <span>{mine.emoji}</span> : <span className="opacity-70">🙂</span>}{mine ? mine.label() : tx('Tepki ver')}
          </Button>
          <AnimatePresence>
            {picker && (
              <motion.div initial={{ opacity: 0, y: 6, scale: 0.95 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, y: 6, scale: 0.95 }}
                className="surface absolute bottom-full left-0 z-20 mb-1 flex gap-0.5 rounded-full border border-border p-1 shadow-lg">
                {REACTIONS.map((r) => (
                  <motion.button key={r.kind} type="button" whileHover={{ scale: 1.25, y: -2 }} onClick={() => react.mutate(r.kind)} aria-label={r.label()} title={r.label()}
                    className={cn('grid size-8 cursor-pointer place-items-center rounded-full text-lg', a.myReaction === r.kind && 'bg-primary/15')}>{r.emoji}</motion.button>
                ))}
              </motion.div>
            )}
          </AnimatePresence>
        </div>
        <Button size="sm" variant="ghost" onClick={() => setOpen((v) => !v)} aria-expanded={open} className="ml-auto">
          <MessageCircle className="size-4" />{a.commentCount ? tx('{0} yorum', [a.commentCount]) : a.allowComments ? tx('Yorum yap') : tx('Yorumlar')}
        </Button>
      </div>
      {open && <Comments a={a} />}
    </div>
  )
}
