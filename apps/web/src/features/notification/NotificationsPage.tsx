import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { motion, useReducedMotion } from 'motion/react'
import { LoaderCircle, Lock, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { NotificationStatusBadge } from '@/components/ui/ModuleBadges'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { notificationApi } from '@/api/notification'
import {
  useMyEmployeeId,
  useNotificationTemplates,
  useNotifications,
  useUnreadCount,
} from '@/api/queries'
import {
  notificationChannelLabels,
  notificationStatusLabels,
  type NotificationChannel,
  type NotificationStatus,
  type NotificationTemplate,
} from '@/api/types'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

type TabKey = 'gelen' | 'sablonlar'

const ALL = '__all__'

function NewTemplateModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [code, setCode] = useState('')
  const [channel, setChannel] = useState<NotificationChannel>('InApp')
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      notificationApi.createTemplate({
        code: code.trim(),
        channel,
        subjectTemplate: subject.trim(),
        bodyTemplate: body.trim(),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['notification'] })
      toast.ok(tx('Şablon eklendi'))
      onClose()
      setCode('')
      setSubject('')
      setBody('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Şablon eklenemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!code.trim()) return setError(tx('Şablon kodu zorunlu.'))
    if (!subject.trim()) return setError(tx('Konu zorunlu.'))
    if (body.trim().length < 5) return setError(tx('Gövde en az 5 karakter olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni bildirim şablonu')}
      note={tx('Servisler bildirimi bu kodla çağırır.')}
      size="lg"
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="new-template"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Şablonu ekle')}
          </Button>
        </>
      }
    >
      <form id="new-template" onSubmit={submit} noValidate className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="tpl-code"
            label={tx('Kod')}
            required
            hint={tx('Örn. leave.approved')}
            className="font-mono"
            value={code}
            onChange={(e) => setCode(e.target.value)}
            error={error?.includes('kodu') ? error : undefined}
          />
          <SelectField
            id="tpl-channel"
            label={tx('Kanal')}
            value={channel}
            onChange={(v) => setChannel(v as NotificationChannel)}
            options={(Object.keys(notificationChannelLabels) as NotificationChannel[]).map((c) => ({
              value: c,
              label: notificationChannelLabels[c],
            }))}
          />
        </div>
        <TextField
          id="tpl-subject"
          label={tx('Konu')}
          required
          value={subject}
          onChange={(e) => setSubject(e.target.value)}
          error={error?.includes('Konu') ? error : undefined}
        />
        <TextAreaField
          id="tpl-body"
          label={tx('Gövde')}
          rows={5}
          required
          hint={tx('Değişkenler için {{ad}} biçimi kullanılabilir.')}
          value={body}
          onChange={(e) => setBody(e.target.value)}
          error={error?.includes('Gövde') ? error : undefined}
        />
      </form>
    </Modal>
  )
}

function DeleteTemplateConfirm({
  template,
  onClose,
}: {
  template: NotificationTemplate | null
  onClose: () => void
}) {
  const toast = useToast()
  const queryClient = useQueryClient()

  const mutation = useMutation({
    mutationFn: () => notificationApi.deleteTemplate(template!.id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['notification'] })
      toast.ok(tx('Şablon silindi'))
      onClose()
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Şablon silinemedi.')),
  })

  if (!template) return null

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Şablonu sil')}
      note={template.code}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            variant="destructive"
            className="cursor-pointer"
            disabled={mutation.isPending}
            onClick={() => mutation.mutate()}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Sil')}
          </Button>
        </>
      }
    >
      <p className="text-[14px] leading-relaxed text-muted-foreground">
        {tx('Bu şablonu kullanan servisler bildirim üretemez hale gelir. İşlem geri alınamaz.')}
      </p>
    </Modal>
  )
}

