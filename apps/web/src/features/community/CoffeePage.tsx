import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Check, Coffee, Play, Settings2, Shuffle, Users } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField } from '@/components/ui/Field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { StatCard } from '@/components/ui/StatCard'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useAction } from '@/features/shared/kit'
import { useAuth } from '@/auth/useAuth'
import { cadenceLabels, coffeeApi, weekdayLabels, type CoffeeCadence, type CoffeeMatch } from '@/api/coffee'
import { formatDate, formatPercent, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const KEY = ['coffee'] as const

function useIsHr() {
  const { roles } = useAuth()
  return roles.some((r) => r === 'hr-admin' || r === 'tenant-admin')
}

function MatchCard({ m, current }: { m: CoffeeMatch; current: boolean }) {
  const met = useAction((v: boolean) => coffeeApi.met(m.id, v), { invalidate: [KEY] })
  return (
    <motion.div initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }}>
      <Panel className={cn(current && 'border-primary/40 bg-gradient-to-br from-primary/10 to-transparent')}>
        <PanelBody className="space-y-3">
          <div className="flex items-center justify-between text-[12px] text-muted-foreground">
            <span>{current ? tx('Bu turdaki eşiniz') : formatDate(m.createdAt)}</span>
            {!current && <span>{formatRelativeToNow(m.createdAt)}</span>}
          </div>
          {m.with.map((p) => (
            <div key={p.employeeId} className="flex items-center gap-3">
              <EmployeeAvatar id={p.employeeId} name={p.name} size={current ? 48 : 34} />
              <div className="min-w-0 flex-1">
                <p className={cn('truncate font-semibold', current ? 'text-[16px]' : 'text-[14px]')}>{p.name}</p>
                <p className="truncate text-[12.5px] text-muted-foreground">{[p.position, p.department].filter(Boolean).join(' · ')}</p>
              </div>
            </div>
          ))}
          <div className="flex items-center gap-2">
            {m.met ? (
              <button type="button" onClick={() => met.mutate(false)} className="flex cursor-pointer items-center gap-1.5 rounded-full bg-primary/10 px-2.5 py-1 text-[12.5px] text-primary">
                <Check className="size-3.5" />{tx('Görüştük')}
              </button>
            ) : (
              <Button size="sm" variant="outline" disabled={met.isPending} onClick={() => met.mutate(true)}><Coffee className="size-4" />{tx('Görüştük olarak işaretle')}</Button>
            )}
          </div>
        </PanelBody>
      </Panel>
    </motion.div>
  )
}

function Admin() {
  const q = useQuery({ queryKey: [...KEY, 'settings'], queryFn: ({ signal }) => coffeeApi.settings(signal) })
  const [form, setForm] = useState<{ enabled: boolean; cadence: CoffeeCadence; dayOfWeek: number; crossDepartment: boolean } | null>(null)
  const f = form ?? (q.data ? { enabled: q.data.enabled, cadence: q.data.cadence, dayOfWeek: q.data.dayOfWeek, crossDepartment: q.data.crossDepartment } : null)
  const save = useAction(() => coffeeApi.saveSettings(f!), { success: tx('Ayarlar kaydedildi.'), invalidate: [KEY], onDone: () => setForm(null) })
  const run = useAction(() => coffeeApi.run(), {
    success: (r) => (r.groups ? tx('{0} grup eşleştirildi; katılımcılara haber verildi.', [r.groups]) : tx('Bu dönemin turu zaten yapıldı ya da en az iki katılımcı yok.')),
    invalidate: [KEY],
  })
  const days = weekdayLabels()
  const cad = cadenceLabels()
  if (q.isPending || !f) return <Panel><PanelBody><RowsSkeleton rows={3} /></PanelBody></Panel>
  if (q.isError) return <ErrorState onRetry={() => void q.refetch()} />
  const s = q.data.stats
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <StatCard label={tx('Katılımcı')} count={q.data.participants} icon={Users} />
        <StatCard label={tx('Yapılan tur')} count={s.rounds} icon={Shuffle} />
        <StatCard label={tx('Son 3 turda görüşme oranı')} value={s.groups ? formatPercent(s.met / s.groups) : '—'} icon={Coffee} />
      </div>
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><Settings2 className="size-4 text-primary" />{tx('Ayarlar')}</span>}
          note={s.lastRoundAt ? tx('Son tur: {0}', [formatDate(s.lastRoundAt)]) : undefined} />
        <PanelBody className="space-y-4">
          <label className="flex cursor-pointer items-start gap-2 text-[13px]">
            <Checkbox checked={f.enabled} onCheckedChange={(v) => setForm({ ...f, enabled: v === true })} className="mt-0.5" />
            <span>{tx('Kahve eşleştirme açık')}<span className="block text-[12px] text-muted-foreground">{tx('Katılım isteğe bağlıdır. Eşleşmeleri yalnızca eşleşen kişiler görür; İK yalnızca sayıları görür.')}</span></span>
          </label>
          <div className="grid gap-3 sm:grid-cols-2">
            <SelectField label={tx('Sıklık')} value={f.cadence} onChange={(v) => setForm({ ...f, cadence: v as CoffeeCadence })} options={(['Weekly', 'Biweekly', 'Monthly'] as const).map((c) => ({ value: c, label: cad[c] }))} />
            <SelectField label={tx('Eşleştirme günü')} value={String(f.dayOfWeek)} onChange={(v) => setForm({ ...f, dayOfWeek: Number(v) })} options={days.map((d, i) => ({ value: String(i + 1), label: d }))} hint={tx('Saat 09:00 sonrasında eşleştirilir.')} />
          </div>
          <label className="flex cursor-pointer items-center gap-2 text-[13px]">
            <Checkbox checked={f.crossDepartment} onCheckedChange={(v) => setForm({ ...f, crossDepartment: v === true })} />{tx('Mümkünse farklı departmanlardan eşleştir')}
          </label>
          <div className="flex flex-wrap gap-2">
            <Button disabled={!form || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
            <Button variant="outline" disabled={!q.data.enabled || run.isPending} onClick={() => run.mutate(undefined)}><Play className="size-4" />{tx('Bu dönemin turunu şimdi başlat')}</Button>
          </div>
        </PanelBody>
      </Panel>
    </div>
  )
}

