import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useDirectory } from '@/api/directory'
import { workflowApi } from '@/api/workflows'
import { formatDate } from '@/lib/format'
import { PersonSelect, isoDate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/** Vekâlet: belirli tarihlerde onaylarınız seçtiğiniz kişiye geçer (Y23). */
export function DelegationsModal({ onClose, all = false, myId }: { onClose: () => void; all?: boolean; myId?: string }) {
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['delegations', all], queryFn: ({ signal }) => workflowApi.delegations(all, signal) })
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [start, setStart] = useState(isoDate())
  const [end, setEnd] = useState(isoDate(new Date(Date.now() + 7 * 864e5)))
  const [reason, setReason] = useState('')
  const create = useAction(() => workflowApi.createDelegation({ fromEmployeeId: all && from ? from : undefined, toEmployeeId: to, startDate: start, endDate: end, reason: reason || undefined }), {
    success: (r) => (r.moved ? tx('Vekâlet verildi; {0} bekleyen talep vekile geçti', [r.moved]) : tx('Vekâlet verildi')),
    invalidate: [['delegations'], ['workflows']], onDone: () => { setTo(''); setReason('') },
  })
  const revoke = useAction((id: string) => workflowApi.revokeDelegation(id), { success: tx('Vekâlet geri alındı'), invalidate: [['delegations'], ['workflows']] })
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Vekâlet')} note={tx('İzindeyken onaylarınız seçtiğiniz kişiye gider. Vekil yalnızca kendisine düşen onay kaydını görür; süre bitince erişimi kapanır ve verdiği her karar kaydedilir.')}>
      <div className="space-y-5">
        <div className="grid gap-3 sm:grid-cols-2">
          {all && <PersonSelect label={tx('Vekâlet veren')} value={from} onChange={setFrom} hint={tx('Boş bırakırsanız siz')} />}
          <PersonSelect label={tx('Vekil')} value={to} onChange={setTo} exclude={myId ? [myId] : []} />
          <TextField label={tx('Başlangıç')} type="date" value={start} onChange={(e) => setStart(e.target.value)} />
          <TextField label={tx('Bitiş')} type="date" value={end} onChange={(e) => setEnd(e.target.value)} />
          <TextField label={tx('Açıklama (isteğe bağlı)')} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={200} />
        </div>
        <Button disabled={!to || create.isPending} onClick={() => create.mutate(undefined)}>{tx('Vekâlet ver')}</Button>
        {q.isPending ? <RowsSkeleton rows={2} /> : !q.data?.length ? <InfoNote>{tx('Vekâlet kaydı yok.')}</InfoNote> : (
          <ul className="divide-y divide-border text-[13px]">
            {q.data.map((d) => (
              <li key={d.id} className="flex flex-wrap items-center gap-2 py-2">
                <span className="min-w-0 flex-1">{nameOf(d.fromEmployeeId)} → {nameOf(d.toEmployeeId)} · {formatDate(d.startDate)} – {formatDate(d.endDate)}{d.reason ? ` · ${d.reason}` : ''}</span>
                {d.revokedAt ? <StatusBadge>{tx('Geri alındı')}</StatusBadge> : d.active ? <StatusBadge tone="success">{tx('Etkin')}</StatusBadge> : <StatusBadge tone="info">{tx('Planlandı')}</StatusBadge>}
                {!d.revokedAt && <Button size="icon" variant="ghost" aria-label={tx('Geri al')} onClick={() => revoke.mutate(d.id)}><Trash2 className="size-4" /></Button>}
              </li>
            ))}
          </ul>
        )}
      </div>
    </Modal>
  )
}
