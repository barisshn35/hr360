import { useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { CalendarCheck2, CalendarClock, CheckCircle2, MapPin, Video } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { RowsSkeleton } from '@/components/ui/states'
import { Callout } from '@/components/ui/callout'
import { careerApi, interviewTypeLabels } from '@/api/recruitment'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'
import { Card, Shell } from './CareerPages'

const dayKey = (iso: string) => new Date(iso).toLocaleDateString(appLocale, { weekday: 'long', day: 'numeric', month: 'long' })
const timeOf = (iso: string) => new Date(iso).toLocaleTimeString(appLocale, { hour: '2-digit', minute: '2-digit' })

/** /kariyer/:tenant/mulakat/:token — adayın mülakat saatini seçtiği sayfa (oturum gerekmez; yenilik haritası #10). */
export function InterviewBookingPage() {
  const { tenant = '', token = '' } = useParams()
  const q = useQuery({ queryKey: ['booking', tenant, token], queryFn: ({ signal }) => careerApi.booking(tenant, token, signal), retry: false })
  const [pick, setPick] = useState<string | null>(null)
  const book = useAction(() => careerApi.book(tenant, token, pick!), { onDone: () => void q.refetch() })
  const d = q.data
  const groups = useMemo(() => {
    const m = new Map<string, string[]>()
    for (const s of d?.slots ?? []) m.set(dayKey(s), [...(m.get(dayKey(s)) ?? []), s])
    return [...m.entries()]
  }, [d?.slots])

  return (
    <Shell company={d?.company ?? undefined}>
      {q.isPending ? <Card><RowsSkeleton rows={3} /></Card> : q.isError || !d ? (
        <Card><p className="text-[14px]">{errMsg(q.error) || tx('Bağlantı geçersiz.')}</p></Card>
      ) : (
        <Card className="space-y-5">
          <div>
            <h1 className="text-[20px] font-semibold tracking-tight">{d.position}</h1>
            <p className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-[13px] text-muted-foreground">
              <span className="flex items-center gap-1"><CalendarClock className="size-4" />{interviewTypeLabels[d.type]} · {tx('{0} dk', [d.durationMinutes])}</span>
              {d.online && <span className="flex items-center gap-1"><Video className="size-4" />{tx('Çevrim içi')}</span>}
              {d.location && <span className="flex items-center gap-1"><MapPin className="size-4" />{d.location}</span>}
            </p>
          </div>
          {d.status === 'Booked' && d.chosen ? (
            <Callout tone="info" icon={CheckCircle2} title={tx('Mülakatınız planlandı: {0}', [formatDateTime(d.chosen.scheduledAt)])}>
              {d.chosen.meetingUrl && <p className="break-all">{tx('Bağlantı: {0}', [d.chosen.meetingUrl])}</p>}
              <p className="mt-1">{tx('Ayrıntılar e-postanıza da gönderildi. Değişiklik için İnsan Kaynakları ile iletişime geçin.')}</p>
            </Callout>
          ) : d.status !== 'Open' ? (
            <p className="text-[14px] text-muted-foreground">{d.status === 'Expired' ? tx('Bu bağlantının süresi doldu. Yeni saat için İnsan Kaynakları ile iletişime geçin.') : tx('Bu bağlantı artık geçerli değil.')}</p>
          ) : !d.slots.length ? (
            <p className="text-[14px] text-muted-foreground">{tx('Önerilen saatlerin hiçbiri artık uygun değil. İnsan Kaynakları sizinle iletişime geçecek.')}</p>
          ) : (
            <>
              <p className="text-[14px]">{tx('Merhaba {0}, size uygun saati seçin ({1} tarihine kadar).', [d.firstName ?? '', formatDateTime(d.expiresAt)])}</p>
              <div className="space-y-4">
                {groups.map(([day, list]) => (
                  <div key={day}>
                    <p className="mb-2 text-[13px] font-medium first-letter:uppercase">{day}</p>
                    <div role="radiogroup" aria-label={day} className="flex flex-wrap gap-2">
                      {list.map((s) => (
                        <button key={s} type="button" role="radio" aria-checked={pick === s} onClick={() => setPick(s)}
                          className={cn('cursor-pointer rounded-xl border px-4 py-2 text-[14px] font-medium tabular-nums transition-colors',
                            pick === s ? 'border-primary bg-primary text-primary-foreground' : 'border-border hover:border-primary/50')}>{timeOf(s)}</button>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
              <p className="text-[12px] text-muted-foreground">{tx('Saatler cihazınızın saat diliminde gösterilir.')}</p>
              <Button className="w-full sm:w-auto" disabled={!pick || book.isPending} onClick={() => book.mutate(undefined)}>
                <CalendarCheck2 className="size-4" />{pick ? tx('{0} saatini onayla', [formatDateTime(pick)]) : tx('Bir saat seçin')}
              </Button>
            </>
          )}
        </Card>
      )}
    </Shell>
  )
}
