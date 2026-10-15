/**
 * QR kodu — tarayıcıda üretilir (uqr, MIT); link hiçbir harici servise gitmez.
 * Modüller tek bir SVG yolu olarak çizilir; renk `currentColor` (koyu temada da
 * okunabilsin diye arka plan her zaman beyaz kart).
 */
import { useMemo } from 'react'
import { cn } from '@/lib/utils'
import { qrPath } from '@/features/access/passwordHelpers'

export function QrCode({ value, size = 184, className, label }: { value: string; size?: number; className?: string; label: string }) {
  const { path, size: n } = useMemo(() => qrPath(value), [value])

  return (
    <div className={cn('inline-flex rounded-xl bg-white p-2 text-black shadow-sm ring-1 ring-black/5', className)}>
      <svg role="img" aria-label={label} width={size} height={size} viewBox={`0 0 ${n} ${n}`} shapeRendering="crispEdges">
        <path d={path} fill="currentColor" />
      </svg>
    </div>
  )
}
