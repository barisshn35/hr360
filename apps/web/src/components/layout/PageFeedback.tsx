import { useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { ThumbsDown, ThumbsUp } from 'lucide-react'
import { apiFetch } from '@/api/client'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/auth/useAuth'
import { pageTemplate } from '@/lib/pageTemplate'
import { tx } from '@/lib/i18n'

const ANSWERED = 'hr360.pageFeedback'
const TTL = 30 * 86_400_000

function answered(): Record<string, number> {
  try { return JSON.parse(localStorage.getItem(ANSWERED) ?? '{}') as Record<string, number> } catch { return {} }
}

/**
 * Ekran kullanım sayacı (yenilik haritası #33): açılan ekran şablonları birikir, 30 saniyede bir ve sekme
 * gizlenince toplu gönderilir. Kişi kimliği gönderilmez.
 */
export function usePageViews() {
  const { tenantSlug } = useAuth()
  const { pathname } = useLocation()
  const queue = useRef<string[]>([])
  useEffect(() => {
    const t = pageTemplate(pathname)
    if (t && tenantSlug) queue.current.push(t)
  }, [pathname, tenantSlug])
  useEffect(() => {
    if (!tenantSlug) return
    const flush = () => {
      if (!queue.current.length) return
      const paths = queue.current.splice(0, 100)
      // keepalive: sekme kapanırken başlayan gönderim yarıda kesilmesin.
      void apiFetch('/api/engagement/product/views', { method: 'POST', body: { paths }, noQueue: true, keepalive: true }).catch(() => undefined)
    }
    const timer = window.setInterval(flush, 30_000)
    const onHide = () => { if (document.visibilityState === 'hidden') flush() }
    document.addEventListener('visibilitychange', onHide)
    return () => { window.clearInterval(timer); document.removeEventListener('visibilitychange', onHide); flush() }
  }, [tenantSlug])
}

/**
 * Sayfa altındaki "Bu ekran işinizi gördü mü?" satırı (isimsiz). Yanıtlanan ekran 30 gün yeniden sorulmaz.
 * Olumsuz oyda kısa yorum istenir.
 */
export function PageFeedback() {
  const { tenantSlug } = useAuth()
  const { pathname } = useLocation()
  const path = pageTemplate(pathname)
  const [state, setState] = useState<'ask' | 'comment' | 'done' | 'hidden'>('ask')
  const [text, setText] = useState('')
  const [prevPath, setPrevPath] = useState(path)
  if (prevPath !== path) { setPrevPath(path); setState('ask'); setText('') }
  if (!tenantSlug || !path || path === '/panel') return null
  const seenAt = answered()[path]
  if (state === 'ask' && seenAt && Date.now() - seenAt < TTL) return null

  const send = (rating: 1 | -1, comment?: string) => {
    void apiFetch('/api/engagement/product/feedback', { method: 'POST', body: { path, rating, comment } }).catch(() => undefined)
    try { localStorage.setItem(ANSWERED, JSON.stringify({ ...answered(), [path]: Date.now() })) } catch { /* özel pencere */ }
    setState('done')
  }

  if (state === 'hidden') return null
  return (
    <div className="mx-auto mt-10 flex w-full max-w-[1480px] flex-wrap items-center justify-center gap-2 px-4 text-[12.5px] text-muted-foreground">
      {state === 'done' ? <span>{tx('Teşekkürler, geri bildiriminiz iletildi.')}</span> : state === 'comment' ? (
        <form className="flex w-full max-w-xl flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); send(-1, text.trim() || undefined) }}>
          <label htmlFor="page-feedback-text" className="sr-only">{tx('Neyi eksik buldunuz?')}</label>
          <input id="page-feedback-text" value={text} maxLength={1000} onChange={(e) => setText(e.target.value)} autoFocus
            placeholder={tx('Neyi eksik ya da zor buldunuz? (isteğe bağlı, isimsiz)')}
            className="h-8 min-w-0 flex-1 rounded-lg border border-border bg-card px-3 text-[12.5px] text-foreground outline-none focus:border-primary" />
          <Button type="submit" size="sm" variant="outline">{tx('Gönder')}</Button>
        </form>
      ) : (
        <>
          <span>{tx('Bu ekran işinizi gördü mü?')}</span>
          <Button size="sm" variant="ghost" aria-label={tx('Evet')} onClick={() => send(1)}><ThumbsUp className="size-4" /></Button>
          <Button size="sm" variant="ghost" aria-label={tx('Hayır')} onClick={() => setState('comment')}><ThumbsDown className="size-4" /></Button>
          <button type="button" className="cursor-pointer underline-offset-2 hover:underline" onClick={() => setState('hidden')}>{tx('Şimdi değil')}</button>
        </>
      )}
    </div>
  )
}
