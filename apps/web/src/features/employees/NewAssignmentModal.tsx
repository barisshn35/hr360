import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { employeeApi } from '@/api/employees'
import { qk, useCompanies } from '@/api/queries'
import { Modal, ErrorSummary, type SummaryItem } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { tx } from '@/lib/i18n'
import { localISODate } from '@/lib/dates'

interface Errors {
  departmentId?: string
  effectiveFrom?: string
}

export function NewAssignmentModal({
  open,
  onClose,
  employeeId,
  hireDate,
}: {
  open: boolean
  onClose: () => void
  employeeId: string
  /** Çalışanın işe giriş tarihi; atama başlangıcı bundan önce olamaz. */
  hireDate?: string
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const companies = useCompanies()

  const [departmentId, setDepartmentId] = useState('')
  const [positionTitle, setPositionTitle] = useState('')
  const [effectiveFrom, setEffectiveFrom] = useState('')
  const [errors, setErrors] = useState<Errors>({})
  const [submitted, setSubmitted] = useState(false)

  useEffect(() => {
    if (!open) {
      setDepartmentId('')
      setPositionTitle('')
      setEffectiveFrom('')
      setErrors({})
      setSubmitted(false)
    }
  }, [open])

  /**
   * Departman adı şirket adıyla birlikte yazılır — iki şirkette de "Satış"
   * varsa kullanıcı hangisini seçtiğini görsün.
   */
  const options = useMemo(
    () =>
      (companies.data ?? []).flatMap((c) =>
        (c.departments ?? []).map((d) => ({ value: d.id, label: `${c.name} — ${d.name}` })),
      ),
    [companies.data],
  )

  // Sunucuyla aynı kural: başlangıç ≥ işe giriş (ileri tarihli işe girişte en geç bugün), ≤ bugün + 1 yıl.
  const today = localISODate()
  const hire = hireDate?.slice(0, 10)
  const minFrom = hire ? (hire > today ? today : hire) : undefined
  const maxFrom = (() => {
    const d = new Date()
    d.setFullYear(d.getFullYear() + 1)
    return localISODate(d)
  })()

  const mutation = useMutation({
    mutationFn: () =>
      employeeApi.addAssignment(employeeId, {
        departmentId,
        positionTitle: positionTitle.trim() || undefined,
        effectiveFrom,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: qk.employee(employeeId) })
      void queryClient.invalidateQueries({ queryKey: qk.employees })
      toast.ok(tx('Atama kaydedildi.'))
      onClose()
    },
    onError: (e: unknown) => {
      toast.stop(e instanceof Error ? e.message : tx('Atama oluşturulamadı.'))
    },
  })

  function validate(): Errors {
    const next: Errors = {}
    if (!departmentId) next.departmentId = tx('Departman seçilmeli.')
    if (!effectiveFrom) next.effectiveFrom = tx('Geçerlilik başlangıcı zorunlu.')
    else if (minFrom && effectiveFrom < minFrom)
      next.effectiveFrom = tx('Atama başlangıcı işe giriş tarihinden önce olamaz.')
    else if (effectiveFrom > maxFrom) next.effectiveFrom = tx('Atama başlangıcı en fazla bir yıl sonrası olabilir.')
    return next
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitted(true)
    const next = validate()
    setErrors(next)
    if (Object.keys(next).length === 0) mutation.mutate()
  }

  const summary: SummaryItem[] = submitted
    ? ([
        errors.departmentId && { fieldId: 'assignment-department', message: errors.departmentId },
        errors.effectiveFrom && { fieldId: 'assignment-from', message: errors.effectiveFrom },
      ].filter(Boolean) as SummaryItem[])
    : []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Departman ataması')}
      note={tx('Çalışanı bir departmana ve pozisyona atayın.')}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="new-assignment-form"
            className="cursor-pointer"
            disabled={mutation.isPending || options.length === 0}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Atamayı kaydet')}
          </Button>
        </>
      }
    >
      <form id="new-assignment-form" onSubmit={handleSubmit} noValidate className="space-y-4">
        <ErrorSummary items={summary} />

        {companies.isPending ? (
          <p className="text-[13px] text-muted-foreground">{tx('Departmanlar yükleniyor…')}</p>
        ) : options.length === 0 ? (
          <p
            role="alert"
            className="rounded-md border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/10 p-3 text-[13px] leading-relaxed"
          >
            {tx('Atama yapabilmek için önce Organizasyon bölümünden departman tanımlamalısınız.')}
          </p>
        ) : (
          <SelectField
            id="assignment-department"
            label={tx('Departman')}
            required
            value={departmentId}
            onChange={(v) => {
              setDepartmentId(v)
              if (submitted) setErrors(validate())
            }}
            options={options}
            placeholder={tx('Departman seçin')}
            error={errors.departmentId}
          />
        )}

        <TextField
          id="assignment-title"
          label={tx('Pozisyon')}
          hint={tx('İsteğe bağlı — ör. Yazılım Mühendisi')}
          value={positionTitle}
          maxLength={200}
          onChange={(e) => setPositionTitle(e.target.value)}
        />

        <TextField
          id="assignment-from"
          label={tx('Geçerlilik başlangıcı')}
          type="date"
          required
          min={minFrom}
          max={maxFrom}
          value={effectiveFrom}
          onChange={(e) => setEffectiveFrom(e.target.value)}
          onBlur={() => submitted && setErrors(validate())}
          error={errors.effectiveFrom}
        />
      </form>
    </Modal>
  )
}
