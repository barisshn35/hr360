import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Activity, BarChart3, Play, Plus, SquareX } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { chatbotApi, type ChatPulse } from '@/api/chatbot'
import type { ChatApp } from '@/api/governance'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/* Dalga 5e — BG20 bot yönetimi (komut aç/kapat, kullanım sayıları, son hatalar) ve B12 nabız anketi. */

const OUTCOMES: Record<string, string> = {
  ok: 'başarılı', disabled: 'kapalı komut', error: 'hata', expired: 'süresi dolmuş düğme', user: 'kişi sınırı', tenant: 'şirket sınırı',
}

/** Uygulamaya özel komut/özellik ayarları ve kullanım istatistikleri (yalnızca sayılar). */
export function ChatBotAdminModal({ app, onClose }: { app: ChatApp; onClose: () => void }) {
  const features = useQuery({ queryKey: ['chat-admin', 'features'], queryFn: ({ signal }) => chatbotApi.features(signal) })
  const stats = useQuery({ queryKey: ['chat-admin', 'stats', app.id], queryFn: ({ signal }) => chatbotApi.stats(app.id, 30, signal) })
  const [disabled, setDisabled] = useState<string[]>(app.disabledFeatures ?? [])
  const save = useAction(() => chatbotApi.setDisabled(app.id, disabled), { success: tx('Komut ayarları kaydedildi'), invalidate: [['chat-apps']], onDone: onClose })
  const toggle = (key: string, on: boolean) => setDisabled((d) => (on ? d.filter((k) => k !== key) : [...new Set([...d, key])]))
  const top = (stats.data?.byFeature ?? []).slice(0, 12)
  const max = Math.max(1, ...top.map((r) => r.count))
  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={tx('{0} — bot yönetimi', [app.name])}
      note={tx('Kapatılan komut sohbette "bu komut şirketinizde kapalı" yanıtını verir; zamanlanmış mesajlar (karşılama, nabız, kutlama...) da gönderilmez.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button><Button disabled={save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}
    >
      <div className="grid gap-6 md:grid-cols-2">
        <section>
          <h3 className="mb-2 text-[13px] font-semibold">{tx('Komutlar ve özellikler')}</h3>
          {features.isPending ? <RowsSkeleton rows={6} /> : (
            <ul className="space-y-1.5 text-[13px]">
              {(features.data ?? []).map((f) => (
                <li key={f.key}>
                  <label className="flex items-center gap-2">
                    <Checkbox checked={!disabled.includes(f.key)} onCheckedChange={(v) => toggle(f.key, v === true)} />
                    <span>{f.label}</span>
                  </label>
                </li>
              ))}
            </ul>
          )}
        </section>
        <section className="space-y-4">
          <div>
            <h3 className="mb-2 flex items-center gap-1.5 text-[13px] font-semibold"><BarChart3 className="size-4" />{' '}{tx('Son 30 gün kullanım')}</h3>
            {stats.isPending ? <RowsSkeleton rows={4} /> : !stats.data || stats.data.total === 0 ? (
              <p className="text-[12.5px] text-muted-foreground">{tx('Henüz kullanım yok.')}</p>
            ) : (
              <>
                <p className="mb-2 text-[12px] text-muted-foreground">{tx('{0} işlem · {1} doğrulanmış kullanıcı. Yalnızca sayılar tutulur; kişi ya da mesaj içeriği saklanmaz.', [stats.data.total, stats.data.linkedUsers])}</p>
                <ul className="space-y-1">
                  {top.map((r) => (
                    <li key={r.feature + r.outcome} className="grid grid-cols-[1fr_auto] items-center gap-2 text-[12px]">
                      <div className="min-w-0">
                        <p className="truncate">{r.feature} <span className="text-muted-foreground">· {tx(OUTCOMES[r.outcome] ?? r.outcome)}</span></p>
                        <div className="mt-0.5 h-1.5 rounded-full bg-muted"><div className="h-1.5 rounded-full bg-primary" style={{ width: `${(100 * r.count) / max}%` }} /></div>
                      </div>
                      <span className="tabular-nums">{r.count}</span>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </div>
          <div>
            <h3 className="mb-2 flex items-center gap-1.5 text-[13px] font-semibold"><Activity className="size-4" />{' '}{tx('Son hatalar')}</h3>
            {(stats.data?.errors ?? []).length === 0 ? <p className="text-[12.5px] text-muted-foreground">{tx('Kayıtlı hata yok.')}</p> : (
              <ul className="space-y-1.5 text-[12px]">
                {stats.data!.errors.map((e, i) => (
                  <li key={i} className="rounded-lg bg-destructive/10 px-2 py-1.5">
                    <span className="text-muted-foreground">{formatRelativeToNow(e.at)} · {e.feature}</span>
                    <p className="break-words text-destructive">{e.message}</p>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </section>
      </div>
    </Modal>
  )
}

function PulseResult({ p }: { p: ChatPulse }) {
  if (p.hidden) return <p className="text-[12px] text-muted-foreground">{tx('{0} yanıt — sonuçlar en az {1} yanıtla gösterilir (anonimlik).', [p.responses, p.threshold])}</p>
  const total = (p.distribution ?? []).reduce((a, b) => a + b, 0) || 1
  return (
    <div className="space-y-1">
      <p className="text-[12px]">{tx('{0} yanıt · ortalama {1} / 5', [p.responses, p.average ?? '—'])}</p>
      <div className="flex h-2 overflow-hidden rounded-full bg-muted" role="img" aria-label={tx('Yanıt dağılımı')}>
        {(p.distribution ?? []).map((n, i) => (
          <div key={i} title={`${i + 1}: ${n}`} style={{ width: `${(100 * n) / total}%`, background: `hsl(${i * 30} 65% 45%)` }} />
        ))}
      </div>
    </div>
  )
}

/** B12: tek soruluk anonim nabız (bot, doğrulanmış çalışanlara 1–5 düğmeli DM gönderir). */
export function ChatPulsePanel() {
  const q = useQuery({ queryKey: ['chat-admin', 'pulses'], queryFn: ({ signal }) => chatbotApi.pulses(signal) })
  const [question, setQuestion] = useState('')
  const [sendAt, setSendAt] = useState('')
  const create = useAction(() => chatbotApi.createPulse({ question, sendAt: sendAt ? new Date(sendAt).toISOString() : null }), {
    success: tx('Nabız anketi planlandı'), invalidate: [['chat-admin', 'pulses']], onDone: () => { setQuestion(''); setSendAt('') },
  })
  const close = useAction((id: string) => chatbotApi.closePulse(id), { success: tx('Nabız kapatıldı'), invalidate: [['chat-admin', 'pulses']] })
  const run = useAction(() => chatbotApi.runJobs(), {
    success: (r) => tx('Zamanlanmış işler çalıştı: {0} mesaj', [Object.entries(r).filter(([k]) => k !== 'contextPurged').reduce((a, [, v]) => a + v, 0)]),
    invalidate: [['chat-admin', 'pulses']],
  })
  return (
    <Panel>
      <PanelHead
        title={tx('Nabız anketi (sohbet)')}
        note={tx('Yanıtlar kimliksiz saklanır (ad, departman, saat yok); yalnızca "yanıtladı" bilgisi ayrı tutulur. Sonuçlar en az 5 yanıtla görünür.')}
        action={<Button size="sm" variant="outline" onClick={() => run.mutate(undefined)} disabled={run.isPending}><Play className="size-4" />{' '}{tx('Zamanlanmış işleri şimdi çalıştır')}</Button>}
      />
      <PanelBody className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-[1fr_220px_auto] sm:items-end">
          <TextField label={tx('Soru (1–5 ölçek)')} value={question} onChange={(e) => setQuestion(e.target.value)} placeholder={tx('Bu hafta iş yükünüzü nasıl buluyorsunuz?')} maxLength={300} />
          <TextField label={tx('Gönderim (boşsa hemen)')} type="datetime-local" value={sendAt} onChange={(e) => setSendAt(e.target.value)} />
          <Button disabled={question.trim().length < 5 || create.isPending} onClick={() => create.mutate(undefined)}><Plus className="size-4" />{' '}{tx('Planla')}</Button>
        </div>
        <InfoNote>{tx('Bot, sessiz saatlerde gönderimi erteler ve kişi başına bir kez sorar. Katılım isteğe bağlıdır.')}</InfoNote>
        {q.isPending ? <RowsSkeleton rows={2} /> : (q.data ?? []).length === 0 ? (
          <EmptyState icon={BarChart3} title={tx('Nabız anketi yok')} detail={tx('Haftalık tek bir soru, ekip ruh halini anonim olarak izlemenin kolay yoludur.')} />
        ) : (
          <ul className="divide-y divide-border">
            {q.data!.map((p) => (
              <li key={p.id} className="grid gap-2 py-3 sm:grid-cols-[1fr_200px_auto] sm:items-center">
                <div className="min-w-0">
                  <p className="text-[13px] font-medium">{p.question}</p>
                  <p className="text-[12px] text-muted-foreground">{tx('Gönderim {0} · {1} kişiye gitti', [formatDateTime(p.sendAt), p.sentCount])}</p>
                </div>
                <PulseResult p={p} />
                <div className="flex items-center gap-2">
                  <StatusBadge tone={p.status === 'Closed' ? 'neutral' : p.status === 'Sent' ? 'success' : 'warning'}>
                    {p.status === 'Closed' ? tx('Kapandı') : p.status === 'Sent' ? tx('Gönderildi') : tx('Planlandı')}
                  </StatusBadge>
                  {p.status !== 'Closed' && <Button size="sm" variant="ghost" aria-label={tx('Kapat')} onClick={() => close.mutate(p.id)}><SquareX className="size-4" /></Button>}
                </div>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}