export function NotificationsPage() {
  const { user, roles } = useAuth()
  const [tab, setTab] = useTabParam<TabKey>('gorunum', 'gelen')
  const [status, setStatus] = useState<string>(ALL)
  const [templateModal, setTemplateModal] = useState(false)
  const [toDelete, setToDelete] = useState<NotificationTemplate | null>(null)
  const reduced = useReducedMotion()
  const queryClient = useQueryClient()

  // Şablon yönetimi backend'de yalnızca İK'ya açık (RequireHrAdmin). Yöneticiler
  // 'notification:manage' (bildirim gönderme) iznine sahip olduğu için sekmeyi
  // görüyor ama 403 alıyordu; sekme artık İK rollerine göre gösterilir.
  const canManage = roles.some((r) =>
    r === 'hr-admin' || r === 'tenant-admin' || r === 'platform-admin' || (r as string) === 'ext-notification-manage')
  // NOT: `user.id` Keycloak `sub` claim'i, Employee.Id DEĞİL - bildirimler
  // backend'de RecipientEmployeeId ile yazılır. `useMyEmployeeId` ile gerçek
  // çalışan kimliği çözülür (hardcore test, 3. tur - bkz. NotificationBell.tsx).
  const { employeeId, notLinked } = useMyEmployeeId(Boolean(user))
  const recipientId = employeeId

  const notifications = useNotifications(
    {
      recipientId,
      status: status === ALL ? undefined : (status as NotificationStatus),
      limit: 100,
    },
    Boolean(recipientId),
  )
  const unread = useUnreadCount(recipientId)
  const templates = useNotificationTemplates(tab === 'sablonlar' && canManage)

  const markRead = useMutation({
    mutationFn: (id: string) => notificationApi.markRead(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['notification'] }),
  })

  const unreadItems = (notifications.data ?? []).filter((n) => n.status !== 'Read')

  const markAllRead = useMutation({
    mutationFn: async () => {
      for (const n of unreadItems) await notificationApi.markRead(n.id)
    },
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['notification'] }),
  })

  const tabs: Array<TabDef<TabKey>> = [
    { key: 'gelen', label: tx('Bildirimlerim'), count: unread.data?.unreadCount },
    ...(canManage ? [{ key: 'sablonlar' as TabKey, label: tx('Şablonlar') }] : []),
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Bildirimler')}
        description={canManage ? tx('Size gönderilen bildirimler ve İK yönetimi için bildirim şablonları.') : tx('Size gönderilen bildirimler.')}
        actions={
          tab === 'gelen'
            ? unreadItems.length > 0 && (
                <Button
                  variant="outline"
                  className="cursor-pointer"
                  disabled={markAllRead.isPending}
                  onClick={() => markAllRead.mutate()}
                >
                  {markAllRead.isPending && <LoaderCircle className="size-4 animate-spin" />}
                  {tx('Tümünü okundu işaretle')}
                </Button>
              )
            : canManage && (
                <Button className="cursor-pointer" onClick={() => setTemplateModal(true)}>
                  <Plus className="size-4" />
                  {tx('Yeni şablon')}
                </Button>
              )
        }
      />

      <Tabs tabs={tabs} value={tab} onChange={setTab} label={tx('Bildirim görünümü')} />

      {tab === 'gelen' && notLinked && (
        <Panel>
          <EmptyState
            icon={Lock}
            title={tx('Hesabınıza bağlı çalışan kaydı bulunamadı')}
            detail={tx('Bildirim alabilmeniz için hesabınızın bir çalışan kaydıyla eşleşmesi gerekiyor. İK yöneticinize başvurun.')}
          />
        </Panel>
      )}

      {tab === 'gelen' && !notLinked && (
        <>
          <div className="w-56">
            <SelectField
              id="notif-status"
              label={tx('Durum')}
              value={status}
              onChange={setStatus}
              options={[
                { value: ALL, label: tx('Tüm durumlar') },
                ...(Object.keys(notificationStatusLabels) as NotificationStatus[]).map((s) => ({
                  value: s,
                  label: notificationStatusLabels[s],
                })),
              ]}
            />
          </div>

          <Panel>
            {notifications.isPending ? (
              <RowsSkeleton rows={6} columns={2} />
            ) : notifications.isError ? (
              <ErrorState
                message={
                  notifications.error instanceof Error ? notifications.error.message : undefined
                }
                onRetry={() => void notifications.refetch()}
              />
            ) : (notifications.data?.length ?? 0) === 0 ? (
              <EmptyState title={tx('Bildirim yok')} detail={tx('Bu filtreye uyan bildirim bulunmuyor.')} />
            ) : (
              <ul className="divide-y divide-border">
                {notifications.data!.map((n, i) => {
                  const isUnread = n.status !== 'Read'
                  return (
                    <motion.li
                      key={n.id}
                      initial={reduced ? false : { opacity: 0, y: 4 }}
                      animate={{ opacity: 1, y: 0 }}
                      transition={{
                        duration: 0.26,
                        ease: 'easeOut',
                        delay: Math.min(i * 0.03, 0.24),
                      }}
                      className={cn(
                        'flex gap-3 border-l-2 px-4 py-4',
                        isUnread ? 'border-primary bg-primary/5' : 'border-transparent',
                      )}
                    >
                      <span className="min-w-0 flex-1">
                        <span
                          className={cn(
                            'block text-[14px]',
                            isUnread ? 'font-semibold' : 'text-muted-foreground',
                          )}
                        >
                          {n.subject}
                        </span>
                        {n.body && (
                          <span className="mt-1 block text-[13px] leading-relaxed text-muted-foreground">
                            {n.body}
                          </span>
                        )}
                        <span className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px] text-muted-foreground">
                          <span>{formatRelativeToNow(n.createdAt)}</span>
                          <span className="tabular">{formatDateTime(n.createdAt)}</span>
                          <span>{notificationChannelLabels[n.channel]}</span>
                        </span>
                      </span>
                      <span className="flex shrink-0 flex-col items-end gap-2">
                        <NotificationStatusBadge status={n.status} />
                        {isUnread && (
                          <Button
                            size="sm"
                            variant="ghost"
                            className="cursor-pointer"
                            disabled={markRead.isPending && markRead.variables === n.id}
                            onClick={() => markRead.mutate(n.id)}
                          >
                            {tx('Okundu')}
                          </Button>
                        )}
                      </span>
                    </motion.li>
                  )
                })}
              </ul>
            )}
          </Panel>
        </>
      )}

      {tab === 'sablonlar' && (
        <Panel>
          {templates.isPending ? (
            <RowsSkeleton rows={4} columns={2} />
          ) : templates.isError ? (
            <ErrorState
              message={templates.error instanceof Error ? templates.error.message : undefined}
              onRetry={() => void templates.refetch()}
            />
          ) : (templates.data?.length ?? 0) === 0 ? (
            <EmptyState
              title={tx('Şablon yok')}
              detail={tx('Servislerin kullanacağı ilk bildirim şablonunu ekleyin.')}
              action={
                <Button size="sm" className="cursor-pointer" onClick={() => setTemplateModal(true)}>
                  {tx('Yeni şablon')}
                </Button>
              }
            />
          ) : (
            <ul className="divide-y divide-border">
              {templates.data!.map((t) => (
                <li key={t.id} className="px-4 py-4">
                  <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1.5">
                    <span className="min-w-0">
                      <span className="block truncate font-mono text-[13px] font-semibold">
                        {t.code}
                      </span>
                      <span className="mt-0.5 block text-[14px]">{t.subjectTemplate}</span>
                    </span>
                    <span className="flex shrink-0 items-center gap-2">
                      <StatusBadge tone="neutral">
                        {notificationChannelLabels[t.channel]}
                      </StatusBadge>
                      <Button
                        size="sm"
                        variant="ghost"
                        className="cursor-pointer text-destructive"
                        onClick={() => setToDelete(t)}
                      >
                        {tx('Sil')}
                      </Button>
                    </span>
                  </div>
                  <div className="mt-2 rounded-md bg-muted/50 p-3">
                    <p className="text-[13px] leading-relaxed whitespace-pre-line text-muted-foreground">
                      {t.bodyTemplate}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      )}

      <NewTemplateModal open={templateModal} onClose={() => setTemplateModal(false)} />
      <DeleteTemplateConfirm template={toDelete} onClose={() => setToDelete(null)} />
    </div>
  )
}
