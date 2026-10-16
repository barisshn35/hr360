/**
 * Ekipler — `/panel/organizasyon/ekipler`.
 *
 *   Diyagram  → herkes: Şirket → Departman → Ekip → Üyeler, salt okunur.
 *   Yönetim   → yalnızca `team:manage`: ekip kur, lider ata, üye ekle/çıkar.
 *   Ekip ağı  → yalnızca İK: ekipler arası etkileşimin 3B ağı (`?gorunum=ag`, tembel yüklenir).
 *
 * Görünüm ve seçili ekip adreste tutulur (`?gorunum=yonetim&ekip=…`);
 * bağlantı paylaşılabilir, geri tuşu doğru çalışır.
 */

import { lazy, Suspense, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { Network, Plus, Presentation, Settings2, Share2, Upload } from 'lucide-react'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorState, InfoNote } from '@/components/ui/States'
import { CountUp } from '@/motion/primitives'
import { Segmented, errorText } from '../components/controls'
import { PerfPageHeader } from '../components/PerfPageHeader'
import { useDepartments } from '../hooks'
import { OrgDiagram, type SheetTarget } from './OrgDiagram'
import { TeamFormDialog } from './TeamDialogs'
import { ImportTeamsModal } from './ImportTeamsModal'
import { TeamSheet } from './TeamSheet'
import { TeamsManage } from './TeamsManage'
import { useOrgTree } from './useOrgTree'
import { tx } from '@/lib/i18n'
import { useTenantScope } from '@/lib/tenant-scope'
import { RowsSkeleton } from '@/components/ui/States'

// 3B ekip ağı: ayrı parça (three.js ayrıca, yalnızca 3B çizilecekse indirilir).
const TeamNetworkView = lazy(() => import('@/features/organization/TeamNetworkView'))

type View = 'diyagram' | 'yonetim' | 'ag'

