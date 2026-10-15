/**
 * Seçili departman kartı — 2B (SVG) ve 3B görünümlerin ortak seçim kartı/eylemleri:
 * odaklan, alt birimleri göster/gizle.
 */

import type { ReactNode } from 'react'
import { Crosshair, GitFork } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { tx } from '@/lib/i18n'

export function OrgSelectionCard(props: {
  name: string
  subtitle: string
  childCount: number
  hiddenCount: number
  onFocus: () => void
  onToggle: () => void
  /** Ek eylemler (ör. 3B'de "3B'yi kapat"). */
  extra?: ReactNode
}) {
  return (
    <div className="absolute top-3 left-3 z-10 flex max-w-[calc(100%-24px)] flex-wrap items-center gap-1.5 rounded-lg border border-border bg-card/95 p-1.5 pl-3 text-[12px] shadow-md backdrop-blur">
      <span className="min-w-0 truncate font-medium">{props.name}</span>
      <span className="text-muted-foreground">{props.subtitle}</span>
      <Button size="sm" variant="ghost" className="h-7 px-2" onClick={props.onFocus}>
        <Crosshair className="size-3.5" aria-hidden /> {tx('Odaklan')}
      </Button>
      {props.childCount > 0 && (
        <Button size="sm" variant="ghost" className="h-7 px-2" onClick={props.onToggle} aria-expanded={props.hiddenCount === 0}>
          <GitFork className="size-3.5" aria-hidden /> {props.hiddenCount ? tx('Alt birimleri göster') : tx('Alt birimleri gizle')}
        </Button>
      )}
      {props.extra}
    </div>
  )
}
