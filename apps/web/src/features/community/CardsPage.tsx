import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { motion } from 'motion/react'
import { Lock, Mail, PenLine, Plus, Send, Trash2, Users } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { PersonSelect, useAction } from '@/features/shared/kit'
import { ApiError } from '@/api/client'
import { useMyEmployeeId } from '@/api/queries'
import { CARD_COLORS, CARD_OCCASIONS, cardOccasionLabels, cardsApi, type CardColor, type CardOccasion, type GroupCard } from '@/api/cards'
import { formatDate, formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const KEY = ['cards'] as const
const EMOJI: Record<CardOccasion, string> = { Birthday: '🎂', Farewell: '👋', Welcome: '🌱', Anniversary: '🏆', Congrats: '🎉', GetWell: '💐', Other: '💌' }
/** Not kâğıdı renkleri (açık/koyu temada okunur kalır). */
const NOTE: Record<CardColor, string> = {
  yellow: 'bg-amber-200/80 text-amber-950 dark:bg-amber-300/20 dark:text-amber-50',
  pink: 'bg-pink-200/80 text-pink-950 dark:bg-pink-400/20 dark:text-pink-50',
  blue: 'bg-sky-200/80 text-sky-950 dark:bg-sky-400/20 dark:text-sky-50',
  green: 'bg-emerald-200/80 text-emerald-950 dark:bg-emerald-400/20 dark:text-emerald-50',
  violet: 'bg-violet-200/80 text-violet-950 dark:bg-violet-400/20 dark:text-violet-50',
  orange: 'bg-orange-200/80 text-orange-950 dark:bg-orange-400/20 dark:text-orange-50',
}
const DOT: Record<CardColor, string> = { yellow: 'bg-amber-300', pink: 'bg-pink-300', blue: 'bg-sky-300', green: 'bg-emerald-300', violet: 'bg-violet-300', orange: 'bg-orange-300' }

/** Varsayılan teslim: verilen tarihin sabahı 09:00 (yerel), geçmişteyse yarın 09:00. */
function defaultDeliver(date?: string | null): string {
  const p = (n: number) => String(n).padStart(2, '0')
  let d = date ? new Date(`${date}T09:00`) : new Date(Date.now() + 86400_000)
  if (Number.isNaN(d.getTime()) || d.getTime() <= Date.now()) { d = new Date(Date.now() + 86400_000); d.setHours(9, 0, 0, 0) }
  if (!date) d.setHours(9, 0, 0, 0)
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
}

function NewCardModal({ initial, onClose, onOpen }: { initial: { to?: string; occasion?: CardOccasion; date?: string | null }; onClose: () => void; onOpen: (id: string) => void }) {
  const { employeeId: me } = useMyEmployeeId()
  const labels = cardOccasionLabels()
  const [f, setF] = useState({ to: initial.to ?? '', occasion: initial.occasion ?? ('Birthday' as CardOccasion), title: '', deliverAt: defaultDeliver(initial.date), notifyTeam: true })
  const create = useAction(async () => {
    try {
      return (await cardsApi.create({ recipientEmployeeId: f.to, occasion: f.occasion, title: f.title.trim(), deliverAt: new Date(f.deliverAt).toISOString(), notifyTeam: f.notifyTeam })).id
    } catch (e) {
      // Aynı kişiye aynı türde açık kart varsa onu aç.
      const id = e instanceof ApiError && e.status === 409 ? (e.detail as { id?: string } | undefined)?.id : undefined
      if (id) return id
      throw e
    }
  }, { invalidate: [KEY], onDone: (id) => { onClose(); onOpen(id) } })
  const ok = !!f.to && f.title.trim().length >= 3 && !!f.deliverAt
  return (
    <Modal open onClose={onClose} title={tx('Kart başlat')} note={tx('Kart, teslim zamanına kadar alıcıdan gizli kalır. Teslimde alıcıya bildirim gider.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ok || create.isPending} onClick={() => create.mutate(undefined)}>{tx('Başlat')}</Button></>}>
      <div className="space-y-4">
        <PersonSelect id="card-to" label={tx('Kime?')} value={f.to} onChange={(v) => setF({ ...f, to: v })} exclude={me ? [me] : []} />
        <div className="grid gap-3 sm:grid-cols-2">
          <SelectField label={tx('Tür')} value={f.occasion} onChange={(v) => setF({ ...f, occasion: v as CardOccasion })} options={CARD_OCCASIONS.map((o) => ({ value: o, label: `${EMOJI[o]} ${labels[o]}` }))} />
          <TextField label={tx('Teslim zamanı')} type="datetime-local" value={f.deliverAt} onChange={(e) => setF({ ...f, deliverAt: e.target.value })} />
        </div>
        <TextField label={tx('Başlık')} value={f.title} maxLength={120} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder={tx('ör. İyi ki doğdun Ayşe!')} />
        <label className="flex cursor-pointer items-center gap-2 text-[13px]">
          <Checkbox checked={f.notifyTeam} onCheckedChange={(v) => setF({ ...f, notifyTeam: v === true })} />{tx('Ekibine imzalaması için haber ver')}
        </label>
      </div>
    </Modal>
  )
}

function CardView({ id, onClose }: { id: string; onClose: () => void }) {
  const q = useQuery({ queryKey: [...KEY, id], queryFn: ({ signal }) => cardsApi.get(id, signal) })
  const confirm = useConfirm()
  const mine = q.data?.signatures.find((s) => s.mine)
  const [msg, setMsg] = useState<string | null>(null)
  const [color, setColor] = useState<CardColor | null>(null)
  const text = msg ?? mine?.message ?? ''
  const col = color ?? mine?.color ?? 'yellow'
  const sign = useAction(() => cardsApi.sign(id, text.trim(), col), { success: tx('Notunuz karta eklendi.'), invalidate: [KEY], onDone: () => { setMsg(null); setColor(null) } })
  const unsign = useAction(() => cardsApi.unsign(id), { invalidate: [KEY] })
  const deliver = useAction(() => cardsApi.deliver(id), { success: tx('Kart teslim edildi.'), invalidate: [KEY] })
  const remove = useAction(() => cardsApi.remove(id), { success: tx('Kart silindi.'), invalidate: [KEY], onDone: onClose })
  const c = q.data?.card
  const open = c && !c.deliveredAt
  return (
    <Modal open onClose={onClose} size="lg" title={c ? `${EMOJI[c.occasion]} ${c.title}` : tx('Kart')}
      note={c ? (c.deliveredAt ? tx('{0} için · {1} tarihinde teslim edildi', [c.recipientName, formatDate(c.deliveredAt)]) : tx('{0} için · {1} tarihinde teslim edilecek', [c.recipientName, formatDateTime(c.deliverAt)])) : undefined}
      footer={c?.canManage && open ? (
        <>
          <Button variant="ghost" onClick={async () => { if (await confirm({ title: tx('Kart silinsin mi?'), note: tx('Tüm imzalar da silinir.'), action: tx('Sil') })) remove.mutate(undefined) }}><Trash2 className="size-4" />{tx('Sil')}</Button>
          <Button variant="outline" disabled={!c.signers || deliver.isPending} onClick={async () => { if (await confirm({ title: tx('Kart şimdi teslim edilsin mi?'), note: tx('Teslimden sonra imza eklenemez.'), action: tx('Teslim et'), destructive: false })) deliver.mutate(undefined) }}><Send className="size-4" />{tx('Şimdi teslim et')}</Button>
        </>
      ) : undefined}>
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : (
        <div className="space-y-5">
          {open && !c!.forMe && (
            <div className="rounded-xl border border-border p-3">
              <TextAreaField label={mine ? tx('Notunuz') : tx('Notunuzu yazın')} value={text} maxLength={500} rows={3} onChange={(e) => setMsg(e.target.value)} />
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <span className="text-[12px] text-muted-foreground">{tx('Renk')}</span>
                {CARD_COLORS.map((k) => (
                  <button key={k} type="button" aria-label={k} aria-pressed={col === k} onClick={() => setColor(k)}
                    className={cn('size-6 cursor-pointer rounded-full ring-offset-2 ring-offset-background', DOT[k], col === k && 'ring-2 ring-primary')} />
                ))}
                <div className="ml-auto flex gap-2">
                  {mine && <Button size="sm" variant="ghost" disabled={unsign.isPending} onClick={() => unsign.mutate(undefined)}>{tx('İmzamı kaldır')}</Button>}
                  <Button size="sm" disabled={!text.trim() || sign.isPending} onClick={() => sign.mutate(undefined)}><PenLine className="size-4" />{mine ? tx('Güncelle') : tx('İmzala')}</Button>
                </div>
              </div>
            </div>
          )}
          {!q.data.signatures.length ? <p className="text-[13px] text-muted-foreground">{tx('Henüz imza yok. İlk notu siz bırakın.')}</p> : (
            <div className="columns-1 gap-3 overflow-hidden p-1 sm:columns-2">
              {q.data.signatures.map((s, i) => (
                <motion.div key={s.id} initial={{ opacity: 0, y: 8, rotate: 0 }} animate={{ opacity: 1, y: 0, rotate: i % 2 ? 0.8 : -0.8 }} transition={{ delay: Math.min(i * 0.04, 0.4) }}
                  className={cn('mb-3 break-inside-avoid rounded-xl p-3.5 shadow-sm', NOTE[s.color] ?? NOTE.yellow)}>
                  <p className="text-[13.5px] leading-relaxed whitespace-pre-line">{s.message}</p>
                  <p className="mt-2 flex items-center gap-1.5 text-[12px] font-medium opacity-80"><EmployeeAvatar id={s.employeeId} name={s.name} size={18} />{s.name}</p>
                </motion.div>
              ))}
            </div>
          )}
        </div>
      )}
    </Modal>
  )
}

function CardTile({ c, onOpen }: { c: GroupCard; onOpen: () => void }) {
  const labels = cardOccasionLabels()
  return (
    <button type="button" onClick={onOpen} className="w-full min-w-0 cursor-pointer text-left">
      <Panel className="transition-colors hover:border-primary/40">
        <PanelBody className="flex items-center gap-3">
          <span className="grid size-11 shrink-0 place-items-center rounded-xl bg-muted text-2xl">{EMOJI[c.occasion]}</span>
          <div className="min-w-0 flex-1">
            <p className="truncate text-[14px] font-semibold">{c.title}</p>
            <p className="truncate text-[12px] text-muted-foreground">
              {labels[c.occasion]} · {c.forMe ? tx('size') : c.recipientName} · {c.deliveredAt ? tx('teslim edildi {0}', [formatDate(c.deliveredAt)]) : tx('teslim {0}', [formatDateTime(c.deliverAt)])}
            </p>
          </div>
          <span className="flex items-center gap-1 text-[12.5px] text-muted-foreground tabular-nums"><Users className="size-3.5" />{c.signers}</span>
          {!c.deliveredAt && (c.signedByMe
            ? <span className="rounded-full bg-primary/10 px-2 py-0.5 text-[11.5px] text-primary">{tx('İmzaladınız')}</span>
            : <span className="rounded-full border border-primary/40 px-2 py-0.5 text-[11.5px] text-primary">{tx('İmzala')}</span>)}
        </PanelBody>
      </Panel>
    </button>
  )
}

/** Grup kartları (yenilik haritası #26). */
export function CardsPage() {
  const [params, setParams] = useSearchParams()
  const [creating, setCreating] = useState<null | { to?: string; occasion?: CardOccasion; date?: string | null }>(null)
  const viewing = params.get('kart')
  const setViewing = (id: string | null) => { const p = new URLSearchParams(params); if (id) p.set('kart', id); else p.delete('kart'); setParams(p, { replace: true }) }
  const q = useQuery({ queryKey: KEY, queryFn: ({ signal }) => cardsApi.list(signal) })

  // Kutlamalar sayfasından gelen "kart başlat" bağlantısı: ?yeni=<çalışan>&tur=<tür>&tarih=<yyyy-aa-gg>
  useEffect(() => {
    const to = params.get('yeni')
    if (!to) return
    setCreating({ to, occasion: (CARD_OCCASIONS as string[]).includes(params.get('tur') ?? '') ? params.get('tur') as CardOccasion : undefined, date: params.get('tarih') })
    const p = new URLSearchParams(params); p.delete('yeni'); p.delete('tur'); p.delete('tarih'); setParams(p, { replace: true })
  }, [params, setParams])

  const forMe = (q.data ?? []).filter((c) => c.forMe)
  const open = (q.data ?? []).filter((c) => !c.deliveredAt && !c.forMe)
  const done = (q.data ?? []).filter((c) => c.deliveredAt && !c.forMe)
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Grup kartları')} eyebrow={[tx('Topluluk')]} description={tx('Doğum günü, veda ya da tebrik için herkesin imzaladığı bir kart hazırlayın. Kart teslim edilene kadar alıcıdan gizlidir.')}
        actions={<Button onClick={() => setCreating({})}><Plus className="size-4" />{tx('Kart başlat')}</Button>} />
      {q.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : !q.data.length ? (
        <EmptyState icon={Mail} title={tx('Henüz kart yok')} detail={tx('Yaklaşan bir doğum günü ya da veda için ilk kartı siz başlatın.')} action={<Button onClick={() => setCreating({})}>{tx('Kart başlat')}</Button>} />
      ) : (
        <>
          {forMe.length > 0 && (
            <section className="space-y-2">
              <h2 className="text-[13px] font-semibold text-muted-foreground">{tx('Size gelen kartlar')}</h2>
              <div className="grid gap-2 md:grid-cols-2">{forMe.map((c) => <CardTile key={c.id} c={c} onOpen={() => setViewing(c.id)} />)}</div>
            </section>
          )}
          <section className="space-y-2">
            <h2 className="flex items-center gap-1.5 text-[13px] font-semibold text-muted-foreground"><Lock className="size-3.5" />{tx('İmza bekleyen kartlar')}</h2>
            {!open.length ? <p className="text-[13px] text-muted-foreground">{tx('Şu an açık kart yok.')}</p> : <div className="grid gap-2 md:grid-cols-2">{open.map((c) => <CardTile key={c.id} c={c} onOpen={() => setViewing(c.id)} />)}</div>}
          </section>
          {done.length > 0 && (
            <section className="space-y-2">
              <h2 className="text-[13px] font-semibold text-muted-foreground">{tx('Teslim edilenler')}</h2>
              <div className="grid gap-2 md:grid-cols-2">{done.map((c) => <CardTile key={c.id} c={c} onOpen={() => setViewing(c.id)} />)}</div>
            </section>
          )}
        </>
      )}
      {creating && <NewCardModal initial={creating} onClose={() => setCreating(null)} onOpen={setViewing} />}
      {viewing && <CardView id={viewing} onClose={() => setViewing(null)} />}
    </div>
  )
}
