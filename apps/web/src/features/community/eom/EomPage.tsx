import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { motion } from 'motion/react'
import { CalendarClock, Check, Crown, Medal, Search, Settings2, ShieldCheck, Trophy, Vote } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { TextAreaField } from '@/components/ui/Field'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useAction } from '@/features/shared/kit'
import {
  eomApi, eomKeys, periodLabel, unitLabel, useEomMe, visibilityNotice,
  type EomMyUnit, type EomPeriod, type EomPerson,
} from '@/api/eom'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'
import { WinnerSpotlight } from './EomWinnerCard'

/** Dönemin aşamaları ve tarihleri (şu anki aşama vurgulu). */
function PhaseSteps({ p }: { p: EomPeriod }) {
  const steps = [
    p.nominationStartsAt && { key: 'Nominating', label: tx('Aday gösterme'), at: p.nominationStartsAt },
    { key: 'Voting', label: p.scope === 'TwoStage' ? tx('1. aşama oylama') : tx('Oylama'), at: p.votingStartsAt },
    p.stage2EndsAt && { key: 'Stage2', label: tx('Final oylaması'), at: p.stage1EndsAt },
    { key: 'Closed', label: tx('Sonuç'), at: p.stage2EndsAt ?? p.stage1EndsAt },
  ].filter(Boolean) as { key: string; label: string; at: string }[]
  const order = ['Scheduled', 'Nominating', 'Voting', 'Stage2', 'Tie', 'Closed']
  const cur = order.indexOf(p.status === 'Tie' ? 'Closed' : p.status)
  return (
    <ol className="grid gap-2 sm:grid-flow-col sm:auto-cols-fr">
      {steps.map((s) => {
        const i = order.indexOf(s.key)
        const state = i < cur ? 'done' : i === cur ? 'now' : 'next'
        return (
          <li key={s.key} className={cn('rounded-xl border px-3 py-2.5 text-[12.5px]',
            state === 'now' ? 'border-primary/50 bg-primary/10' : 'border-border', state === 'done' && 'opacity-60')}>
            <span className="flex items-center gap-1.5 font-medium">
              {state === 'done' ? <Check className="size-3.5 text-primary" /> : state === 'now' ? <span className="size-2 animate-pulse rounded-full bg-primary" /> : <span className="size-2 rounded-full bg-muted-foreground/40" />}
              {s.label}
            </span>
            <span className="text-muted-foreground">{formatDateTime(s.at)}</span>
          </li>
        )
      })}
    </ol>
  )
}

/** Aday kartı (21st.dev "Emoji Radio Group" seçim kalıbı: vurgulu kenar ve köşede onay noktası). */
function CandidateCard({ person, selected, mine, onSelect, disabled }: { person: EomPerson; selected: boolean; mine: boolean; onSelect: () => void; disabled?: boolean }) {
  return (
    <button type="button" role="radio" aria-checked={selected} disabled={disabled} onClick={onSelect}
      className={cn('relative flex cursor-pointer items-center gap-3 rounded-2xl border p-3 text-left transition-all',
        'hover:-translate-y-0.5 hover:border-primary/40 disabled:cursor-not-allowed disabled:opacity-60',
        selected ? 'border-primary bg-primary/10 shadow-[0_0_0_3px_hsl(var(--primary)/0.15)]' : 'border-border bg-card')}>
      <EmployeeAvatar id={person.id} name={person.name} version={person.photoVersion} size={48} />
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[13.5px] font-semibold">{person.name}</span>
        <span className="block truncate text-[12px] text-muted-foreground">{person.position ?? person.department ?? '—'}</span>
      </span>
      {selected && <span className="absolute top-2 right-2 grid size-5 place-items-center rounded-full bg-primary text-primary-foreground shadow-[0_0_10px_hsl(var(--primary))]"><Check className="size-3" /></span>}
      {mine && !selected && <span className="absolute top-2 right-2 rounded-full bg-muted px-1.5 text-[10px] text-muted-foreground">{tx('önceki seçiminiz')}</span>}
    </button>
  )
}

