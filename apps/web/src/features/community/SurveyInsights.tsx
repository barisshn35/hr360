/**
 * G18: eNPS eğilimi (yalnızca en az 5 yanıtlı anketler) ve serbest metinlerin yerel,
 * sözlük tabanlı duygu özeti. Metinler hiçbir dış servise gönderilmez.
 */
import { useQuery } from '@tanstack/react-query'
import { CartesianGrid, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { Lock, TrendingUp } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { RowsSkeleton } from '@/components/ui/States'
import { opsApi } from '@/api/opsPlus'
import type { SurveyResults } from '@/api/engagement'
import { formatDate } from '@/lib/format'
import { tx } from '@/lib/i18n'

export function EnpsTrendPanel() {
  const q = useQuery({ queryKey: ['surveys', 'enps-trend'], queryFn: ({ signal }) => opsApi.enpsTrend(signal) })
  const data = (q.data?.points ?? []).map((p) => ({ ...p, label: formatDate(p.date) }))
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><TrendingUp className="size-4 text-primary" />{' '}{tx('eNPS eğilimi')}</span>}
        note={q.data ? tx('Yalnızca en az {0} yanıtlı eNPS anketleri; {1} anket anonimlik için dışarıda.', [q.data.minResponses, q.data.excluded]) : undefined} />
      <PanelBody>
        {q.isPending ? <RowsSkeleton rows={2} /> : data.length === 0 ? (
          <p className="text-[13px] text-muted-foreground">{tx('Eğilim için en az bir eNPS anketinin 5 ya da daha fazla yanıtı olmalı.')}</p>
        ) : (
          <div className="h-56">
            <ResponsiveContainer>
              <LineChart data={data} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="label" fontSize={11} tickLine={false} axisLine={false} />
                <YAxis domain={[-100, 100]} width={36} fontSize={11} tickLine={false} axisLine={false} />
                <ReferenceLine y={0} stroke="hsl(var(--muted-foreground))" strokeDasharray="4 4" />
                <Tooltip contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12 }}
                  formatter={(v, _n, item) => [`${v} (${tx('{0} yanıt', [(item?.payload as { responses: number }).responses])})`, 'eNPS']}
                  labelFormatter={(_l, items) => (items?.[0]?.payload as { title?: string })?.title ?? ''} />
                <Line type="monotone" dataKey="enps" stroke="hsl(var(--primary))" strokeWidth={2.5} dot={{ r: 4 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

type Q = SurveyResults['questions'][number]

export function SentimentSummary({ q }: { q: Q }) {
  const s = q.sentiment
  if (!s) return null
  const total = s.positive + s.negative + s.neutral || 1
  const seg = [
    { k: 'positive', v: s.positive, label: tx('Olumlu'), cls: 'bg-emerald-500' },
    { k: 'neutral', v: s.neutral, label: tx('Nötr'), cls: 'bg-slate-400' },
    { k: 'negative', v: s.negative, label: tx('Olumsuz'), cls: 'bg-rose-500' },
  ]
  return (
    <div className="mb-3 space-y-2 rounded-xl bg-muted/40 p-3">
      <div className="flex h-2.5 overflow-hidden rounded-full" role="img" aria-label={tx('Duygu dağılımı')}>
        {seg.map((x) => <div key={x.k} className={x.cls} style={{ width: `${(100 * x.v) / total}%` }} />)}
      </div>
      <p className="flex flex-wrap gap-3 text-[12.5px]">
        {seg.map((x) => <span key={x.k} className="flex items-center gap-1.5"><span className={`size-2 rounded-full ${x.cls}`} />{x.label}: <b className="tabular">{x.v}</b></span>)}
      </p>
      {s.topKeywords.length > 0 && (
        <p className="flex flex-wrap gap-1.5 text-[12px]">
          {s.topKeywords.map((k) => <span key={k.word} className="rounded-full border border-border bg-background px-2 py-0.5">{k.word} <span className="text-muted-foreground">{k.count}</span></span>)}
        </p>
      )}
      <p className="flex items-center gap-1.5 text-[11.5px] text-muted-foreground"><Lock className="size-3" />{' '}{tx('{0}. Anahtar sözcükler en az iki farklı yanıtta geçenlerdir.', [s.method])}</p>
    </div>
  )
}
