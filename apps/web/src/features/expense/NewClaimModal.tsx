import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { LoaderCircle, Plus, QrCode, ScanText, Upload } from 'lucide-react'
import { headerField, readSpreadsheet, SpreadsheetError } from '@/lib/spreadsheet'
import { Button } from '@/components/ui/button'
import { Modal, ErrorSummary, type SummaryItem } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { expenseApi } from '@/api/expense'
import { useMyEmployeeId } from '@/api/queries'
import { useAuth } from '@/auth/useAuth'
import { expenseCategoryLabels, type ExpenseCategory, type ExpenseItem } from '@/api/types'
import type { ExpenseClaim } from '@/api/expense'
import { formatMoney, parseDecimal } from '@/lib/format'
import { localISODate } from '@/lib/dates'
import { tx } from '@/lib/i18n'
import { isQueuedOffline } from '@/lib/push'
import { einvoiceToDraft, type EInvoice } from '@/lib/expenseAudit'
import { EInvoiceQr } from './EInvoiceQr'
import { PiiHint } from '@/components/PiiHint'

/** Formda tutulan taslak kalem — tutar kullanıcı yazarken metin kalır. */
interface DraftItem {
  key: number
  category: ExpenseCategory
  amount: string
  expenseDate: string
  description: string
  /** TRY dışı: tutar bu para birimindedir; TL karşılığı harcama günündeki TCMB kuruyla hesaplanır. */
  currency: string
  km: string
  /** Düzenlemede mevcut kalemden korunan alanlar (formda gösterilmez). */
  receiptStorageKey?: string | null
  travelRequestId?: string | null
  /** e-Fatura/e-Arşiv karekodundan (mükerrer fiş denetimi ve onaycı için). */
  supplierTaxId?: string | null
  invoiceNo?: string | null
  ettn?: string | null
  vatAmount?: number | null
}

const CURRENCIES = ['TRY', 'USD', 'EUR', 'GBP', 'CHF']

/**
 * Tutar/km metin olarak girilir ve parseDecimal ile okunur: `type=number` tr-TR'de "123,45"
 * yazımındaki virgülü düşürüp 12345 yapıyordu. Kayıtlı değer forma ondalık virgülle yazılır
 * (aksi hâlde "1.234" binlik ayırıcı sanılırdı).
 */
const toInput = (n: number | null | undefined) => (n == null ? '' : String(n).replace('.', ','))
const num = (s: string) => parseDecimal(s)

/** Kalem tutarı/km alanının hatası (boşsa ve gönderilmemişse yok). */
function amountError(raw: string, required: boolean): string | undefined {
  if (!raw.trim()) return required ? tx('Tutar girilmeli.') : undefined
  const v = num(raw)
  if (v === null) return tx('Geçerli bir sayı girin (ör. 123,45).')
  if (v <= 0) return tx('Değer 0\'dan büyük olmalı.')
  return undefined
}

let nextKey = 1
function emptyItem(): DraftItem {
  return {
    key: nextKey++,
    category: 'Travel',
    amount: '',
    expenseDate: localISODate(),
    description: '',
    currency: 'TRY',
    km: '',
  }
}

/** Kayıtlı kalemi forma çevirir (düzenleme). Yabancı para kalemlerinde özgün tutar gösterilir. */
function draftFromItem(i: ExpenseItem): DraftItem {
  const foreign = i.category !== 'Mileage' && i.originalCurrency && i.originalAmount != null
  return {
    key: nextKey++,
    category: i.category,
    amount: i.category === 'Mileage' ? '' : toInput(foreign ? i.originalAmount : i.amount),
    expenseDate: i.expenseDate,
    description: i.description ?? '',
    currency: foreign ? i.originalCurrency! : 'TRY',
    km: toInput(i.km),
    receiptStorageKey: i.receiptStorageKey,
    travelRequestId: i.travelRequestId,
    supplierTaxId: i.supplierTaxId,
    invoiceNo: i.invoiceNo,
    ettn: i.ettn,
    vatAmount: i.vatAmount,
  }
}

