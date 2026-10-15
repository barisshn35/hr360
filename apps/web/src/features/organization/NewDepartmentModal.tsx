import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { organizationApi } from '@/api/organization'
import { qk } from '@/api/queries'
import type { Department } from '@/api/types'
import { Modal, ErrorSummary, type SummaryItem } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { buildTree } from './DepartmentTree'
import { tx } from '@/lib/i18n'

/**
 * Radix Select boş string değerini kabul etmiyor (o değer "seçim yok"
 * anlamına ayrılmış). "Üst departman yok" için ayrı bir işaret kullanıyoruz.
 */
const ROOT = '__root__'

/** Seçim listesinde hiyerarşiyi girinti ile göstermek için düzleştirir. */
function flattenForSelect(departments: Department[]) {
  const out: Array<{ value: string; label: string }> = []
  const walk = (nodes: ReturnType<typeof buildTree>, depth: number) => {
    for (const node of nodes) {
      out.push({ value: node.id, label: `${'— '.repeat(depth)}${node.name}` })
      walk(node.children, depth + 1)
    }
  }
  walk(buildTree(departments), 0)
  return out
}

export function NewDepartmentModal({
  open,
  onClose,
  companyId,
  departments,
  defaultParentId,
}: {
  open: boolean
  onClose: () => void
  companyId: string
  departments: Department[]
  defaultParentId?: string | null
}) {
  const toast = useToast()
  const queryClient = useQueryClient()

  const [name, setName] = useState('')
  const [parentId, setParentId] = useState<string>(ROOT)
  const [error, setError] = useState<string | undefined>()
  const [submitted, setSubmitted] = useState(false)

  useEffect(() => {
    if (open) {
      setParentId(defaultParentId ?? ROOT)
    } else {
      setName('')
      setParentId(ROOT)
      setError(undefined)
      setSubmitted(false)
    }
  }, [open, defaultParentId])

  const options = useMemo(
    () => [{ value: ROOT, label: tx('Kök seviye (üst departman yok)') }, ...flattenForSelect(departments)],
    [departments],
  )

  const mutation = useMutation({
    mutationFn: () =>
      organizationApi.createDepartment({
        name: name.trim(),
        companyId,
        parentDepartmentId: parentId === ROOT ? null : parentId,
      }),
    onSuccess: (dept) => {
      void queryClient.invalidateQueries({ queryKey: qk.company(companyId) })
      void queryClient.invalidateQueries({ queryKey: qk.companies })
      // Organizasyon şeması departmanları ayrı uçtan (/departments) okur.
      void queryClient.invalidateQueries({ queryKey: ['departments'] })
      toast.ok(tx('"{0}" departmanı eklendi.', [dept.name]))
      onClose()
    },
    onError: (e: unknown) => {
      toast.stop(e instanceof Error ? e.message : tx('Departman oluşturulamadı.'))
    },
  })

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitted(true)
    const next = name.trim().length < 2 ? tx('Departman adı en az 2 karakter olmalı.') : undefined
    setError(next)
    if (!next) mutation.mutate()
  }

  const summary: SummaryItem[] =
    submitted && error ? [{ fieldId: 'department-name', message: error }] : []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni departman')}
      note={tx('Üst departman seçerek hiyerarşiye yerleştirebilirsiniz.')}
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
            form="new-department-form"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Departmanı ekle')}
          </Button>
        </>
      }
    >
      <form id="new-department-form" onSubmit={handleSubmit} noValidate className="space-y-4">
        <ErrorSummary items={summary} />

        <TextField
          id="department-name"
          label={tx('Departman adı')}
          required
          value={name}
          maxLength={200}
          onChange={(e) => setName(e.target.value)}
          onBlur={() =>
            submitted &&
            setError(name.trim().length < 2 ? tx('Departman adı en az 2 karakter olmalı.') : undefined)
          }
          error={error}
        />

        <SelectField
          id="department-parent"
          label={tx('Üst departman')}
          value={parentId}
          hint={tx('Kök seviye seçilirse şirketin doğrudan altında oluşturulur.')}
          onChange={setParentId}
          options={options}
        />
      </form>
    </Modal>
  )
}
