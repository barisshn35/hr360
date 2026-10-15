import { useMemo } from 'react'
import { useDirectory } from '@/api/directory'

/**
 * Kimlikten çalışan adı çözer.
 *
 * Modül uçları çoğu yerde yalnızca `employeeId` döndürüyor; ekranda ham
 * kimlik göstermek okunmaz olurdu. Ad henüz yüklenmediyse ya da kayıt
 * silinmişse kimliğin kendisi görünür — bilgi kaybolmaz.
 *
 * NOT: Önceden tam çalışan listesi (`/api/employee/employees`) kullanılıyordu;
 * bu uç yönetici yetkisi istediği için düz çalışanlar 403 alıyor ve İzin,
 * Masraf, İK vakaları, Eğitim, Onay kutusu ekranlarında isim yerine ham
 * kimlik görüyordu. Herkese açık dizin (yalnızca ad) kullanılır; AppShell
 * zaten açılışta çektiği için ek istek doğurmaz.
 */
export function useEmployeeName(): (employeeId: string | null | undefined) => string {
  const directory = useDirectory()

  return useMemo(() => {
    const byId = new Map<string, string>()
    for (const e of directory.data ?? []) byId.set(e.id, e.fullName || `${e.firstName} ${e.lastName}`.trim())
    return (id) => (id ? (byId.get(id) ?? id) : '—')
  }, [directory.data])
}
