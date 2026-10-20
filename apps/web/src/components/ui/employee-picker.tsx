import { useEffect, useMemo } from 'react'
import { useEmployees } from '@/api/queries'
import { useAuth } from '@/auth/useAuth'
import { canListEmployees } from '@/auth/roles'
import { SelectField } from './Field'
import { fullName } from '@/lib/format'
import { tx } from '@/lib/i18n'

/**
 * Çalışan seçici.
 *
 * "employee" rolündeki kullanıcılar için (manager+ değilse) backend'in
 * /api/employee/employees ucu SADECE kendi e-postasıyla filtrelenmiş
 * sorguya izin verir (bkz. EmployeesController.GetAll) - bu durumda
 * seçici otomatik olarak kendi kaydını bulup seçer ve salt-okunur
 * gösterir, çünkü "employee" rolü başka bir çalışan adına talep açamaz.
 */
export function EmployeePicker({
  value,
  onChange,
  label = tx('Çalışan'),
  hint,
  id = 'employee-picker',
  includeAllOption = false,
}: {
  value: string
  onChange: (employeeId: string) => void
  label?: string
  hint?: string
  id?: string
  /** "Tümü" seçeneği ekler — liste filtrelerinde kullanılır. */
  includeAllOption?: boolean
}) {
  const { user, roles } = useAuth()
  // Backend tüm listeyi kimlere veriyorsa onlar herkesi seçebilir; diğerleri yalnızca kendini.
  const restrictToSelf = !canListEmployees(roles) && !!user?.email

  const employees = useEmployees(restrictToSelf ? { email: user!.email! } : undefined)

  const options = useMemo(() => {
    const list = [...(employees.data ?? [])]
      .sort((a, b) => fullName(a).localeCompare(fullName(b), 'tr-TR'))
      .map((e) => ({ value: e.id, label: fullName(e) }))
    return includeAllOption ? [{ value: 'all', label: tx('Tüm çalışanlar') }, ...list] : list
  }, [employees.data, includeAllOption])

  // Kendi kaydı yüklenince otomatik seç - kullanıcı başka birini seçemez.
  useEffect(() => {
    if (restrictToSelf && !value && options.length === 1) onChange(options[0].value)
  }, [restrictToSelf, value, options, onChange])

  return (
    <SelectField
      id={id}
      label={label}
      hint={
        hint ??
        (employees.isPending
          ? tx('Çalışanlar yükleniyor')
          : employees.isError
            ? tx('Çalışan listesi alınamadı.')
            : undefined)
      }
      value={value}
      onChange={onChange}
      options={options}
      placeholder={tx('Çalışan seçin')}
      disabled={employees.isPending || options.length === 0 || restrictToSelf}
    />
  )
}
