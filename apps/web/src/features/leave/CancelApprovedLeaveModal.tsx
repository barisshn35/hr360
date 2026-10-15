import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { leaveApi } from '@/api/leave'
import { leaveTypeLabels, type LeaveRequest } from '@/api/types'
import { formatDate, formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'

/**
 * Onaylı ve henüz başlamamış izin (madde 44):
 *  - mode "cancel": İK ya da çalışanın yöneticisi iptal eder; gün bakiyeye geri döner, vardiya planı güncellenir,
 *    çalışana bildirim gider. Gerekçe denetim kaydına yazılır.
 *  - mode "request": çalışan iptal ister; yöneticisine bildirim gider (iptali yönetici ya da İK yapar).
 */
export function CancelApprovedLeaveModal({
  request,
  mode,
  onClose,
}: {
  request: LeaveRequest | null
  mode: 'cancel' | 'request'
  onClose: () => void
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [submitted, setSubmitted] = useState(false)
  const error = reason.trim().length < 5 ? tx('Gerekçe en az 5 karakter olmalı.') : undefined

  const mutation = useMutation({
    mutationFn: async () => {
      if (!request) return
      if (mode === 'cancel') {
        await leaveApi.cancelApproved(request.id, reason.trim())
        toast.ok(tx('Onaylı izin iptal edildi; gün bakiyeye geri döndü'))
      } else {
        const r = await leaveApi.requestCancellation(request.id, reason.trim())
        if (r.notified) toast.ok(tx('İptal isteğiniz yöneticinize iletildi'))
        else toast.stop(tx('Yöneticiniz bulunamadı; iptal için İK ile iletişime geçin.'))
      }
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['leave'] })
      setReason('')
      setSubmitted(false)
      onClose()
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İşlem yapılamadı.')),
  })

  const close = () => {
    setReason('')
    setSubmitted(false)
    onClose()
  }

  return (
    <Modal
      open={request !== null}
      onClose={close}
      title={mode === 'cancel' ? tx('Onaylı izni iptal et') : tx('İzin iptali iste')}
      note={request ? tx('{0} · {1} – {2} · {3} gün', [leaveTypeLabels[request.type], formatDate(request.startDate), formatDate(request.endDate), formatNumber(request.days)]) : undefined}
      footer={
        <>
          <Button variant="outline" className="cursor-pointer" onClick={close} disabled={mutation.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button
            variant={mode === 'cancel' ? 'destructive' : 'default'}
            className="cursor-pointer"
            disabled={mutation.isPending}
            onClick={() => {
              setSubmitted(true)
              if (!error) mutation.mutate()
            }}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {mode === 'cancel' ? tx('İzni iptal et') : tx('İsteği gönder')}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <p className="text-[13px] leading-relaxed text-muted-foreground">
          {mode === 'cancel'
            ? tx('İzin iptal edilir, kullanılan gün bakiyeye geri döner ve vardiya planındaki izinli günler kaldırılır. Çalışana bildirim gider; gerekçe denetim kaydına yazılır.')
            : tx('İsteğiniz yöneticinize bildirilir; iptali yöneticiniz ya da İK yapar. Sağlık bilgisi (tanı) yazmayın.')}
        </p>
        <TextAreaField
          id="leave-cancel-reason"
          label={tx('Gerekçe')}
          required
          rows={3}
          maxLength={500}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          error={submitted ? error : undefined}
        />
      </div>
    </Modal>
  )
}
