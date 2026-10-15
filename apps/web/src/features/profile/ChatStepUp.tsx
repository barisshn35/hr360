import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { CheckCircle2, KeyRound, ShieldCheck } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { chatbotApi } from '@/api/chatbot'
import { formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * /panel/sohbet-onay?kod=... — BG13: sohbetten istenen hassas işlemin (bordro özeti, toplu ya da
 * ücretle ilgili onay) HR360'ta oturum açık olarak doğrulanması. Kodsuz açılırsa sohbete yazılacak
 * 6 haneli kodlar gösterilir. Sonuç sohbete gönderilir; burada içerik gösterilmez.
 */
export function ChatStepUpPage() {
  const [params] = useSearchParams()
  const code = params.get('kod') ?? ''
  // Sohbet hesapları şirkete bağlıdır: kiracısız oturumda (platform yöneticisi) istek atılmaz.
  const { tenantSlug } = useAuth()
  const hasTenant = Boolean(tenantSlug)
  const q = useQuery({ queryKey: ['chat-stepup', code], queryFn: ({ signal }) => chatbotApi.stepUpPreview(code, signal), enabled: hasTenant && !!code, retry: false })
  const otps = useQuery({ queryKey: ['chat-stepup', 'otp'], queryFn: ({ signal }) => chatbotApi.stepUpOtps(signal), enabled: hasTenant && !code, refetchOnWindowFocus: false })
  const confirm = useAction(() => chatbotApi.stepUpConfirm(code), { invalidate: [['chat-stepup']] })
  return (
    <>
      <PageHeader title={tx('Sohbet doğrulaması')} description={tx('Sohbet botundan istenen hassas bir işlemi burada onaylayın. Sonuç sohbete gönderilir.')} />
      <div className="max-w-xl space-y-4">
        {!hasTenant ? (
          <EmptyState icon={ShieldCheck} title={tx('Sohbet doğrulaması şirket hesabıyla kullanılır')}
            detail={tx('Sohbet botu bir şirketin çalışanları içindir. Hesabınız bir şirkete bağlı olmadığından bekleyen doğrulama ya da kutlama tercihi yok.')} />
        ) : code ? (
          <Panel>
            <PanelBody className="space-y-4">
              {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <ErrorState title={tx('Doğrulama geçersiz')} message={errMsg(q.error)} />
                : confirm.isSuccess ? (
                  <div className="flex items-start gap-3">
                    <CheckCircle2 className="mt-0.5 size-5 text-[hsl(var(--success))]" aria-hidden />
                    <div>
                      <p className="text-[14px] font-medium">{tx('Doğrulandı.')}</p>
                      <p className="text-[13px] text-muted-foreground">{tx('İşlem yapıldı; sonucu sohbette görebilirsiniz.')}</p>
                    </div>
                  </div>
                ) : (
                  <>
                    <div className="flex items-start gap-3">
                      <ShieldCheck className="mt-0.5 size-5 text-primary" aria-hidden />
                      <dl className="grid flex-1 gap-2 text-[13px] sm:grid-cols-[120px_1fr]">
                        <dt className="text-muted-foreground">{tx('İşlem')}</dt><dd>{q.data!.label}</dd>
                        <dt className="text-muted-foreground">{tx('Ayrıntı')}</dt><dd>{q.data!.summary ?? '—'}</dd>
                        <dt className="text-muted-foreground">{tx('Geçerlilik')}</dt><dd>{formatDateTime(q.data!.expiresAt)}</dd>
                      </dl>
                    </div>
                    <p className="text-[12.5px] text-muted-foreground">{tx('Bu isteği siz başlatmadıysanız onaylamayın; bağlantının süresi kendiliğinden dolar.')}</p>
                    <Button onClick={() => confirm.mutate(undefined)} disabled={confirm.isPending}>{tx('Evet, ben istedim — onayla')}</Button>
                  </>
                )}
            </PanelBody>
          </Panel>
        ) : (
          <Panel>
            <PanelHead title={<span className="flex items-center gap-2"><KeyRound className="size-4 text-primary" />{' '}{tx('Sohbete yazılacak kodlar')}</span>}
              note={tx('Bağlantıyı açamıyorsanız bu kodu sohbete "kod 123456" biçiminde yazın. Kod 5 dakika geçerlidir.')} />
            <PanelBody>
              {otps.isPending ? <RowsSkeleton rows={2} /> : (otps.data ?? []).length === 0 ? (
                <p className="text-[13px] text-muted-foreground">{tx('Bekleyen doğrulamanız yok.')}</p>
              ) : (
                <ul className="divide-y divide-border">
                  {otps.data!.map((o) => (
                    <li key={o.id} className="flex items-center justify-between gap-3 py-3 text-[13px]">
                      <span className="min-w-0">{o.summary}</span>
                      <code className="rounded-lg bg-muted px-3 py-1.5 font-mono text-[18px] tracking-[0.3em]" aria-label={tx('Doğrulama kodu')}>{o.otp}</code>
                    </li>
                  ))}
                </ul>
              )}
            </PanelBody>
          </Panel>
        )}
        {hasTenant && <ChatOptInsPanel />}
      </div>
    </>
  )
}

/** B11: iş yıldönümü kutlaması için açık izin (doğum günü profildeki "doğum günümü göster" ayarıdır). */
export function ChatOptInsPanel() {
  const q = useQuery({ queryKey: ['chat-optins'], queryFn: ({ signal }) => chatbotApi.optIns(signal) })
  const save = useAction((v: boolean) => chatbotApi.setAnniversary(v), { success: tx('Tercih kaydedildi'), invalidate: [['chat-optins']] })
  if (!q.data?.linked) return null
  return (
    <Panel>
      <PanelHead title={tx('Kutlama tercihleri')} note={tx('Sohbet kanalındaki kutlamalar yalnızca izin verenler için yazılır; yaş ya da doğum yılı asla paylaşılmaz.')} />
      <PanelBody className="space-y-3 text-[13px]">
        <label className="flex items-center gap-2">
          <Checkbox checked={q.data.showAnniversary} disabled={save.isPending} onCheckedChange={(v) => save.mutate(v === true)} />
          {tx('İş yıldönümüm şirket kanalında kutlansın')}
        </label>
        <InfoNote>{q.data.showBirthday ? tx('Doğum gününüz profilinizdeki tercihe göre kutlanıyor (yalnızca gün ve ay).') : tx('Doğum gününüz kutlanmıyor; profilinizden "doğum günümü göster" seçeneğiyle açabilirsiniz.')}</InfoNote>
      </PanelBody>
    </Panel>
  )
}
