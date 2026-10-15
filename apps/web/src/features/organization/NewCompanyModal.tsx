import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { organizationApi } from '@/api/organization'
import { qk } from '@/api/queries'
import { Modal, ErrorSummary, type SummaryItem } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { tx } from '@/lib/i18n'

interface Errors {
  name?: string
  taxNumber?: string
}

function validate(name: string, taxNumber: string): Errors {
  const errors: Errors = {}
  if (name.trim().length < 2) errors.name = tx('Şirket adı en az 2 karakter olmalı.')
  if (taxNumber && !/^\d{10,11}$/.test(taxNumber.trim()))
    errors.taxNumber = tx('VKN 10 haneli (veya TCKN 11 haneli) rakamlardan oluşmalı.')
  return errors
}

export function NewCompanyModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()

  const [name, setName] = useState('')
  const [taxNumber, setTaxNumber] = useState('')
  const [errors, setErrors] = useState<Errors>({})
  const [submitted, setSubmitted] = useState(false)

  useEffect(() => {
    if (!open) {
      setName('')
      setTaxNumber('')
      setErrors({})
      setSubmitted(false)
    }
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      organizationApi.createCompany({
        name: name.trim(),
        taxNumber: taxNumber.trim() || undefined,
      }),
    onSuccess: (company) => {
      void queryClient.invalidateQueries({ queryKey: qk.companies })
      toast.ok(tx('"{0}" oluşturuldu.', [company.name]))
      onClose()
    },
    onError: (error: unknown) => {
      toast.stop(error instanceof Error ? error.message : tx('Şirket oluşturulamadı.'))
    },
  })

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitted(true)
    const next = validate(name, taxNumber)
    setErrors(next)
    if (Object.keys(next).length === 0) mutation.mutate()
  }

  const summary: SummaryItem[] = submitted
    ? ([
        errors.name && { fieldId: 'company-name', message: errors.name },
        errors.taxNumber && { fieldId: 'company-tax', message: errors.taxNumber },
      ].filter(Boolean) as SummaryItem[])
    : []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni şirket')}
      note={tx('Organizasyon yapısının kök kaydını oluşturun.')}
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
            form="new-company-form"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Şirketi oluştur')}
          </Button>
        </>
      }
    >
      <form id="new-company-form" onSubmit={handleSubmit} noValidate className="space-y-4">
        <ErrorSummary items={summary} />

        <TextField
          id="company-name"
          label={tx('Şirket adı')}
          required
          value={name}
          maxLength={200}
          autoComplete="organization"
          onChange={(e) => setName(e.target.value)}
          onBlur={() => submitted && setErrors(validate(name, taxNumber))}
          error={errors.name}
        />

        <TextField
          id="company-tax"
          label={tx('Vergi numarası')}
          inputMode="numeric"
          value={taxNumber}
          maxLength={11}
          className="tabular"
          hint={tx('İsteğe bağlı. 10 haneli VKN veya 11 haneli TCKN.')}
          onChange={(e) => setTaxNumber(e.target.value.replace(/\D/g, ''))}
          onBlur={() => submitted && setErrors(validate(name, taxNumber))}
          error={errors.taxNumber}
        />
      </form>
    </Modal>
  )
}
