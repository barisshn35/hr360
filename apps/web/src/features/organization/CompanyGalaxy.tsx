/**
 * Şirket grubu 3B "galaksi" görünümü (Organizasyon → 3B grup görünümü): şirketler merkezde,
 * departmanlar seviyelerine göre iç içe yörüngelerde. Boyut kişi sayısı; kişi sayısını
 * göremeyen (çalışan rolü) kullanıcıda departman sayısı. Yerleşim `galaxyLayout` (saf), çizim
 * `Scene3D`. Yanındaki liste erişilebilir karşılıktır ve 3B açılamazsa tek başına kalır.
 * Bu modül tembel yüklenir; three.js ise ayrıca, yalnızca 3B gerçekten çizilecekse.
 */

import { lazy, useCallback, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Building2, ExternalLink } from 'lucide-react'
import { useEmployees } from '@/api/queries'
import type { Company } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { activeAssignment, isFormerEmployee } from './orgChartModel'
import { deptColor } from './orgEncoding'
import { galaxyLayout, labelCandidates, type Node3D } from './org3d'
import { ThreeDGate, ThreeDOffButton } from './ThreeDGate'

// three.js yalnızca WebGL varsa ve 3B kapatılmamışsa indirilir (kapı açılınca).
const Scene3D = lazy(() => import('./Scene3D'))

const companyKey = (id: string) => `company:${id}`

