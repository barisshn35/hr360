/**
 * Hedef yönetimi — `/panel/performans/hedefler`.
 *
 * Yönetici: dönem + (ekip) seç → solda kişiler ve hedef özetleri, sağda
 * seçili kişinin hedefleri; hedef tanımlar, ilerleme günceller.
 * Çalışan: yalnızca kendi hedeflerini görür, düzenleyemez.
 *
 * Ağırlık oransaldır: her hedefin yanında toplam ağırlığa göre yüzdesi
 * yazar. Sayısal hedef %100'ü aşsa da çubuk 100'de durur (backend aşımı
 * kırpar) ve bu açıkça söylenir.
 */

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { Crosshair, Lock, Plus, Search, Target } from 'lucide-react'
import { formatScore, useDeleteGoal, useGoals, useMyEmployeeId, useTeam, type Goal } from '@/api/performance'
import { useConfirm } from '@/components/ui/Confirm'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState, InfoNote } from '@/components/ui/States'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { errorText } from '../components/controls'
import { PerfPageHeader, SetupTrail } from '../components/PerfPageHeader'
import { PersonAvatar } from '../components/people'
import { CyclePicker, TeamSelect } from '../components/pickers'
import { ShareBar } from '../components/WeightShare'
import { useCurrentCycle, usePeople } from '../hooks'
import { CreateGoalDialog, ProgressDialog } from './GoalDialogs'
import { GoalCard } from './GoalCard'
import { goalSideScore } from './goalMath'
import { tx, appLocale } from '@/lib/i18n'

