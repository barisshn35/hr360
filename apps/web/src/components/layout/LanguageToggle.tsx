import { Languages } from 'lucide-react'
import { useState } from 'react'
import { lang, tx } from '@/lib/i18n'
import { changeLanguage } from '@/lib/languageSync'
import { cn } from '@/lib/utils'

/** Türkçe ↔ İngilizce. Seçim tarayıcıda saklanır; sayfa yeniden yüklenir. */
export function LanguageToggle({ className }: { className?: string }) {
  const next = lang === 'tr' ? 'en' : 'tr'
  const [busy, setBusy] = useState(false)
  return (
    <button
      type="button"
      disabled={busy}
      onClick={() => {
        setBusy(true)
        void changeLanguage(next)
      }}
      // Görünen kısaltma (EN/TR) erişilebilir adın içinde olmalı (WCAG 2.5.3, axe label-content-name-mismatch).
      aria-label={next === 'en' ? tx('{0} — Switch to English', ['EN']) : tx('{0} — Türkçeye geç', ['TR'])}
      title={tx('Dil')}
      className={cn(
        'flex h-9 cursor-pointer items-center gap-1.5 rounded-full px-2.5 text-[12px] font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-foreground',
        className,
      )}
    >
      <Languages className="size-4" strokeWidth={1.75} />
      {next.toUpperCase()}
    </button>
  )
}
