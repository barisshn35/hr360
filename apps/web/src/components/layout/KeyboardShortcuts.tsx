import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Modal } from '@/components/ui/Modal'
import { GOTO_SHORTCUTS, isTypingTarget, useShortcutsEnabled } from '@/lib/shortcuts'
import { tx } from '@/lib/i18n'

/**
 * Genel klavye kısayolları (G23):
 *  ?        kısayol yardımı
 *  /        komut paletini (genel arama) aç
 *  g + harf sayfaya git (d genel bakış, o onaylar, i izin, m masraf, p profil, b bildirimler)
 * Yazı alanlarında, açık bir diyalogda ve değiştirici tuşlarla (Ctrl/⌘/Alt) çalışmaz;
 * Profilim › Erişilebilirlik'ten kapatılabilir.
 */
export function KeyboardShortcuts({ onOpenPalette }: { onOpenPalette: () => void }) {
  const [enabled] = useShortcutsEnabled()
  const [helpOpen, setHelpOpen] = useState(false)
  const navigate = useNavigate()
  const pendingG = useRef<number | null>(null)

  useEffect(() => {
    if (!enabled) return
    const clearG = () => {
      if (pendingG.current !== null) window.clearTimeout(pendingG.current)
      pendingG.current = null
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || e.isComposing) return
      if (isTypingTarget(e.target)) return
      // Başka bir diyalog/menü açıkken (kendi klavye düzeni var) karışma.
      if (document.querySelector('[role="dialog"], [role="alertdialog"], [role="menu"]')) return

      if (pendingG.current !== null) {
        const target = GOTO_SHORTCUTS.find((s) => s.key === e.key.toLowerCase())
        clearG()
        if (target) {
          e.preventDefault()
          navigate(target.path)
        }
        return
      }
      if (e.key === '?') {
        e.preventDefault()
        setHelpOpen(true)
      } else if (e.key === '/') {
        e.preventDefault()
        onOpenPalette()
      } else if (e.key === 'g' && !e.shiftKey) {
        pendingG.current = window.setTimeout(clearG, 1500)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('keydown', onKey)
      clearG()
    }
  }, [enabled, navigate, onOpenPalette])

  return <ShortcutsHelp open={helpOpen} onClose={() => setHelpOpen(false)} />
}

function Keys({ keys }: { keys: string[] }) {
  return (
    <span className="flex items-center gap-1">
      {keys.map((k, i) => (
        <span key={i} className="flex items-center gap-1">
          {i > 0 && <span className="text-[11px] text-muted-foreground">{tx('ardından')}</span>}
          <kbd className="min-w-6 rounded-md border border-border bg-muted px-1.5 py-0.5 text-center font-mono text-[12px]">{k}</kbd>
        </span>
      ))}
    </span>
  )
}

export function ShortcutsHelp({ open, onClose }: { open: boolean; onClose: () => void }) {
  const rows: Array<{ keys: string[]; label: string }> = [
    { keys: ['?'], label: tx('Bu yardımı aç') },
    { keys: ['/'], label: tx('Genel aramayı (komut paleti) aç') },
    { keys: ['⌘K'], label: tx('Komut paleti (Windows/Linux: Ctrl+K)') },
    ...GOTO_SHORTCUTS.map((s) => ({ keys: ['g', s.key], label: tx('Git: {0}', [s.label]) })),
    { keys: ['Esc'], label: tx('Açık pencereyi kapat') },
  ]
  return (
    <Modal open={open} onClose={onClose} title={tx('Klavye kısayolları')} note={tx('Yazı alanındayken kısayollar çalışmaz. Profilim › Erişilebilirlik’ten kapatabilirsiniz.')}>
      <table className="w-full text-[13px]">
        <caption className="sr-only">{tx('Klavye kısayolları')}</caption>
        <thead className="sr-only">
          <tr>
            <th scope="col">{tx('Tuşlar')}</th>
            <th scope="col">{tx('İşlem')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.keys.join('+')} className="border-b border-border/60 last:border-0">
              <td className="py-2 pr-4 align-middle whitespace-nowrap"><Keys keys={r.keys} /></td>
              <td className="py-2 align-middle">{r.label}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Modal>
  )
}
