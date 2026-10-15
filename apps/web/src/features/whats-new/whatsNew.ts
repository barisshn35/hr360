/** Dalga 12 (madde 90): "Yenilikler" okunmamış hesabı (saf; birim testli). */
export interface WhatsNewPref { seen: string }

/**
 * Okunmamış not sayısı. Hiç açmamış kullanıcıya yalnızca en son dalga okunmamış sayılır
 * (yeni kullanıcıya "12 okunmamış" gösterilmesin).
 */
export function unreadCount(ids: string[], seen: string | null | undefined): number {
  if (!ids.length) return 0
  if (!seen) return 1
  const s = Number(seen)
  return ids.filter((id) => Number(id) > s).length
}

/** Panel açılınca kaydedilecek en yeni kimlik. */
export const latestId = (ids: string[]) => ids.reduce((m, id) => (Number(id) > Number(m) ? id : m), ids[0] ?? '0')
