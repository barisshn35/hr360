/**
 * Geri bildirim — `/panel/performans/geri-bildirim`.
 *
 * Sekmeler: Gelen (bana), Gönderdiklerim, yöneticide "Çalışana gelenler"
 * (seçilen çalışanın gelen kutusu, yalnızca yöneticilerin gördüğü notlar
 * dahil). Üstte özet: toplam, okunmamış, tona ve nedene göre dağılım.
 */

import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { Inbox, Lock, MessageSquarePlus, SendHorizontal } from 'lucide-react'
import {
  REASONS,
  SENTIMENTS,
  reasonLabels,
  sentimentLabels,
  useFeedbackSummary,
  useMarkFeedbackRead,
  useMyEmployeeId,
  useReceivedFeedback,
  useSentFeedback,
  type FeedbackReason,
} from '@/api/performance'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { SelectField } from '@/components/ui/Field'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { CountUp, EASE } from '@/motion/primitives'
import { Segmented, errorText } from '../components/controls'
import { PerfPageHeader } from '../components/PerfPageHeader'
import { PersonSelect } from '../components/pickers'
import { usePeople } from '../hooks'
import { FeedbackCard, SENTIMENT_COLOR } from './FeedbackCard'
import { FeedbackComposer } from './FeedbackComposer'
import { tx } from '@/lib/i18n'

type Tab = 'gelen' | 'giden' | 'calisan'
type Since = '30' | '90' | 'all'

const sinceIso = (s: Since) => (s === 'all' ? undefined : new Date(Date.now() - Number(s) * 86_400_000).toISOString())

