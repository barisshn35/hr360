import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle } from 'lucide-react'
import { Checkbox } from '@/components/ui/checkbox'
import { useDirectory } from '@/api/directory'
import { complianceApi, type AckStats } from '@/api/compliance'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'

/** ⟦eşleşme⟧ işaretli düz metni güvenle vurgular (HTML yorumlanmaz). */
export function Snippet({ text }: { text: string }) {
  const parts = text.split(/(⟦[^⟧]*⟧)/g)
  return (
    <span>
      {parts.map((p, i) =>
        p.startsWith('⟦') ? <mark key={i} className="rounded bg-primary/15 px-0.5 text-foreground">{p.slice(1, -1)}</mark> : <span key={i}>{p}</span>,
      )}
    </span>
  )
}

/** Paragraf + **kalın** destekli basit metin (duyuru/politika gövdesi). */
export function RichText({ text, className }: { text: string; className?: string }) {
  return (
    <div className={cn('space-y-2 text-[13.5px] leading-relaxed', className)}>
      {text.split(/\n{2,}/).map((para, i) => (
        <p key={i} className="whitespace-pre-wrap">
          {para.split(/(\*\*[^*]+\*\*)/g).map((seg, j) => (seg.startsWith('**') && seg.endsWith('**') ? <strong key={j}>{seg.slice(2, -2)}</strong> : <span key={j}>{seg}</span>))}
        </p>
      ))}
    </div>
  )
}

/** Departman çoklu seçimi (İK). */
export function DepartmentChecklist({ value, onChange }: { value: string[]; onChange: (ids: string[]) => void }) {
  const q = useQuery({ queryKey: ['compliance', 'departments'], queryFn: ({ signal }) => complianceApi.departments(signal) })
  return (
    <fieldset className="space-y-1.5">
      <legend className="mb-1 text-[13px] font-medium">{tx('Departmanlar')}</legend>
      {(q.data ?? []).map((d) => (
        <label key={d.id} className="flex items-center gap-2 text-[13px]">
          <Checkbox checked={value.includes(d.id)} onCheckedChange={(v) => onChange(v === true ? [...value, d.id] : value.filter((x) => x !== d.id))} />
          {d.name}
        </label>
      ))}
      {q.data?.length === 0 && <p className="text-[12px] text-muted-foreground">{tx('Departman tanımlı değil.')}</p>}
    </fieldset>
  )
}

/** Dizinden çoklu kişi seçimi (aramalı). */
export function PeopleChecklist({ value, onChange, label }: { value: string[]; onChange: (ids: string[]) => void; label: string }) {
  const dir = useDirectory()
  const [filter, setFilter] = useState('')
  const list = useMemo(
    () => (dir.data ?? []).filter((d) => d.fullName.toLocaleLowerCase('tr-TR').includes(filter.toLocaleLowerCase('tr-TR'))).sort((a, b) => a.fullName.localeCompare(b.fullName, 'tr-TR')),
    [dir.data, filter],
  )
  return (
    <fieldset className="space-y-1.5">
      <legend className="mb-1 text-[13px] font-medium">{label} <span className="text-muted-foreground">({value.length})</span></legend>
      <input aria-label={tx('Kişi ara')} placeholder={tx('Kişi ara')} value={filter} onChange={(e) => setFilter(e.target.value)}
        className="h-8 w-full rounded-md border border-border bg-background px-2 text-[13px]" />
      <div className="max-h-44 space-y-1 overflow-y-auto rounded-md border border-border p-2">
        {list.map((d) => (
          <label key={d.id} className="flex items-center gap-2 text-[13px]">
            <Checkbox checked={value.includes(d.id)} onCheckedChange={(v) => onChange(v === true ? [...value, d.id] : value.filter((x) => x !== d.id))} />
            {d.fullName}
          </label>
        ))}
      </div>
    </fieldset>
  )
}

/** Okuma/kabul istatistiği. */
export function AckStatsView({ stats, listMissing }: { stats: AckStats; listMissing: boolean }) {
  const pct = stats.total ? Math.round((stats.read / stats.total) * 100) : null
  return (
    <div className="space-y-2 text-[13px]">
      <p>
        {stats.total === null
          ? tx('{0} kişi okudu (kitle büyüklüğü hesaplanamıyor)', [stats.read])
          : tx('{0} / {1} kişi okudu (%{2})', [stats.read, stats.total, pct ?? 0])}
      </p>
      {stats.total !== null && (
        <div className="h-2 overflow-hidden rounded-full bg-muted"><div className="h-full bg-primary" style={{ width: `${pct ?? 0}%` }} /></div>
      )}
      {listMissing && stats.notRead.length > 0 && (
        <div>
          <p className="mb-1 font-medium">{tx('Henüz onaylamayanlar')}</p>
          <ul className="max-h-48 space-y-0.5 overflow-y-auto text-muted-foreground">
            {stats.notRead.map((p) => <li key={p.employeeId}>{p.name}{p.department ? ` · ${p.department}` : ''}</li>)}
          </ul>
        </div>
      )}
    </div>
  )
}

export function Warnings({ items }: { items?: string[] }) {
  if (!items?.length) return null
  return (
    <div role="alert" className="flex gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-3 text-[13px]">
      <AlertTriangle className="size-4 shrink-0 text-[hsl(var(--warning))]" />
      <div>{items.map((w, i) => <p key={i}>{w}</p>)}</div>
    </div>
  )
}

/** Kişisel veri uyarısı kutusu (KVKK). */
export function KvkkNote({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-2 rounded-xl border border-border bg-muted/40 p-3 text-[12.5px] text-muted-foreground">
      <AlertTriangle className="mt-0.5 size-4 shrink-0" />
      <div>{children}</div>
    </div>
  )
}
