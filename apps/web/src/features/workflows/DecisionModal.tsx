import { useEffect, useState } from 'react'
import { LoaderCircle } from 'lucide-react'
import type { ApprovalStep } from '@/api/types'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { tx } from '@/lib/i18n'

export function DecisionModal({
  state,
  onClose,
  onConfirm,
  pending,
}: {
  state: { step: ApprovalStep; approve: boolean } | null
  onClose: () => void
  onConfirm: (comment?: string) => void
  pending: boolean
}) {
  const [comment, setComment] = useState('')
  const [error, setError] = useState<string | undefined>()

  useEffect(() => {
    if (!state) {
      setComment('')
      setError(undefined)
    }
  }, [state])

  if (!state) return null

  const { step, approve } = state

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    // Ret kararında gerekçe zorunlu — denetim izi için anlamlı kayıt bırakır.
    if (!approve && comment.trim().length < 3) {
      setError(tx('Ret gerekçesi zorunlu (en az 3 karakter).'))
      return
    }
    setError(undefined)
    onConfirm(comment.trim() || undefined)
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={approve ? tx('Adımı onayla') : tx('Adımı reddet')}
      note={tx('{0}. onay adımı için kararınızı kaydedin.', [step.order])}
      footer={
        <>
          <Button variant="outline" className="cursor-pointer" onClick={onClose} disabled={pending}>
            {tx('Vazgeç')}
          </Button>
          <Button
            type="submit"
            form="decision-form"
            variant={approve ? 'default' : 'destructive'}
            className="cursor-pointer"
            disabled={pending}
          >
            {pending && <LoaderCircle className="size-4 animate-spin" />}
            {approve ? tx('Onayla') : tx('Reddet')}
          </Button>
        </>
      }
    >
      <form id="decision-form" onSubmit={handleSubmit} noValidate>
        <TextAreaField
          id="decision-comment"
          label={tx('Gerekçe')}
          required={!approve}
          rows={4}
          value={comment}
          maxLength={1000}
          hint={
            approve
              ? tx('İsteğe bağlı. Denetim izinde görünür.')
              : tx('Zorunlu. Talep sahibi bu gerekçeyi görür.')
          }
          onChange={(e) => setComment(e.target.value)}
          error={error}
        />
      </form>
    </Modal>
  )
}