function UnitBallot({ unit, period }: { unit: EomMyUnit; period: EomPeriod }) {
  const [pick, setPick] = useState<string | null>(unit.myChoice)
  const [comment, setComment] = useState(unit.myComment ?? '')
  const [q, setQ] = useState('')
  const nominate = unit.action === 'nominate'
  const submit = useAction(
    () => (nominate ? eomApi.nominate(unit.id, pick!) : eomApi.vote(unit.id, pick!, comment.trim() || undefined)),
    { success: nominate ? tx('Aday gösterildi.') : tx('Oyunuz kaydedildi.'), invalidate: [eomKeys.all] },
  )
  const list = useMemo(() => {
    const t = q.trim().toLocaleLowerCase(appLocale)
    return t ? unit.candidates.filter((c) => `${c.name} ${c.position ?? ''}`.toLocaleLowerCase(appLocale).includes(t)) : unit.candidates
  }, [q, unit.candidates])
  const stageNote = period.scope === 'TwoStage'
    ? unit.stage === 2 ? tx('Final: alt birim kazananları arasından ana departmanın ayın çalışanını seçin.') : tx('1. aşama: alt biriminizin kazananı finale kalır.')
    : null
  const changed = pick !== unit.myChoice || (!nominate && (comment.trim() || null) !== (unit.myComment ?? null))

  return (
    <Panel>
      <PanelHead
        title={<span className="flex items-center gap-2">{nominate ? <Medal className="size-4 text-primary" /> : <Vote className="size-4 text-primary" />}{unitLabel(unit)}</span>}
        note={stageNote ?? (nominate
          ? tx('Bir çalışma arkadaşınızı aday gösterin. En çok aday gösterilen {0} kişi oylamaya katılır.', [period.finalistCount])
          : tx('{0} kişilik birim · bir oy hakkınız var, kapanana kadar değiştirebilirsiniz.', [unit.memberCount]))}
        action={unit.myChoice && <span className="flex items-center gap-1 rounded-full bg-primary/10 px-2.5 py-1 text-[12px] text-primary"><Check className="size-3.5" />{nominate ? tx('Aday gösterdiniz') : tx('Oy verdiniz')}</span>}
      />
      <PanelBody className="space-y-4">
        {unit.candidates.length > 9 && (
          <div className="relative max-w-sm">
            <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Ad ya da unvan ara')} className="pl-9" aria-label={tx('Aday ara')} />
          </div>
        )}
        <div role="radiogroup" aria-label={tx('Adaylar')} className="grid gap-2.5 sm:grid-cols-2 xl:grid-cols-3">
          {list.map((c, i) => (
            <motion.div key={c.id} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: Math.min(i, 12) * 0.025 }}>
              <CandidateCard person={c} selected={pick === c.id} mine={unit.myChoice === c.id} onSelect={() => setPick(c.id)} disabled={submit.isPending} />
            </motion.div>
          ))}
          {list.length === 0 && <p className="text-[13px] text-muted-foreground">{tx('Aramayla eşleşen aday yok.')}</p>}
        </div>
        {!nominate && period.allowComment && (
          <TextAreaField label={tx('Neden? (isteğe bağlı)')} hint={tx('Kısa bir gerekçe; en fazla 280 karakter. Kazanan, kendisine yazılanları görür.')}
            value={comment} maxLength={280} rows={2} onChange={(e) => setComment(e.target.value)} />
        )}
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground"><ShieldCheck className="size-3.5" />{visibilityNotice(period)}</p>
          <Button onClick={() => submit.mutate(undefined)} disabled={!pick || !changed || submit.isPending}>
            {nominate ? tx('Aday göster') : unit.myChoice ? tx('Oyumu güncelle') : tx('Oyumu ver')}
          </Button>
        </div>
      </PanelBody>
    </Panel>
  )
}

function UnitStatus({ unit, period }: { unit: EomMyUnit; period: EomPeriod }) {
  const text = unit.status === 'Pending'
    ? tx('Final oylaması {0} tarihinde başlar; adaylar alt birim kazananları olacak.', [formatDateTime(period.stage1EndsAt)])
    : unit.status === 'Tie' ? tx('Beraberlik var; İK sonucu belirleyecek.')
    : unit.status === 'NoVotes' ? tx('Bu birimde oy kullanılmadı.')
    : unit.status === 'Closed' ? tx('Bu birimin oylaması tamamlandı. Sonuç dönem kapanınca açıklanır.')
    : period.status === 'Nominating' ? tx('Bu birimde aday gösterilecek kimse yok.') : tx('Bu birimde oylanacak aday yok.')
  return (
    <Panel>
      <PanelHead title={unitLabel(unit)} note={text} />
    </Panel>
  )
}

