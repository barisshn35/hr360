import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { LoaderCircle } from 'lucide-react'
import { employeeApi } from '@/api/employees'
import { tenantApi } from '@/api/tenant'
import { qk } from '@/api/queries'
import { Modal, ErrorSummary, type SummaryItem } from '@/components/ui/modal'
import { Button } from '@/components/ui/button'
import { TextField, SelectField } from '@/components/ui/field'
import { useToast } from '@/components/ui/toast'
import { tx } from '@/lib/i18n'
import type { PasswordLinkView } from '@/api/password-links'
import { usePasswordLinkDialog } from '@/features/access/PasswordLinkCard'
import { localISODate } from '@/lib/dates'

interface DepartmentOption {
  id: string
  label: string
}

interface Form {
  firstName: string
  lastName: string
  email: string
  phone: string
  hireDate: string
  departmentId: string
}

type Errors = Partial<Record<keyof Form, string>>

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/
const PHONE_RE = /^[0-9+()\s-]{7,20}$/
const HIRE_MIN = '1950-01-01'
function hireMax(): string {
  const d = new Date()
  d.setFullYear(d.getFullYear() + 1)
  return localISODate(d)
}

function validate(form: Form): Errors {
  const errors: Errors = {}
  if (form.firstName.trim().length < 2) errors.firstName = tx('Ad en az 2 karakter olmalı.')
  if (form.lastName.trim().length < 2) errors.lastName = tx('Soyad en az 2 karakter olmalı.')
  if (!EMAIL_RE.test(form.email.trim())) errors.email = tx('Geçerli bir e-posta adresi girin.')
  if (form.phone && !PHONE_RE.test(form.phone.trim()))
    errors.phone = tx('Telefon numarası geçerli görünmüyor.')
  if (!form.hireDate) errors.hireDate = tx('İşe giriş tarihi zorunlu.')
  // Sunucuyla aynı aralık: 01.01.1950 – bugün + 1 yıl.
  else if (form.hireDate < HIRE_MIN) errors.hireDate = tx("İşe giriş tarihi 01.01.1950'den önce olamaz.")
  else if (form.hireDate > hireMax()) errors.hireDate = tx('İşe giriş tarihi en fazla bir yıl sonrası olabilir.')
  return errors
}

const EMPTY: Form = {
  firstName: '',
  lastName: '',
  email: '',
  phone: '',
  hireDate: '',
  departmentId: '',
}

