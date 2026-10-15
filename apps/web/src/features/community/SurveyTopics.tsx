import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { RowsSkeleton } from '@/components/ui/States'
import { mlInsightsApi } from '@/api/mlInsights'
import { formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/**
 * Açık uçlu yanıtlarda konu + duygu (ML dalgası 2, madde 46): TF-IDF + NMF konuları, Türkçe sözlük
 * tabanlı duygu. Yalnızca en az 5 yanıtı olan konular ve kişisel veri taramasından geçmiş alıntılar
 * gösterilir; görüntüleme erişim kaydına yazıldığı için analiz istek üzerine çalışır.
 */
export function SurveyTopics({ surveyId, questionId }: { surveyId: string; questionId: string }) {
  const [run, setRun] = useState(false)
  const q = useQuery({
    queryKey: ['surveys', surveyId, 'topics', questionId],
    queryFn: ({ signal }) => mlInsightsApi.surveyTopics(surveyId, questionId, signal),
    enabled: run, retry: false, staleTime: 10 * 60_000,
  })
  if (!run) {
    return (
      <Button size="sm" variant="outline" className="mb-3" onClick={() => setRun(true)}>
        <Sparkles className="size-4" /> {tx('Konuları ve duyguyu çıkar')}
      </Button>
    )
  }
  if (q.isPending) return <div className="mb-3"><RowsSkeleton rows={2} /></div>
  if (q.isError) return <p className="mb-3 text-[12.5px] text-muted-foreground">{errMsg(q.error, tx('Konu analizi yapılamadı.'))}</p>
  const a = q.data?.analysis
  if (!q.data?.available || !a) return <p className="mb-3 text-[12.5px] text-muted-foreground">{tx('Konu analizi için en az 5 metin yanıt gerekir.')}</p>
  return (
    <div className="mb-3 space-y-3 rounded-xl border border-border p-3">
      <p className="text-[12.5px] font-medium">{tx('Öne çıkan konular')}</p>
      {a.topics.length === 0 ? (
        <p className="text-[12.5px] text-muted-foreground">{tx('En az 5 yanıtta ortak geçen bir konu bulunamadı.')}</p>
      ) : (
        <ul className="space-y-3">
          {a.topics.map((t) => (
            <li key={t.label} className="text-[13px]">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{t.label}</span>
                <span className="text-[12px] text-muted-foreground">{tx('{0} yanıt (%{1})', [t.responses, formatNumber(t.share_pct)])}</span>
                <span className="text-[12px] text-[hsl(var(--success))]">+{t.sentiment.positive}</span>
                <span className="text-[12px] text-destructive">−{t.sentiment.negative}</span>
                <span className="text-[12px] text-muted-foreground">○{t.sentiment.neutral}</span>
              </div>
              {t.keywords.length > 3 && <p className="text-[12px] text-muted-foreground">{t.keywords.join(' · ')}</p>}
              {t.snippets.length > 0 && (
                <ul className="mt-1 space-y-1">
                  {t.snippets.map((s, i) => <li key={i} className="rounded-lg bg-muted/50 px-2.5 py-1.5 text-[12.5px]">“{s}”</li>)}
                </ul>
              )}
            </li>
          ))}
        </ul>
      )}
      <p className="text-[11.5px] text-muted-foreground">
        {a.hidden_responses > 0 ? tx('{0} yanıt, 5\'ten az yanıtlı konulara düştüğü için gösterilmedi.', [a.hidden_responses]) + ' ' : ''}
        {tx('Alıntılarda TCKN, IBAN, telefon, e-posta ve özel nitelikli veri gizlenir. Yöntem: TF-IDF + NMF konu modeli ve Türkçe duygu sözlüğü; metin dışarı gönderilmez.')}
      </p>
    </div>
  )
}
