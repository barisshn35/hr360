import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle, Trash2 } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { TextField } from '@/components/ui/Field'
import { Checkbox } from '@/components/ui/checkbox'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { leaveApi } from '@/api/leave'
import { useLeaveHolidays } from '@/api/queries'
import { formatDate } from '@/lib/format'
import { tx } from '@/lib/i18n'

/**
 * Resmi tatil takvimi (İK). Buradaki günler izin günü hesabında düşülür —
 * backend (leave-service) izin gününü kendisi hesaplıyor. "Türkiye resmi tatillerini ekle":
 * 2025–2030 dini bayramları Diyanet takviminden gömülü; arifeler ve 28 Ekim yarım gün (0,5).
 * Aralık ayında gelecek yılın tatilleri otomatik yüklenir (bu yıl yüklenmişse).
 */
export function HolidaysModal({ open, onClose, year }: { open: boolean; onClose: () => void; year: number }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const holidays = useLeaveHolidays(year, open)
  const [date, setDate] = useState('')
  const [name, setName] = useState('')
  const [half, setHalf] = useState(false)
  const [error, setError] = useState<string | undefined>()

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: ['leave', 'holidays'] })

  const add = useMutation({
    mutationFn: () => leaveApi.createHoliday({ date, name: name.trim(), isHalfDay: half }),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Tatil eklendi'))
      setDate('')
      setName('')
      setHalf(false)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Tatil eklenemedi.')),
  })

  const seed = useMutation({
    mutationFn: () => leaveApi.seedTurkishHolidays(year),
    onSuccess: (r) => {
      invalidate()
      toast.ok(r.message)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Tatiller eklenemedi.')),
  })

  const remove = useMutation({
    mutationFn: (id: string) => leaveApi.deleteHoliday(id),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Tatil silindi'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Tatil silinemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!date) return setError(tx('Tarih zorunlu.'))
    if (name.trim().length < 2) return setError(tx('Tatil adı zorunlu.'))
    setError(undefined)
    add.mutate()
  }

  const rows = holidays.data ?? []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('{0} resmi tatilleri', [year])}
      note={tx('Bu günler izin talebinde gün sayısından düşülür (hafta sonları zaten düşülür); yarım gün tatil 0,5 gün sayılır.')}
      size="lg"
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            disabled={seed.isPending}
            onClick={() => seed.mutate()}
          >
            {seed.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Türkiye resmi tatillerini ekle')}
          </Button>
          <Button variant="outline" className="cursor-pointer" onClick={onClose}>
            {tx('Kapat')}
          </Button>
        </>
      }
    >
      <form onSubmit={submit} noValidate className="grid gap-3 sm:grid-cols-[180px_1fr_auto_auto] sm:items-end">
        <TextField
          id="holiday-date"
          label={tx('Tarih')}
          type="date"
          required
          value={date}
          onChange={(e) => setDate(e.target.value)}
          error={error?.includes('Tarih') ? error : undefined}
        />
        <TextField
          id="holiday-name"
          label={tx('Tatil adı')}
          required
          placeholder={tx('Örn. Cumhuriyet Bayramı')}
          value={name}
          onChange={(e) => setName(e.target.value)}
          error={error?.includes('adı') ? error : undefined}
        />
        <label className="flex h-10 items-center gap-2 text-[13px]">
          <Checkbox checked={half} onCheckedChange={(v) => setHalf(v === true)} /> {tx('Yarım gün')}
        </label>
        <Button type="submit" className="cursor-pointer" disabled={add.isPending}>
          {add.isPending && <LoaderCircle className="size-4 animate-spin" />}
          {tx('Ekle')}
        </Button>
      </form>

      <div className="mt-5">
        {holidays.isPending ? (
          <RowsSkeleton rows={3} />
        ) : rows.length === 0 ? (
          <EmptyState title={tx('Bu yıl için tatil girilmemiş')} detail={tx('İzin günleri yalnızca hafta sonları düşülerek hesaplanır.')} />
        ) : (
          <ul className="divide-y divide-border rounded-xl border border-border">
            {rows.map((h) => (
              <li key={h.id} className="flex items-center justify-between gap-3 px-4 py-2.5">
                <div className="min-w-0">
                  <p className="truncate text-[14px] font-medium">{h.name}{h.isHalfDay && <>{' '}<StatusBadge tone="info">{tx('yarım gün')}</StatusBadge></>}</p>
                  <p className="tabular text-[12px] text-muted-foreground">{formatDate(h.date)}</p>
                </div>
                <Button
                  variant="ghost"
                  size="sm"
                  className="cursor-pointer"
                  aria-label={tx('{0} tatilini sil', [h.name])}
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(h.id)}
                >
                  <Trash2 className="size-4" />
                </Button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Modal>
  )
}
