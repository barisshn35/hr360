import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { Ban, KeyRound, LoaderCircle, MailCheck, RefreshCw } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { StatusBadge, type StatusTone } from '@/components/ui/status-badge'
import { RowsSkeleton, EmptyState, ErrorState } from '@/components/ui/states'
import { useToast } from '@/components/ui/toast'
import { useConfirm } from '@/components/ui/confirm'
import { EASE } from '@/motion/primitives'
import {
  passwordLinkApi, passwordLinkKeys, usePasswordLinks, usePasswordMethod,
  type LinkStatus, type PasswordLinkRow, type PasswordMethod,
} from '@/api/password-links'
import { formatDateTime } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { usePasswordLinkDialog } from './PasswordLinkCard'

const STATUS: Record<LinkStatus, { label: string; tone: StatusTone }> = {
  active: { label: tx('Açık'), tone: 'info' },
  pending: { label: tx('Link bekliyor'), tone: 'warning' },
  used: { label: tx('Kullanıldı'), tone: 'success' },
  expired: { label: tx('Süresi doldu'), tone: 'neutral' },
  revoked: { label: tx('İptal edildi'), tone: 'neutral' },
  invalid: { label: tx('Geçersiz'), tone: 'neutral' },
}

function channelText(r: PasswordLinkRow) {
  if (r.channel === 'keycloak') return tx('Keycloak e-postası')
  if (r.emailStatus === 'queued') return r.channel === 'link' ? tx('Link + e-posta') : tx('E-posta')
  if (r.emailStatus === 'failed') return tx('Link (e-posta gönderilemedi)')
  return r.channel === 'email' ? tx('E-posta') : tx('Link')
}

/**
 * Davetler ve parola linkleri: son 30 gün ve hâlâ açık olanlar. Link burada gösterilmez (yalnızca
 * üretildiği anda); "Yeniden üret" yeni link verir ve eskisini iptal eder.
 */
