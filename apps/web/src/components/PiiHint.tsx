import { useEffect, useState } from 'react'
import { ShieldAlert } from 'lucide-react'
import { piiCategories, scanPii, type PiiCategory } from '@/lib/pii'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

/**
 * Serbest metin alanının altında, kişisel / özel nitelikli veri olabileceğine dair BİLGİLENDİRİCİ uyarı
 * (KVKK veri en aza indirme). Tarama tarayıcıda yapılır (lib/pii.ts); metin hiçbir yere gönderilmez —
 * anonim etik bildirim formunda da güvenle kullanılır. Kaydı engellemez.
 */
export function PiiHint({ text, className }: { text: string; className?: string }) {
  const found = useDebouncedPii(text)
  if (found.length === 0) return null
  const special = found.some((c) => c !== 'tckn' && c !== 'iban' && c !== 'phone' && c !== 'email')
  return (
    <div
      role="status"
      aria-live="polite"
      className={cn(
        'flex items-start gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-2.5 text-[12px] leading-relaxed',
        className,
      )}
    >
      <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
      <span>
        {tx('Bu metinde kişisel/özel nitelikli veri olabilir: {0}', [found.map(piiLabel).join(', ')])}
        {'. '}
        {special
          ? tx('Özel nitelikli veriyi (sağlık, sabıka vb.) gerekmedikçe yazmayın; gerekiyorsa yalnızca amaç için zorunlu olanı yazın.')
          : tx('Gerekmedikçe kimlik, hesap ve iletişim bilgisi yazmayın (KVKK veri en aza indirme).')}
      </span>
    </div>
  )
}

/** Kategori adı (lib/pii.ts PII_LABELS ile aynı Türkçe metinler; çeviri anahtarı olsun diye sabit tx çağrıları). */
export function piiLabel(c: PiiCategory): string {
  switch (c) {
    case 'tckn': return tx('TCKN')
    case 'iban': return tx('IBAN')
    case 'phone': return tx('Telefon')
    case 'email': return tx('E-posta')
    case 'health': return tx('Sağlık')
    case 'criminal': return tx('Ceza mahkûmiyeti / güvenlik tedbiri')
    case 'religion': return tx('Din / mezhep / inanç')
    case 'ethnicity': return tx('Irk / etnik köken')
    case 'politics': return tx('Siyasi düşünce')
    case 'union': return tx('Sendika / dernek üyeliği')
    case 'sexual': return tx('Cinsel hayat')
    case 'biometric': return tx('Biyometrik / genetik veri')
  }
}

/** Metni ~400 ms gecikmeyle tarar; yazarken her tuşta yeniden hesaplamaz. */
export function useDebouncedPii(text: string, delay = 400): PiiCategory[] {
  const [found, setFound] = useState<PiiCategory[]>([])
  useEffect(() => {
    if (!text.trim()) {
      setFound([])
      return
    }
    const t = window.setTimeout(() => setFound(piiCategories(scanPii(text))), delay)
    return () => window.clearTimeout(t)
  }, [text, delay])
  return found
}
