import { useCallback, useState, type ReactNode } from 'react'
import Cropper, { type Area } from 'react-easy-crop'
import { Camera, ImageUp, LoaderCircle, Trash2 } from 'lucide-react'
import { photoApi, useEmployeePhoto, useInvalidatePhoto } from '@/api/employeePhoto'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/ui/Toast'
import { tx } from '@/lib/i18n'
import { EmployeeAvatar } from './EmployeeAvatar'

const OUT = 512
const MAX_INPUT_MB = 15

/** Seçilen alanı kare 512 px JPEG'e çevirir (sunucu 512 KB sınırı; EXIF/konum bilgisi atılır). */
async function cropToJpeg(src: string, area: Area): Promise<File> {
  const img = await new Promise<HTMLImageElement>((resolve, reject) => {
    const i = new Image()
    i.onload = () => resolve(i)
    i.onerror = reject
    i.src = src
  })
  const canvas = document.createElement('canvas')
  canvas.width = OUT
  canvas.height = OUT
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error(tx('Fotoğraf işlenemedi'))
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(img, area.x, area.y, area.width, area.height, 0, 0, OUT, OUT)
  const blob = await new Promise<Blob | null>((r) => canvas.toBlob(r, 'image/jpeg', 0.88))
  if (!blob) throw new Error(tx('Fotoğraf işlenemedi'))
  return new File([blob], 'photo.jpg', { type: 'image/jpeg' })
}

/**
 * Çalışan fotoğrafı yükleme (21st.dev "Avatar Uploader" uyarlaması): dosya seçilir, kare alan
 * sürükle/yakınlaştır ile ayarlanır, tarayıcıda 512 px'e küçültülüp yüklenir. Kişinin kendisi ya da İK.
 */
export function PhotoUploader({ employeeId, name, size = 64, children }: {
  employeeId: string
  name: string
  size?: number
  children?: ReactNode
}) {
  const toast = useToast()
  const hasPhoto = !!useEmployeePhoto(employeeId)
  const invalidate = useInvalidatePhoto()
  const [open, setOpen] = useState(false)
  const [src, setSrc] = useState('')
  const [crop, setCrop] = useState({ x: 0, y: 0 })
  const [zoom, setZoom] = useState(1)
  const [area, setArea] = useState<Area | null>(null)
  const [busy, setBusy] = useState(false)

  const onComplete = useCallback((_: Area, px: Area) => setArea(px), [])

  function close() {
    if (busy) return
    if (src) URL.revokeObjectURL(src)
    setSrc(''); setZoom(1); setCrop({ x: 0, y: 0 }); setOpen(false)
  }

  function pick(file: File | undefined) {
    if (!file) return
    if (!/^image\/(png|jpe?g|webp)$/.test(file.type)) { toast.stop(tx('PNG, JPEG ya da WebP bir fotoğraf seçin.')); return }
    if (file.size > MAX_INPUT_MB * 1024 * 1024) { toast.stop(tx('Fotoğraf en fazla {0} MB olabilir.', [MAX_INPUT_MB])); return }
    if (src) URL.revokeObjectURL(src)
    setSrc(URL.createObjectURL(file))
  }

  async function save() {
    if (!src || !area) return
    setBusy(true)
    try {
      await photoApi.upload(employeeId, await cropToJpeg(src, area))
      invalidate()
      toast.ok(tx('Fotoğraf güncellendi.'))
      setBusy(false); close()
    } catch (e) {
      toast.stop(e instanceof Error ? e.message : tx('Fotoğraf yüklenemedi.'))
      setBusy(false)
    }
  }

  async function remove() {
    setBusy(true)
    try {
      await photoApi.remove(employeeId)
      invalidate()
      toast.ok(tx('Fotoğraf kaldırıldı.'))
      setBusy(false); close()
    } catch (e) {
      toast.stop(e instanceof Error ? e.message : tx('Fotoğraf kaldırılamadı.'))
      setBusy(false)
    }
  }

  return (
    <>
      {/* Avatar düğmenin dışında: düğmenin görünür metni (baş harfler) erişilebilir adıyla çelişmesin. */}
      <span className="relative inline-flex shrink-0 rounded-full">
        {children ?? <EmployeeAvatar id={employeeId} name={name} size={size} />}
        <button type="button" onClick={() => setOpen(true)} aria-label={tx('Fotoğrafı değiştir')}
          className="group absolute inset-0 grid cursor-pointer place-items-center rounded-full bg-black/50 text-white opacity-0 transition-opacity hover:opacity-100 focus-visible:opacity-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary">
          <Camera className="size-5" aria-hidden="true" />
        </button>
      </span>

      <Modal open={open} onClose={close} title={tx('Profil fotoğrafı')}
        note={tx('Fotoğraf şirket içinde avatarlarda ve "Ayın çalışanı" kartında görünür. Kare olarak kırpılır.')}
        footer={<>
          {hasPhoto && !src && (
            <Button variant="outline" onClick={() => void remove()} disabled={busy} className="mr-auto">
              <Trash2 className="size-4" />{' '}{tx('Fotoğrafı kaldır')}
            </Button>
          )}
          <Button variant="outline" onClick={close} disabled={busy}>{tx('Vazgeç')}</Button>
          <Button onClick={() => void save()} disabled={!src || busy}>
            {busy && <LoaderCircle className="size-4 animate-spin" />}{tx('Kaydet')}
          </Button>
        </>}>
        {src ? (
          <div className="space-y-3">
            <div className="relative aspect-square w-full overflow-hidden rounded-xl bg-muted">
              <Cropper image={src} crop={crop} zoom={zoom} aspect={1} cropShape="round" showGrid={false}
                onCropChange={setCrop} onZoomChange={setZoom} onCropComplete={onComplete} />
            </div>
            <label className="flex items-center gap-3 text-xs text-muted-foreground">
              {tx('Yakınlaştır')}
              <input type="range" min={1} max={3} step={0.01} value={zoom} onChange={(e) => setZoom(Number(e.target.value))}
                className="flex-1 accent-[hsl(var(--primary))]" />
            </label>
            <button type="button" className="cursor-pointer text-xs text-muted-foreground underline hover:text-foreground" onClick={() => { URL.revokeObjectURL(src); setSrc('') }}>
              {tx('Başka fotoğraf seç')}
            </button>
          </div>
        ) : (
          <label className="flex cursor-pointer flex-col items-center gap-3 rounded-xl border border-dashed border-border px-6 py-8 text-center transition-colors hover:border-primary/50 hover:bg-primary/5"
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => { e.preventDefault(); pick(e.dataTransfer.files?.[0]) }}>
            <EmployeeAvatar id={employeeId} name={name} size={88} />
            <span className="flex items-center gap-1.5 text-sm font-medium"><ImageUp className="size-4" />{tx('Fotoğraf seçin ya da sürükleyin')}</span>
            <span className="text-xs text-muted-foreground">{tx('PNG, JPEG ya da WebP · en fazla {0} MB', [MAX_INPUT_MB])}</span>
            <input type="file" accept="image/png,image/jpeg,image/webp" className="hidden" onChange={(e) => { pick(e.target.files?.[0]); e.target.value = '' }} />
          </label>
        )}
      </Modal>
    </>
  )
}
