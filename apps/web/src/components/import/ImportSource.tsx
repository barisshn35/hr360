import { useId } from 'react'
import { motion } from 'motion/react'
import { Download, Table2, Upload } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { SheetEditor, type SheetColumn } from './SheetEditor'

export type ImportMode = 'file' | 'sheet'

export interface ImportTemplate {
  fileName: string
  headers: string[]
  example: string[][]
}

/** Şablonu CSV olarak indirir: noktalı virgül ve UTF-8 BOM ile Türkçe Excel doğru sütunlara ve karakterlerle açar. */
export function downloadTemplate(template: ImportTemplate) {
  const esc = (v: string) => (/[;"\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v)
  const csv = [template.headers, ...template.example].map((r) => r.map(esc).join(';')).join('\r\n')
  const url = URL.createObjectURL(new Blob(['﻿' + csv + '\r\n'], { type: 'text/csv;charset=utf-8' }))
  const a = document.createElement('a')
  a.href = url; a.download = template.fileName; a.click()
  URL.revokeObjectURL(url)
}

/**
 * İçe aktarma kaynağı: dosya yükleme ya da uygulama içinde Excel benzeri tabloya giriş.
 * İki yol da aynı başlık → değer kayıtlarını üretir; doğrulama ve ön izleme ortaktır.
 */
export function ImportSource({ mode, onModeChange, onFile, template, columns, sheet, onSheetChange }: {
  mode: ImportMode
  onModeChange: (m: ImportMode) => void
  onFile: (file: File) => void
  template: ImportTemplate
  columns: SheetColumn[]
  sheet: string[][]
  onSheetChange: (rows: string[][]) => void
}) {
  const inputId = useId()
  const tabs: { key: ImportMode; label: string; icon: typeof Upload }[] = [
    { key: 'file', label: tx('Dosya yükle'), icon: Upload },
    { key: 'sheet', label: tx('Tabloya gir'), icon: Table2 },
  ]

  return (
    <div className="space-y-4">
      <div role="tablist" aria-label={tx('İçe aktarma kaynağı')} className="inline-flex rounded-lg border border-border p-0.5">
        {tabs.map((t) => (
          <button key={t.key} type="button" role="tab" aria-selected={mode === t.key} onClick={() => onModeChange(t.key)}
            className={cn('flex cursor-pointer items-center gap-1.5 rounded-md px-3 py-1.5 text-sm transition-colors',
              mode === t.key ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:text-foreground')}>
            <t.icon className="size-4" />{t.label}
          </button>
        ))}
      </div>

      {mode === 'file' ? (
        <>
          <label htmlFor={inputId}
            className="group flex cursor-pointer flex-col items-center gap-2 rounded-xl border border-dashed border-border px-6 py-10 text-center transition-colors hover:border-primary/50 hover:bg-primary/5"
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => { e.preventDefault(); const f = e.dataTransfer.files?.[0]; if (f) onFile(f) }}>
            <motion.span animate={{ y: [0, -4, 0] }} transition={{ duration: 2.2, repeat: Infinity, ease: 'easeInOut' }}>
              <Upload className="size-6 text-muted-foreground group-hover:text-primary" />
            </motion.span>
            <span className="text-sm font-medium">{tx('Excel (.xlsx) ya da CSV (.csv) dosyası seçin')}</span>
            <span className="text-xs text-muted-foreground">{tx('veya sürükleyip bırakın — ilk satır başlık olmalı')}</span>
            <input id={inputId} type="file" accept=".xlsx,.csv" className="hidden"
              onChange={(e) => { const f = e.target.files?.[0]; if (f) onFile(f); e.target.value = '' }} />
          </label>
          <Button variant="outline" size="sm" onClick={() => downloadTemplate(template)}><Download className="size-4" />{' '}{tx('Örnek şablonu indir')}</Button>
        </>
      ) : (
        <SheetEditor columns={columns} rows={sheet} onChange={onSheetChange} />
      )}
    </div>
  )
}

/** Şablon başlıklarından tablo sütunları; ipuçları (örnek değer, öneri listesi, genişlik) başlık adıyla verilir. */
export function sheetColumns(headers: string[], hints: Record<string, Omit<SheetColumn, 'header'>> = {}): SheetColumn[] {
  return headers.map((h) => ({ header: h, ...hints[h] }))
}
