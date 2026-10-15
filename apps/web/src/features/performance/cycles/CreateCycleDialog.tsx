/**
 * Yeni dönem. Tür ve yıl seçilince ad ve tarihler önerilir; kullanıcı
 * isterse değiştirir. Çakışan dönem varsa uyarılır (backend karar verir).
 */

import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { CalendarPlus, Info, TriangleAlert } from 'lucide-react'
import { CYCLE_PERIODS, cyclePeriodLabels, useCreateCycle, type CyclePeriod, type ReviewCycle } from '@/api/performance'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { performanceExtrasApi } from '@/api/performanceExtras'
import { useToast } from '@/components/ui/Toast'
import { formatDate } from '@/lib/format'
import { Segmented, errorText } from '../components/controls'
import { NumberStepper } from '../components/NumberStepper'
import { defaultsFor, nextSuggestion, overlapping } from './cycleDefaults'
import { tx } from '@/lib/i18n'

export function CreateCycleDialog({ cycles, onClose }: { cycles: ReviewCycle[]; onClose: () => void }) {
  const toast = useToast()
  const create = useCreateCycle()
  const suggestion = nextSuggestion(cycles)
  const [year, setYear] = useState(suggestion.year)
  const [period, setPeriod] = useState<CyclePeriod>(suggestion.period)
  const [form, setForm] = useState(() => defaultsFor(suggestion.year, suggestion.period))
  const [edited, setEdited] = useState({ name: false, dates: false })
  const [error, setError] = useState<string | null>(null)
  // G12: şablondan dönem — bölümler/sorular, ağırlıklar ve ölçek şablondan kopyalanır.
  const qc = useQueryClient()
  const templates = useQuery({ queryKey: ['perf', 'cycle-templates'], queryFn: ({ signal }) => performanceExtrasApi.templates(signal) })
  const [templateId, setTemplateId] = useState('')
  const [fromTemplatePending, setFromTemplatePending] = useState(false)
  const template = templates.data?.find((t) => t.id === templateId)

  const apply = (y: number, p: CyclePeriod) => {
    const d = defaultsFor(y, p)
    setForm((f) => ({
      name: edited.name ? f.name : d.name,
      startDate: edited.dates ? f.startDate : d.startDate,
      endDate: edited.dates ? f.endDate : d.endDate,
    }))
    setError(null)
  }

  const invalidDates = Boolean(form.startDate && form.endDate && form.endDate <= form.startDate)
  const clash = !invalidDates && form.startDate && form.endDate ? overlapping(cycles, form.startDate, form.endDate) : []
  const duplicate = cycles.find((c) => c.year === year && c.period === period)

  const submit = () => {
    if (!form.name.trim() || invalidDates || !form.startDate || !form.endDate) return
    if (templateId) {
      setFromTemplatePending(true)
      performanceExtrasApi
        .createFromTemplate({ templateId, name: form.name.trim(), year, period, startDate: form.startDate, endDate: form.endDate })
        .then((c) => {
          void qc.invalidateQueries({ queryKey: ['perf', 'cycles'] })
          toast.ok(tx('«{0}» şablondan taslak olarak oluşturuldu.', [c.name]))
          onClose()
        })
        .catch((e) => setError(errorText(e)))
        .finally(() => setFromTemplatePending(false))
      return
    }
    create.mutate(
      { name: form.name.trim(), year, period, startDate: form.startDate, endDate: form.endDate },
      {
        onSuccess: (c) => {
          toast.ok(tx('«{0}» taslak olarak oluşturuldu. Hazır olduğunuzda açın.', [c.name]))
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
      size="lg"
      title={tx('Yeni değerlendirme dönemi')}
      note={tx('Dönem taslak olarak oluşturulur. Açıldığında hedefler ve değerlendirmeler bu döneme bağlanır.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={create.isPending}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={create.isPending || fromTemplatePending || invalidDates || !form.name.trim()}>
            <CalendarPlus aria-hidden />
            {create.isPending ? tx('Oluşturuluyor…') : tx('Dönemi oluştur')}
          </Button>
        </>
      }
    >
      <AnimatePresence>
        {error && (
          <motion.p
            role="alert"
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: 'auto' }}
            exit={{ opacity: 0, height: 0 }}
            className="mb-4 flex items-start gap-2 overflow-hidden rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-[13px] text-destructive"
          >
            <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
            {error}
          </motion.p>
        )}
      </AnimatePresence>

      <div className="flex flex-col gap-5">
        {(templates.data?.length ?? 0) > 0 && (
          <div className="space-y-1.5">
            <SelectField
              label={tx('Şablon')}
              value={templateId || '__none__'}
              onChange={(v) => {
                const id = v === '__none__' ? '' : v
                setTemplateId(id)
                const t = templates.data?.find((x) => x.id === id)
                if (t) {
                  setPeriod(t.period)
                  const d = defaultsFor(year, t.period)
                  setForm((f) => ({ ...f, startDate: d.startDate, endDate: d.endDate }))
                }
              }}
              options={[{ value: '__none__', label: tx('Şablonsuz (boş dönem)') }, ...(templates.data ?? []).map((t) => ({ value: t.id, label: t.name }))]}
              hint={tx('Şablon seçilirse bölümler, sorular, ağırlıklar ve ölçek kopyalanır.')}
            />
            {template?.config && (
              <p className="text-[12px] text-muted-foreground">
                {tx('Ölçek {0}-{1}', [template.config.scale.min, template.config.scale.max])}
                {template.config.sections.length > 0 && ` · ${template.config.sections.map((s) => `${s.title} %${s.weight}`).join(', ')}`}
              </p>
            )}
          </div>
        )}
        <div className="flex flex-wrap items-end gap-4">
          <div>
            <p className="mb-1.5 text-[13px] font-medium">{tx('Yıl')}</p>
            <NumberStepper
              value={year}
              onChange={(v) => {
                setYear(v)
                apply(v, period)
              }}
              min={2000}
              max={2100}
              ariaLabel={tx('Yıl')}
            />
          </div>
          <div className="min-w-0 flex-1">
            <p className="mb-1.5 text-[13px] font-medium">{tx('Dönem türü')}</p>
            <Segmented
              ariaLabel={tx('Dönem türü')}
              value={period}
              onChange={(p) => {
                setPeriod(p)
                apply(year, p)
              }}
              options={CYCLE_PERIODS.map((p) => ({ value: p, label: p === 'Annual' ? tx('Yıllık') : p, title: cyclePeriodLabels[p] }))}
              size="sm"
            />
          </div>
        </div>

        <TextField
          label={tx('Ad')}
          value={form.name}
          onChange={(e) => {
            setForm((f) => ({ ...f, name: e.target.value }))
            setEdited((x) => ({ ...x, name: true }))
          }}
          hint={tx('Listelerde ve raporlarda bu adla görünür.')}
          required
        />

        <div className="grid gap-4 sm:grid-cols-2">
          <TextField
            label={tx('Başlangıç')}
            type="date"
            value={form.startDate}
            onChange={(e) => {
              setForm((f) => ({ ...f, startDate: e.target.value }))
              setEdited((x) => ({ ...x, dates: true }))
            }}
          />
          <TextField
            label={tx('Bitiş')}
            type="date"
            value={form.endDate}
            onChange={(e) => {
              setForm((f) => ({ ...f, endDate: e.target.value }))
              setEdited((x) => ({ ...x, dates: true }))
            }}
            error={invalidDates ? tx('Bitiş tarihi başlangıçtan sonra olmalı.') : undefined}
          />
        </div>

        <AnimatePresence>
          {(duplicate || clash.length > 0) && (
            <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden">
              <p className="flex items-start gap-2 rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2.5 text-[12px] leading-relaxed">
                <Info className="mt-0.5 size-3.5 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
                <span>
                  {duplicate && (
                    <>
                      {tx('{0} yılı için bu türde bir dönem zaten var:', [year])}{' '}<strong className="font-medium">{duplicate.name}</strong>.{' '}
                    </>
                  )}
                  {clash.length > 0 && (
                    <>
                      {tx('Bu tarihler şu dönemlerle çakışıyor:')}{' '}
                      {clash.map((c, i) => (
                        <span key={c.id}>
                          {i > 0 && ', '}
                          <strong className="font-medium">{c.name}</strong> ({formatDate(c.startDate)} – {formatDate(c.endDate)})
                        </span>
                      ))}
                      .
                    </>
                  )}
                </span>
              </p>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    </Modal>
  )
}
