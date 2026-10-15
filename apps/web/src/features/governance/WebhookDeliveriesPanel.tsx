/* Dalga 12 (madde 93): webhook teslimat hataları — başarısız teslimatlar, otomatik yeniden deneme
   planı (üstel geri çekilme), elle yeniden gönderim ve sıradaki denemeyi durdurma. Yük (payload)
   gösterilmez; yeniden gönderim olay kaydından (30 gün) yapılır. */
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, RotateCcw, Send, XCircle } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { SelectField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { governanceApi, type WebhookDeliveryRow } from '@/api/governance'
import { formatDateTime, formatNumber, formatRelativeToNow } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { formatBackoff, retryStateView } from './integrationsHelpers'

const STATES = ['failed', 'pending', 'gave_up', 'all'] as const

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'danger' | 'warning' }) {
  return (
    <div className="surface rounded-2xl border border-border p-4">
      <p className="text-[12px] text-muted-foreground">{label}</p>
      <p className={`tabular mt-1 text-[22px] font-semibold ${tone === 'danger' && value > 0 ? 'text-destructive' : tone === 'warning' && value > 0 ? 'text-amber-600 dark:text-amber-400' : ''}`}>{formatNumber(value)}</p>
    </div>
  )
}

export function WebhookDeliveriesPanel() {
  const [state, setState] = useState<(typeof STATES)[number]>('failed')
  const [webhookId, setWebhookId] = useState('')
  const hooks = useQuery({ queryKey: ['webhooks'], queryFn: ({ signal }) => governanceApi.webhooks(signal) })
  const list = useQuery({
    queryKey: ['webhook-deliveries-all', state, webhookId],
    queryFn: ({ signal }) => governanceApi.allWebhookDeliveries({ state, webhookId }, signal),
    refetchInterval: 15000,
  })
  const resend = useAction((id: string) => governanceApi.resendWebhookDelivery(id), {
    success: (r) => (r.ok ? tx('Yeniden gönderildi (HTTP {0})', [r.statusCode]) : tx('Yeniden gönderim başarısız ({0})', [r.error ?? '—'])),
    invalidate: [['webhook-deliveries-all'], ['webhooks']],
  })
  const cancel = useAction((id: string) => governanceApi.cancelWebhookRetry(id), { success: tx('Otomatik deneme durduruldu'), invalidate: [['webhook-deliveries-all']] })
  const confirm = useConfirm()
  const askResend = async (d: WebhookDeliveryRow) => {
    if (await confirm({
      title: tx('“{0}” olayı yeniden gönderilsin mi?', [d.eventType]),
      note: tx('Olay “{0}” adresine aynı olay kimliğiyle (X-HR360-Delivery) yeniden gönderilir; alıcı tekrarı bu kimlikle ayıklayabilir.', [d.webhookUrl ?? '—']),
      action: tx('Yeniden gönder'),
      destructive: false,
    })) resend.mutate(d.id)
  }
  const askCancel = async (d: WebhookDeliveryRow) => {
    if (await confirm({ title: tx('Otomatik yeniden deneme durdurulsun mu?'), note: tx('Bu teslimat bir daha otomatik denenmez; gerekirse elle yeniden gönderebilirsiniz.'), action: tx('Durdur') })) cancel.mutate(d.id)
  }
  const data = list.data
  const schedule = data?.retry.scheduleSeconds ?? []
  return (
    <div className="space-y-5">
      <InfoNote>
        {tx('Geçici hatalar (bağlantı hatası, zaman aşımı, HTTP 408/429/5xx) otomatik olarak yeniden denenir. İlk gönderim dahil en çok {0} deneme yapılır; denemeler arasındaki bekleme:', [data?.retry.maxAttempts ?? 6])}{' '}
        <b>{schedule.map(formatBackoff).join(' → ') || '—'}</b>.{' '}
        {tx('Diğer 4xx yanıtları ve KVKK aktarım kilidi yeniden denenmez. Olay kayıtları 30 gün saklandığından daha eski teslimatlar yeniden gönderilemez.')}
      </InfoNote>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label={tx('Son 24 saat teslimat')} value={data?.summary.total24h ?? 0} />
        <Stat label={tx('Son 24 saat hata')} value={data?.summary.failed24h ?? 0} tone="danger" />
        <Stat label={tx('Sıradaki otomatik deneme')} value={data?.summary.pendingRetries ?? 0} tone="warning" />
        <Stat label={tx('Vazgeçilen (7 gün)')} value={data?.summary.gaveUp7d ?? 0} tone="danger" />
      </div>
      <Panel>
        <PanelHead title={tx('Teslimatlar')} note={tx('15 sn\'de bir yenilenir')} />
        <PanelBody className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <SelectField label={tx('Durum')} value={state} onChange={(v) => setState(v as (typeof STATES)[number])} options={[
              { value: 'failed', label: tx('Başarısız') },
              { value: 'pending', label: tx('Yeniden denenecek') },
              { value: 'gave_up', label: tx('Vazgeçilen') },
              { value: 'all', label: tx('Tümü') },
            ]} />
            <SelectField label={tx('Webhook')} value={webhookId || 'all'} onChange={(v) => setWebhookId(v === 'all' ? '' : v)} options={[
              { value: 'all', label: tx('Tümü') },
              ...(hooks.data ?? []).map((w) => ({ value: w.id, label: w.name })),
            ]} />
          </div>
          {list.isPending ? <RowsSkeleton /> : (data?.items ?? []).length === 0 ? (
            <EmptyState icon={Send} title={tx('Kayıt yok')} detail={state === 'failed' ? tx('Başarısız teslimat bulunmuyor.') : tx('Bu filtreye uyan teslimat yok.')} />
          ) : (
            <ul className="divide-y divide-border text-[12.5px]" data-testid="webhook-delivery-list">
              {data!.items.map((d) => {
                const rs = retryStateView(d.retryState)
                return (
                  <li key={d.id} className="flex flex-wrap items-center gap-x-3 gap-y-1.5 py-2.5">
                    <span className="tabular w-36 text-muted-foreground">{formatDateTime(d.occurredAt)}</span>
                    <span className="min-w-0 max-w-48 truncate font-medium" title={d.webhookUrl ?? ''}>{d.webhookName ?? tx('(silinmiş webhook)')}</span>
                    <span className="font-mono">{d.eventType}</span>
                    <StatusBadge tone={d.error ? 'danger' : 'success'}>{d.statusCode ?? tx('hata')}</StatusBadge>
                    <span className="text-muted-foreground">{tx('deneme {0}', [d.attempt ?? 1])}{d.manual ? tx(' · elle ({0})', [d.triggeredByName ?? '—']) : ''}</span>
                    {rs && <StatusBadge tone={rs.tone}>{rs.label}</StatusBadge>}
                    {d.retryState === 'pending' && d.nextRetryAt && <span className="text-muted-foreground">{tx('sonraki deneme {0}', [formatRelativeToNow(d.nextRetryAt)])}</span>}
                    {d.error && <span className="flex min-w-0 flex-1 items-center gap-1 truncate text-destructive"><AlertTriangle className="size-3.5 shrink-0" />{d.error}</span>}
                    <span className="ml-auto flex gap-1.5">
                      {d.error && (
                        <Button size="sm" variant="outline" disabled={!d.canResend || resend.isPending} title={d.canResend ? undefined : resendBlockedReason(d)} onClick={() => askResend(d)}>
                          <RotateCcw className="size-4" />{' '}{tx('Yeniden gönder')}
                        </Button>
                      )}
                      {d.retryState === 'pending' && <Button size="sm" variant="ghost" onClick={() => askCancel(d)} aria-label={tx('Otomatik denemeyi durdur')} title={tx('Otomatik denemeyi durdur')}><XCircle className="size-4" /></Button>}
                    </span>
                  </li>
                )
              })}
            </ul>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}

/** Yeniden gönderilemeyen teslimatın gerçek nedeni (sunucudaki canResend kuralıyla aynı sıra). */
function resendBlockedReason(d: { webhookName?: string | null; error?: string | null }): string {
  if (!d.webhookName) return tx('Webhook silinmiş; bu teslimat yeniden gönderilemez.')
  if (d.error === 'transfer_basis_required') return tx('KVKK aktarım dayanağı tanımlanmadan bu hedefe gönderim yapılamaz.')
  return tx('Olay kaydının saklama süresi (30 gün) dolduğu için yeniden gönderilemez.')
}
