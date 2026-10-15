import { useId, useMemo, useState } from 'react';
import { ArrowDown, ArrowRight, ArrowUp } from 'lucide-react';
import {
  ACCENTS,
  formatCompact,
  MetricChart,
  SERIES_COLORS,
  type ChartSeries,
  type ChartView,
  type MetricAccent,
  type MetricSeries,
  type SeriesPoint,
} from './metric-chart';
import { PeriodSelect, ViewToggle, type PeriodOption } from './metric-controls';
import { tx } from '@/lib/i18n'
import { formatPercent } from '@/lib/format'

// Ré-export des types pour que les consommateurs n'importent que ce fichier.
export type { SeriesPoint, MetricSeries, MetricAccent, ChartView, PeriodOption };

export type CardSize = 'sm' | 'md' | 'lg';

export interface ProgressMetricCardProps {
  title: string;
  total?: string | number;
  delta?: string;
  deltaLabel?: string;
  percent?: string;
  trend?: 'up' | 'down';
  unit?: string;
  period?: string;
  periodOptions?: PeriodOption[];
  onPeriodChange?: (option: PeriodOption) => void;
  defaultView?: ChartView;
  accent?: MetricAccent;
  /** Série unique. À fournir, OU `series`. */
  data?: SeriesPoint[];
  /** Plusieurs séries nommées. Prioritaire sur `data`. */
  series?: MetricSeries[];
  defaultIndex?: number;
  size?: CardSize;
  /** Affiche les stats secondaires (peak / low / avg) dans le footer. */
  showStats?: boolean;
  /** Format des valeurs. Par défaut : compact en titre, exact en tooltip. */
  valueFormatter?: (value: number) => string;
  dateFormatter?: (date: string) => string;
  loading?: boolean;
  className?: string;
}

const DEFAULT_PERIODS: PeriodOption[] = [
  { label: tx('Son 7 gün'), points: 4 },
  { label: tx('Son 14 gün'), points: 7 },
  { label: tx('Son 30 gün') },
];

// Part de la card (depuis la droite) occupée par le graphe.
const REGION_W = 62; // %
// Variation sous ce seuil = "stable" → accent neutre.
const NEUTRAL_PCT = 0.5;

const SIZES: Record<
  CardSize,
  {
    minH: string; pad: string; footer: string; title: string; headline: string;
    /** Grafik bolgesinin ustten baslama noktasi - baslik satiriyla (yuzde,
     *  donem secici) eger olur cakisma olur. Boyuta gore degisir cunku
     *  baslik yuksekligi boyuta gore degisiyor. */
    chartTop: string;
  }
> = {
  sm: { minH: 'min-h-[260px]', pad: 'px-6 pt-5', footer: 'px-6 py-3', title: 'text-[15px]', headline: 'text-[46px]', chartTop: 'top-11' },
  md: { minH: 'min-h-[380px]', pad: 'px-8 pt-7', footer: 'px-8 py-4', title: 'text-[17px]', headline: 'text-[72px]', chartTop: 'top-14' },
  lg: { minH: 'min-h-[460px]', pad: 'px-10 pt-9', footer: 'px-10 py-5', title: 'text-[19px]', headline: 'text-[88px]', chartTop: 'top-16' },
};

const sliceWindow = (points: SeriesPoint[], n?: number) =>
  n && n < points.length ? points.slice(-n) : points;

