/**
 * Hedef oluştur/düzenle ve ilerleme güncelle.
 * İkisi de canlı önizleme gösterir: pay (ağırlık → yüzde) ve ilerleme çubuğu.
 */

import { useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { Flame, TriangleAlert } from 'lucide-react'
import {
  GOAL_STATUSES,
  formatShareOf,
  goalStatusLabels,
  shareOf,
  useCreateGoal,
  useUpdateGoal,
  useUpdateGoalProgress,
  type Goal,
  type GoalStatus,
  type ReviewCycle,
} from '@/api/performance'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { TextAreaField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { EASE } from '@/motion/primitives'
import { Segmented, Switch, errorText } from '../components/controls'
import { NumberStepper } from '../components/NumberStepper'
import { ShareBar } from '../components/WeightShare'
import { formatGoalValue, progressOf } from './goalMath'
import { tx, pct } from '@/lib/i18n'
import { formatDate } from '@/lib/format'

function ErrorLine({ message }: { message: string | null }) {
  return (
    <AnimatePresence>
      {message && (
        <motion.p
          role="alert"
          initial={{ opacity: 0, height: 0 }}
          animate={{ opacity: 1, height: 'auto' }}
          exit={{ opacity: 0, height: 0 }}
          className="mb-4 flex items-start gap-2 overflow-hidden rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-[13px] text-destructive"
        >
          <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
          {message}
        </motion.p>
      )}
    </AnimatePresence>
  )
}

const parse = (s: string) => {
  const n = Number(s.replace(/\./g, '').replace(',', '.').trim())
  return s.trim() === '' || !Number.isFinite(n) ? null : n
}

/* --------------------------------- Yeni hedef --------------------------------- */

export function CreateGoalDialog({
  employeeId,
  employeeName,
  cycle,
  existing,
  goal,
  onClose,
}: {
  employeeId: string
  employeeName: string
  cycle: ReviewCycle
  existing: Goal[]
  /** Verilirse düzenleme kipinde açılır (dönem ve çalışan değişmez). */
  goal?: Goal
  onClose: () => void
}) {
  const toast = useToast()
  const createGoal = useCreateGoal()
  const updateGoal = useUpdateGoal()
  const create = goal ? updateGoal : createGoal
  const [title, setTitle] = useState(goal?.title ?? '')
  const [description, setDescription] = useState(goal?.description ?? '')
  const [weight, setWeight] = useState(goal ? goal.weight : existing.length ? 20 : 100)
  const [numeric, setNumeric] = useState(goal ? goal.targetValue !== null : true)
  const [target, setTarget] = useState(goal?.targetValue != null ? String(goal.targetValue).replace('.', ',') : '')
  const [unit, setUnit] = useState(goal?.unit ?? '')
  const [dueDate, setDueDate] = useState(goal?.dueDate ?? '')
  const [touched, setTouched] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const others = existing.filter((g) => g.status !== 'Cancelled' && g.id !== goal?.id)
  const total = others.reduce((a, g) => a + g.weight, 0) + weight
  const targetNum = parse(target)
  const cycleStart = cycle.startDate?.slice(0, 10) || undefined
  const cycleEnd = cycle.endDate?.slice(0, 10) || undefined
  const errs = {
    title: !title.trim() ? tx('Hedefe bir başlık verin.') : undefined,
    target: numeric && (targetNum === null || targetNum <= 0) ? tx('Sıfırdan büyük bir hedef değer girin.') : undefined,
    // Sunucu da aynı kuralı uygular (GoalsController.DueDateError).
    dueDate:
      dueDate && cycleEnd && dueDate > cycleEnd
        ? tx('Son tarih dönem bitişinden ({0}) sonra olamaz.', [formatDate(cycleEnd)])
        : dueDate && cycleStart && dueDate < cycleStart
          ? tx('Son tarih dönem başlangıcından ({0}) önce olamaz.', [formatDate(cycleStart)])
          : undefined,
  }

  const submit = () => {
    setTouched(true)
    if (errs.title || errs.target || errs.dueDate) return
    const body = {
      title: title.trim(),
      description: description.trim() || undefined,
      weight,
      ...(numeric ? { targetValue: targetNum!, unit: unit.trim() || undefined } : {}),
      // Düzenlemede boş = temizle (null); yeni hedefte boşsa alan hiç gönderilmez.
      dueDate: dueDate || (goal ? null : undefined),
    }
    const handlers = { onError: (e: unknown) => setError(errorText(e)) }
    if (goal) {
      updateGoal.mutate({ id: goal.id, input: body }, {
        ...handlers,
        onSuccess: () => {
          toast.ok(tx('«{0}» güncellendi.', [title.trim()]))
          onClose()
        },
      })
      return
    }
    createGoal.mutate({ cycleId: cycle.id, employeeId, ...body }, {
      ...handlers,
      onSuccess: () => {
        toast.ok(tx('«{0}» hedefi {1} için eklendi.', [title.trim(), employeeName]))
        onClose()
      },
    })
  }

  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={goal ? tx('Hedefi düzenle') : tx('Yeni hedef')}
      note={`${employeeName} · ${cycle.name}`}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={create.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={create.isPending}>
            {goal ? (create.isPending ? tx('Kaydediliyor…') : tx('Kaydet')) : create.isPending ? tx('Ekleniyor…') : tx('Hedefi ekle')}
          </Button>
        </>
      }
    >
      <ErrorLine message={error} />
      <div className="flex flex-col gap-4">
        <TextField label={tx('Başlık')} value={title} onChange={(e) => setTitle(e.target.value)} error={touched ? errs.title : undefined} placeholder={tx('ör. Ödeme servisi v2 lansmanı')} autoFocus={!goal} required />
        <TextAreaField label={tx('Açıklama')} value={description} onChange={(e) => setDescription(e.target.value)} placeholder={tx('Başarının tanımı ne?')} />
        <div className="sm:w-56">
          <TextField
            label={tx('Son tarih (isteğe bağlı)')}
            type="date"
            value={dueDate}
            min={cycleStart}
            max={cycleEnd}
            onChange={(e) => setDueDate(e.target.value)}
            error={dueDate ? errs.dueDate : undefined}
          />
        </div>

        <div className="rounded-lg border border-border p-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <p className="text-[13px] font-medium">{tx('Ağırlık')}</p>
              <p className="text-[12px] text-muted-foreground">{tx('Oransal — hedeflerin toplamına göre yüzdeye çevrilir.')}</p>
            </div>
            <NumberStepper value={weight} onChange={setWeight} min={1} max={100} step={5} ariaLabel={tx('Hedef ağırlığı')} />
          </div>
          <ShareBar
            className="mt-3"
            items={[...others.map((g) => ({ id: g.id, label: g.title, weight: g.weight })), { id: '__new__', label: title.trim() || (goal ? goal.title : tx('Yeni hedef')), weight, color: 'hsl(var(--primary))' }]}
            color="hsl(var(--muted-foreground))"
            highlightId="__new__"
            height={10}
          />
          <p className="mt-2 text-[12px]">{tx('Bu hedef, {0} için bu dönemin hedeflerinin', [employeeName])}{' '}
            <span className="tabular font-semibold text-primary">{formatShareOf(shareOf(weight, total))}</span>{' '}{tx('olur.')}
          </p>
        </div>

        <Switch
          checked={numeric}
          onChange={setNumeric}
          label={tx('Sayısal hedef')}
          hint={numeric ? tx('İlerleme gerçekleşen / hedef değerden hesaplanır; %100’de kırpılır.') : tx('Sayısal değer yoksa ilerleme durumdan gelir: Gerçekleşti %100, Devam ediyor %50, Gerçekleşmedi %0.')}
        />
        <AnimatePresence initial={false}>
          {numeric && (
            <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.3, ease: EASE }} className="overflow-hidden">
              <div className="grid gap-4 sm:grid-cols-[1fr_140px]">
                <TextField label={tx('Hedef değer')} inputMode="decimal" value={target} onChange={(e) => setTarget(e.target.value)} error={touched ? errs.target : undefined} placeholder={tx('ör. 80')} className="tabular" />
                <TextField label={tx('Birim')} value={unit} onChange={(e) => setUnit(e.target.value)} placeholder={tx('%, adet, ₺…')} />
              </div>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    </Modal>
  )
}

/* ------------------------------ İlerleme güncelle ------------------------------ */

export function ProgressDialog({ goal, onClose }: { goal: Goal; onClose: () => void }) {
  const toast = useToast()
  const update = useUpdateGoalProgress()
  const numeric = goal.targetValue !== null && goal.targetValue > 0
  const [current, setCurrent] = useState(goal.currentValue !== null ? String(goal.currentValue).replace('.', ',') : '')
  const [status, setStatus] = useState<GoalStatus>(goal.status)
  const [error, setError] = useState<string | null>(null)

  const currentNum = parse(current)
  const preview = progressOf({ targetValue: goal.targetValue, currentValue: numeric ? currentNum : null, status })
  const invalid = numeric && (currentNum === null || currentNum < 0)

  const submit = () => {
    if (invalid) return
    update.mutate(
      { id: goal.id, input: { ...(numeric ? { currentValue: currentNum! } : {}), ...(status !== goal.status ? { status } : {}) } },
      {
        onSuccess: () => {
          toast.ok(tx('«{0}» güncellendi.', [goal.title]))
          onClose()
        },
        onError: (e) => setError(errorText(e)),
      },
    )
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('İlerlemeyi güncelle')}
      note={goal.title}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={update.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={update.isPending || invalid}>
            {update.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}
          </Button>
        </>
      }
    >
      <ErrorLine message={error} />
      <div className="flex flex-col gap-4">
        {numeric && (
          <TextField
            label={tx('Gerçekleşen (hedef: {0})', [formatGoalValue(goal.targetValue, goal.unit)])}
            inputMode="decimal"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            error={invalid ? tx('Sıfır ya da daha büyük bir değer girin.') : undefined}
            autoFocus
            className="tabular"
          />
        )}
        <div>
          <p className="mb-1.5 text-[13px] font-medium">{tx('Durum')}</p>
          <Segmented ariaLabel={tx('Hedef durumu')} size="sm" value={status} onChange={setStatus} options={GOAL_STATUSES.map((s) => ({ value: s, label: goalStatusLabels[s] }))} />
        </div>

        <div className="rounded-lg border border-border bg-muted/30 p-3">
          <div className="mb-1.5 flex justify-between text-[12px]">
            <span className="text-muted-foreground">{tx('Puana yansıyan ilerleme')}</span>
            <span className="tabular font-semibold">{preview.pct === null ? tx('hesaba girmez') : pct(Math.round(preview.pct))}</span>
          </div>
          <div className="h-2.5 overflow-hidden rounded-full bg-muted">
            <motion.div className="h-full rounded-full bg-primary" animate={{ width: `${preview.pct ?? 0}%` }} transition={{ type: 'spring', stiffness: 260, damping: 30 }} />
          </div>
          {numeric && (preview.raw ?? 0) > 100 && (
            <p className="mt-2 inline-flex items-center gap-1 text-[12px] font-medium text-[hsl(var(--success))]">
              <Flame className="size-3.5" aria-hidden />{tx('%{0} gerçekleşme — fazlası puana yansımaz, çubuk %100\'de durur.', [Math.round(preview.raw ?? 0)])}</p>
          )}
          {!numeric && <p className="mt-2 text-[12px] text-muted-foreground">{tx('Sayısal hedef olmadığı için ilerleme durumdan hesaplanır.')}</p>}
        </div>
      </div>
    </Modal>
  )
}
