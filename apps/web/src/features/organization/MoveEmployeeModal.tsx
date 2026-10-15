import { useState } from 'react'
import type { CreateAssignmentInput } from '@/api/types'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField, type SelectOption } from '@/components/ui/Field'
import { Button } from '@/components/ui/button'
import { formatDate } from '@/lib/format'
import { tx } from '@/lib/i18n'

/**
 * Sürükle-bırakın klavye / dokunmatik karşılığı. Sürüklemeden farklı olarak
 * unvan ve başlangıç tarihi de değiştirilebilir (ileri tarihli atama).
 */
export function MoveEmployeeModal({
  name,
  currentDeptId,
  currentTitle,
  currentFrom,
  options,
  defaultDate,
  onClose,
  onSubmit,
}: {
  name: string
  currentDeptId: string | null
  currentTitle: string | null
  /** Aktif atamanın başlangıcı — yeni atama bundan önce başlayamaz (backend 400). */
  currentFrom: string | null
  options: SelectOption[]
  defaultDate: string
  onClose: () => void
  onSubmit: (input: CreateAssignmentInput) => void
}) {
  const [departmentId, setDepartmentId] = useState('')
  const [title, setTitle] = useState(currentTitle ?? '')
  const [effectiveFrom, setEffectiveFrom] = useState(
    currentFrom && currentFrom > defaultDate ? currentFrom : defaultDate,
  )
  const [submitted, setSubmitted] = useState(false)

  const errors = {
    department: !departmentId ? tx('Hedef departmanı seçin.') : undefined,
    date: !effectiveFrom
      ? tx('Başlangıç tarihi gerekli.')
      : currentFrom && effectiveFrom < currentFrom
        ? tx('Mevcut atama {0} tarihinde başlıyor; yeni atama bu tarihten önce başlayamaz.', [formatDate(currentFrom)])
        : undefined,
  }

  const submit = () => {
    setSubmitted(true)
    if (errors.department || errors.date) return
    onSubmit({ departmentId, positionTitle: title.trim() || undefined, effectiveFrom })
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('{0} · departman değiştir', [name])}
      note={tx('Yeni atama eklenir, önceki aktif atama bir gün önce kendiliğinden kapanır; aynı gün yapılan taşımada mevcut atama güncellenir. Geçmiş kayıtlar silinmez.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit}>{tx('Taşı')}</Button>
        </>
      }
    >
      <div className="space-y-4">
        <SelectField
          label={tx('Yeni departman')}
          required
          value={departmentId}
          onChange={setDepartmentId}
          options={options.map((o) => (o.value === currentDeptId ? { ...o, label: tx('{0} (şu anki)', [o.label]), disabled: true } : o))}
          placeholder={tx('Departman seçin')}
          error={submitted ? errors.department : undefined}
        />
        <TextField
          label={tx('Unvan')}
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          hint={tx('Boş bırakılırsa unvansız atanır.')}
          maxLength={120}
        />
        <TextField
          label={tx('Başlangıç tarihi')}
          type="date"
          required
          value={effectiveFrom}
          min={currentFrom ?? undefined}
          onChange={(e) => setEffectiveFrom(e.target.value)}
          error={submitted || (currentFrom !== null && effectiveFrom < currentFrom) ? errors.date : undefined}
          hint={
            currentFrom && effectiveFrom === currentFrom
              ? tx('Mevcut atamayla aynı gün: yeni kayıt açılmaz, mevcut atamanın departmanı ve unvanı güncellenir.')
              : tx('İleri bir tarih seçerseniz kişi şemada yeni departmanında, başlangıç tarihiyle görünür.')
          }
          className="sm:w-56"
        />
      </div>
    </Modal>
  )
}
