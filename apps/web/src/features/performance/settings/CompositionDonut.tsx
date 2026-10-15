/**
 * "Nihai puan nasıl oluşur?" — iç içe halka.
 *
 *   iç halka:  hedefler | metrikler
 *   dış halka: hedefler | Teknik | Davranışsal | …  (metrik payı kategorilere bölünür)
 *
 * Kullanıcı ağırlıkları değiştirdikçe halka yeniden dağılır; altındaki
 * liste her parçanın nihai puandaki yüzdesini yazar ("Teknik %21,6").
 */

import { Cell, Pie, PieChart, ResponsiveContainer } from 'recharts'
import { CATEGORIES, categoryColor, categoryLabels, categoryWeightKey, type ScoringConfigInput } from '@/api/performance'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { tx, appLocale } from '@/lib/i18n'

export const GOAL_COLOR = 'hsl(var(--foreground) / 0.78)'
export const METRIC_COLOR = 'hsl(var(--primary))'

const oneDecimal = (v: number) => new Intl.NumberFormat(appLocale, { maximumFractionDigits: 1 }).format(v)

export function CompositionDonut({ draft }: { draft: ScoringConfigInput }) {
  const gw = draft.goalWeightPercent
  const mw = draft.metricWeightPercent
  const cats = CATEGORIES.map((c) => ({ c, w: Math.max(0, draft[categoryWeightKey[c]] as number) }))
  const total = cats.reduce((a, x) => a + x.w, 0)

  const inner = [
    { name: 'Hedefler', value: gw, color: GOAL_COLOR },
    { name: 'Metrikler', value: mw, color: METRIC_COLOR },
  ].filter((d) => d.value > 0)

  const outer = [
    { name: 'Hedefler', value: gw, color: GOAL_COLOR },
    ...cats.filter((x) => x.w > 0).map((x) => ({ name: categoryLabels[x.c], value: total > 0 ? (mw * x.w) / total : 0, color: categoryColor[x.c] })),
  ].filter((d) => d.value > 0)

  return (
    <div>
      <div className="relative mx-auto aspect-square w-full max-w-[230px]">
        <ResponsiveContainer width="100%" height="100%">
          <PieChart>
            <Pie
              data={inner}
              dataKey="value"
              innerRadius="42%"
              outerRadius="60%"
              startAngle={90}
              endAngle={-270}
              stroke="hsl(var(--card))"
              strokeWidth={2}
              animationDuration={700}
              isAnimationActive
            >
              {inner.map((d) => (
                <Cell key={d.name} fill={d.color} fillOpacity={d.name === 'Metrikler' ? 0.35 : 0.9} aria-label={`${d.name}: ${Math.round(d.value)}`} />
              ))}
            </Pie>
            <Pie
              data={outer}
              dataKey="value"
              innerRadius="64%"
              outerRadius="92%"
              startAngle={90}
              endAngle={-270}
              paddingAngle={1.5}
              stroke="hsl(var(--card))"
              strokeWidth={2}
              cornerRadius={4}
              animationDuration={900}
              isAnimationActive
            >
              {outer.map((d) => (
                <Cell key={d.name} fill={d.color} aria-label={`${d.name}: ${Math.round(d.value)}`} />
              ))}
            </Pie>
          </PieChart>
        </ResponsiveContainer>
        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
          <span className="text-[10px] font-medium text-muted-foreground">{tx('Nihai puan')}</span>
          <span className="text-[22px] leading-none font-semibold">100</span>
        </div>
      </div>

      <ul className="mt-4 flex flex-col gap-1.5 text-[12px]">
        <li className="flex items-center justify-between gap-2">
          <span className="flex items-center gap-2">
            <span className="size-2.5 rounded-sm" style={{ background: GOAL_COLOR }} />
            {tx('Hedefler')}
          </span>
          <span className="font-semibold">
            %<AnimatedNumber value={gw} format={oneDecimal} />
          </span>
        </li>
        {cats.map((x) => {
          const part = total > 0 ? (mw * x.w) / total : 0
          return (
            <li key={x.c} className="flex items-center justify-between gap-2">
              <span className="flex items-center gap-2 text-muted-foreground">
                <span className="size-2.5 rounded-sm" style={{ background: categoryColor[x.c], opacity: x.w > 0 ? 1 : 0.3 }} />
                {categoryLabels[x.c]}
              </span>
              {x.w > 0 ? (
                <span className="font-semibold">
                  %<AnimatedNumber value={part} format={oneDecimal} />
                </span>
              ) : (
                <span className="text-muted-foreground">{tx('hesaba girmez')}</span>
              )}
            </li>
          )
        })}
      </ul>
      <p className="mt-3 border-t border-border pt-3 text-[11px] leading-relaxed text-muted-foreground">
        {tx('Bir çalışanın hedefi ya da metrik puanı yoksa diğer ayak tam ağırlıkla kullanılır; boş ayağın payı kaybolmaz.')}
      </p>
    </div>
  )
}