export function CompanyGalaxy({ companies }: { companies: Company[] }) {
  const { can } = useAuth()
  const navigate = useNavigate()
  const peopleKnown = can('employee:viewAll')
  const employees = useEmployees({ enabled: peopleKnown })

  /** Departmanın kendi kişi sayısı (aktif atamaya göre). */
  const headcount = useMemo(() => {
    const m = new Map<string, number>()
    for (const e of employees.data ?? []) {
      if (isFormerEmployee(e)) continue
      const a = activeAssignment(e)
      if (a) m.set(a.departmentId, (m.get(a.departmentId) ?? 0) + 1)
    }
    return m
  }, [employees.data])

  const data = useMemo(
    () =>
      galaxyLayout(
        companies.map((c) => ({ id: c.id, name: c.name, departments: c.departments ?? [] })),
        (id) => headcount.get(id) ?? 0,
        peopleKnown,
      ),
    [companies, headcount, peopleKnown],
  )
  const byId = useMemo(() => new Map(data.nodes.map((n) => [n.id, n])), [data])
  const [selected, setSelected] = useState<string | null>(null)

  const colorOf = useCallback((n: Node3D) => (n.kind === 'company' ? 'hsl(var(--primary))' : deptColor(n.colorIndex)), [])
  const subOf = useCallback(
    (n: Node3D) => {
      if (n.kind === 'company') {
        const c = companies.find((x) => companyKey(x.id) === n.id)
        const depts = c?.departments?.length ?? 0
        return peopleKnown ? tx('{0} departman · {1} kişi', [formatNumber(depts), formatNumber(n.value)]) : tx('{0} departman', [formatNumber(depts)])
      }
      return peopleKnown ? tx('{0} kişi (alt birimler dahil)', [formatNumber(n.value)]) : n.childCount ? tx('{0} alt departman', [n.childCount]) : tx('Departman')
    },
    [companies, peopleKnown],
  )
  const labelOf = useCallback((n: Node3D) => ({ title: n.name, sub: subOf(n) }), [subOf])
  const highlight = useMemo(() => {
    const s = new Set<string>()
    let cur = selected ? byId.get(selected) : undefined
    while (cur) {
      s.add(cur.id)
      cur = cur.parentId ? byId.get(cur.parentId) : undefined
    }
    return s
  }, [selected, byId])
  const labelIds = useMemo(() => labelCandidates(data.nodes, { selected, highlight }), [data, selected, highlight])
  const [center, setCenter] = useState<{ id: string; seq: number } | null>(null)

  const sel = selected ? byId.get(selected) : undefined
  const open = (n: Node3D) =>
    navigate(n.kind === 'company' ? `/panel/organizasyon/${n.companyId}` : `/panel/organizasyon/${n.companyId}?gorunum=sema&sec=d:${n.id}`)

  const pick = (id: string, fly = false) => {
    setSelected((s) => (s === id ? null : id))
    if (fly) setCenter({ id, seq: Date.now() })
  }

  // Erişilebilir karşılık: şirketler ve kök departmanları (ayrıntı şirketin şemasında).
  const list = (
    <ul className="max-h-[66vh] min-h-[200px] space-y-2 overflow-auto p-3 text-[13px]" aria-label={tx('Şirketler ve kök departmanlar')}>
      {companies.map((c) => {
        const cid = companyKey(c.id)
        const cn_ = byId.get(cid)
        const roots = data.nodes.filter((n) => n.companyId === c.id && n.depth === 1)
        return (
          <li key={c.id}>
            <button
              type="button"
              aria-pressed={selected === cid}
              onClick={() => pick(cid, true)}
              className={cn('flex w-full items-center gap-2 rounded-md px-2 py-1 text-left font-semibold hover:bg-muted', selected === cid && 'bg-primary/10 text-primary')}
            >
              <Building2 className="size-4 shrink-0" aria-hidden />
              <span className="min-w-0 flex-1 truncate">{c.name}</span>
              {cn_ && <span className="tabular shrink-0 text-[11.5px] font-normal text-muted-foreground">{subOf(cn_)}</span>}
            </button>
            {roots.length > 0 && (
              <ul className="mt-0.5 ml-4 border-l border-border pl-2">
                {roots.map((n) => (
                  <li key={n.id}>
                    <button
                      type="button"
                      aria-pressed={selected === n.id}
                      onClick={() => pick(n.id, true)}
                      className={cn('flex w-full items-center gap-2 rounded-md px-2 py-0.5 text-left hover:bg-muted', selected === n.id && 'bg-primary/10 text-primary')}
                    >
                      <span aria-hidden className="size-2 shrink-0 rounded-full" style={{ background: deptColor(n.colorIndex) }} />
                      <span className="min-w-0 flex-1 truncate">{n.name}</span>
                      <span className="tabular shrink-0 text-[11.5px] text-muted-foreground">{subOf(n)}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </li>
        )
      })}
    </ul>
  )

  return (
    <div className="overflow-hidden rounded-xl border border-border bg-card">
      <ThreeDGate fallback={list}>
        <div className="grid lg:grid-cols-[minmax(0,1fr)_320px]">
          <div className="relative">
            <Scene3D
              className="h-[66vh] min-h-[400px] bg-[radial-gradient(hsl(var(--border))_1px,transparent_1px)] [background-size:18px_18px]"
              data={data}
              colorOf={colorOf}
              labelOf={labelOf}
              labelIds={labelIds}
              selected={selected}
              highlight={highlight}
              onPick={(id) => pick(id)}
              centerRequest={center}
              autoRotate={!selected}
              ariaLabel={tx('3B şirket grubu görünümü: şirketler merkezde, departmanlar seviyelerine göre yörüngede. Ok tuşlarıyla döndürün, + ve − ile yakınlaştırın. Şirketler ve departmanlar yandaki listede de gezilebilir.')}
            />
            {sel && (
              <div className="absolute top-3 left-3 z-10 flex max-w-[calc(100%-24px)] flex-wrap items-center gap-1.5 rounded-lg border border-border bg-card/95 p-1.5 pl-3 text-[12px] shadow-md backdrop-blur">
                <span className="min-w-0 truncate font-medium">{sel.name}</span>
                <span className="text-muted-foreground">{subOf(sel)}</span>
                <Button size="sm" variant="ghost" className="h-7 px-2" onClick={() => open(sel)}>
                  <ExternalLink className="size-3.5" aria-hidden />
                  {sel.kind === 'company' ? tx('Şirketi aç') : tx('Şemada göster')}
                </Button>
              </div>
            )}
            <div className="pointer-events-none absolute right-2 bottom-2 left-2 flex flex-wrap items-end justify-between gap-2 text-[11.5px] text-muted-foreground">
              <span className="rounded bg-card/85 px-1.5 py-0.5">
                {peopleKnown ? tx('Küre boyutu kişi sayısını gösterir.') : tx('Küre boyutu departman sayısını gösterir.')}
                {data.truncated > 0 && ` ${tx('Performans için {0} departman çizilmedi.', [formatNumber(data.truncated)])}`}
              </span>
              <span className="pointer-events-auto rounded bg-card/85">
                <ThreeDOffButton />
              </span>
            </div>
          </div>
          <aside className="border-t border-border lg:border-t-0 lg:border-l">{list}</aside>
        </div>
      </ThreeDGate>
    </div>
  )
}

export default CompanyGalaxy
