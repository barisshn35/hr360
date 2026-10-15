/**
 * Yeni modüllerin (topluluk, yönetişim, içgörü) ortak küçük parçaları:
 * işlem kancası, etiket girişi, kişi seçici, plan kapısı, baş harf avatarı.
 */

import { useId, useMemo, useState, type ReactNode } from 'react'
import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { X } from 'lucide-react'
import { useDirectory } from '@/api/directory'
import { ApiError } from '@/api/client'
import { SelectField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { cn } from '@/lib/utils'
import { tx, txServer, appLocale } from '@/lib/i18n'
import { isQueuedOffline } from '@/lib/push'

export function errMsg(e: unknown, fallback = tx('İşlem tamamlanamadı.')) {
  // Servis iletileri Türkçe gelir; İngilizce arayüzde sözlükte karşılığı varsa çevrilir.
  if (e instanceof ApiError || e instanceof Error) return e.message ? txServer(e.message) : fallback
  return fallback
}

/** useMutation + toast + önbellek tazeleme. */
export function useAction<TArgs, TResult>(
  fn: (args: TArgs) => Promise<TResult>,
  opts: { success?: string | ((r: TResult) => string); invalidate?: QueryKey[]; onDone?: (r: TResult) => void } = {},
) {
  const toast = useToast()
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (r) => {
      opts.invalidate?.forEach((k) => void qc.invalidateQueries({ queryKey: k }))
      const msg = typeof opts.success === 'function' ? opts.success(r) : opts.success
      if (msg) toast.ok(msg)
      opts.onDone?.(r)
    },
    onError: (e) => {
      // Çevrimdışı: talep bu cihazda taslak olarak saklandı (bağlantı gelince gönderilir).
      if (isQueuedOffline(e)) { toast.ok(e.message); return }
      toast.stop(errMsg(e))
    },
  })
}

/** Baş harfli yuvarlak avatar; ad sabit bir renk tonuna eşlenir. */
export function Initials({ name, size = 36, className }: { name: string; size?: number; className?: string }) {
  const initials = name.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toLocaleUpperCase(appLocale)).join('')
  const hue = [...name].reduce((a, c) => (a * 31 + c.charCodeAt(0)) % 360, 7)
  return (
    <span
      aria-hidden="true"
      className={cn('grid shrink-0 place-items-center rounded-full font-semibold text-white ring-1 ring-white/10', className)}
      style={{
        width: size,
        height: size,
        fontSize: size * 0.36,
        background: `linear-gradient(135deg, hsl(${hue} 45% 42%), hsl(${(hue + 40) % 360} 50% 30%))`,
      }}
    >
      {initials || '?'}
    </span>
  )
}

