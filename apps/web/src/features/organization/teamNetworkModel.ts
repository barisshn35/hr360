/**
 * 3B ekip ağı: sunucu yanıtını (yalnızca ekip düğümleri/bağları; küçük ekip ve küçük sayı
 * sunucuda gizlenmiş) sahne verisine çevirir — saf hesap, birim testi `teamNetworkModel.test.ts`.
 *
 * Konum `forceLayout3d` ile istemcide hesaplanır (ekip sayısı yüzlerle sınırlı; web worker
 * gerekmez). Düğüm boyutu üye sayısı, renk departman, bağ kalınlığı etkileşim ağırlığı.
 */

import type { TeamNetwork, TeamNetworkEdge, TeamNetworkNode } from '@/api/governance'
import { tx } from '@/lib/i18n'
import { forceLayout3d, sceneOf, type Node3D, type Scene3DData } from './org3d'

export const OTHER_ID = 'other'

export interface TeamGraph {
  scene: Scene3DData
  /** Departman adı → renk dizini (lejant). "Diğer" düğümü -1. */
  departments: Array<{ name: string; colorIndex: number }>
  nodeById: Map<string, TeamNetworkNode>
  /** Ağırlığa göre azalan bağlar (erişilebilir tablo). */
  edges: TeamNetworkEdge[]
  /** En ağır bağ (kalınlık ölçeği için). */
  maxWeight: number
}

/** Bağ türleri — erişilebilir metin ve araç ipucu. */
export function edgeKinds(e: Pick<TeamNetworkEdge, 'kudos' | 'oneOnOnes' | 'sharedGoals'>): string {
  const parts: string[] = []
  if (e.kudos) parts.push(tx('{0} takdir', [e.kudos]))
  if (e.oneOnOnes) parts.push(tx('{0} birebir görüşme', [e.oneOnOnes]))
  if (e.sharedGoals) parts.push(tx('{0} ortak hedef', [e.sharedGoals]))
  return parts.join(' · ')
}

/** `minWeight`: kullanıcının kaydırıcıyla seçtiği alt sınır (sunucu eşiğinin üstünde ek süzgeç). */
export function shapeTeamNetwork(data: TeamNetwork, minWeight = 0): TeamGraph {
  const deptNames = [...new Set(data.nodes.filter((n) => n.id !== OTHER_ID).map((n) => n.department ?? ''))].sort((a, b) =>
    a.localeCompare(b, 'tr-TR'),
  )
  const colorOfDept = new Map(deptNames.map((d, i) => [d, i]))
  const ids = new Set(data.nodes.map((n) => n.id))
  const edges = data.edges
    .filter((e) => ids.has(e.source) && ids.has(e.target) && e.source !== e.target && e.weight >= minWeight)
    .sort((a, b) => b.weight - a.weight || a.source.localeCompare(b.source) || a.target.localeCompare(b.target))
  const maxWeight = edges.reduce((m, e) => Math.max(m, e.weight), 1)
  const pos = forceLayout3d(
    data.nodes.map((n) => n.id),
    edges.map((e) => ({ source: e.source, target: e.target, weight: e.weight })),
  )
  const nodes: Node3D[] = data.nodes.map((n) => {
    const p = pos.get(n.id) ?? { x: 0, y: 0, z: 0 }
    return {
      id: n.id,
      name: n.name,
      depth: 0,
      parentId: null,
      ...p,
      r: Math.min(30, 5 + 2.4 * Math.sqrt(Math.max(0, n.members))),
      colorIndex: n.id === OTHER_ID ? -1 : (colorOfDept.get(n.department ?? '') ?? 0),
      value: n.members,
      childCount: 0,
      hiddenCount: 0,
      kind: 'team',
    }
  })
  const scene = sceneOf(
    nodes,
    edges.map((e) => ({ source: e.source, target: e.target, weight: e.weight })),
  )
  return {
    scene,
    departments: deptNames.map((name, i) => ({ name: name || tx('Departmansız'), colorIndex: i })),
    nodeById: new Map(data.nodes.map((n) => [n.id, n])),
    edges,
    maxWeight,
  }
}

/** Seçili ekibin bağları (en güçlüden). */
export function neighborsOf(graph: TeamGraph, id: string): Array<{ other: string; edge: TeamNetworkEdge }> {
  return graph.edges
    .filter((e) => e.source === id || e.target === id)
    .map((e) => ({ other: e.source === id ? e.target : e.source, edge: e }))
}
