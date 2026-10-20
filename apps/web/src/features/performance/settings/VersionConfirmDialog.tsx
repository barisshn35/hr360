/**
 * Kaydetmeden önce son söz: bu kayıt yeni bir sürüm oluşturur, geçmiş
 * dönemlerin puanları değişmez. Değişen alanlar eski → yeni listelenir.
 */

import { AnimatePresence, motion } from 'motion/react'
import { ArrowDown, ArrowRight, ArrowUp, GitCommitVertical, ShieldCheck } from 'lucide-react'
import { Modal } from '@/components/ui/modal'
import { Button } from '@/components/ui/button'
import { Callout } from '@/components/ui/callout'
import { cn } from '@/lib/utils'
import type { FieldChange } from './fields'
import { tx } from '@/lib/i18n'

export function VersionConfirmDialog({
  open,
  onClose,
  onConfirm,
  changes,
  nextVersion,
  activeCycleName,
  pending,
  error,
}: {
  open: boolean
  onClose: () => void
  onConfirm: () => void
  changes: FieldChange[]
  nextVersion: number
  activeCycleName: string | null
  pending: boolean
  error: string | null
}) {
  return (
    <Modal
      open={open}
      onClose={() => !pending && onClose()}
      title={tx('Sürüm {0} oluşturulsun mu?', [nextVersion])}
      note={tx('{0} alan değişiyor.', [changes.length])}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={pending}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={onConfirm} disabled={pending}>
            <GitCommitVertical aria-hidden />
            {pending ? tx('Kaydediliyor…') : tx('Sürüm {0} olarak yürürlüğe al', [nextVersion])}
          </Button>
        </>
      }
    >
      <Callout tone="info" icon={ShieldCheck} title={tx('Bu değişiklik yeni bir sürüm oluşturur. Geçmiş dönemlerin puanları değişmez.')}>
        {tx('Mevcut sürüm silinmez; geçmişte kalır ve kapanmış dönemler onunla puanlanmış olarak kalır.')}
        {activeCycleName && (
          <>
            {' '}{tx('Açık dönemin (', [])}<span className="font-medium text-foreground">{activeCycleName}</span>{tx(') puanları ve aksiyon önerileri yeni sürümle hesaplanır.')}
          </>
        )}
      </Callout>

      <AnimatePresence>
        {error && (
          <motion.div
            role="alert"
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: 'auto' }}
            exit={{ opacity: 0, height: 0 }}
            className="mt-3 overflow-hidden"
          >
            <Callout tone="danger">{error}</Callout>
          </motion.div>
        )}
      </AnimatePresence>

      <p className="mt-4 mb-2 text-[12px] font-semibold text-muted-foreground">{tx('Değişenler')}</p>
      <ul className="divide-y divide-border rounded-lg border border-border">
        {changes.map((c, i) => {
          const Icon = c.direction === 'up' ? ArrowUp : c.direction === 'down' ? ArrowDown : ArrowRight
          return (
            <motion.li
              key={c.key}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: 0.04 * i }}
              className="flex items-center justify-between gap-3 px-3 py-2 text-[13px]"
            >
              <span className="min-w-0 truncate">{c.label}</span>
              <span className="flex shrink-0 items-center gap-1.5">
                <span className="tabular text-muted-foreground line-through decoration-muted-foreground/40">{c.from}</span>
                <Icon className={cn('size-3.5', c.direction === 'up' ? 'text-primary' : 'text-muted-foreground')} aria-hidden />
                <span className="tabular font-semibold">{c.to}</span>
              </span>
            </motion.li>
          )
        })}
      </ul>
    </Modal>
  )
}
