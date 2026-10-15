/**
 * Organizasyon şeması zaman kaydırıcısı: geçmiş bir ay sonunu seç ya da oynat; şema o güne
 * göre (zaman makinesi verisiyle) yeniden kurulur. Son durak "bugün"dür (canlı görünüm).
 */

import { useEffect, useState } from 'react'
import { History, LoaderCircle, Pause, Play, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { formatDate } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { nearestStop } from './orgTimeline'

export function OrgTimeSlider(props: {
  stops: string[]
  date: string | null
  onChange: (date: string | null) => void
  loading: boolean
  error: boolean
  /** O tarihin özeti (kişi/departman sayısı). */
  summary: string | null
}) {
  const { stops, date, onChange } = props
  const idx = nearestStop(stops, date)
  const last = stops.length - 1
  const [playing, setPlaying] = useState(false)

  useEffect(() => {
    if (!playing) return
    // Yükleme sürerken bir sonraki adıma geçme (yavaş bağlantıda kareler atlanmasın).
    if (props.loading) return
    const t = window.setTimeout(() => {
      const next = idx + 1
      if (next >= last) {
        setPlaying(false)
        onChange(null)
      } else onChange(stops[next])
    }, 1100)
    return () => window.clearTimeout(t)
  }, [playing, idx, last, stops, onChange, props.loading])

  // Adresteki tarih durak dışında olabilir (paylaşılan bağlantı): etiket gerçek tarihi gösterir.
  const label = date ? formatDate(date) : tx('Bugün')

  return (
    <div className="flex flex-col gap-2 rounded-lg border border-border bg-background/60 p-2.5 sm:flex-row sm:flex-wrap sm:items-center">
      <div className="flex shrink-0 items-center gap-2 text-[13px] font-medium">
        <History className="size-4 text-primary" aria-hidden />
        <span className="tabular min-w-24" aria-live="polite">
          {label}
        </span>
        {props.loading && <LoaderCircle className="size-3.5 animate-spin text-muted-foreground motion-reduce:animate-none" aria-label={tx('Yükleniyor')} />}
      </div>
      <input
        type="range"
        min={0}
        max={last}
        step={1}
        value={idx}
        onChange={(e) => {
          setPlaying(false)
          const i = Number(e.target.value)
          onChange(i >= last ? null : stops[i])
        }}
        aria-label={tx('Şemanın tarihi')}
        aria-valuetext={label}
        className="min-w-0 flex-1 accent-[hsl(var(--primary))]"
      />
      <div className="flex shrink-0 items-center gap-1.5">
        <Button
          size="sm"
          variant="outline"
          onClick={() => {
            if (playing) setPlaying(false)
            else {
              if (idx >= last) onChange(stops[0])
              setPlaying(true)
            }
          }}
        >
          {playing ? <Pause aria-hidden /> : <Play aria-hidden />}
          {playing ? tx('Durdur') : tx('Oynat')}
        </Button>
        <Button size="sm" variant="ghost" disabled={idx === last} onClick={() => (setPlaying(false), onChange(null))}>
          <RotateCcw aria-hidden />
          {tx('Bugüne dön')}
        </Button>
      </div>
      {(props.summary || props.error) && (
        <p className="text-[12px] text-muted-foreground sm:basis-full">
          {props.error ? tx('Bu tarihin verisi alınamadı.') : props.summary}
        </p>
      )}
    </div>
  )
}
