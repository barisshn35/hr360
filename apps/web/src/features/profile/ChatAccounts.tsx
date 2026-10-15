import { useQuery } from '@tanstack/react-query'
import { useSearchParams, Link } from 'react-router-dom'
import { CheckCircle2, MessageSquare, ShieldAlert } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, RowsSkeleton } from '@/components/ui/States'
import { governanceApi } from '@/api/governance'
import { formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/** Profilim › Güvenlik: bağlı Slack / Teams hesapları. */
export function MyChatAccounts() {
  const q = useQuery({ queryKey: ['chat-link', 'mine'], queryFn: ({ signal }) => governanceApi.myChatIdentities(signal) })
  const unlink = useAction((id: string) => governanceApi.unlinkMyChatIdentity(id), { success: tx('Sohbet hesabının bağı kaldırıldı.'), invalidate: [['chat-link']] })
  return (
    <Panel className="max-w-3xl">
      <PanelHead title={<span className="flex items-center gap-2"><MessageSquare className="size-4 text-primary" />{' '}{tx('Bağlı sohbet hesapları')}</span>}
        note={tx('Slack ya da Teams botu yalnızca burada bağlı ve doğrulanmış hesaplara talep içeriği gönderir. Bağlamak için botta "ben" yazın ve gelen bağlantıyı açın.')} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-4"><RowsSkeleton rows={2} /></div> : !q.data?.length ? (
          <p className="p-5 text-[13px] text-muted-foreground">{tx('Bağlı sohbet hesabınız yok.')}</p>
        ) : (
          <ul className="divide-y divide-border">
            {q.data.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <div className="min-w-0 flex-1">
                  <p className="font-medium">{i.platform} · {i.appName ?? '—'}</p>
                  <p className="text-[12px] text-muted-foreground">{i.displayName ?? i.email ?? '—'}{i.verifiedAt ? ' · ' + tx('doğrulandı {0}', [formatDateTime(i.verifiedAt)]) : ''}</p>
                </div>
                {i.verifiedAt ? <StatusBadge tone="success">{tx('Doğrulandı')}</StatusBadge> : <StatusBadge tone="warning">{tx('Doğrulanmadı')}</StatusBadge>}
                <Button size="sm" variant="outline" onClick={() => unlink.mutate(i.id)}>{tx('Bağı kaldır')}</Button>
              </li>
            ))}
          </ul>
        )}
        <p className="border-t border-border px-5 py-3 text-[12px] text-muted-foreground">
          <Link className="text-primary underline" to="/panel/sohbet-onay">{tx('Sohbet doğrulama kodları ve kutlama tercihleri')}</Link>
        </p>
      </PanelBody>
    </Panel>
  )
}

/** /panel/sohbet-bagla?kod=...: botun gönderdiği bağlantı. Kişi giriş yapmış olarak onaylar. */
export function ChatLinkPage() {
  const [params] = useSearchParams()
  const code = params.get('kod') ?? ''
  const q = useQuery({ queryKey: ['chat-link', 'preview', code], queryFn: ({ signal }) => governanceApi.chatLinkPreview(code, signal), enabled: !!code, retry: false })
  const link = useAction(() => governanceApi.chatLink(code), { invalidate: [['chat-link']] })
  return (
    <>
      <PageHeader title={tx('Sohbet hesabını bağla')} description={tx('Slack ya da Microsoft Teams hesabınızı HR360 hesabınıza bağlayın; onay talepleri ve sorgular sohbete gelsin.')} />
      <Panel className="max-w-xl">
        <PanelBody className="space-y-4">
          {!code ? <ErrorState title={tx('Bağlantı eksik')} message={tx('Botta "ben" yazarak yeni bir bağlantı alın.')} />
            : q.isPending ? <RowsSkeleton rows={2} />
              : q.isError ? <ErrorState title={tx('Bağlantı geçersiz')} message={errMsg(q.error)} />
                : link.isSuccess ? (
                  <div className="flex items-start gap-3">
                    <CheckCircle2 className="mt-0.5 size-5 text-[hsl(var(--success))]" aria-hidden />
                    <div>
                      <p className="text-[14px] font-medium">{tx('Hesabınız bağlandı.')}</p>
                      <p className="text-[13px] text-muted-foreground">{tx('Sohbete dönebilirsiniz; onay talepleriniz artık oraya gelecek.')}</p>
                      <Button asChild variant="outline" size="sm" className="mt-3"><Link to="/panel/profil?sekme=guvenlik">{tx('Bağlı hesaplarım')}</Link></Button>
                    </div>
                  </div>
                ) : (
                  <>
                    <dl className="grid gap-2 text-[13px] sm:grid-cols-[140px_1fr]">
                      <dt className="text-muted-foreground">{tx('Platform')}</dt><dd>{q.data!.platform} · {q.data!.appName ?? '—'}</dd>
                      <dt className="text-muted-foreground">{tx('Sohbet hesabı')}</dt><dd>{q.data!.displayName ?? '—'}{q.data!.email ? ` (${q.data!.email})` : ''}</dd>
                      <dt className="text-muted-foreground">{tx('HR360 hesabınız')}</dt><dd>{q.data!.myName ?? '—'}</dd>
                    </dl>
                    {!q.data!.emailMatches ? (
                      <div role="alert" className="flex items-start gap-2 rounded-xl border border-destructive/40 bg-destructive/10 p-3 text-[13px]">
                        <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
                        <span>{tx('Bu sohbet hesabının e-postası HR360 e-postanızla aynı değil; size bağlanamaz. Bağlantıyı size başkası gönderdiyse kullanmayın.')}</span>
                      </div>
                    ) : (
                      <>
                        <p className="text-[12.5px] text-muted-foreground">{tx('Yalnızca bu sohbet hesabı size aitse onaylayın. Bağladıktan sonra bot size onay talepleri ve izin bakiyesi gibi bilgiler gönderir.')}</p>
                        <Button onClick={() => link.mutate(undefined)} disabled={link.isPending}>{tx('Bu hesap benim, bağla')}</Button>
                      </>
                    )}
                  </>
                )}
        </PanelBody>
      </Panel>
    </>
  )
}
