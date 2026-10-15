import { useEffect, useRef } from 'react'
import { useSearchParams } from 'react-router-dom'

/**
 * Adresteki tek seferlik bayrağı (`?<ad>=1`) okur, adresten siler ve işlevi çağırır.
 * Dalga 12: komut paleti eylemleri (izin talebi oluştur, masraf ekle, paneli düzenle) sayfaları böyle açar.
 */
export function useNewParamFlag(name: string, open: () => void, allowed = true) {
  const [params, setParams] = useSearchParams()
  const openRef = useRef(open)
  openRef.current = open
  useEffect(() => {
    if (params.get(name) !== '1') return
    const next = new URLSearchParams(params)
    next.delete(name)
    setParams(next, { replace: true })
    if (allowed) openRef.current()
  }, [params, setParams, allowed, name])
}

/**
 * Komut paletindeki "İzin talebi oluştur / Masraf ekle" eylemleri sayfayı `?yeni=1` ile açar; sayfa yeni
 * kayıt penceresini açar. Yetki yoksa pencere açılmaz (palet zaten göstermez; adres elle yazılsa da).
 */
export function useNewParam(allowed: boolean, open: () => void) {
  useNewParamFlag('yeni', open, allowed)
}
