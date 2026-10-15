import { useCallback, useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { ChevronLeft, ChevronRight, Crown, Maximize2, Pause, Play, X } from 'lucide-react'
import { organizationApi } from '@/api/organization'
import { engagementApi } from '@/api/engagement'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { GradientText } from '@/components/fx/shiny-text'
import { CenteredSpinner } from '@/components/ui/States'
import { Initials } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

interface Slide {
  kind: 'cover' | 'dept'
  title: string
  subtitle?: string
  head?: string
  people: Array<{ id: string; name: string; position: string | null; skills: string[] }>
  children?: string[]
  depth?: number
}

/**
 * Organizasyon şeması sunum modu: toplantıda büyük ekrana yansıtmak için tam
 * ekran, otomatik ilerleyen slaytlar. Önce şirket özeti, sonra her departman
 * (başı, üyeleri, alt birimleri) sırayla canlanarak gelir. ← → / boşluk / Esc.
 */
export function OrgPresentationPage() {
  const navigate = useNavigate()
  const { tenant } = useAuth()
  // Şemadan açıldıysa: `odak` yalnızca o birimin alt ağacını sunar, `donus` kapatınca şemaya döner.
  const [params] = useSearchParams()
  const focusId = params.get('odak')
  const backTo = params.get('donus')
  const close = useCallback(
    () => navigate(backTo && backTo.startsWith('/panel/') ? backTo : '/panel/organizasyon/ekipler'),
    [navigate, backTo],
  )
  const depts = useQuery({ queryKey: ['organization', 'departments', 'present'], queryFn: ({ signal }) => organizationApi.listDepartments(undefined, signal) })
  const people = useQuery({ queryKey: ['directory-skills', ''], queryFn: ({ signal }) => engagementApi.directory('', signal) })
  const [i, setI] = useState(0)
  const [auto, setAuto] = useState(true)

  const slides = useMemo<Slide[]>(() => {
    if (!depts.data || !people.data) return []
    const all = people.data.people
    const byName = new Map<string, typeof all>()
    all.forEach((p) => byName.set(p.department ?? '—', [...(byName.get(p.department ?? '—') ?? []), p]))
    const ordered: Array<{ d: (typeof depts.data)[number]; depth: number }> = []
    const walk = (parent: string | null, depth: number) =>
      depts.data!.filter((d) => (d.parentDepartmentId ?? null) === parent).sort((a, b) => a.name.localeCompare(b.name, 'tr-TR')).forEach((d) => { ordered.push({ d, depth }); walk(d.id, depth + 1) })
    const focus = focusId ? depts.data.find((d) => d.id === focusId) : undefined
    if (focus) { ordered.push({ d: focus, depth: 0 }); walk(focus.id, 1) } else walk(null, 0)
    const scoped = focus ? all.filter((p) => ordered.some(({ d }) => d.name === (p.department ?? '—'))) : all
    const cover: Slide = {
      kind: 'cover', title: focus ? focus.name : (tenant?.name ?? tx('Organizasyon')), subtitle: tx('{0} kişi · {1} departman', [scoped.length, focus ? ordered.length : depts.data.length]),
      people: scoped.slice(0, 40).map((p) => ({ id: p.employeeId, name: p.name, position: p.position, skills: p.skills })),
    }
    return [cover, ...ordered.map(({ d, depth }) => ({
      kind: 'dept' as const, title: d.name, depth,
      head: all.find((p) => p.employeeId === d.headEmployeeId)?.name,
      children: depts.data!.filter((c) => c.parentDepartmentId === d.id).map((c) => c.name),
      people: (byName.get(d.name) ?? []).map((p) => ({ id: p.employeeId, name: p.name, position: p.position, skills: p.skills })),
    }))]
  }, [depts.data, people.data, tenant?.name, focusId])

  const go = useCallback((delta: number) => setI((x) => Math.max(0, Math.min(slides.length - 1, x + delta))), [slides.length])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'ArrowRight' || e.key === ' ') { e.preventDefault(); go(1) }
      else if (e.key === 'ArrowLeft') go(-1)
      else if (e.key === 'Escape') close()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [go, close])
  useEffect(() => {
    if (!auto || slides.length < 2) return
    const t = setTimeout(() => setI((x) => (x + 1) % slides.length), 7000)
    return () => clearTimeout(t)
  }, [auto, i, slides.length])

  if (depts.isPending || people.isPending) return <CenteredSpinner label={tx('Sunum hazırlanıyor')} />
  const s = slides[i]
  // Portal: sayfa geçiş animasyonunun filtre/transform sarmalayıcısı "fixed" katmanı içine hapsetmesin.
  return createPortal(
    <div className="fixed inset-0 z-[70] overflow-hidden bg-background text-foreground">
      <AmbientBackground />
      <div className="absolute top-4 right-4 z-10 flex gap-2">
        <Button size="icon" variant="ghost" aria-label={tx('Tam ekran')} onClick={() => void document.documentElement.requestFullscreen?.()}><Maximize2 className="size-4" /></Button>
        <Button size="icon" variant="ghost" aria-label={auto ? tx('Durdur') : tx('Oynat')} onClick={() => setAuto((a) => !a)}>{auto ? <Pause className="size-4" /> : <Play className="size-4" />}</Button>
        <Button size="icon" variant="ghost" aria-label={tx('Kapat')} onClick={close}><X className="size-4" /></Button>
      </div>
      <div className="relative z-[1] flex h-full flex-col items-center justify-center px-8 pb-20">
        <AnimatePresence mode="wait">
          {s && (
            <motion.div key={i} initial={{ opacity: 0, scale: 0.96, filter: 'blur(12px)' }} animate={{ opacity: 1, scale: 1, filter: 'blur(0px)' }} exit={{ opacity: 0, scale: 1.03, filter: 'blur(10px)' }} transition={{ duration: 0.6 }} className="w-full max-w-6xl text-center">
              {s.kind === 'dept' && s.depth! > 0 && <p className="mb-2 text-[13px] tracking-[0.3em] text-muted-foreground uppercase">{tx('Alt birim')}</p>}
              <h1 className="text-[56px] leading-tight font-semibold tracking-[-0.04em] md:text-[76px]"><GradientText>{s.title}</GradientText></h1>
              {s.subtitle && <p className="mt-2 text-[20px] text-muted-foreground">{s.subtitle}</p>}
              {s.head && (
                <motion.div initial={{ opacity: 0, y: 16 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.3 }} className="mx-auto mt-6 inline-flex items-center gap-3 rounded-full border border-amber-400/40 bg-amber-400/10 py-2 pr-5 pl-2">
                  <Initials name={s.head} size={44} /><span className="text-[18px] font-medium">{s.head}</span><Crown className="size-5 text-amber-400" />
                </motion.div>
              )}
              <div className="mx-auto mt-10 flex max-w-5xl flex-wrap justify-center gap-3">
                {s.people.map((p, n) => (
                  <motion.div key={p.id} initial={{ opacity: 0, y: 30, rotate: -3 }} animate={{ opacity: 1, y: 0, rotate: 0 }} transition={{ delay: 0.45 + n * 0.06, type: 'spring', stiffness: 220, damping: 20 }}
                    className="surface flex w-56 items-center gap-3 rounded-2xl border border-border p-3 text-left">
                    <Initials name={p.name} size={42} />
                    <div className="min-w-0"><p className="truncate text-[15px] font-semibold">{p.name}</p><p className="truncate text-[12.5px] text-muted-foreground">{p.position ?? '—'}</p>
                      {p.skills.length > 0 && <p className="truncate text-[11px] text-primary">{p.skills.slice(0, 3).join(' · ')}</p>}</div>
                  </motion.div>
                ))}
                {s.people.length === 0 && <p className="text-[15px] text-muted-foreground">{tx('Bu birimde henüz kimse yok.')}</p>}
              </div>
              {s.children && s.children.length > 0 && <p className="mt-8 text-[14px] text-muted-foreground">{tx('Alt birimler: {0}', [s.children.join(' · ')])}</p>}
            </motion.div>
          )}
        </AnimatePresence>
      </div>
      <div className="absolute inset-x-0 bottom-0 z-[1] flex items-center gap-4 px-8 pb-6">
        <Button size="icon" variant="outline" aria-label={tx('Önceki')} onClick={() => go(-1)} disabled={i === 0}><ChevronLeft className="size-4" /></Button>
        <div className="flex flex-1 gap-1.5">
          {slides.map((_, n) => (
            <button key={n} onClick={() => setI(n)} className="relative h-1.5 flex-1 cursor-pointer overflow-hidden rounded-full bg-muted" aria-label={tx('Slayt {0}', [n + 1])}>
              {n < i && <span className="absolute inset-0 bg-primary" />}
              {n === i && <motion.span key={`${i}-${auto}`} className="absolute inset-y-0 left-0 bg-primary" initial={{ width: auto ? '0%' : '100%' }} animate={{ width: '100%' }} transition={{ duration: auto ? 7 : 0, ease: 'linear' }} />}
            </button>
          ))}
        </div>
        <span className="tabular text-[13px] text-muted-foreground">{i + 1}/{slides.length}</span>
        <Button size="icon" variant="outline" aria-label={tx('Sonraki')} onClick={() => go(1)} disabled={i === slides.length - 1}><ChevronRight className="size-4" /></Button>
      </div>
    </div>,
    document.body,
  )
}
