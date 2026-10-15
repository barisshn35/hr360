import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { leaveApi } from '@/api/leave'
import { leaveTypeLabels, type LeaveType } from '@/api/types'
import { tx } from '@/lib/i18n'

/**
 * Yıllık izin bakiyesi tanımlama.
 *
 * `leave:manageBalance` izni izin matrisinde İK yöneticisine tanımlı ve
 * backend'de `POST /api/leave/leave-balances` ucu var; önceki sürümde
 * karşılığı bir ekran yoktu, bakiyeler yalnızca API'den girilebiliyordu.
 */
export function NewBalanceModal({
  open,
  onClose,
  defaultEmployeeId = '',
  defaultYear,
}: {
  open: boolean
  onClose: () => void
  defaultEmployeeId?: string
  defaultYear: number
}) {
  const toast = useToast()
  const queryClient = useQueryClient()

  const [employeeId, setEmployeeId] = useState(defaultEmployeeId)
  const [year, setYear] = useState(String(defaultYear))
  const [type, setType] = useState<LeaveType>('Annual')
  const [entitledDays, setDays] = useState('14')
  const [error, setError] = useState<string | undefined>()

  useEffect(() => {
    if (open) {
      setEmployeeId(defaultEmployeeId)
      setYear(String(defaultYear))
    } else {
      setType('Annual')
      setDays('14')
      setError(undefined)
    }
  }, [open, defaultEmployeeId, defaultYear])

  const mutation = useMutation({
    mutationFn: () =>
      leaveApi.createBalance({
        employeeId,
        year: Number(year),
        type,
        entitledDays: Number(entitledDays),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['leave'] })
      toast.ok(tx('Bakiye tanımlandı'))
      onClose()
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Bakiye tanımlanamadı.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!employeeId) return setError(tx('Çalışan seçilmeli.'))
    if (!Number(year)) return setError(tx('Yıl zorunlu.'))
    if (!(Number(entitledDays) > 0)) return setError(tx('Hak edilen gün sıfırdan büyük olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('İzin bakiyesi tanımla')}
      note={tx('Aynı çalışan, yıl ve tür için bakiye zaten varsa mevcut bakiye güncellenir.')}
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
            form="new-balance"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Bakiyeyi tanımla')}
          </Button>
        </>
      }
    >
      <form id="new-balance" onSubmit={submit} noValidate className="space-y-4">
        <EmployeePicker
          id="balance-employee"
          value={employeeId}
          onChange={setEmployeeId}
          hint={error?.includes('Çalışan') ? error : undefined}
        />

        <SelectField
          id="balance-type"
          label={tx('İzin türü')}
          required
          value={type}
          onChange={(v) => setType(v as LeaveType)}
          options={(Object.keys(leaveTypeLabels) as LeaveType[]).map((t) => ({
            value: t,
            label: leaveTypeLabels[t],
          }))}
        />

        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            id="balance-year"
            label={tx('Yıl')}
            type="number"
            min={2020}
            max={2100}
            required
            className="tabular"
            value={year}
            onChange={(e) => setYear(e.target.value)}
            error={error?.includes('Yıl') ? error : undefined}
          />
          <TextField
            id="balance-days"
            label={tx('Hak edilen gün')}
            type="number"
            min={1}
            max={365}
            required
            className="tabular"
            value={entitledDays}
            onChange={(e) => setDays(e.target.value)}
            error={error?.includes('Hak edilen') ? error : undefined}
          />
        </div>
      </form>
    </Modal>
  )
}
