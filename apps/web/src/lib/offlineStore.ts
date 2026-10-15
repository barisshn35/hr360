/**
 * Dalga 12 (madde 85): çevrimdışı taslak kuyruğu için küçük IndexedDB deposu.
 *
 * localStorage yerine IndexedDB: eşzamanlı sekmelerde daha güvenli, servis çalışanından da
 * okunabilir ve kota daha geniş. IndexedDB yoksa/kapalıysa (gizli pencere, eski tarayıcı) çağıran
 * null/false alır ve localStorage'a düşer. Yazımlar kayıt bazındadır (ekle/sil); liste hiçbir zaman
 * temizlenip yeniden yazılmaz, böylece eşzamanlı sekmeler birbirinin taslağını ezmez.
 */
const DB_NAME = 'hr360-offline'
const STORE = 'queue'

let dbPromise: Promise<IDBDatabase | null> | null = null

function openDb(): Promise<IDBDatabase | null> {
  if (dbPromise) return dbPromise
  dbPromise = new Promise((resolve) => {
    try {
      if (typeof indexedDB === 'undefined') return resolve(null)
      const req = indexedDB.open(DB_NAME, 1)
      req.onupgradeneeded = () => {
        if (!req.result.objectStoreNames.contains(STORE)) req.result.createObjectStore(STORE, { keyPath: 'id' })
      }
      req.onsuccess = () => resolve(req.result)
      req.onerror = () => resolve(null)
      req.onblocked = () => resolve(null)
    } catch {
      resolve(null)
    }
  })
  return dbPromise
}

/** Tüm kayıtlar; IndexedDB kullanılamıyorsa null. */
export async function idbAll<T>(): Promise<T[] | null> {
  const db = await openDb()
  if (!db) return null
  return new Promise((resolve) => {
    try {
      const req = db.transaction(STORE, 'readonly').objectStore(STORE).getAll()
      req.onsuccess = () => resolve(req.result as T[])
      req.onerror = () => resolve(null)
    } catch {
      resolve(null)
    }
  })
}

/**
 * Kayıt ekler/siler — tek işlemde (transaction), yalnızca dokunulan kayıtlar. Birden çok sekme aynı
 * anda yazdığında birbirinin taslağını silmez (eskiden liste önce temizlenip yeniden yazılıyordu).
 * Başarılıysa true.
 */
export async function idbApply<T extends { id: string }>(ops: { put?: T[]; del?: string[]; clear?: boolean }): Promise<boolean> {
  const db = await openDb()
  if (!db) return false
  return new Promise((resolve) => {
    try {
      const tx = db.transaction(STORE, 'readwrite')
      const store = tx.objectStore(STORE)
      if (ops.clear) store.clear()
      for (const id of ops.del ?? []) store.delete(id)
      for (const it of ops.put ?? []) store.put(it)
      tx.oncomplete = () => resolve(true)
      tx.onerror = () => resolve(false)
      tx.onabort = () => resolve(false)
    } catch {
      resolve(false)
    }
  })
}
