/**
 * Dalga 12 (madde 89): kişiselleştirilebilir ana panel — kart sırası ve gizli kartlar (saf; birim testli).
 * Kayıt kişiye özel (ui-prefs `dashboard`); kayıt yoksa role göre varsayılan düzen kullanılır.
 * Yetkiyle görünmeyen kartlar düzende olsa da çizilmez (yetki sunucuda ayrıca denetlenir).
 */
export const WIDGET_IDS = ['welcome', 'queue', 'pending', 'overdue', 'kpis', 'chart', 'organization', 'modules', 'pinned'] as const
export type WidgetId = (typeof WIDGET_IDS)[number]

export interface DashboardPref {
  order: string[]
  hidden: string[]
}

export type DashboardRole = 'employee' | 'manager' | 'hr'

/** Rol varsayılanı: çalışan sade, yönetici onay odaklı, İK geniş. */
export function defaultLayout(role: DashboardRole): DashboardPref {
  switch (role) {
    case 'employee':
      return { order: ['welcome', 'pending', 'kpis', 'modules', 'queue', 'chart', 'organization', 'overdue', 'pinned'], hidden: ['chart'] }
    case 'manager':
      return { order: ['welcome', 'queue', 'overdue', 'pending', 'chart', 'kpis', 'modules', 'organization', 'pinned'], hidden: [] }
    default:
      return { order: ['welcome', 'pending', 'overdue', 'kpis', 'chart', 'queue', 'organization', 'modules', 'pinned'], hidden: [] }
  }
}

const isWidget = (id: string): id is WidgetId => (WIDGET_IDS as readonly string[]).includes(id)

/**
 * Görünür kartların sırası. Kayıtta olmayan (sonradan eklenen) kartlar varsayılandaki yerine göre
 * sona eklenir; bilinmeyen kimlikler atılır.
 */
export function resolveLayout(available: WidgetId[], saved: DashboardPref | null | undefined, defaults: DashboardPref): { order: WidgetId[]; hidden: Set<WidgetId> } {
  const base = saved ?? defaults
  const order: WidgetId[] = []
  for (const id of [...base.order, ...defaults.order, ...WIDGET_IDS]) {
    if (isWidget(id) && available.includes(id) && !order.includes(id)) order.push(id)
  }
  const hidden = new Set<WidgetId>((base.hidden ?? []).filter(isWidget))
  return { order, hidden }
}

/** Kartı bir yukarı/aşağı taşır (sınırda değişmez). */
export function moveWidget(order: WidgetId[], id: WidgetId, dir: -1 | 1): WidgetId[] {
  const i = order.indexOf(id)
  const j = i + dir
  if (i < 0 || j < 0 || j >= order.length) return order
  const next = [...order]
  ;[next[i], next[j]] = [next[j], next[i]]
  return next
}