export function FeedbackPage() {
  const { can } = useAuth()
  const manager = can('performance:manage')
  const toast = useToast()
  const me = useMyEmployeeId()
  const people = usePeople()
  const [params, setParams] = useSearchParams()
  const tab = (['gelen', 'giden', 'calisan'].includes(params.get('sekme') ?? '') ? params.get('sekme') : 'gelen') as Tab
  const [reason, setReason] = useState<FeedbackReason | '__all__'>('__all__')
  const [since, setSince] = useState<Since>('all')
  // "Benim performansım › Geri bildirim yaz" bağlantısı ?yaz=1 ile gelir; yazabilen kişide form açık başlar.
  const [composing, setComposing] = useState(() => manager && params.get('yaz') === '1')
  const target = params.get('calisan')

  const filters = { reason: reason === '__all__' ? undefined : reason, since: sinceIso(since) }
  const received = useReceivedFeedback(tab === 'gelen' ? me.employeeId : undefined, filters)
  const employeeFeed = useReceivedFeedback(tab === 'calisan' && manager ? (target ?? undefined) : undefined, { ...filters, asManager: true })
  const sent = useSentFeedback(tab === 'giden' ? me.employeeId : undefined)
  const summary = useFeedbackSummary(me.employeeId)
  const markRead = useMarkFeedbackRead()

  const active = tab === 'gelen' ? received : tab === 'giden' ? sent : employeeFeed
  const list = useMemo(() => {
    const data = active.data ?? []
    if (tab !== 'giden') return data
    return data.filter((f) => (reason === '__all__' || f.reason === reason) && (!filters.since || f.createdAt >= filters.since))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active.data, tab, reason, since])

  const set = (k: string, v: string | null) => {
    const p = new URLSearchParams(params)
    if (v) p.set(k, v)
    else p.delete(k)
    setParams(p, { replace: true })
  }

  if (me.notLinked) {
    return (
      <div className="mx-auto w-full max-w-5xl">
        <Panel>
          <EmptyState icon={Lock} title={tx('Hesabınıza bağlı çalışan kaydı bulunamadı')} detail={tx('Geri bildirim yazıp alabilmeniz için hesabınızın bir çalışan kaydıyla eşleşmesi gerekiyor. İK yöneticinize başvurun.')} />
        </Panel>
      </div>
    )
  }

  const s = summary.data
  const sentimentTotal = s ? SENTIMENTS.reduce((a, x) => a + (s.bySentiment[x] ?? 0), 0) : 0
  const topReasons = s ? REASONS.map((r) => ({ r, n: s.byReason[r] ?? 0 })).filter((x) => x.n > 0).sort((a, b) => b.n - a.n).slice(0, 4) : []

  return (
    <div className="mx-auto w-full max-w-5xl">
      <PerfPageHeader
        eyebrow={tx('Performans')}
        title={tx('Geri bildirim')}
        description={tx('Dönem boyunca sürekli geri bildirim. Anonim değildir: alıcı kimin yazdığını görür. Yapıcı eleştiri somut bir gerekçe ister.')}
        actions={
          // Geri bildirim yazmak backend'de yöneticiye ve üstüne açık (RequireManagerOrAbove).
          manager ? (
            <Button onClick={() => setComposing(true)} disabled={!me.employeeId}>
              <MessageSquarePlus aria-hidden />
              {tx('Geri bildirim yaz')}
            </Button>
          ) : undefined
        }
      >
        <div className="grid gap-5 md:grid-cols-[auto_minmax(0,1fr)] md:items-center">
          <dl className="grid grid-cols-2 gap-6">
            <div>
              <dt className="text-[11px] text-muted-foreground">{tx('Bana gelen')}</dt>
              <dd className="text-[22px] leading-tight font-semibold">{summary.isPending ? <Skeleton className="mt-1 h-6 w-8" /> : <CountUp to={s?.total ?? 0} duration={0.8} />}</dd>
            </div>
            <div>
              <dt className="text-[11px] text-muted-foreground">{tx('Okunmamış')}</dt>
              <dd className="text-[22px] leading-tight font-semibold text-primary">{summary.isPending ? <Skeleton className="mt-1 h-6 w-8" /> : <CountUp to={s?.unread ?? 0} duration={0.8} delay={0.08} />}</dd>
            </div>
          </dl>
          {s && sentimentTotal > 0 && (
            <div>
              <div className="flex h-2.5 w-full overflow-hidden rounded-full bg-muted">
                {SENTIMENTS.map((x, i) => (
                  <motion.span
                    key={x}
                    className="h-full"
                    style={{ background: SENTIMENT_COLOR[x] }}
                    initial={{ width: 0 }}
                    animate={{ width: `${((s.bySentiment[x] ?? 0) / sentimentTotal) * 100}%` }}
                    transition={{ duration: 0.8, ease: EASE, delay: 0.1 + i * 0.08 }}
                  />
                ))}
              </div>
              <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
                {SENTIMENTS.map((x) => (
                  <span key={x} className="inline-flex items-center gap-1.5">
                    <span className="size-2 rounded-full" style={{ background: SENTIMENT_COLOR[x] }} />
                    {sentimentLabels[x]} <span className="tabular font-semibold text-foreground">{s.bySentiment[x] ?? 0}</span>
                  </span>
                ))}
                {topReasons.length > 0 && <span className="text-muted-foreground">{tx('· En sık: {0}', [topReasons.map((x) => `${reasonLabels[x.r]} (${x.n})`).join(', ')])}</span>}
              </div>
            </div>
          )}
        </div>
      </PerfPageHeader>

      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <Segmented
          ariaLabel={tx('Kutu')}
          value={tab}
          onChange={(v) => set('sekme', v === 'gelen' ? null : v)}
          options={[
            { value: 'gelen', label: <span className="inline-flex items-center gap-1.5"><Inbox className="size-3.5" aria-hidden />{tx('Gelen')}{s?.unread ? <span className="tabular rounded-full bg-primary px-1.5 text-[10px] text-primary-foreground">{s.unread}</span> : null}</span> },
            { value: 'giden', label: <span className="inline-flex items-center gap-1.5"><SendHorizontal className="size-3.5" aria-hidden />{tx('Gönderdiklerim')}</span> },
            ...(manager ? [{ value: 'calisan' as const, label: tx('Çalışana gelenler') }] : []),
          ]}
        />
        <div className="flex flex-wrap items-end gap-3">
          {tab === 'calisan' && <PersonSelect className="w-56" value={target ?? ''} onChange={(v) => set('calisan', v)} />}
          <SelectField
            className="w-48"
            label={tx('Neden')}
            value={reason}
            onChange={(v) => setReason(v as FeedbackReason | '__all__')}
            options={[{ value: '__all__', label: tx('Tüm nedenler') }, ...REASONS.map((r) => ({ value: r, label: reasonLabels[r] }))]}
          />
          <Segmented ariaLabel={tx('Zaman')} size="sm" value={since} onChange={setSince} options={[{ value: '30', label: tx('30 gün') }, { value: '90', label: tx('90 gün') }, { value: 'all', label: tx('Tümü') }]} />
        </div>
      </div>

      {tab === 'calisan' && (
        <p className="mb-3 text-[12px] text-muted-foreground">{tx('Bu görünümde yalnızca yöneticilerin görebildiği notlar da listelenir ve "Yalnızca yöneticiler görür" etiketi taşır.')}</p>
      )}

      {tab === 'calisan' && !target ? (
        <Panel>
          <EmptyState icon={Inbox} title={tx('Bir çalışan seçin')} detail={tx('Seçtiğiniz çalışana gelen tüm geri bildirimler, gizli yönetici notlarıyla birlikte burada görünür.')} />
        </Panel>
      ) : active.isError ? (
        <Panel>
          <ErrorState title={tx('Geri bildirimler alınamadı')} message={errorText(active.error)} onRetry={() => void active.refetch()} />
        </Panel>
      ) : active.isPending || me.isPending ? (
        <div className="flex flex-col gap-3" aria-busy="true">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} className="h-32 rounded-xl" />
          ))}
        </div>
      ) : list.length === 0 ? (
        <Panel>
          <EmptyState
            icon={tab === 'giden' ? SendHorizontal : Inbox}
            title={tab === 'giden' ? tx('Henüz geri bildirim göndermediniz') : tx('Bu filtrede geri bildirim yok')}
            detail={
              tab !== 'giden'
                ? tx('Filtreleri genişletmeyi deneyin.')
                : manager
                  ? tx('Bir ekip arkadaşınızın iyi yaptığı bir şeyi fark ettiyseniz söylemenin tam zamanı.')
                  : tx('Geri bildirimi yöneticiler ve İK yazar; size gelenler "Gelen" sekmesinde görünür.')
            }
            action={
              tab === 'giden' && manager ? (
                <Button onClick={() => setComposing(true)}>
                  <MessageSquarePlus aria-hidden />
                  {tx('Geri bildirim yaz')}
                </Button>
              ) : undefined
            }
          />
        </Panel>
      ) : (
        <div className="flex flex-col gap-3">
          <AnimatePresence initial={false}>
            {list.map((f, i) => (
              <FeedbackCard
                key={f.id}
                feedback={f}
                index={i}
                perspective={tab === 'gelen' ? 'received' : tab === 'giden' ? 'sent' : 'manager'}
                nameOf={people.nameOf}
                marking={markRead.isPending && markRead.variables === f.id}
                onMarkRead={
                  tab === 'gelen'
                    ? () =>
                        markRead.mutate(f.id, {
                          onError: (e) => toast.stop(errorText(e)),
                        })
                    : undefined
                }
              />
            ))}
          </AnimatePresence>
        </div>
      )}

      {me.employeeId && <FeedbackComposer key={composing ? 'open' : 'closed'} open={composing} onClose={() => setComposing(false)} me={me.employeeId} manager={manager} presetTo={tab === 'calisan' ? target : null} />}
    </div>
  )
}