export function PasswordLinksPanel() {
  const reduced = useReducedMotion()
  const toast = useToast()
  const confirm = useConfirm()
  const dialog = usePasswordLinkDialog()
  const qc = useQueryClient()
  const list = usePasswordLinks()
  const [filter, setFilter] = useState<'open' | 'all'>('open')
  const refresh = () => void qc.invalidateQueries({ queryKey: passwordLinkKeys.list })

  const revoke = useMutation({
    mutationFn: (id: string) => passwordLinkApi.revoke(id),
    onSuccess: () => { refresh(); toast.ok(tx('Link iptal edildi.')) },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Link iptal edilemedi.')),
  })
  const regenerate = useMutation({
    mutationFn: (r: PasswordLinkRow) => passwordLinkApi.regenerate(r.id).then((v) => ({ v, r })),
    onSuccess: ({ v, r }) => { refresh(); dialog.show(v, r.fullName ?? r.email) },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Link üretilemedi.')),
  })

  const rows = useMemo(() => {
    const all = list.data ?? []
    return filter === 'open' ? all.filter((r) => r.status === 'active' || r.status === 'pending') : all
  }, [list.data, filter])

  return (
    <Panel>
      <PanelHead
        title={tx('Davetler ve parola linkleri')}
        note={tx('Süreli, tek kullanımlık linkler. Link yalnızca üretildiği anda gösterilir.')}
        action={
          <div className="flex rounded-md border border-border p-0.5 text-[12px]" role="tablist">
            {(['open', 'all'] as const).map((f) => (
              <button key={f} type="button" role="tab" aria-selected={filter === f} onClick={() => setFilter(f)}
                className={cn('relative cursor-pointer rounded px-2.5 py-1 transition-colors', filter === f ? 'text-foreground' : 'text-muted-foreground hover:text-foreground')}>
                {filter === f && (
                  <motion.span layoutId="links-filter" className="absolute inset-0 rounded bg-muted" transition={{ type: 'spring', stiffness: 400, damping: 32 }} />
                )}
                <span className="relative">{f === 'open' ? tx('Açık olanlar') : tx('Son 30 gün')}</span>
              </button>
            ))}
          </div>
        }
      />
      <PanelBody>
        {list.isPending ? <RowsSkeleton rows={3} columns={3} /> : list.isError ? (
          <ErrorState message={list.error instanceof Error ? list.error.message : undefined} onRetry={() => void list.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyState icons={[MailCheck, KeyRound, RefreshCw]} title={filter === 'open' ? tx('Açık link yok') : tx('Son 30 günde link yok')}
            detail={tx('Çalışana giriş erişimi verdiğinizde ya da parola sıfırlama linki ürettiğinizde burada görünür.')} />
        ) : (
          <ul className="divide-y divide-border" data-testid="password-links">
            <AnimatePresence initial={false}>
              {rows.map((r) => {
                const s = STATUS[r.status] ?? STATUS.invalid
                const open = r.status === 'active' || r.status === 'pending'
                return (
                  <motion.li key={r.id} layout={!reduced}
                    initial={reduced ? false : { opacity: 0, x: -12 }} animate={{ opacity: 1, x: 0 }}
                    exit={reduced ? { opacity: 0 } : { opacity: 0, x: 24, height: 0, paddingTop: 0, paddingBottom: 0 }}
                    transition={{ duration: 0.35, ease: EASE }}
                    className="flex flex-wrap items-center gap-x-4 gap-y-2 py-3">
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[13px] font-medium">{r.fullName || r.email}</p>
                      <p className="truncate text-[12px] text-muted-foreground">
                        {r.fullName ? `${r.email} · ` : ''}
                        {r.purpose === 'reset' ? tx('Sıfırlama') : tx('İlk parola')} · {channelText(r)}
                      </p>
                      <p className="text-[11.5px] text-muted-foreground">
                        {r.usedAt ? tx('Kullanıldı: {0}', [formatDateTime(r.usedAt)])
                          : r.revokedAt ? tx('İptal: {0}', [formatDateTime(r.revokedAt)])
                          : tx('Son kullanma: {0}', [formatDateTime(r.expiresAt)])}
                        {r.createdByName ? ` · ${tx('üreten: {0}', [r.createdByName])}` : ''}
                      </p>
                    </div>
                    <StatusBadge tone={s.tone}>{s.label}</StatusBadge>
                    <div className="flex gap-1.5">
                      <Button size="sm" variant="outline" disabled={regenerate.isPending}
                        onClick={async () => {
                          if (open && !(await confirm({ title: tx('Yeni link üretilsin mi?'), note: tx('Açık link iptal edilir; yeni link yalnızca bir kez gösterilir.'), action: tx('Yeni link üret'), destructive: false }))) return
                          regenerate.mutate(r)
                        }}>
                        {regenerate.isPending && regenerate.variables?.id === r.id ? <LoaderCircle className="size-3.5 animate-spin" /> : <RefreshCw className="size-3.5" />}
                        {open ? tx('Yeniden üret') : tx('Yeni link')}
                      </Button>
                      {open && r.channel !== 'keycloak' && (
                        <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" disabled={revoke.isPending}
                          onClick={async () => {
                            if (await confirm({ title: tx('Link iptal edilsin mi?'), note: tx('{0} artık bu linkle parola belirleyemez.', [r.fullName || r.email]), action: tx('İptal et'), destructive: true }))
                              revoke.mutate(r.id)
                          }}>
                          <Ban className="size-3.5" />{tx('İptal et')}
                        </Button>
                      )}
                    </div>
                  </motion.li>
                )
              })}
            </AnimatePresence>
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

/** Şirket ayarı: parola belirleme yöntemi (varsayılan: süreli davet linki). */
export function PasswordMethodPanel({ canEdit }: { canEdit: boolean }) {
  const reduced = useReducedMotion()
  const toast = useToast()
  const qc = useQueryClient()
  const q = usePasswordMethod()
  const save = useMutation({
    mutationFn: (m: PasswordMethod | null) => passwordLinkApi.setMethod(m),
    onSuccess: (v) => { qc.setQueryData(passwordLinkKeys.method, v); toast.ok(tx('Parola belirleme yöntemi kaydedildi.')) },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kaydedilemedi.')),
  })
  const d = q.data

  const options: Array<{ value: PasswordMethod; title: string; detail: string; disabled?: boolean }> = [
    {
      value: 'invite',
      title: tx('Süreli davet linki (önerilen)'),
      detail: tx('Tek kullanımlık link size gösterilir (kopyala / QR); e-posta açıksa e-postayla da gider. Süre: {0} saat.', [d?.inviteTtlHours ?? 72]),
    },
    {
      value: 'keycloak',
      title: tx('Keycloak e-postası (eski yöntem)'),
      detail: d?.keycloakAvailable
        ? tx('Kimlik sunucusu parola bağlantısını kendisi e-postayla gönderir; link size gösterilmez. Gönderilemezse davet linkine düşülür.')
        : tx('Platform e-posta sunucusu ayarlanınca seçilebilir.'),
      disabled: !d?.keycloakAvailable,
    },
  ]

  return (
    <Panel>
      <PanelHead title={tx('Parola belirleme yöntemi')} note={tx('Yeni hesaplar ve parola sıfırlama için')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} columns={1} /> : q.isError ? <ErrorState message={q.error instanceof Error ? q.error.message : undefined} onRetry={() => void q.refetch()} /> : (
          <>
            <div role="radiogroup" aria-label={tx('Parola belirleme yöntemi')} className="grid gap-2.5 sm:grid-cols-2">
              {options.map((o) => {
                const active = d!.selected === o.value
                return (
                  <button key={o.value} type="button" role="radio" aria-checked={active} disabled={!canEdit || o.disabled || save.isPending}
                    onClick={() => !active && save.mutate(o.value)}
                    className={cn('relative cursor-pointer rounded-xl border p-3.5 text-left transition-colors disabled:cursor-not-allowed',
                      active ? 'border-primary/50 bg-primary/[0.05]' : 'border-border hover:bg-muted/40', o.disabled && 'opacity-60')}>
                    {active && (
                      <motion.span layoutId="pw-method" className="absolute inset-0 rounded-xl ring-2 ring-primary/40"
                        transition={reduced ? { duration: 0 } : { type: 'spring', stiffness: 380, damping: 30 }} />
                    )}
                    <span className="relative block text-[13px] font-medium">{o.title}</span>
                    <span className="relative mt-1 block text-[12px] leading-relaxed text-muted-foreground">{o.detail}</span>
                  </button>
                )
              })}
            </div>
            <AnimatePresence>
              {d!.selected !== d!.effective && (
                <motion.p initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }}
                  className="text-[12px] text-[hsl(var(--warning))]">
                  {tx('E-posta sunucusu şu an ayarlı değil; davet linki kullanılıyor.')}
                </motion.p>
              )}
            </AnimatePresence>
            <p className="text-[12px] text-muted-foreground">
              {d!.choice === null
                ? tx('Platform varsayılanı izleniyor.')
                : canEdit && (
                  <button type="button" className="cursor-pointer underline underline-offset-2 hover:text-foreground" onClick={() => save.mutate(null)}>
                    {tx('Platform varsayılanına dön')}
                  </button>
                )}
            </p>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}
