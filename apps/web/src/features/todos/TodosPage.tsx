import { Link } from 'react-router-dom'
import { motion } from 'motion/react'
import {
  Award, BookOpen, CheckCheck, ClipboardList, FileSignature, FileWarning, Sprout, Gavel, Inbox, Megaphone, MessageSquareHeart, Rocket, ShieldCheck, Vote,
} from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { todoKindLabels, useMyTodos, type Todo, type TodoKind } from './useMyTodos'

const ICON: Record<TodoKind, React.ElementType> = {
  newhire: Sprout,
  document: FileWarning,
  approval: Inbox, signature: FileSignature, defence: Gavel, training: BookOpen, onboarding: Rocket, survey: MessageSquareHeart,
  consent: ShieldCheck, announcement: Megaphone, feedback360: Award, vote: Vote,
}

/** Son tarihe kalan gün (bugün 0, geçmiş eksi); tarih yoksa null. */
export function daysLeft(due: string | null | undefined, now = new Date()): number | null {
  if (!due) return null
  const [y, m, d] = due.slice(0, 10).split('-').map(Number)
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  return Math.round((new Date(y, m - 1, d).getTime() - today) / 86_400_000)
}

function DueChip({ due }: { due?: string | null }) {
  const n = daysLeft(due)
  if (n === null) return null
  const text = n < 0 ? tx('{0} gün gecikti', [-n]) : n === 0 ? tx('Bugün') : n === 1 ? tx('Yarın') : tx('{0} gün kaldı', [n])
  return (
    <span title={formatDate(due!)} className={cn('shrink-0 rounded-full px-2 py-0.5 text-[11.5px] tabular-nums',
      n < 0 ? 'bg-destructive/10 text-destructive' : n <= 2 ? 'bg-amber-400/15 text-amber-600 dark:text-amber-300' : 'bg-muted text-muted-foreground')}>{text}</span>
  )
}

export function TodoRow({ t, i = 0 }: { t: Todo; i?: number }) {
  const Icon = ICON[t.kind]
  return (
    <motion.li initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: Math.min(i, 10) * 0.03 }}>
      <Link to={t.href} className="flex items-center gap-3 px-5 py-3 transition-colors hover:bg-accent/60">
        <span className="grid size-9 shrink-0 place-items-center rounded-xl border border-border bg-card text-primary"><Icon className="size-4" /></span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[13.5px] font-medium">{t.title}</span>
          <span className="block truncate text-[12px] text-muted-foreground">{todoKindLabels()[t.kind]}{t.detail ? ` · ${t.detail}` : ''}</span>
        </span>
        <DueChip due={t.due} />
      </Link>
    </motion.li>
  )
}

/** Ana sayfa kartı: en yakın beş iş. */
export function TodosWidget({ className }: { className?: string }) {
  const { items, isPending } = useMyTodos()
  return (
    <Panel className={cn('h-full gap-0 overflow-hidden py-0', className)}>
      <div className="flex items-center justify-between px-5 pt-5 pb-2">
        <span className="flex items-center gap-2 text-[14px] font-semibold"><ClipboardList className="size-4 text-primary" />{tx('Yapılacaklarım')}</span>
        {items.length > 0 && <span className="rounded-full bg-primary/10 px-2 text-[12px] text-primary tabular-nums">{items.length}</span>}
      </div>
      {isPending && !items.length ? <div className="px-5 pb-5"><RowsSkeleton rows={3} columns={1} /></div> : !items.length ? (
        <p className="flex items-center gap-2 px-5 pb-6 text-[13px] text-muted-foreground"><CheckCheck className="size-4 text-primary" />{tx('Bekleyen işiniz yok.')}</p>
      ) : (
        <>
          <ul className="divide-y divide-border">{items.slice(0, 5).map((t, i) => <TodoRow key={t.id} t={t} i={i} />)}</ul>
          <div className="px-5 py-3"><Button size="sm" variant="ghost" asChild><Link to="/panel/yapilacaklarim">{tx('Tümünü gör')}</Link></Button></div>
        </>
      )}
    </Panel>
  )
}

/** Yapılacaklarım (yenilik haritası #2). */
export function TodosPage() {
  const { items, isPending, failed } = useMyTodos()
  const groups = (Object.keys(todoKindLabels()) as TodoKind[]).map((k) => [k, items.filter((t) => t.kind === k)] as const).filter(([, l]) => l.length)
  const late = items.filter((t) => (daysLeft(t.due) ?? 1) < 0).length
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Yapılacaklarım')} eyebrow={[tx('Günlük iş')]}
        description={tx('Onay, imza, eğitim, anket ve diğer modüllerde sizi bekleyen her şey tek listede.')} />
      {failed > 0 && <InfoNote>{tx('{0} kaynak şu an okunamadı; liste eksik olabilir.', [failed])}</InfoNote>}
      {isPending && !items.length ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : !items.length ? (
        <EmptyState icon={CheckCheck} title={tx('Bekleyen işiniz yok')} detail={tx('Yeni bir onay, imza ya da eğitim geldiğinde burada görünür.')} />
      ) : (
        <>
          <div className="grid gap-3 sm:grid-cols-3">
            <Panel className="gap-1 p-4"><span className="text-[12px] text-muted-foreground">{tx('Toplam')}</span><span className="text-[26px] font-semibold tabular-nums">{items.length}</span></Panel>
            <Panel className="gap-1 p-4"><span className="text-[12px] text-muted-foreground">{tx('Gecikmiş')}</span><span className={cn('text-[26px] font-semibold tabular-nums', late > 0 && 'text-destructive')}>{late}</span></Panel>
            <Panel className="gap-1 p-4"><span className="text-[12px] text-muted-foreground">{tx('Bu hafta')}</span><span className="text-[26px] font-semibold tabular-nums">{items.filter((t) => { const n = daysLeft(t.due); return n !== null && n >= 0 && n <= 7 }).length}</span></Panel>
          </div>
          <Panel>
            <PanelHead title={tx('Son tarihe göre')} />
            <ul className="divide-y divide-border">{items.map((t, i) => <TodoRow key={t.id} t={t} i={i} />)}</ul>
          </Panel>
          <div className="flex flex-wrap gap-2 text-[12px] text-muted-foreground">
            {groups.map(([k, l]) => <span key={k} className="rounded-full border border-border px-2.5 py-1">{todoKindLabels()[k]} · {l.length}</span>)}
          </div>
        </>
      )}
    </div>
  )
}
