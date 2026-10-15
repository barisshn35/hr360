/**
 * Kişi gösterimi: baş harf avatarı, avatar yığını ve aranabilir kişi seçici.
 *
 * Avatar rengi kişi kimliğinden türetilir (`--chart-1..5`): aynı kişi her
 * ekranda aynı renkte görünür, sabit renk yazılmaz.
 */

import { useMemo, useState } from 'react'
import { motion } from 'motion/react'
import { Check, Search } from 'lucide-react'
import { cn } from '@/lib/utils'
import { initialsOf, type PickerPerson } from '../hooks'
import { tx, appLocale } from '@/lib/i18n'

const PALETTE = ['--chart-1', '--chart-2', '--chart-3', '--chart-4', '--chart-5']

export function colorOf(id: string): string {
  let h = 0
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0
  return `hsl(var(${PALETTE[h % PALETTE.length]}))`
}

const SIZES = {
  xs: 'size-6 text-[9px]',
  sm: 'size-7 text-[10px]',
  md: 'size-9 text-[12px]',
  lg: 'size-11 text-[14px]',
}

export function PersonAvatar({
  id,
  name,
  size = 'sm',
  className,
  ring,
}: {
  id: string
  name: string
  size?: keyof typeof SIZES
  className?: string
  ring?: boolean
}) {
  const color = colorOf(id)
  return (
    <span
      aria-hidden
      title={name}
      className={cn('inline-flex shrink-0 items-center justify-center rounded-full font-semibold', SIZES[size], ring && 'ring-2 ring-card', className)}
      style={{ background: `color-mix(in oklab, ${color} 18%, hsl(var(--card)))`, color }}
    >
      {initialsOf(name) || '?'}
    </span>
  )
}

export function AvatarStack({
  people,
  max = 5,
  size = 'xs',
}: {
  people: { id: string; name: string }[]
  max?: number
  size?: keyof typeof SIZES
}) {
  const shown = people.slice(0, max)
  const rest = people.length - shown.length
  return (
    <span className="flex items-center -space-x-1.5">
      {shown.map((p) => (
        <PersonAvatar key={p.id} id={p.id} name={p.name} size={size} ring />
      ))}
      {rest > 0 && (
        <span className={cn('inline-flex items-center justify-center rounded-full bg-muted font-semibold text-muted-foreground ring-2 ring-card', SIZES[size])}>
          +{rest}
        </span>
      )}
    </span>
  )
}

/**
 * Aranabilir kişi listesi — diyalog içinde kullanılır. Yirmiden fazla
 * çalışanda açılır menü yerine arama kutusu çok daha hızlı. Liste dizinden
 * gelir (her rol görebilir); alt satır (unvan · departman) biliniyorsa yazılır.
 */
export function PersonPicker({
  people,
  value,
  onChange,
  exclude = [],
  note,
  detailOf,
  height = 280,
}: {
  people: PickerPerson[]
  value: string | null
  onChange: (id: string) => void
  exclude?: string[]
  /** Kişinin yanında küçük not (ör. "zaten üye"). */
  note?: (id: string) => string | null
  /** Alt satır: unvan, departman… */
  detailOf?: (id: string) => string | null
  height?: number
}) {
  const [q, setQ] = useState('')
  const list = useMemo(() => {
    const needle = q.trim().toLocaleLowerCase(appLocale)
    return people.filter((p) => !exclude.includes(p.id)).filter((p) => !needle || p.name.toLocaleLowerCase(appLocale).includes(needle))
  }, [people, exclude, q])

  return (
    <div className="rounded-lg border border-border">
      <label className="relative block border-b border-border">
        <span className="sr-only">{tx('Kişi ara')}</span>
        <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <input
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder={tx('Ad ara')}
          className="h-10 w-full bg-transparent pr-3 pl-9 text-[13px] outline-none"
          autoFocus
        />
      </label>
      <ul role="listbox" aria-label={tx('Kişiler')} className="overflow-y-auto p-1" style={{ maxHeight: height }}>
        {list.length === 0 && <li className="px-3 py-6 text-center text-[13px] text-muted-foreground">{tx('Eşleşen kişi yok.')}</li>}
        {list.map((p) => {
          const on = value === p.id
          const n = note?.(p.id)
          const detail = detailOf?.(p.id)
          return (
            <li key={p.id} role="option" aria-selected={on}>
              <button
                type="button"
                onClick={() => onChange(p.id)}
                className={cn(
                  'relative flex w-full items-center gap-3 rounded-md px-2.5 py-2 text-left transition-colors',
                  on ? 'text-foreground' : 'hover:bg-muted/60',
                )}
              >
                {on && (
                  <motion.span layoutId="person-picker-sel" className="absolute inset-0 rounded-md bg-primary/10 ring-1 ring-primary/30" transition={{ type: 'spring', stiffness: 500, damping: 38 }} />
                )}
                <PersonAvatar id={p.id} name={p.name} className="relative" />
                <span className="relative min-w-0 flex-1">
                  <span className="block truncate text-[13px] font-medium">{p.name}</span>
                  {detail && <span className="block truncate text-[11px] text-muted-foreground">{detail}</span>}
                </span>
                {n && <span className="relative shrink-0 text-[11px] text-muted-foreground">{n}</span>}
                {on && <Check className="relative size-4 shrink-0 text-primary" aria-hidden />}
              </button>
            </li>
          )
        })}
      </ul>
    </div>
  )
}