export function TeamsPage() {
  const { can, hasRole } = useAuth()
  const { hasTenant } = useTenantScope()
  const canManage = can('team:manage')
  // Etkileşim verisinin toplu görünümü: yalnızca İK / şirket yöneticisi (sunucu da RequireHrAdmin).
  const canNetwork = (hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')) && hasTenant
  const [params, setParams] = useSearchParams()
  const requested = params.get('gorunum')
  const view: View = canManage && requested === 'yonetim' ? 'yonetim' : canNetwork && requested === 'ag' ? 'ag' : 'diyagram'
  const selected = params.get('ekip')
  const [showInactive, setShowInactive] = useState(false)
  const [sheet, setSheet] = useState<SheetTarget | null>(null)
  const [creating, setCreating] = useState(false)
  const [importing, setImporting] = useState(false)

  const org = useOrgTree({ includeInactive: view === 'yonetim' && showInactive })
  const depts = useDepartments()

  const setParam = (next: Partial<Record<'gorunum' | 'ekip', string | null>>) => {
    const p = new URLSearchParams(params)
    for (const [k, v] of Object.entries(next)) {
      if (v) p.set(k, v)
      else p.delete(k)
    }
    setParams(p, { replace: true })
  }

  const company = org.tree[0]
  const unassigned = org.unassignedCount
  const stats = company
    ? [
        { label: tx('Departman'), value: org.tree.reduce((a, c) => a + c.departmentCount, 0) },
        { label: tx('Etkin ekip'), value: org.tree.reduce((a, c) => a + c.teamCount, 0) },
        { label: tx('Ekip üyesi'), value: org.tree.reduce((a, c) => a + c.memberCount, 0) },
        // Çalışan rolünde hesaplanamaz (tam çalışan listesi yönetici yetkisi ister) — hiç gösterilmez.
        ...(unassigned !== null ? [{ label: tx('Ekipte olmayan'), value: unassigned }] : []),
      ]
    : []

  return (
    <div className="mx-auto w-full max-w-7xl">
      <PerfPageHeader
        eyebrow={tx('Organizasyon')}
        title={tx('Ekipler')}
        description={
          canManage
            ? tx('Şirket → departman → ekip → üyeler. Diyagram herkese açık; yönetim görünümünde ekip kurar, lider atar ve üyelikleri yönetirsiniz.')
            : tx('Şirketin departmanları, ekipleri ve ekip üyeleri. Bir ekibe tıklayıp üyelerini görebilirsiniz.')
        }
        actions={
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" asChild>
              <Link to="/panel/organizasyon/sunum">
                <Presentation aria-hidden />
                {tx('Sunum modu')}
              </Link>
            </Button>
            {canManage && (
              <Button variant="outline" onClick={() => setImporting(true)}>
                <Upload aria-hidden />
                {tx('İçe aktar')}
              </Button>
            )}
            {canManage && (
              <Button onClick={() => (view === 'yonetim' ? setCreating(true) : (setParam({ gorunum: 'yonetim' }), setCreating(true)))}>
                <Plus aria-hidden />
                {tx('Yeni ekip')}
              </Button>
            )}
          </div>
        }
      >
        <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
          {canManage || canNetwork ? (
            <Segmented
              ariaLabel={tx('Görünüm')}
              value={view}
              onChange={(v) => setParam({ gorunum: v === 'diyagram' ? null : v })}
              options={[
                { value: 'diyagram' as View, label: <span className="inline-flex items-center gap-1.5"><Network className="size-3.5" aria-hidden />{tx('Diyagram')}</span> },
                ...(canManage ? [{ value: 'yonetim' as View, label: <span className="inline-flex items-center gap-1.5"><Settings2 className="size-3.5" aria-hidden />{tx('Yönetim')}</span> }] : []),
                ...(canNetwork ? [{ value: 'ag' as View, label: <span className="inline-flex items-center gap-1.5"><Share2 className="size-3.5" aria-hidden />{tx('Ekip ağı (3B)')}</span> }] : []),
              ]}
            />
          ) : (
            <span />
          )}
          <dl className="grid max-w-lg gap-4 sm:gap-6" style={{ gridTemplateColumns: `repeat(${stats.length || 4}, minmax(0, 1fr))` }}>
            {org.isPending
              ? [0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-9 w-16" />)
              : stats.map((s, i) => (
                  <div key={s.label} className="min-w-0">
                    <dt className="truncate text-[11px] text-muted-foreground">{s.label}</dt>
                    <dd className="text-[20px] leading-tight font-semibold">
                      <CountUp to={s.value} duration={0.9} delay={0.08 * i} />
                    </dd>
                  </div>
                ))}
          </dl>
        </div>
      </PerfPageHeader>

      {org.namesUnavailable && (
        <div className="mb-4">
          <InfoNote>{tx('Çalışan dizinine erişilemediği için bazı adlar görünmeyebilir.')}</InfoNote>
        </div>
      )}

      {org.error ? (
        <Panel>
          <ErrorState title={tx('Ekipler alınamadı')} message={errorText(org.error)} onRetry={org.refetch} />
        </Panel>
      ) : org.isPending ? (
        <Panel className="flex min-h-[420px] flex-col items-center justify-center gap-6 p-8" aria-busy="true">
          <span className="sr-only">{tx('Ekipler yükleniyor')}</span>
          <Skeleton className="h-20 w-60 rounded-2xl" />
          <div className="flex gap-6">
            {[0, 1, 2, 3].map((i) => (
              <Skeleton key={i} className="h-14 w-40 rounded-xl" />
            ))}
          </div>
          <div className="flex gap-4">
            {[0, 1, 2, 3, 4].map((i) => (
              <Skeleton key={i} className="h-24 w-44 rounded-xl" />
            ))}
          </div>
        </Panel>
      ) : view === 'ag' ? (
        <Suspense fallback={<Panel><RowsSkeleton rows={6} columns={4} /></Panel>}>
          <TeamNetworkView />
        </Suspense>
      ) : view === 'yonetim' ? (
        <TeamsManage
          tree={org.tree}
          departments={depts.list}
          selectedId={selected}
          onSelect={(id) => setParam({ ekip: id })}
          showInactive={showInactive}
          onShowInactive={setShowInactive}
          onShowInDiagram={(id) => setParam({ gorunum: null, ekip: id })}
        />
      ) : (
        <OrgDiagram tree={org.tree} onOpen={setSheet} highlightTeamId={selected} />
      )}

      <TeamSheet
        target={sheet}
        onClose={() => setSheet(null)}
        canManage={canManage}
        onManage={(id) => {
          setSheet(null)
          setParam({ gorunum: 'yonetim', ekip: id })
        }}
      />

      {creating && (
        <TeamFormDialog team={null} departments={depts.list} onClose={() => setCreating(false)} onSaved={(t) => setParam({ gorunum: 'yonetim', ekip: t.id })} />
      )}
      {canManage && <ImportTeamsModal open={importing} onClose={() => setImporting(false)} departments={depts.list} />}
    </div>
  )
}
