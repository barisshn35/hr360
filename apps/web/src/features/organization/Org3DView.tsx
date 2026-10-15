/**
 * Organizasyon şemasının 3B katmanlı görünümü: her hiyerarşi seviyesi bir kat, kat içinde
 * radyal ağaç. Yerleşim `computeLayout('layers3d')` + `layered3d` (saf), çizim `Scene3D`.
 * Seçim, odak, arama vurgusu, kapalı dallar ve matris bağları 2B görünümle aynı durumdan
 * gelir; seçim kartı ortak (`OrgSelectionCard`). Ekran okuyucu ve klavye için yanında
 * anahat ağacı (`aside`) gösterilir. Bu modül ve three.js tembel yüklenir.
 */

import { useCallback, useMemo, type ReactNode } from 'react'
import { formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import type { DepartmentLink } from '@/api/types'
import type { ChartModel } from './orgChartModel'
import { computeLayout, ROOT_ID } from './orgLayouts'
import { labelCandidates, layered3d, type Node3D } from './org3d'
import { OrgSelectionCard } from './OrgSelectionCard'
import Scene3D from './Scene3D'
import { ThreeDOffButton } from './ThreeDGate'

export interface Org3DViewProps {
  model: ChartModel
  rootLabel: string
  focus: string | null
  collapsed: ReadonlySet<string>
  peopleKnown: boolean
  colorOf: (deptId: string) => string
  subtitleOf: (deptId: string) => string
  path: ReadonlySet<string>
  matches: ReadonlySet<string>
  selectedDept: string | null
  matrix: DepartmentLink[] | null
  zoom: number
  onSelect: (deptId: string) => void
  onFocus: (deptId: string) => void
  onToggle: (deptId: string) => void
  centerRequest: { id: string; seq: number } | null
  fitRequest: number
  pulse?: ReadonlySet<string>
  pulseSeq?: number
  /** Erişilebilir karşılık (anahat ağacı) — tuvalin yanında. */
  aside: ReactNode
}

export function Org3DView(props: Org3DViewProps) {
  const { model, focus, collapsed, peopleKnown, colorOf, subtitleOf, path, matches, selectedDept, rootLabel } = props
  const layout = useMemo(
    () => computeLayout(model, 'layers3d', { focus, collapsed, rootLabel, peopleKnown }),
    [model, focus, collapsed, rootLabel, peopleKnown],
  )
  const data = useMemo(() => layered3d(layout, { peopleKnown }), [layout, peopleKnown])
  const nodeColor = useCallback((n: Node3D) => (n.id === ROOT_ID ? 'hsl(var(--primary))' : colorOf(n.id)), [colorOf])
  const labelOf = useCallback(
    (n: Node3D) =>
      n.id === ROOT_ID
        ? { title: rootLabel, sub: peopleKnown ? tx('{0} kişi', [formatNumber(n.value)]) : undefined }
        : { title: n.name, sub: subtitleOf(n.id) + (n.hiddenCount ? tx(', {0} alt departman gizli', [n.hiddenCount]) : '') },
    [rootLabel, peopleKnown, subtitleOf],
  )
  const highlight = useMemo(() => new Set([...path, ...matches]), [path, matches])
  const labelIds = useMemo(() => labelCandidates(data.nodes, { selected: selectedDept, highlight }), [data, selectedDept, highlight])
  const extraLinks = useMemo(
    () => (props.matrix ?? []).map((m) => ({ source: m.fromDepartmentId, target: m.toDepartmentId })),
    [props.matrix],
  )
  const sel = selectedDept ? layout.byId.get(selectedDept) : undefined

  return (
    <div className="grid lg:grid-cols-[minmax(0,1fr)_320px]">
      <div className="relative">
        <Scene3D
          className="h-[72vh] min-h-[420px] bg-[radial-gradient(hsl(var(--border))_1px,transparent_1px)] [background-size:18px_18px]"
          data={data}
          colorOf={nodeColor}
          labelOf={labelOf}
          labelIds={labelIds}
          selected={selectedDept}
          highlight={highlight}
          pulse={props.pulse}
          pulseSeq={props.pulseSeq}
          extraLinks={extraLinks}
          onPick={(id) => {
            if (id !== ROOT_ID) props.onSelect(id)
          }}
          centerRequest={props.centerRequest}
          fitRequest={props.fitRequest}
          zoom={props.zoom}
          autoRotate={!selectedDept && !focus}
          ariaLabel={tx('3B organizasyon şeması: her kat bir hiyerarşi seviyesi. Ok tuşlarıyla döndürün, + ve − ile yakınlaştırın, 0 ile sığdırın. Departmanlar yandaki ağaç listesinde gezilebilir.')}
        />
        {sel && sel.id !== ROOT_ID && (
          <OrgSelectionCard
            name={sel.name}
            subtitle={subtitleOf(sel.id)}
            childCount={sel.childCount}
            hiddenCount={sel.hiddenCount}
            onFocus={() => props.onFocus(sel.id)}
            onToggle={() => props.onToggle(sel.id)}
          />
        )}
        <div className="pointer-events-none absolute right-2 bottom-2 left-2 flex flex-wrap items-end justify-between gap-2 text-[11.5px] text-muted-foreground">
          <span className="rounded bg-card/85 px-1.5 py-0.5">
            {tx('Sürükleyerek döndürün, sağ tuş/iki parmakla kaydırın, tekerlekle yakınlaştırın.')}
            {data.truncated > 0 && ` ${tx('Performans için {0} departman çizilmedi; dalları kapatın ya da odaklanın.', [formatNumber(data.truncated)])}`}
          </span>
          <span className="pointer-events-auto rounded bg-card/85">
            <ThreeDOffButton />
          </span>
        </div>
      </div>
      <aside aria-label={tx('Departman ağacı (3B görünümün erişilebilir karşılığı)')} className="border-t border-border lg:border-t-0 lg:border-l">
        {props.aside}
      </aside>
    </div>
  )
}

export default Org3DView
