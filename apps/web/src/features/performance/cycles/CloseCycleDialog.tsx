/**
 * Dönem kapanışı — sıradan bir durum değişikliği değil.
 *
 *   1. Hazırlık: readiness çağrılır → hazır / geçici kalacak / bekleyen
 *      değerlendirme sayıları ve kimin neden geçici kalacağı listelenir.
 *      "Kapanan dönem yeniden açılamaz." açıkça yazılır; onay kutusu
 *      işaretlenmeden kapatılamaz.
 *   2. Sonuç: POST status=Closed → "N çalışanın nihai puanı sabitlendi".
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { CheckCircle2, ClipboardList, Hourglass, Lock, Search, ShieldAlert, UserCheck } from 'lucide-react'
import { useCycleReadiness, useSetCycleStatus, type ReviewCycle } from '@/api/performance'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorState } from '@/components/ui/States'
import { cn } from '@/lib/utils'
import { CountUp, EASE } from '@/motion/primitives'
import { errorText } from '../components/controls'
import { PersonAvatar } from '../components/people'
import { usePeople } from '../hooks'
import { tx, appLocale } from '@/lib/i18n'

export function CloseCycleDialog({ cycle, onClose }: { cycle: ReviewCycle; onClose: () => void }) {
  const readiness = useCycleReadiness(cycle.id)
  const close = useSetCycleStatus()
  const people = usePeople()
  const [ack, setAck] = useState(false)
  const [showAll, setShowAll] = useState(false)
  const [q, setQ] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<ReviewCycle | null>(null)

  const r = readiness.data
  const list = useMemo(() => {
    const needle = q.trim().toLocaleLowerCase(appLocale)
    return (r?.employees ?? [])
      .filter((e) => showAll || e.isProvisional)
      .map((e) => ({ ...e, displayName: people.nameOf(e.employeeId, e.name) }))
      .filter((e) => !needle || e.displayName.toLocaleLowerCase(appLocale).includes(needle))
  }, [r, showAll, q, people])

  const confirm = () => {
    setError(null)
    close.mutate(
      { id: cycle.id, status: 'Closed' },
      { onSuccess: (c) => setDone(c), onError: (e) => setError(errorText(e)) },
    )
  }

  if (done) {
    const n = done.finalizedEmployeeCount ?? r?.readyCount ?? 0
    return (
      <Modal open onClose={onClose} title={tx('{0} kapandı', [cycle.name])} footer={<Button onClick={onClose}>{tx('Tamam')}</Button>}>
        <div className="flex flex-col items-center py-4 text-center">
          <motion.span
            initial={{ scale: 0, rotate: -30 }}
            animate={{ scale: 1, rotate: 0 }}
            transition={{ type: 'spring', stiffness: 260, damping: 16 }}
            className="relative flex size-16 items-center justify-center rounded-full bg-[hsl(var(--success))]/15 text-[hsl(var(--success))]"
          >
            <span aria-hidden className="hr-ring absolute inset-0 rounded-full border-2 border-[hsl(var(--success))]" />
            <CheckCircle2 className="size-8" aria-hidden />
          </motion.span>
          <p className="mt-4 text-[15px]">
            <CountUp to={n} duration={1} className="text-[28px] font-semibold" />
            <span className="ml-1.5 font-medium">{tx('çalışanın nihai puanı sabitlendi.')}</span>
          </p>
          <p className="mt-1 max-w-sm text-[13px] text-muted-foreground">
            {tx('Bu puanlar artık değişmez; puanlama ayarı değişse bile dönem, kapandığı sürümle hesaplanmış olarak kalır.')}
          </p>
          <div className="mt-5 flex flex-wrap justify-center gap-2">
            <Button asChild variant="outline" size="sm">
              <Link to={`/panel/performans/analiz?sekme=donem&donem=${cycle.id}`}>{tx('Dönem sonuçlarını gör')}</Link>
            </Button>
            <Button asChild variant="outline" size="sm">
              <Link to="/panel/performans/oneriler">{tx('Aksiyon önerileri')}</Link>
            </Button>
          </div>
        </div>
      </Modal>
    )
  }

  return (
    <Modal
      open
      onClose={() => !close.isPending && onClose()}
      size="lg"
      title={tx('{0} kapatılsın mı?', [cycle.name])}
      note={tx('Kapanıştan önce kimin puanının hazır, kimin geçici kalacağını gözden geçirin.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={close.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button variant="destructive" onClick={confirm} disabled={!ack || close.isPending || !r}>
            <Lock aria-hidden />
            {close.isPending ? tx('Kapatılıyor…') : tx('Dönemi kalıcı olarak kapat')}
          </Button>
        </>
      }
    >
      {readiness.isPending && (
        <div aria-busy="true" className="flex flex-col gap-3">
          <div className="grid grid-cols-3 gap-3">
            {[0, 1, 2].map((i) => (
              <Skeleton key={i} className="h-20 rounded-xl" />
            ))}
          </div>
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      )}

      {readiness.isError && <ErrorState title={tx('Hazırlık bilgisi alınamadı')} message={errorText(readiness.error)} onRetry={() => void readiness.refetch()} />}

      {r && (
        <div className="flex flex-col gap-5">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            {[
              { icon: UserCheck, label: tx('Puanı hazır'), value: r.readyCount, tone: 'hsl(var(--success))', hint: tx('Nihai puanı sabitlenecek') },
              { icon: Hourglass, label: tx('Geçici kalacak'), value: r.provisionalCount, tone: 'hsl(var(--warning))', hint: tx('Yetersiz değerlendirme') },
              { icon: ClipboardList, label: tx('Bekleyen değerlendirme'), value: r.pendingReviewTotal, tone: 'hsl(var(--primary))', hint: tx('Kapanıştan sonra gönderilemez') },
            ].map((s, i) => (
              <motion.div
                key={s.label}
                initial={{ opacity: 0, y: 10 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.45, ease: EASE, delay: i * 0.08 }}
                className="rounded-xl border border-border p-3"
              >
                <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground">
                  <s.icon className="size-3.5" style={{ color: s.tone }} aria-hidden />
                  {s.label}
                </p>
                <p className="mt-1 text-[26px] leading-none font-semibold" style={{ color: s.tone }}>
                  <CountUp to={s.value} duration={0.8} />
                </p>
                <p className="mt-1 text-[11px] text-muted-foreground">{s.hint}</p>
              </motion.div>
            ))}
          </div>

          <div>
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <p className="text-[13px] font-semibold">
                {showAll ? tx('Tüm çalışanlar') : r.provisionalCount ? tx('Puanı geçici kalacaklar ve nedeni') : tx('Geçici kalacak kimse yok')}
              </p>
              <div className="flex items-center gap-2">
                <label className="relative">
                  <span className="sr-only">{tx('Kişi ara')}</span>
                  <Search className="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden />
                  <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Ara')} className="h-8 w-32 rounded-md border border-input bg-background pr-2 pl-7 text-[12px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50" />
                </label>
                <button type="button" onClick={() => setShowAll((v) => !v)} className="text-[12px] font-medium text-primary hover:underline">
                  {showAll ? tx('Yalnızca geçici kalacaklar') : tx('Herkesi göster ({0})', [r.employees.length])}
                </button>
              </div>
            </div>
            <ul className="max-h-64 divide-y divide-border overflow-y-auto rounded-xl border border-border">
              {list.length === 0 && (
                <li className="px-4 py-6 text-center text-[13px] text-muted-foreground">
                  {r.provisionalCount === 0 && !showAll ? tx('Tüm çalışanların puanı yeterli değerlendirmeye dayanıyor.') : tx('Eşleşen kişi yok.')}
                </li>
              )}
              {list.map((e, i) => (
                <motion.li
                  key={e.employeeId}
                  initial={{ opacity: 0, x: -8 }}
                  animate={{ opacity: 1, x: 0 }}
                  transition={{ delay: Math.min(i, 12) * 0.03 }}
                  className="flex items-start gap-3 px-3 py-2.5"
                >
                  <PersonAvatar id={e.employeeId} name={e.displayName} />
                  <div className="min-w-0 flex-1">
                    <p className="flex flex-wrap items-center gap-1.5 text-[13px] font-medium">
                      {e.displayName}
                      {e.isProvisional ? (
                        <span className="inline-flex items-center gap-1 rounded-md bg-[hsl(var(--warning))]/12 px-1.5 py-0.5 text-[11px] text-[hsl(var(--warning))]">
                          <Hourglass className="size-3" aria-hidden />
                          {tx('geçici kalacak')}
                        </span>
                      ) : (
                        <span className="rounded-md bg-[hsl(var(--success))]/12 px-1.5 py-0.5 text-[11px] text-[hsl(var(--success))]">{tx('hazır')}</span>
                      )}
                    </p>
                    {e.reason && <p className="mt-0.5 text-[12px] leading-relaxed text-muted-foreground">{e.reason}</p>}
                  </div>
                  <span className="tabular shrink-0 text-right text-[11px] text-muted-foreground">
                    {e.reviewCount !== undefined && <span className="block">{tx('{0} değerlendirme', [e.reviewCount])}</span>}
                    {e.pendingReviews ? <span className="block text-primary">{tx('{0} bekliyor', [e.pendingReviews])}</span> : null}
                  </span>
                </motion.li>
              ))}
            </ul>
          </div>

          <div className="rounded-xl border border-destructive/30 bg-destructive/5 p-4">
            <p className="flex items-center gap-2 text-[14px] font-semibold text-destructive">
              <ShieldAlert className="size-4.5" aria-hidden />
              {tx('Kapanan dönem yeniden açılamaz.')}
            </p>
            <p className="mt-1 text-[13px] leading-relaxed text-foreground/80">{tx('Kapanışta {0} çalışanın puanı nihai olarak sabitlenir; {1}Bu dönem için yeni değerlendirme gönderilemez, hedef ilerlemesi güncellenemez.', [r.readyCount, r.provisionalCount > 0 ? tx('{0} çalışanın puanı geçici olarak kalır. ', [r.provisionalCount]) : ''])}</p>
            <label className={cn('mt-3 flex cursor-pointer items-center gap-2.5 text-[13px] font-medium', ack && 'text-destructive')}>
              <Checkbox checked={ack} onCheckedChange={(v) => setAck(v === true)} />{tx('Anladım, {0} dönemini kalıcı olarak kapat.', [cycle.name])}</label>
          </div>

          <AnimatePresence>
            {error && (
              <motion.p role="alert" initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden text-[13px] text-destructive">
                {error}
              </motion.p>
            )}
          </AnimatePresence>
        </div>
      )}
    </Modal>
  )
}
