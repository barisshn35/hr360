import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Check, Route, TriangleAlert } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { governanceApi, type HireSaga } from '@/api/governance'
import { cn } from '@/lib/utils'
import { TenantGate, isoDate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const stageLabel: Record<HireSaga['stage'], string> = {
  Offer: tx('Teklif aşamasında'), AwaitingEmployee: tx('Çalışan kaydı bekliyor'), AwaitingOnboarding: tx('Onboarding bekliyor'), Completed: tx('Tamamlandı'),
}

function Steps({ s }: { s: HireSaga }) {
  return (
    <div className="flex items-center">
      {s.steps.map((st, i) => (
        <div key={st.key} className="flex items-center">
          <motion.span initial={{ scale: 0 }} animate={{ scale: 1 }} transition={{ delay: i * 0.08 }} title={st.label}
            className={cn('grid size-6 place-items-center rounded-full border text-[10px]', st.done ? 'border-primary bg-primary text-primary-foreground' : 'border-border text-muted-foreground')}>
            {st.done ? <Check className="size-3.5" /> : i + 1}
          </motion.span>
          {i < s.steps.length - 1 && <span className={cn('h-0.5 w-8', s.steps[i + 1].done ? 'bg-primary' : 'bg-border')} />}
        </div>
      ))}
    </div>
  )
}

export function HireSagaPage() {
  const q = useQuery({ queryKey: ['sagas'], queryFn: ({ signal }) => governanceApi.hireSagas(signal) })
  const [adv, setAdv] = useState<HireSaga | null>(null)
  const [start, setStart] = useState(isoDate(new Date(Date.now() + 14 * 86400000)))
  const [log, setLog] = useState<string[] | null>(null)
  const advance = useAction(() => governanceApi.advanceSaga(adv!.applicationId, start), { success: tx('Saga ilerletildi'), invalidate: [['sagas']], onDone: (r) => { setLog(r.log); setAdv(null) } })
  const stuck = (q.data ?? []).filter((s) => s.stuck).length
  return (
    <TenantGate>
      <PageHeader title={tx('Teklif → işe giriş')} description={tx('İşe alım (recruitment) → çalışan kaydı (employee) → onboarding planı adımlarının uçtan uca takibi. Takılan başvuruyu tek tıkla ilerletin.')} />
      <div className="mb-5"><InfoNote>{tx('Her adım idempotenttir: önce sonucun zaten var olup olmadığı kontrol edilir, yoksa ilgili servisin API\'si sizin yetkinizle çağrılır. Başarısız adım tekrar denenebilir.')}</InfoNote></div>
      {log && <div className="mb-5 rounded-2xl border border-[hsl(var(--success))]/30 bg-[hsl(var(--success))]/10 p-4 text-[13px]">{log.map((l) => <p key={l}>✓ {l}</p>)}</div>}
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState message={(q.error as Error).message} /> : q.data.length === 0 ? (
        <EmptyState icon={Route} title={tx('Teklif veya işe alım aşamasında aday yok')} detail={tx('Adaylar İşe alım ekranında “Teklif” ve “İşe alındı” aşamasına geldiğinde burada izlenir.')} />
      ) : (
        <Panel>
          <PanelHead title={tx('Süreçler')} note={stuck ? tx('{0} başvuru 2 günden uzun süredir bekliyor', [stuck]) : tx('Takılan süreç yok')} />
          <PanelBody className="p-0">
            <ul className="divide-y divide-border">
              {q.data.map((s) => (
                <li key={s.applicationId} className="flex flex-wrap items-center gap-4 px-5 py-4">
                  <div className="min-w-48 flex-1"><p className="text-[14px] font-medium">{s.candidate}</p><p className="text-[12px] text-muted-foreground">{s.posting} · {s.email}</p></div>
                  <Steps s={s} />
                  <StatusBadge tone={s.stage === 'Completed' ? 'success' : s.stuck ? 'danger' : 'warning'}>{s.stuck && <TriangleAlert className="size-3" />} {stageLabel[s.stage]}{s.stage !== 'Completed' ? tx(' · {0} gün', [s.daysInStage]) : ''}</StatusBadge>
                  {(s.stage === 'AwaitingEmployee' || s.stage === 'AwaitingOnboarding') && <Button size="sm" onClick={() => setAdv(s)}>{tx('İlerlet')}</Button>}
                </li>
              ))}
            </ul>
          </PanelBody>
        </Panel>
      )}
      {adv && (
        <Modal open onClose={() => setAdv(null)} title={tx('{0} — süreci ilerlet', [adv.candidate])} note={tx('Eksik adımlar sırayla tamamlanır: çalışan kaydı → onboarding planı.')}
          footer={<><Button variant="outline" onClick={() => setAdv(null)}>{tx('Vazgeç')}</Button><Button onClick={() => advance.mutate(undefined)} disabled={advance.isPending}>{tx('Çalıştır')}</Button></>}>
          <TextField label={tx('İşe başlama tarihi')} type="date" value={start} onChange={(e) => setStart(e.target.value)} />
        </Modal>
      )}
    </TenantGate>
  )
}
