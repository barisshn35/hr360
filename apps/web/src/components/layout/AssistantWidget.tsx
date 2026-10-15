import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { Bot, CornerDownLeft, Sparkles, X } from 'lucide-react'
import { governanceApi, type AssistantReply } from '@/api/governance'
import { useTenantScope } from '@/lib/tenant-scope'
import { cn } from '@/lib/utils'
import { MiniMarkdown, errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

type Msg = { role: 'user' | 'bot'; text: string; reply?: AssistantReply }

const QUICK = [tx('İzin bakiyem'), tx('Bekleyen taleplerim'), tx('Sonraki resmî tatil'), tx('Bugün kim izinde?'), tx('Uzaktan çalışma politikası')]

/**
 * Sağ altta yüzen İK asistanı. Kişisel sorulara (izin bakiyem, taleplerim)
 * veriden, politika sorularına İK bilgi bankasından yanıt verir; yöneticinin
 * analitik sorularını rapor motoruna yönlendirir. Kural tabanlıdır, uydurmaz.
 */
export function AssistantWidget() {
  const { hasTenant } = useTenantScope()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [msgs, setMsgs] = useState<Msg[]>([{ role: 'bot', text: tx('Merhaba! İzin, talepler, tatiller veya şirket politikaları hakkında sorabilirsiniz.') }])
  const [q, setQ] = useState('')
  const [busy, setBusy] = useState(false)
  const end = useRef<HTMLDivElement>(null)
  // Gövde süslü parantezde: yeni tarayıcılarda scrollIntoView bir Promise döndürür; ok fonksiyonu onu
  // döndürürse React temizleme fonksiyonu sanıp çağırır ve tüm arayüz çöker.
  useEffect(() => {
    end.current?.scrollIntoView({ behavior: 'smooth' })
  }, [msgs, open])
  if (!hasTenant) return null

  const ask = async (text: string) => {
    if (!text.trim() || busy) return
    setMsgs((m) => [...m, { role: 'user', text }])
    setQ('')
    setBusy(true)
    try {
      const r = await governanceApi.assistant(text)
      setMsgs((m) => [...m, { role: 'bot', text: r.reply, reply: r }])
    } catch (e) {
      setMsgs((m) => [...m, { role: 'bot', text: errMsg(e, tx('Şu an yanıt veremiyorum.')) }])
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <motion.button
        type="button"
        aria-label={tx('İK asistanı')}
        onClick={() => setOpen((o) => !o)}
        whileHover={{ scale: 1.06 }}
        whileTap={{ scale: 0.94 }}
        className="fixed right-3 bottom-[calc(5rem+env(safe-area-inset-bottom))] z-40 grid size-11 cursor-pointer place-items-center rounded-2xl border border-primary/40 bg-card/80 text-primary shadow-[0_10px_40px_-10px_hsl(var(--primary)/0.6)] backdrop-blur-xl md:right-5 md:bottom-6 md:size-13"
      >
        <AnimatePresence mode="wait" initial={false}>
          <motion.span key={open ? 'x' : 'b'} initial={{ rotate: -90, opacity: 0 }} animate={{ rotate: 0, opacity: 1 }} exit={{ rotate: 90, opacity: 0 }}>
            {open ? <X className="size-5" /> : <Bot className="size-5" />}
          </motion.span>
        </AnimatePresence>
        {!open && <span className="absolute -top-1 -right-1 size-3 animate-ping rounded-full bg-primary/60" />}
      </motion.button>
      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, y: 20, scale: 0.96, filter: 'blur(6px)' }}
            animate={{ opacity: 1, y: 0, scale: 1, filter: 'blur(0px)' }}
            exit={{ opacity: 0, y: 16, scale: 0.97 }}
            // Yay (spring) hedefi aşabildiğinden blur negatife düşüp tarayıcı uyarısı veriyordu: filtre ease ile gider.
            transition={{ type: 'spring', stiffness: 320, damping: 28, filter: { type: 'tween', ease: 'easeOut', duration: 0.2 } }}
            className="fixed right-3 bottom-[calc(8.25rem+env(safe-area-inset-bottom))] z-40 flex h-[min(560px,calc(100dvh-10rem))] w-[min(400px,calc(100vw-1.5rem))] md:h-[min(560px,70dvh)] md:right-5 flex-col overflow-hidden rounded-3xl border border-border bg-popover shadow-2xl md:bottom-22"
          >
            <div className="flex items-center gap-2.5 border-b border-border bg-gradient-to-r from-primary/15 to-transparent px-4 py-3">
              <span className="grid size-8 place-items-center rounded-xl bg-primary/15 text-primary"><Sparkles className="size-4" /></span>
              <div><p className="text-[14px] font-semibold">{tx('İK asistanı')}</p><p className="text-[11px] text-muted-foreground">{tx('Verinizden ve İK bilgi bankasından yanıtlar')}</p></div>
            </div>
            <div className="flex-1 space-y-3 overflow-y-auto px-4 py-3 text-[13px]">
              {msgs.map((m, i) => (
                <motion.div key={i} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} className={cn('flex', m.role === 'user' ? 'justify-end' : 'justify-start')}>
                  <div className={cn('max-w-[88%] rounded-2xl px-3.5 py-2.5 leading-relaxed', m.role === 'user' ? 'rounded-br-md bg-primary text-primary-foreground' : 'rounded-bl-md bg-muted')}>
                    <MiniMarkdown text={m.text} />
                    {m.reply?.source === 'llm' && (
                      <p className="mt-1.5 text-[10.5px] text-muted-foreground">{tx('Yapay zekâ yanıtı · bilgi bankasına dayanır{0}; hatalı olabilir.', [m.reply.related?.length ? ` (${m.reply.related.join(', ')})` : ''])}</p>
                    )}
                    {m.reply?.report?.understood && m.reply.report.rows.length > 0 && (
                      <table className="mt-2 w-full text-[12px]"><tbody>{m.reply.report.rows.slice(0, 8).map((r, j) => <tr key={j} className="border-t border-border/50"><td className="py-1">{String(r[0])}</td><td className="tabular py-1 text-right font-medium">{r[1] == null ? tx('gizli') : String(r[1])}</td></tr>)}</tbody></table>
                    )}
                    {m.reply?.links && m.reply.links.length > 0 && (
                      <div className="mt-2 flex flex-wrap gap-1.5">
                        {m.reply.links.map((l) => <button key={l.path} onClick={() => { navigate(l.path); setOpen(false) }} className="cursor-pointer rounded-full border border-primary/40 bg-background/60 px-2.5 py-0.5 text-[11.5px] text-primary hover:bg-primary/10">{l.label} →</button>)}
                      </div>
                    )}
                  </div>
                </motion.div>
              ))}
              {busy && (
                <div className="flex gap-1 px-2">{[0, 1, 2].map((d) => <motion.span key={d} className="size-1.5 rounded-full bg-muted-foreground" animate={{ y: [0, -4, 0] }} transition={{ repeat: Infinity, duration: 0.8, delay: d * 0.15 }} />)}</div>
              )}
              <div ref={end} />
            </div>
            <div className="border-t border-border p-3">
              <div className="no-scrollbar mb-2 flex gap-1.5 overflow-x-auto">
                {QUICK.map((s) => <button key={s} onClick={() => void ask(s)} className="shrink-0 cursor-pointer rounded-full border border-border px-2.5 py-1 text-[11.5px] text-muted-foreground hover:border-primary/50 hover:text-foreground">{s}</button>)}
              </div>
              <form onSubmit={(e) => { e.preventDefault(); void ask(q) }} className="flex items-center gap-2 rounded-xl border border-input bg-background/60 px-3 focus-within:ring-2 focus-within:ring-primary/30">
                <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Bir şey sorun…')} className="h-10 flex-1 bg-transparent text-[13.5px] outline-none" />
                <button type="submit" disabled={!q.trim() || busy} className="cursor-pointer text-primary disabled:opacity-40" aria-label={tx('Gönder')}><CornerDownLeft className="size-4" /></button>
              </form>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </>
  )
}
