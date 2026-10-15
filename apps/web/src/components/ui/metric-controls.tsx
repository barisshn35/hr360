/**
 * `progress-metric-card`'ın ikinci eksik parçası (bkz. metric-chart.tsx).
 * Dönem seçici ve grafik türü anahtarı.
 */

import { Activity, BarChart3, ChevronDown } from 'lucide-react'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { cn } from '@/lib/utils'
import type { ChartView } from './metric-chart'
import { tx } from '@/lib/i18n'

export type PeriodOption = {
  label: string
  /** Gösterilecek son N nokta. Boşsa serinin tamamı. */
  points?: number
}

export function PeriodSelect({
  value,
  options,
  onChange,
  accentText,
}: {
  value: string
  options: PeriodOption[]
  onChange: (option: PeriodOption) => void
  accentText: string
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className="pointer-events-auto flex cursor-pointer items-center gap-1 rounded-md px-1.5 py-0.5 text-[13px] font-medium transition-colors hover:bg-accent"
          style={{ color: accentText }}
        >
          {value}
          <ChevronDown className="size-3.5" strokeWidth={2} />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        {options.map((option) => (
          <DropdownMenuItem
            key={option.label}
            onSelect={() => onChange(option)}
            className={cn(option.label === value && 'font-medium text-primary')}
          >
            {option.label}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

const VIEWS: Array<{ value: ChartView; label: string; icon: React.ElementType }> = [
  { value: 'curve', label: tx('Eğri'), icon: Activity },
  { value: 'bars', label: tx('Sütun'), icon: BarChart3 },
]

export function ViewToggle({
  value,
  onChange,
}: {
  value: ChartView
  onChange: (view: ChartView) => void
}) {
  return (
    <div
      role="group"
      aria-label={tx('Grafik türü')}
      className="pointer-events-auto flex items-center gap-0.5 rounded-md border border-border p-0.5"
    >
      {VIEWS.map((view) => {
        const Icon = view.icon
        const active = view.value === value
        return (
          <button
            key={view.value}
            type="button"
            aria-label={view.label}
            aria-pressed={active}
            onClick={() => onChange(view.value)}
            className={cn(
              'flex size-6 cursor-pointer items-center justify-center rounded transition-colors',
              active
                ? 'bg-accent text-foreground'
                : 'text-muted-foreground hover:text-foreground',
            )}
          >
            <Icon className="size-3.5" strokeWidth={1.75} />
          </button>
        )
      })}
    </div>
  )
}
