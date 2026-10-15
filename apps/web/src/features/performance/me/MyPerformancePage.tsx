/**
 * Çalışanın kendi görünümü — `/panel/performans/benim`.
 *
 * Görür: kendi trendi, dönem geçmişi, gelen geri bildirimler (kimden geldiği
 * dahil), okunmamış sayısı.
 * Görmez: aksiyon önerileri, ekip sıralaması, başkalarının puanları.
 *
 * `/me` uçları token e-postasını çalışan kaydıyla eşler; eşleşme yoksa 404
 * döner ve açık bir yönlendirme gösterilir.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { ArrowRight, CalendarCheck, ClipboardList, Crosshair, Gauge, Hourglass, Inbox, Lock, MessageSquarePlus, UserRoundX } from 'lucide-react'
import {
  PERIODS,
  formatScore,
  isNoEmployeeRecord,
  periodLabels,
  useMarkFeedbackRead,
  useMyAnalytics,
  useMyCycles,
  type AnalyticsPeriod,
} from '@/api/performance'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { CHART, ChartLegend, TrendChart } from '../components/charts'
import { Chip, Segmented, errorText } from '../components/controls'
import { FeedbackCard } from '../feedback/FeedbackCard'
import { usePeople } from '../hooks'
import { tx } from '@/lib/i18n'

export function MyPerformancePage() {
  const { user, can } = useAuth()
  // Geri bildirim yazmak yöneticiye ve üstüne açık; çalışan yalnızca aldıklarını görür.
  const canWriteFeedback = can('performance:manage')
  const toast = useToast()
  const people = usePeople()
  const [period, setPeriod] = useState<AnalyticsPeriod>('quarter')
  const [onlyUnread, setOnlyUnread] = useState(false)
  const me = useMyAnalytics(period)
  const cycles = useMyCycles()
  const markRead = useMarkFeedbackRead()

  if (isNoEmployeeRecord(me.error) || isNoEmployeeRecord(cycles.error)) {
    return (
      <div className="mx-auto w-full max-w-3xl pt-6">
        <Panel>
          <EmptyState
            icon={UserRoundX}
            title={tx('Hesabınıza bağlı çalışan kaydı bulunamadı')}
            detail={tx('Performans bilgilerinizi görebilmeniz için oturum açtığınız e-posta adresinin bir çalışan kaydıyla eşleşmesi gerekiyor. İK yöneticinize başvurun.')}
          />
        </Panel>
      </div>
    )
  }

  const a = me.data
  const firstName = (user?.fullName ?? '').split(' ')[0]
  const feedback = (a?.feedback ?? []).filter((f) => !onlyUnread || !f.isRead)
  const history = cycles.data?.cycles ?? []

  return (
    <div className="mx-auto w-full max-w-6xl">
      {/* --------------------------------- karşılama --------------------------------- */}
      <motion.header
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.6, ease: EASE }}
        className="relative mb-6 overflow-hidden rounded-2xl border border-border bg-card"
      >
        <div aria-hidden className="pointer-events-none absolute inset-0">
          <div className="hr-aurora absolute -top-24 -left-20 h-64 w-80 rounded-full bg-primary/15 blur-3xl" />
          <div className="hr-aurora hr-aurora-slow hr-aurora-delay absolute -right-16 -bottom-24 h-64 w-80 rounded-full bg-[hsl(var(--chart-3))]/10 blur-3xl" />
        </div>
        <div className="relative grid gap-6 p-6 md:grid-cols-[minmax(0,1fr)_auto] md:items-end">
          <div>
            <p className="text-[12px] font-semibold text-primary">{tx('Benim performansım')}</p>
            <h1 className="mt-1 text-[26px] leading-tight font-semibold tracking-tight">{tx('Merhaba{0}', [firstName ? `, ${firstName}` : ''])}</h1>
            <p className="mt-1.5 max-w-xl text-[13px] leading-relaxed text-muted-foreground">
              {tx('Puanınızın zaman içindeki seyri, dönem sonuçlarınız ve size gelen geri bildirimler. Puanın nasıl hesaplandığını her zaman puan dökümünde görebilirsiniz.')}
            </p>
            <div className="mt-4 flex flex-wrap gap-2">
              <Button asChild size="sm">
                <Link to="/panel/performans/puan">
                  <Gauge aria-hidden />
                  {tx('Puan dökümüm')}
                </Link>
              </Button>
              <Button asChild size="sm" variant="outline">
                <Link to="/panel/performans/hedefler">
                  <Crosshair aria-hidden />
                  {tx('Hedeflerim')}
                </Link>
              </Button>
              <Button asChild size="sm" variant="outline">
                <Link to="/panel/performans/degerlendirme">
                  <ClipboardList aria-hidden />
                  {tx('Değerlendirmelerim')}
                </Link>
              </Button>
              <Button asChild size="sm" variant="ghost">
                <Link to={canWriteFeedback ? '/panel/performans/geri-bildirim?yaz=1' : '/panel/performans/geri-bildirim'}>
                  <MessageSquarePlus aria-hidden />
                  {canWriteFeedback ? tx('Geri bildirim yaz') : tx('Geri bildirimlerim')}
                </Link>
              </Button>
            </div>
          </div>
          <div className="rounded-2xl border border-border bg-background/70 p-4 backdrop-blur-sm md:min-w-60">
            <p className="text-[11px] text-muted-foreground">{tx('Güncel puan · {0}', [a?.periodLabel ?? '…'])}</p>
            {me.isPending ? (
              <Skeleton className="mt-2 h-10 w-28" />
            ) : (
              <p className="mt-1 text-[40px] leading-none font-semibold tracking-tight">{a?.current === null || a?.current === undefined ? '—' : <AnimatedNumber value={a.current} format={(v) => formatScore(v)} duration={1.2} />}</p>
            )}
            {a && (
              <div className="mt-2 flex flex-wrap items-center gap-2 text-[12px]">
                {a.change !== null && (
                  <span className={cn('tabular font-semibold', a.change > 0 ? 'text-[hsl(var(--success))]' : a.change < 0 ? 'text-destructive' : 'text-muted-foreground')}>
                    {a.change > 0 ? '▲ +' : a.change < 0 ? '▼ −' : ''}
                    {formatScore(Math.abs(a.change))}
                  </span>
                )}
                {a.trendLabel && <Chip tone={a.trend?.includes('Down') ? 'danger' : a.trend?.includes('Up') ? 'success' : 'muted'}>{a.trendLabel}</Chip>}
                {a.series.at(-1)?.isProvisional && (
                  <Chip tone="warning">
                    <Hourglass className="size-3" aria-hidden />
                    {tx('geçici')}
                  </Chip>
                )}
              </div>
            )}
          </div>
        </div>
      </motion.header>

      {me.isError && !isNoEmployeeRecord(me.error) && (
        <Panel className="mb-5">
          <ErrorState title={tx('Performans bilgileriniz alınamadı')} message={errorText(me.error)} onRetry={() => void me.refetch()} />
        </Panel>
      )}

      <div className="grid items-start gap-5 lg:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]">
        <div className="flex min-w-0 flex-col gap-5">
          {/* trend */}
          <Panel className="p-5">
            <div className="mb-3 flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <h2 className="text-[15px] font-semibold">{tx('Gidişatım')}</h2>
                <ChartLegend items={[{ label: tx('Kesin'), color: CHART.c1 }, { label: tx('Geçici (az değerlendirme)'), color: CHART.c1, hollow: true }]} />
              </div>
              <Segmented ariaLabel={tx('Aralık')} size="sm" value={period} onChange={setPeriod} options={PERIODS.map((p) => ({ value: p, label: periodLabels[p] }))} />
            </div>
            {me.isPending ? <Skeleton className="h-60 rounded-xl" /> : a && a.series.some((p) => p.score !== null) ? <TrendChart data={a.series} color={CHART.c1} label={tx('Puanım')} /> : <p className="py-16 text-center text-[13px] text-muted-foreground">{tx('Bu aralıkta puanınız yok.')}</p>}
          </Panel>

          {/* dönem geçmişi */}
          <Panel className="p-5">
            <h2 className="mb-1 flex items-center gap-2 text-[15px] font-semibold">
              <CalendarCheck className="size-4 text-muted-foreground" aria-hidden />
              {tx('Dönem geçmişim')}
            </h2>
            <p className="mb-4 text-[12px] text-muted-foreground">{tx('Kapanan dönemlerin puanı kesindir; açık dönemin puanı yeni değerlendirmelerle değişebilir.')}</p>
            {cycles.isPending ? (
              <div className="grid gap-3 sm:grid-cols-2">
                {[0, 1, 2, 3].map((i) => (
                  <Skeleton key={i} className="h-24 rounded-xl" />
                ))}
              </div>
            ) : cycles.isError ? (
              <p className="text-[13px] text-destructive">{errorText(cycles.error)}</p>
            ) : history.length === 0 ? (
              <p className="py-6 text-center text-[13px] text-muted-foreground">{tx('Henüz dönem sonucunuz yok.')}</p>
            ) : (
              <ol className="relative grid gap-3 sm:grid-cols-2">
                {[...history].reverse().map((c, i) => (
                  <motion.li
                    key={c.cycleId}
                    initial={{ opacity: 0, y: 10 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ duration: 0.45, ease: EASE, delay: i * 0.06 }}
                    className={cn('rounded-xl border p-4', c.isFinal ? 'border-border' : 'border-dashed border-primary/40 bg-primary/5')}
                  >
                    <div className="flex items-start justify-between gap-2">
                      <p className="text-[13px] font-medium">{c.cycleName}</p>
                      {c.isFinal ? (
                        <Chip tone="success">
                          <Lock className="size-3" aria-hidden />
                          {tx('Kesin')}
                        </Chip>
                      ) : (
                        <Chip tone="primary">{tx('Açık dönem')}</Chip>
                      )}
                    </div>
                    <p className="mt-2 text-[28px] leading-none font-semibold tracking-tight">{c.score === null ? '—' : <AnimatedNumber value={c.score} format={(v) => formatScore(v)} />}</p>
                    <div className="mt-2 flex items-center justify-between gap-2">
                      {c.isProvisional ? (
                        <span className="inline-flex items-center gap-1 text-[11px] text-[hsl(var(--warning))]">
                          <Hourglass className="size-3" aria-hidden />
                          {tx('geçici puan')}
                        </span>
                      ) : (
                        <span />
                      )}
                      <Link to={`/panel/performans/puan?donem=${c.cycleId}`} className="inline-flex items-center gap-1 text-[12px] font-medium text-primary hover:underline">
                        {tx('Döküm')}
                        <ArrowRight className="size-3" aria-hidden />
                      </Link>
                    </div>
                  </motion.li>
                ))}
              </ol>
            )}
          </Panel>
        </div>

        {/* geri bildirimler */}
        <Panel className="p-5 lg:sticky lg:top-20">
          <div className="mb-3 flex items-center justify-between gap-2">
            <h2 className="flex items-center gap-2 text-[15px] font-semibold">
              <Inbox className="size-4 text-muted-foreground" aria-hidden />
              {tx('Bana gelen geri bildirimler')}
              {a && a.unreadCount > 0 && (
                <motion.span initial={{ scale: 0 }} animate={{ scale: 1 }} className="tabular rounded-full bg-primary px-2 text-[11px] text-primary-foreground">
                  {tx('{0} yeni', [a.unreadCount])}</motion.span>
              )}
            </h2>
            <Segmented ariaLabel={tx('Süzgeç')} size="sm" value={onlyUnread ? 'u' : 'a'} onChange={(v) => setOnlyUnread(v === 'u')} options={[{ value: 'a', label: tx('Tümü') }, { value: 'u', label: tx('Okunmamış') }]} />
          </div>
          {me.isPending ? (
            <div className="flex flex-col gap-3">
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} className="h-28 rounded-xl" />
              ))}
            </div>
          ) : feedback.length === 0 ? (
            <p className="py-10 text-center text-[13px] text-muted-foreground">{onlyUnread ? tx('Okunmamış geri bildiriminiz yok.') : tx('Henüz size geri bildirim yazılmadı.')}</p>
          ) : (
            <div className="flex max-h-[70vh] flex-col gap-3 overflow-y-auto pr-1">
              <AnimatePresence initial={false}>
                {feedback.map((f, i) => (
                  <FeedbackCard
                    key={f.id}
                    feedback={f}
                    index={i}
                    perspective="received"
                    nameOf={people.nameOf}
                    marking={markRead.isPending && markRead.variables === f.id}
                    onMarkRead={() => markRead.mutate(f.id, { onError: (e) => toast.stop(errorText(e)) })}
                  />
                ))}
              </AnimatePresence>
            </div>
          )}
          <p className="mt-3 text-[11px] text-muted-foreground">{tx('Geri bildirimler anonim değildir; her kartta kimin yazdığı görünür.')}</p>
        </Panel>
      </div>
    </div>
  )
}