// Türkçe/İngilizce kategori adını ExpenseCategory değerine çevirir.
const CATEGORY_BY_LABEL: Record<string, ExpenseCategory> = Object.fromEntries(
  (Object.keys(expenseCategoryLabels) as ExpenseCategory[]).flatMap((c) => [
    [c.toLowerCase(), c],
    [expenseCategoryLabels[c].toLowerCase(), c],
  ]),
)

function excelDateToIso(value: unknown): string {
  if (value instanceof Date) {
    const y = value.getFullYear()
    const m = String(value.getMonth() + 1).padStart(2, '0')
    const d = String(value.getDate()).padStart(2, '0')
    return `${y}-${m}-${d}`
  }
  const s = String(value ?? '').trim()
  const m1 = s.match(/^(\d{1,2})[./](\d{1,2})[./](\d{4})$/)
  if (m1) return `${m1[3]}-${m1[2].padStart(2, '0')}-${m1[1].padStart(2, '0')}`
  if (/^\d{4}-\d{2}-\d{2}$/.test(s)) return s
  return localISODate()
}

const ITEM_HEADER_ALIASES: Record<string, 'category' | 'amount' | 'expenseDate' | 'description'> = {
  tür: 'category',
  tur: 'category',
  kategori: 'category',
  tutar: 'amount',
  tarih: 'expenseDate',
  açıklama: 'description',
  aciklama: 'description',
}

/** Excel dosyasını okuyup DraftItem listesine çevirir - tutarı 0 ya da
 * negatif olan satırlar (başlık tekrarı, boş satır vb.) atlanır. */
async function parseItemsFromFile(file: File): Promise<DraftItem[]> {
  const raw = await readSpreadsheet(file)

  const drafts: DraftItem[] = []
  for (const record of raw) {
    const mapped: Record<string, unknown> = {}
    for (const [key, value] of Object.entries(record)) {
      const field = headerField(ITEM_HEADER_ALIASES, key)
      if (field) mapped[field] = value
    }
    const amount = typeof mapped.amount === 'number' ? mapped.amount : num(String(mapped.amount ?? ''))
    if (!amount || amount <= 0) continue

    const categoryRaw = String(mapped.category ?? '').trim().toLowerCase()
    drafts.push({
      key: nextKey++,
      category: CATEGORY_BY_LABEL[categoryRaw] ?? 'Other',
      amount: toInput(amount),
      expenseDate: excelDateToIso(mapped.expenseDate),
      description: String(mapped.description ?? '').trim(),
      currency: 'TRY',
      km: '',
    })
  }
  return drafts
}

interface Errors {
  employeeId?: string
  title?: string
  items?: string
}

/**
 * Çok kalemli masraf formu.
 *
 * Toplam kullanıcı yazdıkça kendiliğinden güncellenir; backend zaten
 * kalemlerden hesapladığı için gönderilmez, burada yalnızca gösterilir.
 */
