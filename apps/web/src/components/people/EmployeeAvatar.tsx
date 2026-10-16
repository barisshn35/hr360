import { useEmployeePhoto, usePhotoUrl } from '@/api/employeePhoto'
import { Initials } from '@/features/shared/kit'
import { cn } from '@/lib/utils'

/**
 * Çalışan avatarı: fotoğraf varsa fotoğraf, yoksa baş harfler. `version` verilmezse sürüm dizinden
 * (önbellekte) bulunur; çalışan kaydı elde olan ekranlar `version` geçerek dizin isteğinden kaçınır.
 */
export function EmployeeAvatar({ id, name, size = 36, version, className }: {
  id: string | null | undefined
  name: string
  size?: number
  version?: number | null
  className?: string
}) {
  const fromDir = useEmployeePhoto(version === undefined ? id : null)
  const direct = usePhotoUrl(version === undefined ? null : id, version)
  const url = version === undefined ? fromDir : direct
  if (!url) return <Initials name={name} size={size} className={className} />
  return (
    <img src={url} alt="" width={size} height={size} draggable={false}
      className={cn('shrink-0 rounded-full object-cover ring-1 ring-white/10', className)}
      style={{ width: size, height: size }} />
  )
}