/** Kahve eşleştirme (yenilik haritası #27). */
export function CoffeePage() {
  const isHr = useIsHr()
  const q = useQuery({ queryKey: [...KEY, 'me'], queryFn: ({ signal }) => coffeeApi.me(signal) })
  const join = useAction((v: boolean) => coffeeApi.join(v), {
    success: (r) => (r.joined ? tx('Katıldınız; bir sonraki turda eşleşeceksiniz.') : tx('Kahve eşleştirmeden ayrıldınız.')), invalidate: [KEY],
  })
  const days = weekdayLabels()
  const cad = cadenceLabels()
  const d = q.data
  const [current, ...past] = d?.matches ?? []
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Kahve eşleştirme')} eyebrow={[tx('Topluluk')]} description={tx('Şirketten tanımadığınız biriyle rastgele eşleşin, bir kahve içip tanışın.')} />
      {q.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : (
        <>
          {!d!.settings.enabled ? (
            <EmptyState icon={Coffee} title={tx('Kahve eşleştirme kapalı')} detail={isHr ? tx('Aşağıdaki ayarlardan açabilirsiniz.') : tx('İK açtığında buradan katılabilirsiniz.')} />
          ) : (
            <Panel>
              <PanelBody className="flex flex-wrap items-center gap-4">
                <span className="grid size-12 place-items-center rounded-2xl bg-amber-400/15 text-amber-600 dark:text-amber-300"><Coffee className="size-6" /></span>
                <div className="min-w-0 flex-1">
                  <p className="text-[15px] font-semibold">{d!.joined ? tx('Katılıyorsunuz') : tx('Henüz katılmadınız')}</p>
                  <p className="text-[12.5px] text-muted-foreground">
                    {tx('{0}, {1} günü eşleştirilir', [cad[d!.settings.cadence], days[d!.settings.dayOfWeek - 1]])} · {tx('{0} katılımcı', [d!.participants])}
                  </p>
                </div>
                <Button variant={d!.joined ? 'outline' : 'default'} disabled={join.isPending} onClick={() => join.mutate(!d!.joined)}>
                  {d!.joined ? tx('Ayrıl') : tx('Katıl')}
                </Button>
              </PanelBody>
            </Panel>
          )}
          {current ? (
            <div className="grid gap-4 lg:grid-cols-[1.2fr_1fr]">
              <MatchCard m={current} current />
              {past.length > 0 && (
                <div className="space-y-2">
                  <h2 className="text-[13px] font-semibold text-muted-foreground">{tx('Önceki eşleşmeler')}</h2>
                  <div className="grid gap-2">{past.slice(0, 5).map((m) => <MatchCard key={m.id} m={m} current={false} />)}</div>
                </div>
              )}
            </div>
          ) : d!.settings.enabled && d!.joined && (
            <p className="text-[13px] text-muted-foreground">{tx('Henüz eşleşmeniz yok. Bir sonraki turda size haber verilecek.')}</p>
          )}
        </>
      )}
      {isHr && <Admin />}
    </div>
  )
}
