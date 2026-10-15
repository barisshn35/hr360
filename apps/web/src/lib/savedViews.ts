/**
 * Dalga 12 (madde 87): liste ekranlarında kayıtlı filtre/görünümler ve paylaşılabilir bağlantı.
 *
 * Görünüm = adres çubuğu parametrelerinin anlık görüntüsü: tablonun arama/sıralama/filtre durumu
 * `v_` önekli parametrelere yazılır (v_q, v_s=sütun:yön, v_f.<filtre>=değer); sekme gibi sayfanın
 * kendi parametreleri (ör. ?durum=) olduğu gibi korunur. Böylece kaydetme, paylaşma ve uygulama
 * tek biçimle çalışır. Görünüm listesi kişiye özeldir (ui-prefs `views:<tablo>`).
 */
import type { SortState } from '@/components/ui/DataTable'

export const VIEW_PARAM_PREFIX = 'v_'
export const MAX_SAVED_VIEWS = 20

export interface TableViewState {
  q: string
  sort: SortState | null
  filters: Record<string, string>
}

export interface SavedView {
  id: string
  name: string
  /** URLSearchParams.toString() — sayfa parametreleri + v_ önekli tablo durumu. */
  params: string
  createdAt: string
}

export interface SavedViewsPref {
  views: SavedView[]
}

/** Sayfanın kendi parametrelerini korur, tablo durumunu v_ önekli parametrelere yazar. */
export function viewToParams(state: TableViewState, current: URLSearchParams): URLSearchParams {
  const out = new URLSearchParams()
  current.forEach((v, k) => {
    if (!k.startsWith(VIEW_PARAM_PREFIX)) out.append(k, v)
  })
  const q = state.q.trim()
  if (q) out.set('v_q', q)
  if (state.sort) out.set('v_s', `${state.sort.columnId}:${state.sort.dir}`)
  for (const [id, value] of Object.entries(state.filters).sort(([a], [b]) => a.localeCompare(b))) {
    if (value) out.set(`v_f.${id}`, value)
  }
  return out
}

/** Adresteki v_ parametrelerinden tablo durumunu okur; hiç yoksa null. */
export function paramsToView(params: URLSearchParams): Partial<TableViewState> | null {
  let found = false
  const view: Partial<TableViewState> = {}
  const q = params.get('v_q')
  if (q !== null) {
    view.q = q.slice(0, 200)
    found = true
  }
  const s = params.get('v_s')
  if (s) {
    const m = /^([\w.-]{1,64}):(asc|desc)$/.exec(s)
    if (m) {
      view.sort = { columnId: m[1], dir: m[2] as 'asc' | 'desc' }
      found = true
    }
  }
  const filters: Record<string, string> = {}
  params.forEach((v, k) => {
    if (k.startsWith('v_f.') && k.length > 4) filters[k.slice(4)] = v.slice(0, 200)
  })
  if (Object.keys(filters).length) {
    view.filters = filters
    found = true
  }
  return found ? view : null
}

/** Adresten v_ parametrelerini atar (uygulandıktan sonra adres sade kalsın). */
export function stripViewParams(params: URLSearchParams): URLSearchParams {
  const out = new URLSearchParams()
  params.forEach((v, k) => {
    if (!k.startsWith(VIEW_PARAM_PREFIX)) out.append(k, v)
  })
  return out
}

/** Aynı adlı görünümün üzerine yazar (büyük/küçük harf duyarsız), en yeni başta; en fazla 20. */
export function addView(list: SavedView[], name: string, params: string, id: string, now: Date = new Date()): SavedView[] {
  const clean = name.trim().slice(0, 60)
  if (!clean) return list
  const key = clean.toLocaleLowerCase('tr-TR')
  const rest = list.filter((v) => v.name.toLocaleLowerCase('tr-TR') !== key)
  return [{ id, name: clean, params, createdAt: now.toISOString() }, ...rest].slice(0, MAX_SAVED_VIEWS)
}

export const removeView = (list: SavedView[], id: string) => list.filter((v) => v.id !== id)