/** Serbest etiket girişi (beceri, ilgi alanı). Enter veya virgül ekler. */
export function ChipInput({
  value,
  onChange,
  placeholder = tx('Yazıp Enter’a basın'),
  suggestions = [],
  id,
  label,
}: {
  value: string[]
  onChange: (next: string[]) => void
  placeholder?: string
  suggestions?: string[]
  id?: string
  label?: string
}) {
  const [draft, setDraft] = useState('')
  // id verilmezse etiket girdiye bağlanamıyordu (htmlFor={undefined}): kararlı bir kimlik üretilir.
  const autoId = useId()
  const inputId = id ?? autoId
  const add = (raw: string) => {
    const v = raw.trim()
    if (!v || value.some((x) => x.toLocaleLowerCase(appLocale) === v.toLocaleLowerCase(appLocale))) return
    onChange([...value, v])
  }
  const remaining = suggestions.filter((s) => !value.includes(s)).slice(0, 8)
  return (
    <div className="space-y-1.5">
      {label && (
        <label htmlFor={inputId} className="text-[13px] font-medium">
          {label}
        </label>
      )}
      <div className="flex min-h-10 flex-wrap items-center gap-1.5 rounded-xl border border-input bg-background/60 px-2 py-1.5 focus-within:ring-2 focus-within:ring-primary/30">
        {value.map((v) => (
          <span key={v} className="inline-flex items-center gap-1 rounded-full bg-primary/10 px-2.5 py-0.5 text-[12.5px] text-primary">
            {v}
            <button type="button" aria-label={tx('{0} kaldır', [v])} onClick={() => onChange(value.filter((x) => x !== v))} className="cursor-pointer opacity-70 hover:opacity-100">
              <X className="size-3" />
            </button>
          </span>
        ))}
        <input
          id={inputId}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ',') {
              e.preventDefault()
              add(draft)
              setDraft('')
            } else if (e.key === 'Backspace' && !draft && value.length) onChange(value.slice(0, -1))
          }}
          onBlur={() => {
            if (draft.trim()) {
              add(draft)
              setDraft('')
            }
          }}
          placeholder={value.length ? '' : placeholder}
          className="min-w-24 flex-1 bg-transparent px-1 py-1 text-[13.5px] outline-none placeholder:text-muted-foreground"
        />
      </div>
      {remaining.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {remaining.map((s) => (
            <button key={s} type="button" onClick={() => add(s)} className="cursor-pointer rounded-full border border-dashed border-border px-2 py-0.5 text-[11.5px] text-muted-foreground hover:border-primary/50 hover:text-foreground">
              + {s}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

/** Şirketteki herkesi (dizinden) seçebilen kişi seçici — yetkiden bağımsız, yalnızca ad. */
export function PersonSelect({
  value,
  onChange,
  label = tx('Çalışan'),
  exclude = [],
  id,
  hint,
}: {
  value: string
  onChange: (id: string) => void
  label?: string
  exclude?: string[]
  id?: string
  hint?: string
}) {
  const dir = useDirectory()
  const options = useMemo(
    () =>
      (dir.data ?? [])
        .filter((d) => !exclude.includes(d.id))
        .sort((a, b) => a.fullName.localeCompare(b.fullName, 'tr-TR'))
        .map((d) => ({ value: d.id, label: d.fullName })),
    [dir.data, exclude],
  )
  return <SelectField id={id} label={label} value={value} onChange={onChange} options={options} hint={hint ?? (dir.isPending ? tx('Yükleniyor') : undefined)} />
}

export { TenantGate } from './FeatureGate'

/** Küçük metrik karosu (StatCard'dan sade). */
export function Metric({ label, value, hint, tone }: { label: string; value: ReactNode; hint?: ReactNode; tone?: 'good' | 'bad' | 'warn' }) {
  return (
    <motion.div
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      className="surface rounded-2xl border border-border p-4"
    >
      <p className="text-[12px] text-muted-foreground">{label}</p>
      <p
        className={cn(
          'tabular mt-1 text-[24px] font-semibold tracking-tight',
          tone === 'good' && 'text-[hsl(var(--success))]',
          tone === 'bad' && 'text-destructive',
          tone === 'warn' && 'text-[hsl(var(--warning))]',
        )}
      >
        {value}
      </p>
      {hint && <p className="mt-0.5 text-[12px] text-muted-foreground">{hint}</p>}
    </motion.div>
  )
}

/** Yerel tarih (YYYY-MM-DD), saat dilimi kaymadan. */
export function isoDate(d = new Date()) {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

export function minutesToHHMM(m: number) {
  return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
}

/** Çok basit markdown: **kalın**, satır başı "•", satır sonu. Asistan yanıtları için. */
export function MiniMarkdown({ text }: { text: string }) {
  const parts = text.split('\n')
  return (
    <div className="space-y-1">
      {parts.map((line, i) => (
        <p key={i} className={cn(line.startsWith('•') && 'pl-2')}>
          {line.split(/(\*\*[^*]+\*\*|\*[^*]+\*)/g).map((seg, j) =>
            seg.startsWith('**') ? <strong key={j}>{seg.slice(2, -2)}</strong> : seg.startsWith('*') && seg.endsWith('*') && seg.length > 2 ? <em key={j}>{seg.slice(1, -1)}</em> : <span key={j}>{seg}</span>,
          )}
        </p>
      ))}
    </div>
  )
}
