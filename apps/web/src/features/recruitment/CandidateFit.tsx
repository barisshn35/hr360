import { useQuery } from '@tanstack/react-query'
import { Scale } from 'lucide-react'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { ml10Api, type CandidateFit, type FitResult } from '@/api/wave10'
import { errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * Dalga 10 (madde 51): aday–ilan uygunluk puanı — işe alım uzmanına YARDIMCI. Otomatik eleme/ret yoktur;
 * ad, cinsiyet, yaş, fotoğraf ve adres puanlamaya girmez (model kartı: GET /ai/recruit-fit/card).
 */
export function useCandidateFit(postingId: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: ['recruitment', 'fit', postingId],
    queryFn: ({ signal }) => ml10Api.candidateFit(postingId!, signal),
    enabled: enabled && Boolean(postingId),
    staleTime: 60_000,
  })
}

export function FitSummary({ q }: { q: ReturnType<typeof useCandidateFit> }) {
  if (q.isPending) return <div className="px-4 pb-3"><RowsSkeleton rows={1} /></div>
  if (q.isError) return <div className="px-4 pb-3"><ErrorState message={errMsg(q.error)} /></div>
  const p: FitResult['posting'] = q.data.posting
  return (
    <div className="space-y-2 px-4 pb-3">
      <InfoNote>{q.data.note}</InfoNote>
      <p className="text-[12px] text-muted-foreground">
        {tx('İlandan çıkarılan ölçütler')}: {p.required_skills.length ? p.required_skills.join(', ') : tx('zorunlu beceri bulunamadı')}
        {p.min_years ? ` · ${tx('en az {0} yıl deneyim', [p.min_years])}` : ''}
        {p.qualifications.length ? ` · ${tx('{0} nitelik satırı', [p.qualifications.length])}` : ''}
        {p.preferred_skills.length ? ` · ${tx('tercih sebebi: {0}', [p.preferred_skills.join(', ')])}` : ''}
      </p>
    </div>
  )
}

export function FitBadge({ fit }: { fit: CandidateFit | undefined }) {
  if (!fit) return null
  const redacted = Object.values(fit.redactions ?? {}).reduce((a, b) => a + b, 0)
  return (
    <details className="mt-2 rounded-lg border border-dashed border-border px-3 py-2 text-[12px]">
      <summary className="flex cursor-pointer flex-wrap items-center gap-2">
        <Scale className="size-3.5 text-muted-foreground" aria-hidden />
        <span className="text-muted-foreground">{tx('Uygunluk (yardımcı)')}</span>
        {fit.score == null ? <StatusBadge>{tx('Ölçüt yok')}</StatusBadge>
          : <StatusBadge tone={fit.score >= 70 ? 'success' : fit.score >= 40 ? 'info' : 'neutral'}>{tx('{0}/100', [fit.score])}</StatusBadge>}
        {fit.flags.includes('insufficient_text') && <StatusBadge tone="warning">{tx('Metin yetersiz')}</StatusBadge>}
      </summary>
      <ul className="mt-2 list-disc space-y-0.5 pl-4 text-muted-foreground">{fit.reasons.map((r) => <li key={r}>{r}</li>)}</ul>
      {redacted > 0 && <p className="mt-1 text-[11.5px] text-muted-foreground">{tx('Puanlamadan önce {0} kişisel/demografik ifade metinden çıkarıldı.', [redacted])}</p>}
    </details>
  )
}
