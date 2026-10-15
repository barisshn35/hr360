import { useQuery } from '@tanstack/react-query'
import { Network } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { mlInsightsApi } from '@/api/mlInsights'
import { formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/**
 * Şirket beceri haritası (ML dalgası 2, madde 47): profil becerileri ve 3+ seviyedeki yetkinlik
 * değerlendirmelerinden beceri ↔ departman sayıları ve birlikte görülen beceriler. 5'ten az kişide
 * görülen beceri, hücre ve bağlar gösterilmez. Görüntüleme erişim kaydına yazılır.
 */
export function SkillGraphPanel() {
  const q = useQuery({ queryKey: ['skills', 'graph'], queryFn: ({ signal }) => mlInsightsApi.skillGraph(signal), retry: false, staleTime: 5 * 60_000 })
  const g = q.data
  const max = Math.max(1, ...(g?.nodes.map((n) => n.people) ?? [1]))
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Network className="size-4 text-primary" />{' '}{tx('Şirket beceri haritası')}</span>}
        note={tx('Profil becerileri ve 3 ve üstü seviyedeki yetkinlik değerlendirmeleri. 5 kişiden az görülen beceriler gösterilmez.')} />
      <PanelBody className="space-y-5">
        {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{errMsg(q.error, tx('Beceri haritası oluşturulamadı.'))}</p> : g && (
          g.nodes.length === 0 ? <InfoNote>{tx('En az 5 kişide görülen bir beceri yok. Çalışanlar profillerine beceri ekledikçe harita oluşur.')}</InfoNote> : (
            <>
              <div className="grid gap-5 lg:grid-cols-2">
                <div>
                  <p className="mb-2 text-[13px] font-medium">{tx('En yaygın beceriler')}</p>
                  <ul className="space-y-1.5">
                    {g.nodes.slice(0, 15).map((n) => (
                      <li key={n.skill} className="text-[12.5px]">
                        <div className="flex items-center gap-2">
                          <span className="w-40 truncate" title={n.skill}>{n.skill}</span>
                          <div className="h-2 flex-1 overflow-hidden rounded-full bg-muted"><div className="h-full rounded-full bg-primary" style={{ width: `${(100 * n.people) / max}%` }} /></div>
                          <span className="w-8 text-right tabular-nums">{n.people}</span>
                        </div>
                        {n.departments.length > 0 && <p className="pl-[10.5rem] text-[11.5px] text-muted-foreground">{n.departments.slice(0, 3).map((d) => `${d.department} ${d.people}`).join(' · ')}</p>}
                      </li>
                    ))}
                  </ul>
                </div>
                <div>
                  <p className="mb-2 text-[13px] font-medium">{tx('Birlikte görülen beceriler')}</p>
                  {g.edges.length === 0 ? <p className="text-[12.5px] text-muted-foreground">{tx('En az 5 kişide birlikte görülen beceri çifti yok.')}</p> : (
                    <ul className="space-y-1 text-[12.5px]">
                      {g.edges.slice(0, 15).map((e) => (
                        <li key={`${e.a}|${e.b}`} className="flex items-center gap-2">
                          <span className="min-w-0 flex-1 truncate">{e.a} ↔ {e.b}</span>
                          <span className="tabular-nums text-muted-foreground">{tx('{0} kişi · benzerlik {1}', [e.people, formatNumber(e.jaccard)])}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </div>
              <div>
                <p className="mb-2 text-[13px] font-medium">{tx('Departmanlara göre öne çıkan beceriler')}</p>
                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                  {g.departments.filter((d) => d.top_skills.length > 0).map((d) => (
                    <div key={d.department} className="rounded-xl border border-border p-3 text-[12.5px]">
                      <p className="font-medium">{d.department === 'Diğer' ? tx('Diğer') : d.department} <span className="text-muted-foreground">· {tx('{0} kişi', [d.people])}</span></p>
                      <p className="mt-1 text-muted-foreground">{d.top_skills.map((s) => `${s.skill} (${s.people})`).join(', ')}</p>
                    </div>
                  ))}
                </div>
              </div>
              <p className="text-[11.5px] text-muted-foreground">
                {tx('{0} çalışandan {1} kişinin beceri bilgisi var. Gizlenen: {2} beceri, {3} departman hücresi, {4} kişi (5\'ten küçük gruplar).', [g.people, g.people_with_skills, g.hidden_skills, g.hidden_cells, g.hidden_people])}
              </p>
            </>
          )
        )}
      </PanelBody>
    </Panel>
  )
}
