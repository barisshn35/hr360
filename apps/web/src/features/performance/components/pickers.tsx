/**
 * Filtre çubuklarında kullanılan seçiciler: dönem, kişi, ekip.
 * Hepsi Radix Select tabanlı (klavye ve yazarak arama hazır).
 */

import { useMemo } from 'react'
import { cycleStatusLabels, useTeams, type ReviewCycle } from '@/api/performance'
import { SelectField } from '@/components/ui/Field'
import { usePeople } from '../hooks'
import { tx } from '@/lib/i18n'

const statusSuffix = (c: ReviewCycle) => (c.status === 'Open' ? tx(' · açık') : c.status === 'Planned' ? ' · taslak' : '')

export function CyclePicker({
  cycles,
  value,
  onChange,
  includeDraft = false,
  label = tx('Dönem'),
  className,
}: {
  cycles: ReviewCycle[]
  value: string
  onChange: (id: string) => void
  includeDraft?: boolean
  label?: string
  className?: string
}) {
  const options = cycles
    .filter((c) => includeDraft || c.status !== 'Planned')
    .map((c) => ({ value: c.id, label: `${c.name}${statusSuffix(c)}` }))
  return (
    <div className={className}>
      <SelectField
        label={label}
        value={value}
        onChange={onChange}
        options={options}
        placeholder={options.length ? tx('Dönem seçin') : tx('Dönem yok')}
        disabled={!options.length}
        hint={value ? undefined : undefined}
      />
    </div>
  )
}

export function PersonSelect({
  value,
  onChange,
  label = tx('Çalışan'),
  allowAll,
  allLabel = tx('Tüm çalışanlar'),
  only,
  className,
}: {
  value: string
  onChange: (id: string) => void
  label?: string
  /** "Tümü" seçeneği — değer boş dize değil `__all__` (Radix boş değeri kabul etmez). */
  allowAll?: boolean
  allLabel?: string
  /** Yalnızca bu kişiler listelensin (ör. ekip üyeleri). */
  only?: string[]
  className?: string
}) {
  const people = usePeople()
  const options = useMemo(() => {
    const list = people.list.filter((p) => !only || only.includes(p.id)).map((p) => ({ value: p.id, label: p.name }))
    return allowAll ? [{ value: '__all__', label: allLabel }, ...list] : list
  }, [people.list, only, allowAll, allLabel])
  return (
    <div className={className}>
      <SelectField label={label} value={value} onChange={onChange} options={options} placeholder={people.isPending ? tx('Yükleniyor…') : tx('Kişi seçin')} disabled={!options.length} />
    </div>
  )
}

export function TeamSelect({
  value,
  onChange,
  label = 'Ekip',
  allowAll,
  allLabel = tx('Tüm şirket'),
  className,
}: {
  value: string
  onChange: (id: string) => void
  label?: string
  allowAll?: boolean
  allLabel?: string
  className?: string
}) {
  const teams = useTeams()
  const options = useMemo(() => {
    const list = (teams.data ?? []).filter((t) => t.isActive).map((t) => ({ value: t.id, label: tx('{0} · {1} kişi', [t.name, t.memberCount]) }))
    return allowAll ? [{ value: '__all__', label: allLabel }, ...list] : list
  }, [teams.data, allowAll, allLabel])
  return (
    <div className={className}>
      <SelectField label={label} value={value} onChange={onChange} options={options} placeholder={teams.isPending ? tx('Yükleniyor…') : tx('Ekip seçin')} disabled={!options.length} />
    </div>
  )
}

export const cycleStatusText = (c: ReviewCycle) => cycleStatusLabels[c.status] ?? tx('Diğer')