function History() {
  const h = useQuery({ queryKey: eomKeys.history, queryFn: ({ signal }) => eomApi.history(signal) })
  if (!h.data?.length) return null
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Trophy className="size-4 text-amber-400" />{tx('Önceki kazananlar')}</span>} />
      <PanelBody className="space-y-4">
        {h.data.map((row) => (
          <div key={row.period.id}>
            <div className="mb-2 flex items-center justify-between text-[12.5px]">
              <span className="font-medium">{periodLabel(row.period.key)}</span>
              <Link to={`/panel/ayin-calisani/sonuc/${row.period.id}`} className="text-muted-foreground underline-offset-2 hover:text-foreground hover:underline">{tx('Sonuçlar')}</Link>
            </div>
            <ul className="flex flex-wrap gap-2">
              {row.winners.map((w) => (
                <li key={w.unitId} className="flex items-center gap-2 rounded-full border border-border py-1 pr-3 pl-1">
                  <EmployeeAvatar id={w.winner.id} name={w.winner.name} version={w.winner.photoVersion} size={28} />
                  <span className="text-[12.5px]"><span className="font-medium">{w.winner.name}</span> <span className="text-muted-foreground">· {w.name}</span></span>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </PanelBody>
    </Panel>
  )
}

export function EomPage() {
  const me = useEomMe()
  const d = me.data
  const p = d?.period
  const active = p && p.status !== 'Scheduled'

  return (
    <div className="space-y-5">
      <PageHeader title={tx('Ayın çalışanı')} eyebrow={[tx('Topluluk')]}
        description={tx('Departmanınızda en çok takdiri hak eden çalışma arkadaşınızı seçin.')}
        actions={d?.isHr && (
          <Button variant="outline" asChild>
            <Link to="/panel/ayin-calisani/yonetim"><Settings2 className="size-4" />{tx('Ayarlar ve yönetim')}</Link>
          </Button>
        )} />

      {me.isPending ? <RowsSkeleton /> : me.isError ? <ErrorState onRetry={() => void me.refetch()} /> : !d?.enabled && !p ? (
        <EmptyState icon={Crown} title={tx('Ayın çalışanı kapalı')}
          detail={d?.isHr ? tx('Ayarlardan açtığınızda çalışanlar departmanlarında oylama yapabilir.') : tx('Bu özellik şirketinizde henüz açılmadı.')}
          action={d?.isHr && <Button asChild><Link to="/panel/ayin-calisani/yonetim">{tx('Ayarlara git')}</Link></Button>} />
      ) : (
        <>
          <WinnerSpotlight />

          {active ? (
            <Panel>
              <PanelHead title={tx('{0} oylaması', [periodLabel(p.key)])}
                note={p.status === 'Tie' ? tx('Oylama kapandı; beraberlik İK tarafından çözülünce sonuç açıklanacak.') : undefined} />
              <PanelBody className="space-y-3">
                <PhaseSteps p={p} />
                {d.myWeight != null && d.myWeight !== 1 && (
                  <p className="text-[12px] text-muted-foreground">{tx('Oyunuzun katsayısı: {0}', [d.myWeight])}</p>
                )}
              </PanelBody>
            </Panel>
          ) : (
            <InfoNote>
              <span className="flex items-center gap-2">
                <CalendarClock className="size-4 shrink-0" />
                {p ? tx('Sıradaki oylama ({0}) {1} tarihinde başlıyor.', [periodLabel(p.key), formatDateTime(p.nominationStartsAt ?? p.votingStartsAt)])
                  : d.next ? tx('Sıradaki oylama ({0}) {1} tarihinde başlıyor.', [periodLabel(d.next.key), formatDateTime(d.next.startsAt)])
                  : tx('Şu an açık oylama yok.')}
              </span>
            </InfoNote>
          )}

          {active && (d.units ?? []).length === 0 && (
            <InfoNote>{tx('Bu dönemde oy kullanabileceğiniz bir birim yok. Oylamaya katılmak için bir departmana atanmış olmanız gerekir.')}</InfoNote>
          )}
          {active && (d.units ?? []).map((u) => (u.action
            ? <UnitBallot key={`${u.id}:${u.action}:${u.myChoice ?? ''}`} unit={u} period={p} />
            : <UnitStatus key={u.id} unit={u} period={p} />))}

          <History />
        </>
      )}
    </div>
  )
}
