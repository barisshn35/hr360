import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Bell } from 'lucide-react'
import { notificationApi } from '@/api/notification'
import { useNotifications, useUnreadCount, useMyEmployeeId } from '@/api/queries'
import { useAuth } from '@/auth/useAuth'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { cn } from '@/lib/utils'
import { formatRelativeToNow } from '@/lib/format'
import { tx } from '@/lib/i18n'

/**
 * Bildirim çanı.
 *
 * NOT: bildirimler backend'de Employee.Id ile yazılır (RecipientEmployeeId) -
 * Keycloak `sub` (user.id) DEĞİL, ayrı bir kimlik uzayı. Önceden burada
 * doğrudan `user.id` kullanılıyordu, yani gerçek bir çalışan için bildirimler
 * asla eşleşmiyordu (hardcore test, 3. tur). `useMyEmployeeId` ile gerçek
 * Employee.Id çözülür; çalışan kaydı olmayan hesaplarda (platform/tenant-admin)
 * çan sessizce gizli kalır.
 */
export function NotificationBell() {
  const { user, can } = useAuth()
  const [open, setOpen] = useState(false)
  const queryClient = useQueryClient()

  const allowed = can('notification:view')
  const { employeeId, notLinked } = useMyEmployeeId(allowed)
  const recipientId = allowed ? employeeId : undefined
  const unread = useUnreadCount(recipientId)
  const list = useNotifications({ recipientId, limit: 8 }, open && Boolean(recipientId))

  const markRead = useMutation({
    mutationFn: (id: string) => notificationApi.markRead(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['notification'] }),
  })

  if (!user || !allowed || notLinked) return null

  const count = unread.data?.unreadCount ?? 0

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label={count > 0 ? tx('Bildirimler, {0} okunmamış', [count]) : tx('Bildirimler')}
          className="group relative flex size-9 cursor-pointer items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
        >
          <Bell
            aria-hidden="true"
            className={cn('size-[18px] origin-top', count > 0 && 'group-hover:animate-[wiggle_0.6s_ease-in-out]')}
            strokeWidth={1.75}
          />
          {count > 0 && <span className="absolute top-1 right-1 size-3 animate-ping rounded-full bg-primary/50" />}
          {count > 0 && (
            <span className="tabular absolute top-1 right-0.5 flex h-[17px] min-w-[17px] items-center justify-center rounded-full bg-primary px-1 text-[10px] leading-none font-bold text-primary-foreground">
              {count > 99 ? '99+' : count}
            </span>
          )}
        </button>
      </PopoverTrigger>

      <PopoverContent align="end" className="w-[min(20rem,calc(100vw-1.5rem))] p-0">
        <div className="flex items-baseline justify-between gap-3 border-b border-border px-3.5 py-3">
          <span className="text-[13px] font-semibold">{tx('Bildirimler')}</span>
          <span className="tabular text-[11px] text-muted-foreground">
            {count > 0 ? tx('{0} okunmamış', [count]) : tx('hepsi okundu')}
          </span>
        </div>

        <div className="max-h-80 overflow-y-auto">
          {list.isPending ? (
            <p className="px-3.5 py-6 text-center text-[13px] text-muted-foreground">{tx('Yükleniyor')}</p>
          ) : list.isError ? (
            <p className="px-3.5 py-6 text-center text-[13px] text-destructive">
              {tx('Bildirimler alınamadı.')}
            </p>
          ) : (list.data?.length ?? 0) === 0 ? (
            <p className="px-3.5 py-6 text-center text-[13px] text-muted-foreground">
              {tx('Henüz bildirim yok.')}
            </p>
          ) : (
            <ul className="divide-y divide-border">
              {list.data!.map((n) => {
                const isUnread = n.status !== 'Read'
                return (
                  <li key={n.id}>
                    <button
                      type="button"
                      disabled={!isUnread || markRead.isPending}
                      onClick={() => markRead.mutate(n.id)}
                      className={cn(
                        'flex w-full gap-2.5 px-3.5 py-3 text-left transition-colors',
                        isUnread ? 'cursor-pointer hover:bg-accent' : 'cursor-default',
                      )}
                    >
                      <span
                        aria-hidden="true"
                        className={cn(
                          'mt-1.5 size-1.5 shrink-0 rounded-full',
                          isUnread ? 'bg-primary' : 'bg-transparent',
                        )}
                      />
                      <span className="min-w-0 flex-1">
                        <span
                          className={cn(
                            'block truncate text-[13px]',
                            isUnread ? 'font-semibold' : 'text-muted-foreground',
                          )}
                        >
                          {n.subject}
                        </span>
                        {n.body && (
                          <span className="mt-0.5 line-clamp-2 block text-[12px] text-muted-foreground">
                            {n.body}
                          </span>
                        )}
                        <span className="tabular mt-1 block text-[11px] text-muted-foreground/70">
                          {formatRelativeToNow(n.createdAt)}
                          {isUnread ? tx(', okundu işaretlemek için tıklayın') : ''}
                        </span>
                      </span>
                    </button>
                  </li>
                )
              })}
            </ul>
          )}
        </div>

        <Link
          to="/panel/bildirimler"
          onClick={() => setOpen(false)}
          className="flex min-h-11 items-center justify-center border-t border-border text-[13px] font-medium transition-colors hover:bg-accent"
        >
          {tx('Tümünü gör')}
        </Link>
      </PopoverContent>
    </Popover>
  )
}
