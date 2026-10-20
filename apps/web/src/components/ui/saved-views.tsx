import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Bookmark, Check, Link2, Save, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Modal } from '@/components/ui/Modal'
import { TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { useUiPref } from '@/lib/uiPrefs'
import {
  addView,
  paramsToView,
  removeView,
  stripViewParams,
  viewToParams,
  type SavedViewsPref,
  type TableViewState,
} from '@/lib/savedViews'
import { tx } from '@/lib/i18n'

const EMPTY: SavedViewsPref = { views: [] }

/**
 * Dalga 12 (madde 87): tablonun "Görünümler" menüsü — kayıtlı görünümler (kişiye özel, sunucuda),
 * mevcut görünümü kaydetme ve paylaşılabilir bağlantı. Adreste v_ parametreleri varsa (paylaşılan
 * bağlantı ya da seçilen görünüm) tabloya uygulanır ve adresten atılır.
 */
export function SavedViews({
  viewKey,
  state,
  apply,
}: {
  viewKey: string
  state: TableViewState
  apply: (view: Partial<TableViewState>) => void
}) {
  const toast = useToast()
  const [params, setParams] = useSearchParams()
  const pref = useUiPref<SavedViewsPref>(`views:${viewKey}`, EMPTY)
  const views = pref.value.views ?? []
  const [naming, setNaming] = useState(false)
  const [name, setName] = useState('')
  const applyRef = useRef(apply)
  applyRef.current = apply

  // Adresteki görünüm parametrelerini uygula (paylaşılan bağlantı / seçilen görünüm), sonra adresi sadeleştir.
  const paramString = params.toString()
  useEffect(() => {
    const view = paramsToView(new URLSearchParams(paramString))
    if (!view) return
    applyRef.current(view)
    setParams(stripViewParams(new URLSearchParams(paramString)), { replace: true })
  }, [paramString, setParams])

  const currentParams = () => viewToParams(state, params).toString()

  const copyLink = async () => {
    const qs = currentParams()
    const url = `${window.location.origin}${window.location.pathname}${qs ? `?${qs}` : ''}`
    try {
      await navigator.clipboard.writeText(url)
      toast.ok(tx('Bağlantı kopyalandı: aynı filtrelerle açılır (veriyi yalnızca yetkisi olan görür).'))
    } catch {
      toast.stop(tx('Bağlantı kopyalanamadı.'))
    }
  }

  const save = async () => {
    const clean = name.trim()
    if (!clean) return
    await pref.set({ views: addView(views, clean, currentParams(), crypto.randomUUID()) })
    setNaming(false)
    setName('')
    toast.ok(tx('Görünüm kaydedildi'))
  }

  const current = currentParams()

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="outline" size="sm" className="cursor-pointer" aria-label={tx('Kayıtlı görünümler')}>
            <Bookmark />
            {tx('Görünümler')}
            {views.length > 0 && <span className="tabular text-muted-foreground">{views.length}</span>}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-72">
          <DropdownMenuLabel className="text-[11.5px] font-medium text-muted-foreground">
            {views.length ? tx('Kayıtlı görünümleriniz') : tx('Henüz kayıtlı görünüm yok')}
          </DropdownMenuLabel>
          {views.map((v) => (
            <DropdownMenuItem
              key={v.id}
              onSelect={() => {
                // Kayıtlı görünüm: arama boşsa temizlenir; sekme gibi sayfa parametreleri adrese yazılır.
                const p = new URLSearchParams(v.params)
                apply({ q: '', ...(paramsToView(p) ?? {}) })
                setParams(stripViewParams(p))
              }}
              className="group"
            >
              {v.params === current ? <Check className="size-4 text-primary" /> : <Bookmark className="size-4 opacity-50" />}
              <span className="flex-1 truncate">{v.name}</span>
              <button
                type="button"
                aria-label={tx('"{0}" görünümünü sil', [v.name])}
                className="rounded p-0.5 text-muted-foreground opacity-60 hover:text-destructive hover:opacity-100"
                onClick={(e) => {
                  e.preventDefault()
                  e.stopPropagation()
                  void pref.set({ views: removeView(views, v.id) })
                }}
              >
                <Trash2 className="size-3.5" />
              </button>
            </DropdownMenuItem>
          ))}
          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={() => { setName(''); setNaming(true) }}>
            <Save className="size-4" />
            {tx('Bu görünümü kaydet…')}
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => void copyLink()}>
            <Link2 className="size-4" />
            {tx('Bağlantıyı kopyala')}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <Modal
        open={naming}
        onClose={() => setNaming(false)}
        title={tx('Görünümü kaydet')}
        note={tx('Arama, sıralama, filtre ve sekme seçimi kaydedilir. Görünümler yalnızca size görünür.')}
        footer={
          <>
            <Button variant="outline" onClick={() => setNaming(false)}>{tx('Vazgeç')}</Button>
            <Button type="submit" form="save-view-form" disabled={!name.trim()}>{tx('Kaydet')}</Button>
          </>
        }
      >
        <form id="save-view-form" noValidate onSubmit={(e) => { e.preventDefault(); void save() }}>
          <TextField
            id="save-view-name"
            label={tx('Görünüm adı')}
            required
            maxLength={60}
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            hint={tx('Aynı adlı görünümün üzerine yazılır.')}
          />
        </form>
      </Modal>
    </>
  )
}
