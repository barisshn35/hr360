import { useEffect, useMemo, useState } from 'react'
import { Megaphone } from 'lucide-react'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { useUiPref } from '@/lib/uiPrefs'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { changelog } from './changelog.gen'
import { latestId, unreadCount, type WhatsNewPref } from './whatsNew'

/** Komut paletinden ya da başka bir yerden paneli açmak için olay adı. */
export const OPEN_WHATS_NEW_EVENT = 'hr360:whats-new'

/**
 * Dalga 12 (madde 90): "Yenilikler" düğmesi ve paneli. Kaynak docs/CHANGELOG-tr.md (derlemeye gömülü,
 * ağdan veri çekmez). Okunmamış rozet kişiye özeldir (ui-prefs `whatsnew`); panel açılınca okundu sayılır.
 */
export function WhatsNewButton({ className }: { className?: string }) {
  const [open, setOpen] = useState(false)
  const entries = useMemo(() => changelog(), [])
  const ids = useMemo(() => entries.map((e) => e.id), [entries])
  const pref = useUiPref<WhatsNewPref | null>('whatsnew', null)
  const unread = pref.ready ? unreadCount(ids, pref.value?.seen) : 0
  const lastSeen = pref.value?.seen ? Number(pref.value.seen) : null

  useEffect(() => {
    const l = () => setOpen(true)
    window.addEventListener(OPEN_WHATS_NEW_EVENT, l)
    return () => window.removeEventListener(OPEN_WHATS_NEW_EVENT, l)
  }, [])

  // Açılınca okundu: bir sonraki açılışta "yeni" işaretleri kalksın (bu açılışta görünür kalır).
  const [markedFrom, setMarkedFrom] = useState<number | null>(null)
  useEffect(() => {
    if (!open || !pref.ready) return
    const latest = latestId(ids)
    // Hiç açmamış kullanıcıda yalnızca en son dalga "Yeni" sayılır (unreadCount ile aynı).
    setMarkedFrom(lastSeen ?? Number(latest) - 1)
    if (pref.value?.seen !== latest) void pref.set({ seen: latest })
  }, [open, pref.ready])

  const isNew = (id: string) => markedFrom !== null && Number(id) > markedFrom

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={unread > 0 ? tx('Yenilikler, {0} okunmamış', [unread]) : tx('Yenilikler')}
        title={tx('Yenilikler')}
        className={cn('relative size-9 cursor-pointer items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-accent hover:text-foreground', className)}
      >
        <Megaphone aria-hidden="true" className="size-[18px]" strokeWidth={1.75} />
        {unread > 0 && (
          <span className="tabular absolute top-1 right-0.5 flex h-[17px] min-w-[17px] items-center justify-center rounded-full bg-primary px-1 text-[10px] leading-none font-bold text-primary-foreground">
            {unread}
          </span>
        )}
      </button>
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="right" className="w-full overflow-y-auto sm:max-w-md">
          <SheetHeader className="border-b border-border">
            <SheetTitle className="flex items-center gap-2"><Megaphone className="size-4 text-primary" />{tx('Yenilikler')}</SheetTitle>
            <SheetDescription>{tx('HR360\'a son dalgalarda gelenler, sade bir dille.')}</SheetDescription>
          </SheetHeader>
          <ol className="space-y-6 px-4 pb-8">
            {entries.map((e) => (
              <li key={e.id}>
                <div className="flex flex-wrap items-baseline gap-2">
                  <h3 className="text-[14px] font-semibold tracking-tight">{e.title}</h3>
                  {isNew(e.id) && (
                    <span className="rounded-full bg-primary/15 px-2 py-0.5 text-[10.5px] font-semibold text-primary">{tx('Yeni')}</span>
                  )}
                </div>
                <p className="mt-0.5 text-[11.5px] text-muted-foreground">
                  {tx('Dalga {0}', [e.id])} · {formatDate(e.date)}
                </p>
                <ul className="mt-2 list-disc space-y-1 pl-5 text-[13px] leading-relaxed text-foreground/90">
                  {e.items.map((it) => <li key={it}>{it}</li>)}
                </ul>
              </li>
            ))}
          </ol>
        </SheetContent>
      </Sheet>
    </>
  )
}