export function GoalsPage() {
  const { can } = useAuth()
  const manager = can('performance:manage')
  const me = useMyEmployeeId()
  const people = usePeople()
  const { cycles, current, isPending: cyclesPending } = useCurrentCycle()
  const [params, setParams] = useSearchParams()
  const cycleId = params.get('donem') ?? current?.id ?? ''
  const cycle = cycles.find((c) => c.id === cycleId) ?? null
  const [teamId, setTeamId] = useState('__all__')
  const [q, setQ] = useState('')
  const selected = manager ? params.get('calisan') : (me.employeeId ?? null)
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<Goal | null>(null)
  const [editingGoal, setEditingGoal] = useState<Goal | null>(null)
  const confirm = useConfirm()
  const toast = useToast()
  const deleteGoal = useDeleteGoal()
  const removeGoal = async (g: Goal) => {
    if (!(await confirm({ title: tx('Hedef silinsin mi?'), note: tx('«{0}» hedefi ve ilerlemesi kalıcı olarak silinir; hedef ayağı puanı yeniden hesaplanır.', [g.title]), action: tx('Sil') }))) return
    deleteGoal.mutate(g.id, {
      onSuccess: () => toast.ok(tx('«{0}» silindi.', [g.title])),
      onError: (e) => toast.stop(errorText(e)),
    })
  }

  const setParam = (k: string, v: string | null) => {
    const p = new URLSearchParams(params)
    if (v) p.set(k, v)
    else p.delete(k)
    setParams(p, { replace: true })
  }

  // Yönetici: dönemdeki tüm hedefler (sol özet için). Çalışan: yalnızca kendisi.
  const allGoals = useGoals({ cycleId: cycleId || undefined }, manager && Boolean(cycleId))
  const myGoals = useGoals({ cycleId: cycleId || undefined, employeeId: me.employeeId }, !manager && Boolean(cycleId && me.employeeId))
  const goalsQ = manager ? allGoals : myGoals
  const team = useTeam(teamId !== '__all__' ? teamId : undefined)

  const byEmployee = useMemo(() => {
    const m = new Map<string, Goal[]>()
    for (const g of goalsQ.data ?? []) m.set(g.employeeId, [...(m.get(g.employeeId) ?? []), g])
    return m
  }, [goalsQ.data])

  const roster = useMemo(() => {
    const members = teamId !== '__all__' ? (team.data?.members ?? []).filter((x) => !x.leftOn).map((x) => x.employeeId) : null
    const needle = q.trim().toLocaleLowerCase(appLocale)
    return people.list
      .filter((p) => !members || members.includes(p.id))
      .filter((p) => !needle || p.name.toLocaleLowerCase(appLocale).includes(needle))
      .map((p) => ({ ...p, goals: byEmployee.get(p.id) ?? [] }))
      .sort((a, b) => Number(b.goals.length > 0) - Number(a.goals.length > 0) || a.name.localeCompare(b.name, 'tr-TR'))
  }, [people.list, teamId, team.data, q, byEmployee])

  // Yönetici ilk açılışta hedefi olan ilk kişiyi seçer.
  useEffect(() => {
    if (manager && !selected && roster.length && goalsQ.data) {
      const first = roster.find((r) => r.goals.length) ?? roster[0]
      setParam('calisan', first.id)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [manager, selected, roster.length, goalsQ.data])

  const goals = (selected ? (byEmployee.get(selected) ?? []) : []).slice().sort((a, b) => Number(a.status === 'Cancelled') - Number(b.status === 'Cancelled') || b.weight - a.weight)
  const totalWeight = goals.filter((g) => g.status !== 'Cancelled').reduce((a, g) => a + g.weight, 0)
  const side = goalSideScore(goals)
  const editable = manager && cycle?.status !== 'Closed'
  const selectedName = selected ? people.nameOf(selected) : ''

  if (!manager && me.notLinked) {
    return (
      <div className="mx-auto w-full max-w-5xl">
        <Panel>
          <EmptyState icon={Lock} title={tx('Hesabınıza bağlı çalışan kaydı bulunamadı')} detail={tx('Hedeflerinizi görebilmeniz için hesabınızın bir çalışan kaydıyla eşleşmesi gerekiyor. İK yöneticinize başvurun.')} />
        </Panel>
      </div>
    )
  }

  return (
    <div className="mx-auto w-full max-w-7xl">
      <PerfPageHeader
        eyebrow={manager ? tx('Performans kurulumu · 4. adım') : tx('Performans')}
        title={manager ? tx('Hedefler') : tx('Hedeflerim')}
        description={
          manager
            ? tx('Her çalışanın dönem hedeflerini tanımlayın. Ağırlıklar oransaldır; hedef ayağı puanı ilerlemelerin ağırlıklı ortalamasıdır.')
            : tx('Bu dönem için yöneticinizin tanımladığı hedefler ve ilerlemeniz. Hedefler puanınızın hedef ayağını oluşturur.')
        }
        actions={
          editable && selected && cycle ? (
            <Button onClick={() => setCreating(true)}>
              <Plus aria-hidden />
              {tx('Yeni hedef')}
            </Button>
          ) : undefined
        }
      >
        <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
          {manager ? <SetupTrail current="goals" /> : <span />}
          <div className="flex flex-wrap items-end gap-3">
            <CyclePicker className="w-56" cycles={cycles} value={cycleId} onChange={(v) => setParam('donem', v)} includeDraft={manager} />
            {manager && <TeamSelect className="w-56" value={teamId} onChange={setTeamId} allowAll allLabel={tx('Tüm çalışanlar')} />}
          </div>
        </div>
      </PerfPageHeader>

      {cycle?.status === 'Closed' && (
        <div className="mb-4">
          <InfoNote>
            {tx('{0} kapandı; hedefler ve ilerlemeler kapanıştaki hâliyle sabit. Değişiklik yapılamaz.', [cycle.name])}</InfoNote>
        </div>
      )}

      {!cyclesPending && !cycles.length && (
        <Panel>
          <EmptyState icon={Crosshair} title={tx('Önce bir dönem gerekiyor')} detail={manager ? tx('Hedefler bir döneme bağlıdır. Dönemler ekranından ilk dönemi oluşturun.') : tx('Hedefler bir döneme bağlıdır. İK bir performans dönemi açtığında hedeflerinizi buradan girebilirsiniz.')} />
        </Panel>
      )}

      {goalsQ.isError && (
        <Panel>
          <ErrorState title={tx('Hedefler alınamadı')} message={errorText(goalsQ.error)} onRetry={() => void goalsQ.refetch()} />
        </Panel>
      )}

      {cycles.length > 0 && !goalsQ.isError && (
        <div className={cn('grid items-start gap-5', manager && 'lg:grid-cols-[300px_minmax(0,1fr)]')}>
          {manager && (
            <Panel className="lg:sticky lg:top-20">
              <div className="border-b border-border p-3">
                <label className="relative block">
                  <span className="sr-only">{tx('Kişi ara')}</span>
                  <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden />
                  <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Kişi ara')} className="h-8 w-full rounded-md border border-input bg-background pr-2 pl-8 text-[12px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50" />
                </label>
              </div>
              <ul className="max-h-[62vh] overflow-y-auto p-2">
                {goalsQ.isPending &&
                  [0, 1, 2, 3, 4].map((i) => (
                    <li key={i} className="flex items-center gap-3 px-2 py-2">
                      <Skeleton className="size-7 rounded-full" />
                      <Skeleton className="h-4 flex-1" />
                    </li>
                  ))}
                {!goalsQ.isPending && roster.length === 0 && <li className="px-2 py-6 text-center text-[12px] text-muted-foreground">{tx('Kişi bulunamadı.')}</li>}
                {!goalsQ.isPending &&
                  roster.map((r) => {
                    const on = r.id === selected
                    const s = goalSideScore(r.goals)
                    return (
                      <li key={r.id}>
                        <button
                          type="button"
                          onClick={() => setParam('calisan', r.id)}
                          className={cn('relative flex w-full items-center gap-3 rounded-md px-2 py-2 text-left transition-colors', on ? '' : 'hover:bg-muted/60')}
                        >
                          {on && <motion.span layoutId="goal-person" className="absolute inset-0 rounded-md bg-primary/10 ring-1 ring-primary/25" transition={{ type: 'spring', stiffness: 500, damping: 38 }} />}
                          <PersonAvatar id={r.id} name={r.name} className="relative" />
                          <span className="relative min-w-0 flex-1">
                            <span className="block truncate text-[13px] font-medium">{r.name}</span>
                            {r.goals.length ? (
                              <span className="mt-1 flex items-center gap-2">
                                <span className="h-1 flex-1 overflow-hidden rounded-full bg-muted">
                                  <motion.span className="block h-full rounded-full bg-primary" initial={{ width: 0 }} animate={{ width: `${s ?? 0}%` }} transition={{ duration: 0.8, ease: EASE }} />
                                </span>
                                <span className="tabular text-[11px] text-muted-foreground">{tx('{0} hedef', [r.goals.length])}</span>
                              </span>
                            ) : (
                              <span className="block text-[11px] text-muted-foreground">{tx('Hedef yok')}</span>
                            )}
                          </span>
                        </button>
                      </li>
                    )
                  })}
              </ul>
            </Panel>
          )}

          <div className="min-w-0">
            {goalsQ.isPending || (!manager && me.isPending) ? (
              <div className="grid gap-4 md:grid-cols-2" aria-busy="true">
                <Skeleton className="h-36 rounded-2xl md:col-span-2" />
                {[0, 1, 2, 3].map((i) => (
                  <Skeleton key={i} className="h-44 rounded-xl" />
                ))}
              </div>
            ) : selected ? (
              <>
                {/* özet */}
                <motion.section
                  key={`${selected}-${cycleId}`}
                  initial={{ opacity: 0, y: 10 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ duration: 0.5, ease: EASE }}
                  className="mb-4 rounded-2xl border border-border bg-card p-5"
                >
                  <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
                    <div className="flex items-center gap-3">
                      <PersonAvatar id={selected} name={selectedName} size="lg" />
                      <div>
                        <p className="text-[16px] font-semibold">{selectedName}</p>
                        <p className="text-[12px] text-muted-foreground">
                          {people.titleOf(selected) ? `${people.titleOf(selected)} · ` : ''}
                          {cycle?.name}
                        </p>
                      </div>
                    </div>
                    <div className="text-left sm:text-right">
                      <p className="text-[11px] text-muted-foreground">{tx('Hedef ayağı')}</p>
                      <p className="text-[28px] leading-none font-semibold tracking-tight">
                        {side === null ? '—' : <AnimatedNumber value={side} format={(v) => formatScore(v)} duration={1} />}
                      </p>
                      <p className="mt-0.5 text-[11px] text-muted-foreground">{tx('ilerlemelerin ağırlıklı ortalaması')}</p>
                    </div>
                  </div>
                  {goals.length > 0 && (
                    <div className="mt-4">
                      <ShareBar items={goals.filter((g) => g.status !== 'Cancelled').map((g) => ({ id: g.id, label: g.title, weight: g.weight }))} height={10} showLabels />
                      <p className="mt-2 text-[11px] text-muted-foreground">{tx('Toplam ağırlık {0}. Ağırlıklar oransaldır: 60 ve 40 yazmak ile 3 ve 2 yazmak aynı sonucu verir.', [totalWeight])}</p>
                    </div>
                  )}
                </motion.section>

                {goals.length === 0 ? (
                  <Panel>
                    <EmptyState
                      icon={Target}
                      title={manager ? tx('Bu dönem için hedef tanımlanmamış') : tx('Bu dönem için hedefiniz yok')}
                      detail={
                        manager
                          ? tx('Hedef yoksa puanın hedef ayağı boş kalır ve metrik puanı tam ağırlıkla kullanılır.')
                          : tx('Hedefleri yöneticiniz tanımlar. Hedef yoksa puanınız yalnızca metrik değerlendirmelerinden oluşur.')
                      }
                      action={
                        editable && cycle ? (
                          <Button onClick={() => setCreating(true)}>
                            <Plus aria-hidden />
                            {tx('İlk hedefi ekle')}
                          </Button>
                        ) : undefined
                      }
                    />
                  </Panel>
                ) : (
                  <div className="grid gap-3 md:grid-cols-2">
                    <AnimatePresence initial={false}>
                      {goals.map((g, i) => (
                        <GoalCard key={g.id} goal={g} totalWeight={totalWeight} index={i} canEdit={editable} onUpdate={() => setEditing(g)} onEdit={() => setEditingGoal(g)} onDelete={() => void removeGoal(g)} />
                      ))}
                    </AnimatePresence>
                  </div>
                )}
                {!manager && <p className="mt-4 text-[12px] text-muted-foreground">{tx('Hedeflerinizi ve ilerlemenizi yöneticiniz günceller. Bir güncelleme eksikse yöneticinizle konuşun.')}</p>}
              </>
            ) : (
              <Panel>
                <EmptyState icon={Target} title={tx('Soldan bir çalışan seçin')} detail={tx('Seçtiğiniz kişinin bu dönemdeki hedefleri burada görünür.')} />
              </Panel>
            )}
          </div>
        </div>
      )}

      {creating && selected && cycle && <CreateGoalDialog employeeId={selected} employeeName={selectedName} cycle={cycle} existing={goals} onClose={() => setCreating(false)} />}
      {editingGoal && selected && cycle && <CreateGoalDialog employeeId={selected} employeeName={selectedName} cycle={cycle} existing={goals} goal={editingGoal} onClose={() => setEditingGoal(null)} />}
      {editing && <ProgressDialog goal={editing} onClose={() => setEditing(null)} />}
    </div>
  )
}
