import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { Link } from 'react-router-dom'
import { Award, Coins, Gift, Heart, PartyPopper, Send, Trash2, Trophy } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { TextAreaField } from '@/components/ui/Field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { engagementApi, type Kudos } from '@/api/engagement'
import { useMyEmployeeId } from '@/api/queries'
import { recognitionApi } from '@/api/recognition'
import { formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Initials, PersonSelect, TenantGate, useAction } from '@/features/shared/kit'
import { CelebrationList } from './CelebrationsPage'
import { tx } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'
import { useConfirm } from '@/components/ui/Confirm'

const BADGE_STYLE: Record<string, { emoji: string; ring: string }> = {
  teamwork: { emoji: '🤝', ring: 'from-sky-500/30 to-sky-500/0' },
  customer: { emoji: '🌟', ring: 'from-amber-400/30 to-amber-400/0' },
  innovation: { emoji: '💡', ring: 'from-yellow-300/30 to-yellow-300/0' },
  mentor: { emoji: '🧭', ring: 'from-violet-500/30 to-violet-500/0' },
  'extra-mile': { emoji: '🚀', ring: 'from-rose-500/30 to-rose-500/0' },
  thanks: { emoji: '🙏', ring: 'from-emerald-500/30 to-emerald-500/0' },
}

function SendKudosModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const badges = useQuery({ queryKey: ['kudos', 'badges'], queryFn: ({ signal }) => engagementApi.kudosBadges(signal), staleTime: Infinity })
  const { employeeId: me } = useMyEmployeeId()
  const [to, setTo] = useState('')
  const [badge, setBadge] = useState('thanks')
  const [message, setMessage] = useState('')
  const [points, setPoints] = useState(0)
  const wallet = useQuery({ queryKey: ['recognition', 'me'], queryFn: ({ signal }) => recognitionApi.me(signal), enabled: open, retry: false })
  const pts = wallet.data?.enabled ? wallet.data : null
  const choices = pts ? [0, 5, 10, 25, 50, 100].filter((n) => n <= Math.min(pts.maxPerKudos, pts.remaining)) : []
  const send = useAction(() => engagementApi.sendKudos({ toEmployeeId: to, badge, message: message.trim(), points }), {
    success: tx('Takdiriniz gönderildi 🎉'),
    invalidate: [['kudos'], ['recognition']],
    onDone: () => {
      onClose()
      setTo('')
      setMessage('')
      setPoints(0)
    },
  })
  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Takdir gönder')}
      note={tx('Takdirler herkese açık duvarda görünür; alıcıya bildirim gider.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
          <Button disabled={!to || message.trim().length < 3 || send.isPending} onClick={() => send.mutate(undefined)}>
            <Send className="size-4" />{' '}{tx('Gönder')}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <PersonSelect id="kudos-to" label={tx('Kime?')} value={to} onChange={setTo} exclude={me ? [me] : []} />
        <div>
          <p className="mb-2 text-[13px] font-medium">{tx('Rozet')}</p>
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
            {(badges.data ?? []).map((b) => (
              <button
                key={b.id}
                type="button"
                onClick={() => setBadge(b.id)}
                className={cn(
                  'flex cursor-pointer items-center gap-2 rounded-xl border px-3 py-2.5 text-left text-[13px] transition',
                  badge === b.id ? 'border-primary bg-primary/10 shadow-[0_0_0_3px_hsl(var(--primary)/0.15)]' : 'border-border hover:border-primary/40',
                )}
              >
                <span className="text-lg">{BADGE_STYLE[b.id]?.emoji ?? '⭐'}</span>
                {b.label}
              </button>
            ))}
          </div>
        </div>
        {pts && (
          <div>
            <p className="mb-2 flex items-center justify-between text-[13px] font-medium">
              <span>{tx('Puan ekle (isteğe bağlı)')}</span>
              <span className="text-[12px] font-normal text-muted-foreground">{tx('Bu ay kalan: {0}', [pts.remaining])}</span>
            </p>
            <div className="flex flex-wrap gap-2">
              {choices.map((n) => (
                <button key={n} type="button" onClick={() => setPoints(n)} aria-pressed={points === n}
                  className={cn('cursor-pointer rounded-full border px-3 py-1 text-[12.5px] tabular-nums', points === n ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:text-foreground')}>
                  {n === 0 ? tx('Puansız') : `+${n}`}
                </button>
              ))}
            </div>
          </div>
        )}
        <TextAreaField id="kudos-msg" label={tx('Mesajınız')} rows={3} maxLength={500} value={message} onChange={(e) => setMessage(e.target.value)} hint={`${message.length}/500`} />
        <PiiHint text={message} />
      </div>
    </Modal>
  )
}

function KudosCard({ k, index }: { k: Kudos; index: number }) {
  const like = useAction(() => engagementApi.likeKudos(k.id), { invalidate: [['kudos', 'wall']] })
  const del = useAction(() => engagementApi.deleteKudos(k.id), { success: tx('Silindi'), invalidate: [['kudos']] })
  const confirm = useConfirm()
  const askDelete = async () => {
    if (await confirm({ title: tx('Takdir silinsin mi?'), note: tx('{0} için gönderdiğiniz takdir duvardan kaldırılır.', [k.toName]), action: tx('Sil') })) del.mutate(undefined)
  }
  const style = BADGE_STYLE[k.badge] ?? BADGE_STYLE.thanks
  return (
    <motion.article
      layout
      initial={{ opacity: 0, y: 20, scale: 0.97 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      exit={{ opacity: 0, scale: 0.95 }}
      transition={{ delay: Math.min(index * 0.04, 0.4), type: 'spring', stiffness: 260, damping: 24 }}
      className="surface group relative mb-4 break-inside-avoid overflow-hidden rounded-2xl border border-border p-4"
    >
      <div aria-hidden="true" className={cn('pointer-events-none absolute -top-10 -right-10 size-32 rounded-full blur-2xl', `bg-gradient-to-br ${style.ring}`)} />
      <div className="relative flex items-center gap-2.5">
        <Initials name={k.toName} size={38} />
        <div className="min-w-0 flex-1">
          <p className="truncate text-[14px] font-semibold">{k.toName}</p>
          <p className="truncate text-[12px] text-muted-foreground">
            {k.fromName} · {formatRelativeToNow(k.createdAt)}
          </p>
        </div>
        <motion.span whileHover={{ rotate: [0, -12, 12, 0], scale: 1.15 }} className="text-2xl" title={k.badgeLabel}>
          {style.emoji}
        </motion.span>
      </div>
      <p className="relative mt-3 text-[13.5px] leading-relaxed">{k.message}</p>
      <div className="relative mt-3 flex items-center justify-between">
        <span className="flex items-center gap-1.5">
          <span className="rounded-full bg-muted px-2.5 py-0.5 text-[11.5px] text-muted-foreground">{k.badgeLabel}</span>
          {k.points > 0 && <span className="flex items-center gap-1 rounded-full bg-amber-400/15 px-2 py-0.5 text-[11.5px] font-medium text-amber-600 tabular-nums dark:text-amber-300"><Coins className="size-3" />+{k.points}</span>}
        </span>
        <div className="flex items-center gap-1">
          {k.mine && !k.points && (
            <button type="button" aria-label={tx('Sil')} onClick={askDelete} className="cursor-pointer rounded-lg p-1.5 text-muted-foreground opacity-0 transition group-hover:opacity-100 hover:text-destructive">
              <Trash2 className="size-3.5" />
            </button>
          )}
          <motion.button
            type="button"
            whileTap={{ scale: 1.35 }}
            onClick={() => like.mutate(undefined)}
            className={cn('flex cursor-pointer items-center gap-1 rounded-full px-2 py-1 text-[12.5px] transition', k.likedByMe ? 'text-rose-500' : 'text-muted-foreground hover:text-rose-400')}
          >
            <Heart className={cn('size-4', k.likedByMe && 'fill-current')} /> {k.likeCount}
          </motion.button>
        </div>
      </div>
    </motion.article>
  )
}

/** Puanlar açıksa bakiye ve bu ay kalan bütçe. */
function WalletCard() {
  const q = useQuery({ queryKey: ['recognition', 'me'], queryFn: ({ signal }) => recognitionApi.me(signal), retry: false })
  if (!q.data?.enabled) return null
  return (
    <Panel>
      <PanelBody className="flex items-center gap-3">
        <span className="grid size-10 place-items-center rounded-xl bg-amber-400/15 text-amber-500"><Coins className="size-5" /></span>
        <div className="min-w-0 flex-1">
          <p className="text-[20px] leading-tight font-semibold tabular-nums">{q.data.balance}</p>
          <p className="text-[12px] text-muted-foreground">{tx('puanınız · bu ay verebileceğiniz: {0}', [q.data.remaining])}</p>
        </div>
        <Button asChild size="sm" variant="outline"><Link to="/panel/oduller"><Gift className="size-4" />{tx('Ödüller')}</Link></Button>
      </PanelBody>
    </Panel>
  )
}

export function KudosPage() {
  const [open, setOpen] = useState(false)
  const wall = useQuery({ queryKey: ['kudos', 'wall'], queryFn: ({ signal }) => engagementApi.kudos({ limit: 80 }, signal), refetchInterval: 30_000 })
  const board = useQuery({ queryKey: ['kudos', 'board'], queryFn: ({ signal }) => engagementApi.kudosLeaderboard(30, signal) })
  return (
    <TenantGate>
      <PageHeader
        title={tx('Takdir duvarı')}
        description={tx('Bir çalışma arkadaşınızın emeğini görünür kılın. Küçük bir teşekkür, büyük bir motivasyondur.')}
        actions={
          <Button onClick={() => setOpen(true)}>
            <Award className="size-4" />{' '}{tx('Takdir gönder')}
          </Button>
        }
      />
      <div className="grid gap-6 lg:grid-cols-[1fr_320px]">
        <div>
          {wall.isPending ? (
            <RowsSkeleton rows={4} columns={2} />
          ) : wall.isError ? (
            <ErrorState message={(wall.error as Error).message} onRetry={() => wall.refetch()} />
          ) : wall.data.length === 0 ? (
            <EmptyState icon={PartyPopper} title={tx('Duvar henüz boş')} detail={tx('İlk takdiri siz gönderin; bir teşekkür bütün ekibin gününü değiştirebilir.')} action={<Button onClick={() => setOpen(true)}>{tx('Takdir gönder')}</Button>} />
          ) : (
            <div className="columns-1 gap-4 md:columns-2">
              <AnimatePresence>
                {wall.data.map((k, i) => (
                  <KudosCard key={k.id} k={k} index={i} />
                ))}
              </AnimatePresence>
            </div>
          )}
        </div>
        <aside className="space-y-5">
          <WalletCard />
          <Panel>
            <PanelHead title={<span className="flex items-center gap-2"><Trophy className="size-4 text-amber-400" />{' '}{tx('Son 30 gün')}</span>} note={board.data ? tx('{0} takdir · {1} kişi verdi', [board.data.total, board.data.givers]) : undefined} />
            <PanelBody className="space-y-2.5">
              {(board.data?.top ?? []).length === 0 && <p className="text-[13px] text-muted-foreground">{tx('Henüz veri yok.')}</p>}
              {board.data?.top.map((t, i) => (
                <motion.div key={t.employeeId} initial={{ opacity: 0, x: 10 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: i * 0.06 }} className="flex items-center gap-2.5">
                  <span className={cn('tabular w-5 text-center text-[13px] font-semibold', i === 0 ? 'text-amber-400' : i === 1 ? 'text-zinc-300' : i === 2 ? 'text-orange-400' : 'text-muted-foreground')}>{i + 1}</span>
                  <Initials name={t.name} size={28} />
                  <span className="min-w-0 flex-1 truncate text-[13px]">{t.name}</span>
                  <span className="text-[13px]">{BADGE_STYLE[t.topBadge]?.emoji}</span>
                  <span className="tabular text-[13px] font-semibold">{t.count}</span>
                </motion.div>
              ))}
            </PanelBody>
          </Panel>
          <Panel>
            <PanelHead title={<span className="flex items-center gap-2"><PartyPopper className="size-4 text-primary" />{' '}{tx('Yaklaşan kutlamalar')}</span>} />
            <PanelBody className="p-3">
              <CelebrationList days={14} compact />
            </PanelBody>
          </Panel>
        </aside>
      </div>
      <SendKudosModal open={open} onClose={() => setOpen(false)} />
    </TenantGate>
  )
}