export default function ProgressMetricCard({
  title,
  total,
  delta,
  deltaLabel = tx('bugün'),
  percent,
  trend,
  unit,
  period = tx('Son 30 gün'),
  periodOptions,
  onPeriodChange,
  defaultView = 'curve',
  accent,
  data,
  series,
  defaultIndex,
  size = 'md',
  showStats = true,
  valueFormatter,
  dateFormatter,
  loading = false,
  className = '',
}: ProgressMetricCardProps) {
  const gridId = `grid-${useId().replace(/:/g, '')}`;
  const sz = SIZES[size];
  const shell = `surface relative flex ${sz.minH} w-full flex-col overflow-hidden rounded-xl ${className}`;

  const periods = periodOptions ?? DEFAULT_PERIODS;
  const [selectedLabel, setSelectedLabel] = useState(period);
  const [view, setView] = useState<ChartView>(defaultView);

  // Normalise l'entrée en liste de séries (un `data` simple → une seule série).
  const baseSeries: MetricSeries[] = useMemo(
    () => (series?.length ? series : [{ name: title, data: data ?? [], accent }]),
    [series, data, title, accent],
  );

  const selectedOption =
    periods.find((p) => p.label === selectedLabel) ?? periods[periods.length - 1];

  // Découpe chaque série sur la période choisie.
  const visibleSeries = useMemo(
    () => baseSeries.map((s) => ({ ...s, data: sliceWindow(s.data, selectedOption?.points) })),
    [baseSeries, selectedOption],
  );

  const primary = visibleSeries[0];
  const isMulti = visibleSeries.length > 1;
  const hasData = (primary?.data.length ?? 0) >= 2;

  // Tous les chiffres dérivent de la série principale → la card reste cohérente
  // et réagit au changement de période. Les props restent prioritaires.
  // Yüzde, dönemin TOPLAMINI bir önceki eşit uzunluktaki dönemin toplamıyla karşılaştırır (ör. son 14
  // gün ↔ önceki 14 gün). Eskiden dönemin ilk ve son GÜNÜ karşılaştırılıyordu: ikisi de 0 iken "%0,0"
  // görünürken altta "−190 düne göre" yazıyordu (çelişkili). Önceki dönem verisi yoksa yüzde gösterilmez.
  const stats = useMemo(() => {
    const vals = primary?.data.map((d) => d.value) ?? [];
    const sum = vals.reduce((a, b) => a + b, 0);
    const first = vals[0] ?? 0;
    const last = vals[vals.length - 1] ?? 0;
    const prev = vals[vals.length - 2] ?? first;
    const all = baseSeries[0]?.data ?? [];
    const n = vals.length;
    const hasPrevPeriod = n > 0 && all.length >= 2 * n;
    const prevSum = hasPrevPeriod ? all.slice(-2 * n, -n).reduce((a, d) => a + d.value, 0) : 0;
    const net = sum - prevSum;
    return {
      sum,
      net,
      hasPrevPeriod,
      pct: prevSum ? (net / prevSum) * 100 : 0,
      // Önceki dönem 0 iken yüzde tanımsızdır ("0.0%" ve düz ok yanıltıcıydı).
      fromZero: prevSum === 0 && net !== 0,
      step: last - prev,
      peak: vals.length ? Math.max(...vals) : 0,
      low: vals.length ? Math.min(...vals) : 0,
      avg: vals.length ? sum / vals.length : 0,
    };
  }, [primary, baseSeries]);

  // La couleur dépend du sens (dernier vs premier point), avec une zone neutre.
  const resolvedTrend: 'up' | 'down' | 'flat' =
    trend ?? (!stats.hasPrevPeriod ? 'flat' : stats.fromZero ? (stats.net > 0 ? 'up' : 'down') : Math.abs(stats.pct) < NEUTRAL_PCT ? 'flat' : stats.net >= 0 ? 'up' : 'down');
  const resolvedAccent: MetricAccent =
    accent ?? (resolvedTrend === 'up' ? 'emerald' : resolvedTrend === 'down' ? 'rose' : 'neutral');
  const color = ACCENTS[resolvedAccent];
  const TrendIcon =
    resolvedTrend === 'flat' ? ArrowRight : resolvedTrend === 'down' ? ArrowDown : ArrowUp;

  const fmtCompact = valueFormatter ?? formatCompact;
  const fmtFull = valueFormatter ?? ((n: number) => n.toLocaleString() + (unit ? ` ${unit}` : ''));
  const fmtDate = dateFormatter ?? ((d: string) => d);
  const sign = (n: number) => (n >= 0 ? '+' : '−') + fmtCompact(Math.abs(n));

  const displayTotal = total ?? fmtCompact(stats.sum);
  const displayDelta = delta ?? sign(stats.step);
  const displayPercent =
    percent ?? (!stats.hasPrevPeriod ? null : stats.fromZero ? tx('yeni') : formatPercent(Math.abs(stats.pct) / 100, 1));

  // Couleur de chaque série : accent défini → palette → couleur du titre.
  const chartSeries: ChartSeries[] = visibleSeries.map((s, i) => ({
    name: s.name,
    data: s.data,
    color: s.accent
      ? ACCENTS[s.accent].stroke
      : isMulti
        ? SERIES_COLORS[i % SERIES_COLORS.length]
        : color.stroke,
  }));

  const lastIndex = (primary?.data.length ?? 1) - 1;
  const fallback = Math.min(defaultIndex ?? lastIndex, lastIndex);

  const handlePeriodChange = (option: PeriodOption) => {
    setSelectedLabel(option.label);
    onPeriodChange?.(option);
  };

  if (loading) {
    return (
      <div className={shell} aria-busy="true">
        <div className={`flex flex-1 flex-col ${sz.pad}`}>
          <div className="flex items-center justify-between">
            <div className="h-5 w-32 animate-pulse rounded bg-muted" />
            <div className="h-5 w-24 animate-pulse rounded bg-muted" />
          </div>
          <div className="mt-6 h-14 w-48 animate-pulse rounded-lg bg-muted" />
          <div className="mt-auto h-24 w-full animate-pulse rounded-lg bg-muted/50" />
        </div>
        <div className={`border-t border-foreground/[0.06] ${sz.footer}`}>
          <div className="h-4 w-40 animate-pulse rounded bg-muted" />
        </div>
      </div>
    );
  }

  if (!hasData) {
    return (
      <div className={shell}>
        <div className={`flex flex-1 flex-col ${sz.pad}`}>
          <h3 className={`${sz.title} font-semibold tracking-tight text-foreground`}>{title}</h3>
          <div className="flex flex-1 flex-col items-center justify-center gap-1 py-10 text-center">
            <p className="text-sm font-medium text-foreground">{tx('Henüz veri yok')}</p>
            <p className="text-xs text-muted-foreground">
              {tx('Veri geldiğinde metrikler burada görünecek.')}
            </p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className={shell}>
      {/* Zone du graphe (à droite, derrière le contenu) */}
      <div className={`absolute right-0 bottom-0 z-0 ${sz.chartTop}`} style={{ width: `${REGION_W}%` }}>
        <div
          className="absolute inset-0"
          style={{ background: `linear-gradient(to left, ${color.wash}, transparent 75%)` }}
        />
        <div
          className="absolute inset-0 text-foreground/[0.13]"
          style={{
            WebkitMaskImage: 'linear-gradient(to right, transparent, black 55%)',
            maskImage: 'linear-gradient(to right, transparent, black 55%)',
          }}
        >
          <svg className="h-full w-full" aria-hidden>
            <defs>
              <pattern id={gridId} width="14" height="14" patternUnits="userSpaceOnUse">
                <circle cx="1" cy="1" r="1" fill="currentColor" />
              </pattern>
            </defs>
            <rect width="100%" height="100%" fill={`url(#${gridId})`} />
          </svg>
        </div>

        <MetricChart
          series={chartSeries}
          view={view}
          defaultIndex={fallback}
          valueFormatter={fmtFull}
          dateFormatter={fmtDate}
        />
      </div>

      {/* Contenu principal */}
      <div className={`pointer-events-none relative z-10 flex flex-1 flex-col ${sz.pad}`}>
        <div className="flex items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            <h3 className={`${sz.title} font-semibold tracking-tight text-foreground`}>{title}</h3>
            <ViewToggle value={view} onChange={setView} />
          </div>
          {/* Zemin: chartTop ile grafik zaten bu satirdan uzak baslar,
              ama uc noktalarda (kisa donem, dik egim) egri yine de yakina
              gelebilir. Yari saydam zemin metnin her durumda okunur
              kalmasini garantiliyor. */}
          <div className="flex items-center gap-3.5 rounded-full bg-card/80 px-2.5 py-1 text-[14px] backdrop-blur-sm">
            {displayPercent != null && (
              <span
                className="flex items-center gap-1 font-medium"
                style={{ color: color.text }}
                title={percent ? undefined : tx('Önceki eşit uzunluktaki döneme göre')}
              >
                <TrendIcon size={16} strokeWidth={2.5} />
                {displayPercent}
                {!percent && <span className="sr-only">{' '}{tx('Önceki eşit uzunluktaki döneme göre')}</span>}
              </span>
            )}
            <PeriodSelect
              value={selectedLabel}
              options={periods}
              onChange={handlePeriodChange}
              accentText={color.text}
            />
          </div>
        </div>

        {/* Légende (seulement en multi-séries) */}
        {isMulti && (
          <div className="mt-2.5 flex flex-wrap items-center gap-x-4 gap-y-1">
            {chartSeries.map((s) => (
              <span
                key={s.name}
                className="flex items-center gap-1.5 text-[12px] text-muted-foreground"
              >
                <span className="h-2 w-2 rounded-full" style={{ background: s.color }} />
                {s.name}
              </span>
            ))}
          </div>
        )}

        <div
          className={`mt-5 ${sz.headline} font-medium leading-none tracking-tight text-foreground`}
        >
          {displayTotal}
        </div>
      </div>

      {/* Footer opaque : delta à gauche, stats secondaires à droite */}
      <div
        className={`relative z-10 flex items-center justify-between gap-4 border-t border-border bg-card/80 backdrop-blur-sm ${sz.footer} text-[14px]`}
      >
        <div>
          <span className="font-medium" style={{ color: color.text }}>
            {displayDelta}
          </span>{' '}
          <span className="text-muted-foreground">{deltaLabel}</span>
        </div>
        {showStats && (
          <div className="flex items-center gap-2.5 text-[12px] text-muted-foreground">
            <span>
              <span className="font-medium text-foreground/80">{fmtCompact(stats.peak)}</span>{' '}{tx('en yüksek')}
            </span>
            <span className="opacity-40">·</span>
            <span>
              <span className="font-medium text-foreground/80">{fmtCompact(stats.low)}</span>{' '}{tx('en düşük')}
            </span>
            <span className="opacity-40">·</span>
            <span>
              <span className="font-medium text-foreground/80">
                {fmtCompact(Math.round(stats.avg))}
              </span>{' '}{tx('ortalama', [])}</span>
          </div>
        )}
      </div>
    </div>
  );
}
