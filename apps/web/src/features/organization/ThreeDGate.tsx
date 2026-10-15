/**
 * 3B görünüm kapısı: WebGL 2 yoksa, kullanıcı 3B'yi kapattıysa ya da 3B parçası yüklenemez/
 * çökerse 2B karşılığını (fallback) bir notla gösterir. 3B içeriği (three.js) yalnızca kapı
 * açıkken ve tembel olarak yüklenir; ana pakete girmez.
 */

import { Component, Suspense, type ReactNode } from 'react'
import { Box, LoaderCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { InfoNote } from '@/components/ui/States'
import { tx } from '@/lib/i18n'
import { useThreeDSupport } from './webgl'

class Boundary extends Component<{ fallback: ReactNode; children: ReactNode }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() {
    return { failed: true }
  }
  componentDidCatch(error: unknown) {
    // Konsol hatası değil uyarı: 2B karşılığı gösteriliyor, kullanıcı için kesinti yok.
    console.warn('3B görünüm açılamadı, 2B gösteriliyor:', error)
  }
  render() {
    if (!this.state.failed) return this.props.children
    return (
      <>
        <div className="px-3 pt-3">
          <InfoNote>{tx('3B görünüm bu tarayıcıda açılamadı; 2B görünüm gösteriliyor.')}</InfoNote>
        </div>
        {this.props.fallback}
      </>
    )
  }
}

export function ThreeDLoading() {
  return (
    <div className="flex h-[60vh] min-h-[380px] items-center justify-center gap-2 text-[13px] text-muted-foreground" role="status">
      <LoaderCircle className="size-4 animate-spin motion-reduce:animate-none" aria-hidden />
      {tx('3B görünüm yükleniyor…')}
    </div>
  )
}

export function ThreeDGate({ children, fallback }: { children: ReactNode; fallback: ReactNode }) {
  const s = useThreeDSupport()
  if (!s.available) {
    return (
      <>
        <div className="px-3 pt-3">
          <InfoNote>{tx('Tarayıcınızda WebGL 2 kullanılamıyor (ya da donanım hızlandırma kapalı); 2B görünüm gösteriliyor.')}</InfoNote>
        </div>
        {fallback}
      </>
    )
  }
  if (s.disabled) {
    return (
      <>
        <div className="flex flex-wrap items-center gap-2 px-3 pt-3 text-[12.5px] text-muted-foreground">
          <span>{tx('3B görünümü kapattınız; 2B görünüm gösteriliyor.')}</span>
          <Button size="xs" variant="outline" onClick={() => s.setDisabled(false)}>
            <Box aria-hidden />
            {tx("3B'yi aç")}
          </Button>
        </div>
        {fallback}
      </>
    )
  }
  return (
    <Boundary fallback={fallback}>
      <Suspense fallback={<ThreeDLoading />}>{children}</Suspense>
    </Boundary>
  )
}

/** 3B görünümün köşesindeki "3B'yi kapat" düğmesi (tercih tarayıcıda hatırlanır). */
export function ThreeDOffButton() {
  const s = useThreeDSupport()
  return (
    <Button size="xs" variant="ghost" onClick={() => s.setDisabled(true)} title={tx('3B görünümü kapat; tercih bu tarayıcıda hatırlanır')}>
      {tx("3B'yi kapat")}
    </Button>
  )
}
