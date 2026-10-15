import { useState } from 'react'
import { LoaderCircle, QrCode } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { expenseApi } from '@/api/expense'
import { qrDetectorAvailable, readQrFromImage, type EInvoice } from '@/lib/expenseAudit'
import { tx } from '@/lib/i18n'

/**
 * e-Fatura / e-Arşiv karekodu (kalem içinde açılan alan). Karekod tarayıcıda çözülür
 * (BarcodeDetector varsa; görüntü sunucuya gönderilmez) ya da okuyucunun verdiği metin yapıştırılır.
 * Metin expense-service üzerinden ml-inference'ta ayrıştırılıp doğrulanır; uyarılar kullanıcıya
 * gösterilir, alanlar forma önerilir (kullanıcı kontrol eder).
 */
export function EInvoiceQr({ id, onParsed, onClose }: {
  id: string
  onParsed: (e: EInvoice) => void
  onClose: () => void
}) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [warnings, setWarnings] = useState<string[]>([])
  const canScan = qrDetectorAvailable()

  async function parse(raw: string) {
    setBusy(true)
    setError(null)
    try {
      const e = await expenseApi.einvoice(raw.trim())
      setWarnings(e.warnings)
      onParsed(e)
    } catch (err) {
      setError(err instanceof Error ? err.message : tx('Karekod okunamadı'))
    } finally {
      setBusy(false)
    }
  }

  async function scan(file: File) {
    setError(null)
    try {
      const raw = await readQrFromImage(file)
      if (!raw) {
        setError(tx('Görüntüde karekod bulunamadı; karekod metnini yapıştırabilirsiniz.'))
        return
      }
      setText(raw)
      await parse(raw)
    } catch {
      setError(tx('Görüntü okunamadı; karekod metnini yapıştırabilirsiniz.'))
    }
  }

  return (
    <div className="mt-2 space-y-2 rounded-md border border-dashed border-border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="flex items-center gap-1.5 text-[12.5px] font-medium">
          <QrCode className="size-3.5" />
          {tx('e-Fatura / e-Arşiv karekodu')}
        </span>
        <Button type="button" size="sm" variant="ghost" className="cursor-pointer" onClick={onClose}>
          {tx('Kapat')}
        </Button>
      </div>
      {canScan ? (
        <label className="inline-flex cursor-pointer items-center gap-1 text-[12px] text-muted-foreground underline hover:text-foreground">
          <QrCode className="size-3" />
          {tx('Fotoğraftan oku (karekod tarayıcınızda çözülür, görüntü gönderilmez)')}
          <input type="file" accept="image/*" capture="environment" className="hidden"
            onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void scan(f) }} />
        </label>
      ) : (
        <p className="text-[12px] text-muted-foreground">
          {tx('Tarayıcınız karekodu görüntüden okuyamıyor; bir karekod okuyucunun verdiği metni yapıştırın.')}
        </p>
      )}
      <TextAreaField
        id={id}
        label={tx('Karekod metni')}
        rows={3}
        value={text}
        maxLength={8000}
        onChange={(e) => setText(e.target.value)}
        error={error ?? undefined}
      />
      <Button type="button" size="sm" variant="outline" className="cursor-pointer" disabled={busy || text.trim().length < 2}
        onClick={() => void parse(text)}>
        {busy && <LoaderCircle className="size-4 animate-spin" />}
        {tx('Çözümle ve doldur')}
      </Button>
      {warnings.length > 0 && (
        <ul role="status" className="list-disc space-y-0.5 pl-5 text-[12px] text-[hsl(var(--warning))]">
          {warnings.map((w) => <li key={w}>{w}</li>)}
        </ul>
      )}
    </div>
  )
}
