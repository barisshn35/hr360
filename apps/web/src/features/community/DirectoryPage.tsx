import { useDeferredValue, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Mail, Search, UserSearch } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { engagementApi } from '@/api/engagement'
import { Initials, TenantGate } from '@/features/shared/kit'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { normalizeSearch } from '@/lib/format'

export function DirectoryPage() {
  // Komut paletinden "Kişi ara" ile ?ara=<metin> gelir (dalga 12).
  const [params] = useSearchParams()
  const [q, setQ] = useState(() => (params.get('ara') ?? '').slice(0, 100))
  const term = useDeferredValue(q)
  const res = useQuery({ queryKey: ['directory-skills', term], queryFn: ({ signal }) => engagementApi.directory(term, signal), placeholderData: (p) => p })
  // Sunucudaki katlamayla aynı: "AYSE" araması "Ayşe" becerisini/adını da vurgular.
  const hl = (s: string) => term && normalizeSearch(s).includes(normalizeSearch(term))
  return (
    <TenantGate>
      <PageHeader title={tx('Yetenek dizini')} description={tx('“Kubernetes bilen kim?”, “Almanca konuşan var mı?” — beceri ve ilgi alanına göre çalışma arkadaşı bulun.')} />
      <div className="relative mb-5 max-w-xl">
        <Search className="absolute top-1/2 left-3.5 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder={tx('Beceri, ad, departman veya pozisyon ara')} className="h-11 rounded-2xl pl-10" autoFocus />
      </div>
      {res.data && res.data.topSkills.length > 0 && (
        <div className="mb-6 flex flex-wrap gap-1.5">
          {res.data.topSkills.map((s) => (
            <button key={s.skill} onClick={() => setQ(s.skill)} className="cursor-pointer rounded-full border border-border bg-card/50 px-3 py-1 text-[12.5px] transition hover:border-primary/50">
              {s.skill} <span className="text-muted-foreground">· {s.count}</span>
            </button>
          ))}
        </div>
      )}
      {res.isPending ? <RowsSkeleton /> : res.isError ? <ErrorState message={(res.error as Error).message} /> : res.data.people.length === 0 ? (
        <EmptyState icon={UserSearch} title={tx('Eşleşen kimse yok')} detail={tx('Çalışanlar becerilerini Profilim sayfasından ekler.')} />
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {res.data.people.map((p, i) => (
            <motion.div key={p.employeeId} layout initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: Math.min(i * 0.03, 0.3) }} className="surface rounded-2xl border border-border p-4">
              <div className="flex items-center gap-3">
                <Initials name={p.name} size={40} />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[14px] font-semibold">{p.name}</p>
                  <p className="truncate text-[12.5px] text-muted-foreground">{p.position ?? '—'} · {p.department ?? '—'}</p>
                </div>
                {p.email && <a href={`mailto:${p.email}`} className="rounded-lg p-2 text-muted-foreground hover:bg-accent hover:text-foreground" aria-label="E-posta"><Mail className="size-4" /></a>}
              </div>
              {p.bio && <p className="mt-2 line-clamp-2 text-[12.5px] text-muted-foreground">{p.bio}</p>}
              <div className="mt-3 flex flex-wrap gap-1">
                {p.skills.map((s) => <span key={s} className={cn('rounded-full px-2 py-0.5 text-[11.5px]', hl(s) ? 'bg-primary text-primary-foreground' : 'bg-primary/10 text-primary')}>{s}</span>)}
                {p.interests.map((s) => <span key={s} className={cn('rounded-full px-2 py-0.5 text-[11.5px]', hl(s) ? 'bg-foreground text-background' : 'bg-muted text-muted-foreground')}>{s}</span>)}
                {p.skills.length + p.interests.length === 0 && <span className="text-[12px] text-muted-foreground">{tx('Henüz beceri eklenmemiş')}</span>}
              </div>
            </motion.div>
          ))}
        </div>
      )}
    </TenantGate>
  )
}