export function NewClaimModal({ open, onClose, claim }: {
  open: boolean
  onClose: () => void
  /** Verilirse taslak düzenleme kipinde açılır (talep sahibi değiştirilemez). */
  claim?: ExpenseClaim
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const reduced = useReducedMotion()

  // Başkası adına beyan yalnızca İK açabilir (backend 403 döner);
  // diğerleri için talep sahibi her zaman kendileridir.
  const { roles } = useAuth()
  const canPickOthers = roles.some((r) => r === 'hr-admin' || r === 'tenant-admin' || r === 'platform-admin')
  const me = useMyEmployeeId(!canPickOthers)

  const [pickedEmployeeId, setEmployeeId] = useState('')
  const employeeId = canPickOthers ? pickedEmployeeId : (me.employeeId ?? '')
  const [title, setTitle] = useState('')
  const [items, setItems] = useState<DraftItem[]>([emptyItem()])
  const [errors, setErrors] = useState<Errors>({})
  const [submitted, setSubmitted] = useState(false)
  const editing = Boolean(claim)

  // Düzenleme: pencere her açıldığında formu kayıtlı taslaktan doldur.
  useEffect(() => {
    if (!open || !claim) return
    setTitle(claim.title)
    setItems((claim.items ?? []).length > 0 ? (claim.items ?? []).map(draftFromItem) : [emptyItem()])
    setErrors({})
    setSubmitted(false)
  }, [open, claim])

  const policy = useQuery({ queryKey: ['expense', 'policy'], queryFn: ({ signal }) => expenseApi.policy(signal), enabled: open, staleTime: 300_000 })
  const kmRate = policy.data?.kmRate ?? 0
  const [rates, setRates] = useState<Record<string, number>>({})
  const rateKey = (i: DraftItem) => `${i.currency}|${i.expenseDate}`
  useEffect(() => {
    for (const i of items) {
      if (i.currency === 'TRY' || !i.expenseDate || rates[rateKey(i)] !== undefined) continue
      const k = rateKey(i)
      expenseApi.fx(i.currency, i.expenseDate).then((r) => setRates((p) => ({ ...p, [k]: r.rate })), () => setRates((p) => ({ ...p, [k]: 0 })))
    }
  }, [items, rates])
  const tlOf = (i: DraftItem) => i.category === 'Mileage' ? (num(i.km) || 0) * kmRate
    : i.currency === 'TRY' ? num(i.amount) || 0 : (num(i.amount) || 0) * (rates[rateKey(i)] || 0)
  const total = items.reduce((sum, i) => sum + tlOf(i), 0)
  const [ocrBusy, setOcrBusy] = useState<number | null>(null)
  async function readReceipt(key: number, file: File) {
    setOcrBusy(key)
    try {
      const r = await expenseApi.ocr(file)
      patch(key, { ...(r.amount ? { amount: toInput(r.amount) } : {}), ...(r.date ? { expenseDate: r.date } : {}) })
      toast.ok(r.amount ? tx('Fişten okundu; tutarı ve tarihi kontrol edin.') : tx('Tutar okunamadı; elle girin.'))
    } catch (e) {
      toast.stop(e instanceof Error ? e.message : tx('Fiş okunamadı'))
    } finally {
      setOcrBusy(null)
    }
  }

  const [qrOpen, setQrOpen] = useState<number | null>(null)
  function applyEInvoice(key: number, e: EInvoice) {
    const d = einvoiceToDraft(e, CURRENCIES)
    patch(key, {
      ...(d.amount ? { amount: d.amount } : {}),
      ...(d.currency ? { currency: d.currency } : {}),
      ...(d.expenseDate ? { expenseDate: d.expenseDate } : {}),
      supplierTaxId: d.supplierTaxId ?? null,
      invoiceNo: d.invoiceNo ?? null,
      ettn: d.ettn ?? null,
      vatAmount: d.vatAmount ?? null,
    })
    toast.ok(e.warnings.length ? tx('Karekod okundu; uyarıları ve alanları kontrol edin.') : tx('Karekod okundu; tutarı ve tarihi kontrol edin.'))
  }

  function patch(key: number, changes: Partial<DraftItem>) {
    setItems((prev) => prev.map((i) => (i.key === key ? { ...i, ...changes } : i)))
  }

  function reset() {
    setTitle('')
    setItems([emptyItem()])
    setErrors({})
    setSubmitted(false)
  }

  async function handleImportFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    try {
      const imported = await parseItemsFromFile(file)
      if (imported.length === 0) {
        toast.stop(tx('Dosyada geçerli (tutarı olan) satır bulunamadı.'))
        return
      }
      // Bos ilk kalemi (kullanici hic dokunmadiysa) ithal edilenlerle degistir.
      setItems((prev) => {
        const isUntouched =
          prev.length === 1 && !prev[0].amount && !prev[0].description
        return isUntouched ? imported : [...prev, ...imported]
      })
      toast.ok(tx('{0} kalem içe aktarıldı.', [imported.length]))
    } catch (err) {
      toast.stop(err instanceof SpreadsheetError ? err.message : tx('Dosya okunamadı. Geçerli bir .xlsx ya da .csv dosyası seçin.'))
    }
  }

  function validate(overrideEmployeeId?: string): Errors {
    const next: Errors = {}
    if (!editing && !(overrideEmployeeId ?? employeeId)) next.employeeId = tx('Çalışan seçilmeli.')
    if (title.trim().length < 3) next.title = tx('Başlık en az 3 karakter olmalı.')
    if (items.length === 0) {
      next.items = tx('En az bir kalem eklenmeli.')
    } else if (items.some((i) => amountError(i.category === 'Mileage' ? i.km : i.amount, true))) {
      // GUVENLIK/VERI BUTUNLUGU: onceki hali yalnizca "!Number(i.amount)"
      // kontrol ediyordu - bu, negatif tutarlari (orn. -50) YAKALAMIYORDU,
      // cunku Number('-50') JavaScript'te "truthy". Form negatif tutari
      // kabul edip gonderiyordu, backend'in jenerik "istek gecersiz"
      // hatasina kadar hicbir uyari gorunmuyordu.
      next.items = tx('Her kalemin tutarı 0\'dan büyük olmalı.')
    } else if (items.some((i) => !i.expenseDate)) {
      next.items = tx('Her kalemin tarihi girilmeli.')
    }
    return next
  }

  const mutation = useMutation({
    mutationFn: () => {
      const payload: ExpenseItem[] = items.map((i) => ({
        category: i.category,
        amount: i.category === 'Mileage' ? 0 : num(i.amount) ?? 0,
        expenseDate: i.expenseDate,
        description: i.description.trim() || undefined,
        receiptStorageKey: i.receiptStorageKey ?? undefined,
        travelRequestId: i.travelRequestId ?? undefined,
        supplierTaxId: i.supplierTaxId ?? undefined,
        invoiceNo: i.invoiceNo ?? undefined,
        ettn: i.ettn ?? undefined,
        vatAmount: i.vatAmount ?? undefined,
        ...(i.category === 'Mileage' ? { km: num(i.km) ?? 0 } : {}),
        ...(i.category !== 'Mileage' && i.currency !== 'TRY' ? { originalCurrency: i.currency, originalAmount: num(i.amount) ?? 0 } : {}),
      }))
      if (claim) return expenseApi.updateClaim(claim.id, { title: title.trim(), currency: 'TRY', items: payload })
      return expenseApi.createClaim({
        employeeId,
        title: title.trim(),
        currency: 'TRY',
        items: payload,
      })
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(editing ? tx('Taslak güncellendi') : tx('Masraf talebi taslak olarak oluşturuldu'))
      onClose()
      reset()
    },
    onError: (e: unknown) => {
      // Çevrimdışı: masraf bu cihazda taslak olarak saklandı; bağlantı gelince taslak olarak oluşturulur.
      if (isQueuedOffline(e)) {
        toast.ok(e.message)
        onClose()
        reset()
        return
      }
      toast.stop(e instanceof Error ? e.message : editing ? tx('Taslak kaydedilemedi.') : tx('Talep oluşturulamadı.'))
    },
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitted(true)
    const next = validate()
    setErrors(next)
    if (Object.keys(next).length === 0) mutation.mutate()
  }

  const summary: SummaryItem[] = submitted
    ? ([
        errors.employeeId && { fieldId: 'claim-employee', message: errors.employeeId },
        errors.title && { fieldId: 'claim-title', message: errors.title },
        errors.items && { fieldId: `item-amount-${items[0]?.key}`, message: errors.items },
      ].filter(Boolean) as SummaryItem[])
    : []

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={editing ? tx('Taslağı düzenle') : tx('Yeni masraf talebi')}
      note={editing ? tx('Yalnızca taslak düzenlenebilir; onaya göndermek ayrı bir adımdır.') : tx('Taslak olarak açılır; onaya göndermek ayrı bir adımdır.')}
      size="lg"
      footer={
        <>
          <span className="tabular mr-auto text-[13px] text-muted-foreground">{tx('Toplam', [])}{' '}
            <motion.span
              key={total}
              className="text-[15px] font-semibold text-foreground"
              initial={reduced ? false : { opacity: 0, y: 3 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.2, ease: 'easeOut' }}
            >
              {formatMoney(total)}
            </motion.span>
          </span>
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
            form="new-claim"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {editing ? tx('Kaydet') : tx('Taslağı oluştur')}
          </Button>
        </>
      }
    >
      <form id="new-claim" onSubmit={submit} noValidate className="space-y-5">
        <ErrorSummary items={summary} />

        {editing ? null : canPickOthers ? (
          <EmployeePicker
            id="claim-employee"
            value={employeeId}
            onChange={(next) => {
              setEmployeeId(next)
              if (submitted) setErrors(validate(next))
            }}
            hint={submitted ? errors.employeeId : undefined}
          />
        ) : (
          <p className="text-[13px] text-muted-foreground">
            {me.notLinked
              ? tx('Hesabınıza bağlı çalışan kaydı bulunamadı; masraf beyanı açamazsınız.')
              : tx('Beyan sizin adınıza oluşturulacak.')}
          </p>
        )}
        <TextField
          id="claim-title"
          label={tx('Başlık')}
          required
          value={title}
          maxLength={200}
          onChange={(e) => setTitle(e.target.value)}
          onBlur={() => submitted && setErrors(validate())}
          error={errors.title}
        />

        <div className="space-y-3">
          <div className="flex items-baseline justify-between gap-4">
            <h3 className="text-[14px] font-semibold">{tx('Kalemler')}</h3>
            <div className="flex items-center gap-3">
              <span className="tabular text-[12px] text-muted-foreground">
                {tx('{0} kalem', [items.length])}</span>
              <label
                htmlFor="claim-items-import"
                className="flex cursor-pointer items-center gap-1 text-[12px] text-muted-foreground underline hover:text-foreground"
              >
                <Upload className="size-3" />
                {tx('Excel\'den içe aktar')}
              </label>
              <input
                id="claim-items-import"
                type="file"
                accept=".xlsx,.csv"
                className="hidden"
                onChange={handleImportFile}
              />
            </div>
          </div>

          <ul className="space-y-3">
            <AnimatePresence initial={false}>
              {items.map((item, index) => (
                <motion.li
                  key={item.key}
                  layout={!reduced}
                  initial={reduced ? false : { opacity: 0, height: 0 }}
                  animate={{ opacity: 1, height: 'auto' }}
                  exit={reduced ? undefined : { opacity: 0, height: 0 }}
                  transition={{ duration: 0.24, ease: 'easeOut' }}
                  className="overflow-hidden"
                >
                  <div className="rounded-md border border-border p-3">
                    <div className="mb-2 flex items-center justify-between gap-3">
                      <span className="tabular text-[12px] text-muted-foreground">{tx('Kalem {0}', [index + 1])}
                      </span>
                      {items.length > 1 && (
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          className="cursor-pointer"
                          onClick={() => setItems((prev) => prev.filter((i) => i.key !== item.key))}
                        >
                          {tx('Kaldır')}
                        </Button>
                      )}
                    </div>
                    <div className="grid gap-3 sm:grid-cols-4">
                      <SelectField
                        id={`item-cat-${item.key}`}
                        label={tx('Tür')}
                        value={item.category}
                        onChange={(v) => patch(item.key, { category: v as ExpenseCategory })}
                        options={(Object.keys(expenseCategoryLabels) as ExpenseCategory[]).map(
                          (c) => ({ value: c, label: expenseCategoryLabels[c] }),
                        )}
                      />
                      {item.category === 'Mileage' ? (
                        <TextField
                          id={`item-amount-${item.key}`}
                          label={tx('Kilometre')}
                          inputMode="decimal"
                          autoComplete="off"
                          className="tabular"
                          value={item.km}
                          error={amountError(item.km, submitted)}
                          hint={tx('km × {0} = {1}', [formatMoney(kmRate), formatMoney(tlOf(item))])}
                          onChange={(e) => patch(item.key, { km: e.target.value })}
                        />
                      ) : (
                        <TextField
                          id={`item-amount-${item.key}`}
                          label={item.currency === 'TRY' ? tx('Tutar') : tx('Tutar ({0})', [item.currency])}
                          inputMode="decimal"
                          autoComplete="off"
                          className="tabular"
                          value={item.amount}
                          error={amountError(item.amount, submitted)}
                          hint={item.currency !== 'TRY' ? (rates[rateKey(item)] ? tx('TCMB kuru {0} → {1}', [rates[rateKey(item)], formatMoney(tlOf(item))]) : tx('Kur alınıyor…')) : undefined}
                          onChange={(e) => patch(item.key, { amount: e.target.value })}
                          onBlur={() => submitted && setErrors(validate())}
                        />
                      )}
                      {item.category === 'Mileage' ? <div /> : (
                        <SelectField
                          id={`item-cur-${item.key}`}
                          label={tx('Para birimi')}
                          value={item.currency}
                          onChange={(v) => patch(item.key, { currency: v })}
                          options={CURRENCIES.map((c) => ({ value: c, label: c }))}
                        />
                      )}
                      <TextField
                        id={`item-date-${item.key}`}
                        label={tx('Tarih')}
                        type="date"
                        value={item.expenseDate}
                        onChange={(e) => patch(item.key, { expenseDate: e.target.value })}
                      />
                    </div>
                    <div className="mt-3">
                      <TextField
                        id={`item-desc-${item.key}`}
                        label={tx('Açıklama')}
                        hint={tx('İsteğe bağlı')}
                        value={item.description}
                        onChange={(e) => patch(item.key, { description: e.target.value })}
                      />
                      <PiiHint text={item.description} className="mt-2" />
                      {item.category !== 'Mileage' && (
                        <label className="mt-2 inline-flex cursor-pointer items-center gap-1 text-[12px] text-muted-foreground underline hover:text-foreground">
                          {ocrBusy === item.key ? <LoaderCircle className="size-3 animate-spin" /> : <ScanText className="size-3" />}
                          {tx('Fişten oku (görüntü sunucuda okunur, saklanmaz)')}
                          <input type="file" accept="image/jpeg,image/png,image/webp" className="hidden"
                            onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void readReceipt(item.key, f) }} />
                        </label>
                      )}
                      {item.category !== 'Mileage' && qrOpen !== item.key && (
                        <button type="button" onClick={() => setQrOpen(item.key)}
                          className="mt-2 ml-4 inline-flex cursor-pointer items-center gap-1 text-[12px] text-muted-foreground underline hover:text-foreground">
                          <QrCode className="size-3" />
                          {tx('e-Fatura QR oku')}
                        </button>
                      )}
                      {qrOpen === item.key && (
                        <EInvoiceQr id={`item-qr-${item.key}`} onClose={() => setQrOpen(null)}
                          onParsed={(e) => { applyEInvoice(item.key, e); setQrOpen(null) }} />
                      )}
                      {(item.supplierTaxId || item.invoiceNo || item.ettn) && (
                        <p className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-muted-foreground">
                          <span>{tx('e-Fatura')}:</span>
                          {item.supplierTaxId && <span className="tabular">{tx('VKN/TCKN {0}', [item.supplierTaxId])}</span>}
                          {item.invoiceNo && <span className="tabular">{tx('No {0}', [item.invoiceNo])}</span>}
                          {item.vatAmount != null && <span className="tabular">{tx('KDV {0}', [formatMoney(item.vatAmount, item.currency)])}</span>}
                          {item.ettn && <span className="tabular" title={item.ettn}>ETTN {item.ettn.slice(0, 8)}…</span>}
                          <button type="button" className="cursor-pointer underline hover:text-foreground"
                            onClick={() => patch(item.key, { supplierTaxId: null, invoiceNo: null, ettn: null, vatAmount: null })}>
                            {tx('Temizle')}
                          </button>
                        </p>
                      )}
                    </div>
                  </div>
                </motion.li>
              ))}
            </AnimatePresence>
          </ul>

          <Button
            type="button"
            size="sm"
            variant="outline"
            className="cursor-pointer"
            onClick={() => setItems((prev) => [...prev, emptyItem()])}
          >
            <Plus className="size-4" />
            {tx('Kalem ekle')}
          </Button>

          {submitted && errors.items && (
            <p role="alert" className="text-[13px] text-destructive">
              {errors.items}
            </p>
          )}
        </div>
      </form>
    </Modal>
  )
}
