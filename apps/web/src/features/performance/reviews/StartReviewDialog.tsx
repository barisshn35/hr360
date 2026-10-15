/**
 * Yeni değerlendirme başlat: kişi + tür + (açık) dönem → POST /reviews →
 * forma git. Aynı türde taslak zaten varsa backend 400 döner; yanıtta
 * mevcut taslağın kimliği varsa oraya tek tıkla gidilir.
 */

import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { ArrowRight, TriangleAlert } from 'lucide-react'
import { ApiError } from '@/api/client'
import { REVIEW_TYPES, reviewTypeHints, reviewTypeLabels, useCreateReview, type ReviewCycle, type ReviewType } from '@/api/performance'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { errorText } from '../components/controls'
import { PersonPicker } from '../components/people'
import { CyclePicker } from '../components/pickers'
import { usePeople } from '../hooks'
import { tx } from '@/lib/i18n'

export function StartReviewDialog({
  cycles,
  defaultCycleId,
  me,
  manager,
  preset,
  onClose,
}: {
  cycles: ReviewCycle[]
  defaultCycleId: string
  me: string
  manager: boolean
  preset?: { employeeId?: string; type?: ReviewType }
  onClose: () => void
}) {
  const navigate = useNavigate()
  const people = usePeople()
  const create = useCreateReview()
  const open = cycles.filter((c) => c.status === 'Open')
  const [cycleId, setCycleId] = useState(open.find((c) => c.id === defaultCycleId)?.id ?? open[0]?.id ?? '')
  const [type, setType] = useState<ReviewType>(preset?.type ?? (manager ? 'Manager' : 'Self'))
  const [employeeId, setEmployeeId] = useState<string | null>(preset?.employeeId ?? (type === 'Self' ? me : null))
  const [error, setError] = useState<{ message: string; existing?: string } | null>(null)

  const types = REVIEW_TYPES.filter((t) => manager || (t !== 'Manager' && t !== 'TeamLead'))
  const target = type === 'Self' ? me : employeeId

  const submit = () => {
    if (!target || !cycleId) return
    setError(null)
    create.mutate(
      { cycleId, employeeId: target, reviewerEmployeeId: me, type },
      {
        onSuccess: (r) => {
          onClose()
          navigate(`/panel/performans/degerlendirme/${r.id}`)
        },
        onError: (e) => {
          const existing = e instanceof ApiError ? ((e.detail as { existingReviewId?: string } | undefined)?.existingReviewId ?? undefined) : undefined
          setError({ message: errorText(e), existing })
        },
      },
    )
  }

  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={tx('Yeni değerlendirme')}
      note={tx('Önce boş bir taslak oluşur; formu doldurup gönderdiğinizde kilitlenir.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={create.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={!target || !cycleId || create.isPending}>
            {create.isPending ? tx('Oluşturuluyor…') : tx('Başlat')}
            <ArrowRight aria-hidden />
          </Button>
        </>
      }
    >
      <AnimatePresence>
        {error && (
          <motion.div role="alert" initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="mb-4 overflow-hidden">
            <div className="flex flex-col gap-2 rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-[13px] text-destructive sm:flex-row sm:items-center sm:justify-between">
              <span className="flex items-start gap-2">
                <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
                {error.message}
              </span>
              {error.existing && (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    onClose()
                    navigate(`/panel/performans/degerlendirme/${error.existing}`)
                  }}
                >
                  {tx('Mevcut değerlendirmeye git')}
                </Button>
              )}
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {open.length === 0 ? (
        <p className="rounded-lg border border-dashed border-border p-4 text-[13px] text-muted-foreground">{tx('Açık dönem yok. Değerlendirme yalnızca açık dönemde başlatılabilir.')}</p>
      ) : (
        <div className="flex flex-col gap-5">
          <fieldset>
            <legend className="mb-2 text-[13px] font-medium">{tx('Değerlendirme türü')}</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {types.map((t) => (
                <button
                  key={t}
                  type="button"
                  role="radio"
                  aria-checked={type === t}
                  onClick={() => {
                    setType(t)
                    setError(null)
                    if (t === 'Self') setEmployeeId(me)
                    else if (employeeId === me) setEmployeeId(null)
                  }}
                  className={cn('relative rounded-lg border p-2.5 text-left transition-colors', type === t ? 'border-transparent' : 'border-border hover:border-primary/30')}
                >
                  {type === t && <motion.span layoutId="review-type" className="absolute inset-0 rounded-lg border-2 border-primary bg-primary/5" transition={{ type: 'spring', stiffness: 500, damping: 36 }} />}
                  <span className="relative block text-[13px] font-medium">{reviewTypeLabels[t]}</span>
                  <span className="relative block text-[11px] text-muted-foreground">{reviewTypeHints[t]}</span>
                </button>
              ))}
            </div>
          </fieldset>

          {type !== 'Self' && (
            <div>
              <p className="mb-2 text-[13px] font-medium">{tx('Kimi değerlendiriyorsunuz?')}</p>
              <PersonPicker people={people.list} value={employeeId} onChange={setEmployeeId} exclude={[me]} detailOf={people.titleOf} height={220} />
            </div>
          )}

          <CyclePicker cycles={open} value={cycleId} onChange={setCycleId} label={tx('Dönem')} />
        </div>
      )}
    </Modal>
  )
}