export function NewEmployeeModal({
  open,
  onClose,
  departments,
}: {
  open: boolean
  onClose: () => void
  departments: DepartmentOption[]
}) {
  const toast = useToast()
  const navigate = useNavigate()
  const linkDialog = usePasswordLinkDialog()
  const queryClient = useQueryClient()

  const [form, setForm] = useState<Form>(EMPTY)
  const [errors, setErrors] = useState<Errors>({})
  const [submitted, setSubmitted] = useState(false)

  useEffect(() => {
    if (!open) {
      setForm(EMPTY)
      setErrors({})
      setSubmitted(false)
    }
  }, [open])

  const set = (key: keyof Form) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [key]: e.target.value }))

  const setDepartment = (value: string) => setForm((f) => ({ ...f, departmentId: value }))

  const revalidate = () => submitted && setErrors(validate(form))

  const mutation = useMutation({
    mutationFn: async () => {
      const employee = await employeeApi.create({
        firstName: form.firstName.trim(),
        lastName: form.lastName.trim(),
        email: form.email.trim(),
        phone: form.phone.trim() || undefined,
        hireDate: form.hireDate,
      })

      if (form.departmentId) {
        try {
          await employeeApi.addAssignment(employee.id, {
            departmentId: form.departmentId,
            positionTitle: undefined,
            effectiveFrom: form.hireDate,
          })
        } catch {
          // Çalışan zaten oluşturuldu; departman ataması Şema/Liste'den
          // elle tamamlanabilir - burada sessiz kalmak akışı kesmez.
        }
      }

      let inviteSent = false
      let passwordLink: PasswordLinkView | null | undefined = null
      try {
        passwordLink = (await tenantApi.inviteMember(employee.id)).passwordLink
        inviteSent = true
      } catch {
        // Giriş erişimi açılamadı - çalışan kaydı yine de geçerli; Roller ekranından
        // "Giriş erişimi ver" ile sonra açılabilir.
      }

      return { employee, inviteSent, passwordLink }
    },
    onSuccess: ({ employee, inviteSent, passwordLink }) => {
      linkDialog.show(passwordLink, `${employee.firstName} ${employee.lastName}`)
      void queryClient.invalidateQueries({ queryKey: qk.employees })
      void queryClient.invalidateQueries({ queryKey: ['departments'] })
      toast.ok(
        inviteSent
          ? tx('{0} {1} kaydedildi. Parola belirleme linki hazır.', [employee.firstName, employee.lastName])
          : tx('{0} {1} kaydedildi. Giriş erişimi açılamadı; Roller ekranından tekrar deneyin.', [employee.firstName, employee.lastName]),
      )
      onClose()
      navigate(`/panel/calisanlar/${employee.id}`)
    },
    onError: (e: unknown) => {
      toast.stop(e instanceof Error ? e.message : tx('Çalışan oluşturulamadı.'))
    },
  })

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitted(true)
    const next = validate(form)
    setErrors(next)
    if (Object.keys(next).length === 0) mutation.mutate()
  }

  const summary: SummaryItem[] = submitted
    ? (Object.entries(errors)
        .filter(([, message]) => Boolean(message))
        .map(([key, message]) => ({
          fieldId: `employee-${key}`,
          message: message!,
        })) as SummaryItem[])
    : []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni çalışan')}
      note={tx('Temel bilgileri girin. Kayıt tamamlanınca giriş için parola belirleme e-postası gönderilir.')}
      size="lg"
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
            form="new-employee-form"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Çalışanı kaydet')}
          </Button>
        </>
      }
    >
      <form id="new-employee-form" onSubmit={handleSubmit} noValidate className="space-y-4">
        <ErrorSummary items={summary} />

        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="employee-firstName"
            label={tx('Ad')}
            required
            autoComplete="given-name"
            value={form.firstName}
            onChange={set('firstName')}
            onBlur={revalidate}
            error={errors.firstName}
          />
          <TextField
            id="employee-lastName"
            label={tx('Soyad')}
            required
            autoComplete="family-name"
            value={form.lastName}
            onChange={set('lastName')}
            onBlur={revalidate}
            error={errors.lastName}
          />
        </div>

        <TextField
          id="employee-email"
          label="E-posta"
          type="email"
          required
          autoComplete="email"
          value={form.email}
          onChange={set('email')}
          onBlur={revalidate}
          error={errors.email}
        />

        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="employee-phone"
            label={tx('Telefon')}
            type="tel"
            autoComplete="tel"
            hint={tx('İsteğe bağlı')}
            value={form.phone}
            onChange={set('phone')}
            onBlur={revalidate}
            error={errors.phone}
          />
          <TextField
            id="employee-hireDate"
            label={tx('İşe giriş tarihi')}
            type="date"
            required
            min={HIRE_MIN}
            max={hireMax()}
            value={form.hireDate}
            onChange={set('hireDate')}
            onBlur={revalidate}
            error={errors.hireDate}
          />
        </div>

        <SelectField
          id="employee-department"
          label={tx('Departman')}
          value={form.departmentId}
          onChange={setDepartment}
          placeholder={tx('Departman seçin (isteğe bağlı)')}
          options={departments.map((d) => ({ value: d.id, label: d.label }))}
          hint={
            departments.length === 0
              ? tx('Henüz departman yok; daha sonra Organizasyon sayfasından atayabilirsiniz.')
              : undefined
          }
        />
      </form>
    </Modal>
  )
}
