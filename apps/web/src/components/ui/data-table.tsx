/**
 * HR360'ın liste deseni.
 *
 * Kaynak: 21st.dev "Users List Datatable" (shadcnstore, id 25159). Oradaki
 * kullanıcı listesine özel yapı genelleştirildi: sütunlar dışarıdan tanımlanıyor,
 * arama/sıralama/filtre/sayfalama/seçim/dışa aktarma burada; yükleme, boş ve
 * hata durumları da tabloya gömülü — 10 ekranın hepsi aynı davranışı alsın diye.
 */

import { useCallback, useEffect, useId, useMemo, useState, type ReactNode } from 'react'
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, ChevronsUpDown, Download, MoreHorizontal, Search } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { InputGroup, InputGroupAddon, InputGroupInput } from '@/components/ui/input-group'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { EASE } from '@/motion/primitives'
import { motion, useReducedMotion } from 'motion/react'
import { ApiError } from '@/api/client'
import { normalizeSearch } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { requestTrace, withCsvWatermark } from '@/lib/watermark'
import type { TableViewState } from '@/lib/savedViews'
import { SavedViews } from '@/components/ui/SavedViews'

export type Column<T> = {
  id: string
  header: string
  cell: (row: T) => ReactNode
  /** Arama kutusunun tarayacağı düz metin. Verilmezse sütun aranmaz. */
  searchText?: (row: T) => string
  /** Sıralanabilir sütunlar bunu verir; başlık tıklanabilir olur. */
  sortValue?: (row: T) => string | number
  /** CSV çıktısı. Verilmezse searchText kullanılır, o da yoksa sütun atlanır. */
  exportText?: (row: T) => string
  align?: 'left' | 'right'
  /** Dar ekranda gizlenecek sütunlar — en önemli 2-3 sütun mobilde kalsın. */
  hideBelow?: 'sm' | 'md' | 'lg'
  className?: string
}

export type TableFilter = {
  id: string
  label: string
  value: string
  options: Array<{ value: string; label: string }>
  onChange: (value: string) => void
}

export type RowAction<T> = {
  label: string
  onSelect: (row: T) => void
  destructive?: boolean
  hidden?: (row: T) => boolean
}

export type SortState = { columnId: string; dir: 'asc' | 'desc' }

/**
 * Sunucu tarafı sayfalama (G24). Verilirse tablo satırları kendisi süzmez, sıralamaz ve
 * bölmez: `rows` o anki sayfadır; arama, sıralama ve sayfa değişimi çağırana bildirilir.
 * Görünüm ve etkileşim istemci modundakiyle aynıdır.
 */
export type ServerPaging<T> = {
  total: number
  page: number
  onPageChange: (page: number) => void
  /** Arama kutusu değişti (çağıran gecikmeli olarak sunucuya gönderir ve 1. sayfaya döner). */
  onQueryChange: (query: string) => void
  sort: SortState | null
  onSortChange: (sort: SortState) => void
  /** Sunucunun sıralayabildiği sütun kimlikleri; diğerlerinde başlık tıklanmaz. */
  sortable?: string[]
  /** CSV için tüm eşleşen kayıtları getirir; verilmezse yalnızca görünen sayfa aktarılır. */
  fetchAll?: () => Promise<T[]>
  /** Arka planda yeni sayfa yükleniyor (önceki sayfa gösterilirken). */
  fetching?: boolean
}

export interface DataTableProps<T> {
  rows: T[] | undefined
  rowKey: (row: T) => string
  columns: Array<Column<T>>

  isLoading?: boolean
  error?: unknown
  onRetry?: () => void

  emptyTitle?: string
  emptyDetail?: string
  emptyAction?: ReactNode

  searchPlaceholder?: string
  filters?: TableFilter[]
  /** Sağ üstteki birincil eylem (ör. "Yeni talep"). */
  toolbarActions?: ReactNode

  rowActions?: Array<RowAction<T>>
  onRowClick?: (row: T) => void
  /** Satırı vurgulamak için (ör. bant dışı ücret satırı). */
  rowClassName?: (row: T) => string | undefined

  selectable?: boolean
  onSelectionChange?: (keys: string[]) => void
  /** Satır seçiliyken görünen toplu eylem şeridi. */
  bulkActions?: (keys: string[]) => ReactNode
  /** Değiştiğinde seçim temizlenir (ör. toplu işlem tamamlandı). */
  selectionResetKey?: unknown

  pageSize?: number
  /** Verilirse araç çubuğuna CSV dışa aktarma düğmesi eklenir. */
  exportFileName?: string
  initialSort?: { columnId: string; dir: 'asc' | 'desc' }

  /** Tablonun üstünde duran açıklama (ör. Kafka otomasyonu notu). */
  notice?: ReactNode

  /** Sunucu tarafı sayfalama; bkz. {@link ServerPaging}. */
  server?: ServerPaging<T>

  /**
   * Dalga 12: verilirse araç çubuğunda "Görünümler" menüsü çıkar — arama/sıralama/filtre kişiye özel
   * kaydedilir ve bağlantıyla paylaşılır (adres parametreleri v_q, v_s, v_f.<filtre>). Ekran başına tekil ad.
   */
  viewKey?: string
}

const HEAD_CLASS = 'h-11 px-4 py-0 text-[11px] font-medium tracking-[0.08em] text-muted-foreground uppercase'

const HIDE_CLASS: Record<NonNullable<Column<unknown>['hideBelow']>, string> = {
  sm: 'hidden sm:table-cell',
  md: 'hidden md:table-cell',
  lg: 'hidden lg:table-cell',
}

/** Hata nesnesinden okunabilir mesaj — ApiError zaten Türkçe metin taşıyor. */
function messageOf(error: unknown): string | undefined {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return undefined
}

/**
 * Excel Türkçe yerelinde noktalı virgül ayırıcı bekliyor; BOM olmadan da
 * Türkçe karakterler bozuk açılıyor. İkisi de burada.
 */
function toCsv<T>(rows: T[], columns: Array<Column<T>>): string {
  const usable = columns.filter((c) => c.exportText ?? c.searchText)
  const escape = (value: string) => `"${value.replace(/"/g, '""')}"`
  const lines = [usable.map((c) => escape(c.header)).join(';')]
  for (const row of rows) {
    lines.push(usable.map((c) => escape((c.exportText ?? c.searchText)!(row))).join(';'))
  }
  return `﻿${lines.join('\r\n')}`
}

function download(fileName: string, content: string) {
  const blob = new Blob([content], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}

/** Çok sayfalı listelerde tüm sayfa numaralarını basmak yerine pencere gösterir. */
function pageWindow(current: number, total: number): Array<number | 'gap'> {
  if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1)
  const out: Array<number | 'gap'> = [1]
  const from = Math.max(2, current - 1)
  const to = Math.min(total - 1, current + 1)
  if (from > 2) out.push('gap')
  for (let p = from; p <= to; p++) out.push(p)
  if (to < total - 1) out.push('gap')
  out.push(total)
  return out
}

export function DataTable<T>({
  rows,
  rowKey,
  columns,
  isLoading = false,
  error,
  onRetry,
  emptyTitle = tx('Kayıt yok'),
  emptyDetail,
  emptyAction,
  searchPlaceholder = tx('Ara'),
  filters,
  toolbarActions,
  rowActions,
  onRowClick,
  rowClassName,
  selectable = false,
  onSelectionChange,
  bulkActions,
  selectionResetKey,
  pageSize = 10,
  exportFileName,
  initialSort,
  notice,
  server,
  viewKey,
}: DataTableProps<T>) {
  const reduced = useReducedMotion()
  const searchId = useId()
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<string[]>([])
  const [localSort, setLocalSort] = useState<SortState | null>(initialSort ?? null)
  const sort = server ? server.sort : localSort
  const setSort = (next: (prev: SortState | null) => SortState) =>
    server ? server.onSortChange(next(server.sort)) : setLocalSort(next)
  const [exporting, setExporting] = useState(false)

  const searchable = useMemo(() => columns.filter((c) => c.searchText), [columns])

  const filtered = useMemo(() => {
    if (!rows) return []
    if (server) return rows
    const needle = normalizeSearch(query)
    if (!needle) return rows
    return rows.filter((row) =>
      searchable.some((c) => normalizeSearch(c.searchText!(row)).includes(needle)),
    )
  }, [rows, query, searchable, server])

  const sorted = useMemo(() => {
    if (!sort || server) return filtered
    const column = columns.find((c) => c.id === sort.columnId)
    if (!column?.sortValue) return filtered
    const dir = sort.dir === 'asc' ? 1 : -1
    return [...filtered].sort((a, b) => {
      const va = column.sortValue!(a)
      const vb = column.sortValue!(b)
      if (typeof va === 'number' && typeof vb === 'number') return (va - vb) * dir
      return String(va).localeCompare(String(vb), 'tr-TR') * dir
    })
  }, [filtered, sort, columns, server])

  const totalCount = server ? server.total : sorted.length
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))
  const safePage = server ? Math.min(server.page, totalPages) : Math.min(page, totalPages)
  const visible = server ? sorted : sorted.slice((safePage - 1) * pageSize, safePage * pageSize)
  const goTo = (p: number) => (server ? server.onPageChange(p) : setPage(p))

  // Güvenlik dalgası 2B: dosyanın ilk satırı indiren kişi · zaman · iz kodu (denetim kaydına bağlı).
  const exportCsv = async () => {
    if (!exportFileName) return
    setExporting(true)
    try {
      const list = server?.fetchAll ? await server.fetchAll() : sorted
      const trace = await requestTrace(`table:${exportFileName}`.slice(0, 60), { format: 'csv', rows: list.length })
      download(`${exportFileName}.csv`, withCsvWatermark(toCsv(list, columns), trace?.text))
    } finally {
      setExporting(false)
    }
  }

  // Dalga 12: kayıtlı görünüm / paylaşılan bağlantı uygulanır (yalnızca tanınan filtre değerleri).
  const applyView = (v: Partial<TableViewState>) => {
    if (v.q !== undefined) {
      setQuery(v.q)
      server?.onQueryChange(v.q)
    }
    if (v.sort) {
      if (server) server.onSortChange(v.sort)
      else setLocalSort(v.sort)
    } else if (v.sort === undefined && !server && v.q !== undefined) {
      setLocalSort(initialSort ?? null)
    }
    for (const f of filters ?? []) {
      const val = v.filters?.[f.id]
      if (val !== undefined && val !== f.value && f.options.some((o) => o.value === val)) f.onChange(val)
    }
  }
  const viewState: TableViewState = {
    q: query,
    sort,
    filters: Object.fromEntries((filters ?? []).map((f) => [f.id, f.value])),
  }

  // Arama/filtre değişince ilk sayfaya dön — yoksa boş sayfada kalınıyor.
  useEffect(() => setPage(1), [query, filters?.map((f) => f.value).join('|')])

  useEffect(() => onSelectionChange?.(selected), [selected, onSelectionChange])

  // Toplu işlemden sonra seçim temizlenir; listeden kalkan satırlar (yerel modda) seçimden düşer.
  useEffect(() => setSelected([]), [selectionResetKey])
  useEffect(() => {
    if (server || !rows) return
    const keys = new Set(rows.map(rowKey))
    setSelected((prev) => (prev.every((k) => keys.has(k)) ? prev : prev.filter((k) => keys.has(k))))
    // rowKey çoğunlukla satır içi fonksiyon; yalnızca satırlar değişince çalışsın.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, server])

  const toggleRow = useCallback((key: string) => {
    setSelected((prev) => (prev.includes(key) ? prev.filter((k) => k !== key) : [...prev, key]))
  }, [])

  const visibleKeys = visible.map(rowKey)
  const allSelected = visibleKeys.length > 0 && visibleKeys.every((k) => selected.includes(k))

  const toggleAll = () => {
    setSelected((prev) =>
      allSelected
        ? prev.filter((k) => !visibleKeys.includes(k))
        : [...new Set([...prev, ...visibleKeys])],
    )
  }

  const colSpan = columns.length + (selectable ? 1 : 0) + (rowActions?.length ? 1 : 0)

  return (
    <div className="flex flex-col gap-4">
      {notice}

      <Card className="gap-0 overflow-hidden pb-0">
        <CardHeader className="gap-3 border-b border-border">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
            <InputGroup className="w-full sm:max-w-xs">
              <InputGroupAddon>
                <Search />
              </InputGroupAddon>
              <InputGroupInput
                id={searchId}
                value={query}
                onChange={(e) => {
                  setQuery(e.target.value)
                  server?.onQueryChange(e.target.value)
                }}
                placeholder={searchPlaceholder}
                aria-label={searchPlaceholder}
              />
            </InputGroup>

            <div className="flex flex-wrap items-center gap-2 sm:ml-auto">
              {filters?.map((filter) => (
                <Select key={filter.id} value={filter.value} onValueChange={filter.onChange}>
                  <SelectTrigger size="sm" className="min-w-36" aria-label={filter.label}>
                    <SelectValue placeholder={filter.label} />
                  </SelectTrigger>
                  <SelectContent>
                    {filter.options.map((option) => (
                      <SelectItem key={option.value} value={option.value}>
                        {option.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              ))}

              {viewKey && <SavedViews viewKey={viewKey} state={viewState} apply={applyView} />}

              {exportFileName && (
                <Button
                  variant="outline"
                  size="sm"
                  className="cursor-pointer"
                  disabled={sorted.length === 0 || exporting}
                  onClick={() => void exportCsv()}
                >
                  <Download />
                  {tx('CSV')}
                </Button>
              )}

              {toolbarActions}
            </div>
          </div>

          {selectable && selected.length > 0 && (
            <div className="flex flex-wrap items-center gap-3 rounded-lg border border-primary/20 bg-primary/[0.06] px-3 py-2">
              <Badge variant="secondary" className="tabular">
                {tx('{0} satır seçili', [selected.length])}</Badge>
              {bulkActions?.(selected)}
              <Button
                variant="ghost"
                size="sm"
                className="ml-auto cursor-pointer"
                onClick={() => setSelected([])}
              >
                {tx('Seçimi temizle')}
              </Button>
            </div>
          )}
        </CardHeader>

        <CardContent className="p-0">
          {isLoading ? (
            <RowsSkeleton rows={Math.min(pageSize, 6)} columns={Math.min(colSpan, 5)} />
          ) : error ? (
            <ErrorState message={messageOf(error)} onRetry={onRetry} />
          ) : sorted.length === 0 ? (
            <EmptyState
              title={query ? tx('Aramanızla eşleşen kayıt yok') : emptyTitle}
              detail={query ? tx('Farklı bir arama deneyin ya da filtreleri temizleyin.') : emptyDetail}
              action={query ? undefined : emptyAction}
            />
          ) : (
            <div className={cn('w-full overflow-x-auto transition-opacity', server?.fetching && 'opacity-60')}>
              <Table>
                <TableHeader>
                  <TableRow className="bg-muted/40 hover:bg-muted/40">
                    {selectable && (
                      <TableHead className="w-12 p-4">
                        <Checkbox
                          checked={allSelected}
                          onCheckedChange={toggleAll}
                          aria-label={tx('Sayfadaki tüm satırları seç')}
                        />
                      </TableHead>
                    )}
                    {columns.map((column) => {
                      const sortable = Boolean(column.sortValue)
                        && (!server?.sortable || server.sortable.includes(column.id))
                      const active = sort?.columnId === column.id
                      const Icon = !active ? ChevronsUpDown : sort!.dir === 'asc' ? ArrowUp : ArrowDown
                      return (
                        <TableHead
                          key={column.id}
                          className={cn(
                            HEAD_CLASS,
                            column.align === 'right' && 'text-right',
                            column.hideBelow && HIDE_CLASS[column.hideBelow],
                          )}
                        >
                          {sortable ? (
                            <button
                              type="button"
                              onClick={() =>
                                setSort((prev) =>
                                  prev?.columnId === column.id
                                    ? { columnId: column.id, dir: prev.dir === 'asc' ? 'desc' : 'asc' }
                                    : { columnId: column.id, dir: 'asc' },
                                )
                              }
                              className={cn(
                                'inline-flex cursor-pointer items-center gap-1 uppercase transition-colors hover:text-foreground',
                                active && 'text-foreground',
                              )}
                            >
                              {column.header}
                              <Icon className="size-3" strokeWidth={2} />
                            </button>
                          ) : (
                            column.header
                          )}
                        </TableHead>
                      )
                    })}
                    {rowActions?.length ? (
                      <TableHead className={cn(HEAD_CLASS, 'w-16 text-right')}>
                        <span className="sr-only">{tx('İşlemler')}</span>
                      </TableHead>
                    ) : null}
                  </TableRow>
                </TableHeader>

                <TableBody>
                  {visible.map((row, rowIndex) => {
                    const key = rowKey(row)
                    const actions = rowActions?.filter((a) => !a.hidden?.(row)) ?? []
                    return (
                      <motion.tr
                        key={key}
                        data-slot="table-row"
                        initial={reduced ? false : { opacity: 0, y: 6 }}
                        animate={{ opacity: 1, y: 0 }}
                        transition={{
                          duration: 0.32,
                          // Sayfa başına en fazla 0,2 sn: uzun listede bekletmesin.
                          delay: reduced ? 0 : Math.min(rowIndex * 0.028, 0.2),
                          ease: EASE,
                        }}
                        onClick={onRowClick ? () => onRowClick(row) : undefined}
                        className={cn(
                          'border-b border-border/70 transition-[background-color,box-shadow] duration-200 hover:bg-accent/60 hover:shadow-[inset_2px_0_0_hsl(var(--primary))]',
                          onRowClick && 'cursor-pointer',
                          rowClassName?.(row),
                        )}
                      >
                        {selectable && (
                          <TableCell className="p-4" onClick={(e) => e.stopPropagation()}>
                            <Checkbox
                              checked={selected.includes(key)}
                              onCheckedChange={() => toggleRow(key)}
                              aria-label={tx('Satırı seç')}
                            />
                          </TableCell>
                        )}
                        {columns.map((column) => (
                          <TableCell
                            key={column.id}
                            className={cn(
                              'p-4 text-sm',
                              column.align === 'right' && 'tabular text-right',
                              column.hideBelow && HIDE_CLASS[column.hideBelow],
                              column.className,
                            )}
                          >
                            {column.cell(row)}
                          </TableCell>
                        ))}
                        {rowActions?.length ? (
                          <TableCell className="p-4 text-right" onClick={(e) => e.stopPropagation()}>
                            {actions.length > 0 && (
                              <DropdownMenu>
                                <DropdownMenuTrigger asChild>
                                  <Button
                                    variant="ghost"
                                    size="icon-sm"
                                    className="cursor-pointer"
                                    aria-label={tx('Satır işlemleri')}
                                  >
                                    <MoreHorizontal />
                                  </Button>
                                </DropdownMenuTrigger>
                                <DropdownMenuContent align="end">
                                  {actions.map((action) => (
                                    <DropdownMenuItem
                                      key={action.label}
                                      onSelect={() => action.onSelect(row)}
                                      className={cn(
                                        'cursor-pointer',
                                        action.destructive && 'text-destructive',
                                      )}
                                    >
                                      {action.label}
                                    </DropdownMenuItem>
                                  ))}
                                </DropdownMenuContent>
                              </DropdownMenu>
                            )}
                          </TableCell>
                        ) : null}
                      </motion.tr>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
          )}

          {!isLoading && !error && sorted.length > 0 && (
            <div className="flex flex-col items-center justify-between gap-3 border-t border-border p-4 sm:flex-row">
              <p className="tabular text-[13px] text-muted-foreground">
                {tx('{0} kayıttan {1}–{2} arası', [totalCount, (safePage - 1) * pageSize + 1, Math.min(safePage * pageSize, totalCount)])}</p>
              {totalPages > 1 && (
                <div className="flex items-center gap-1">
                  <Button
                    variant="outline"
                    size="icon-sm"
                    className="cursor-pointer"
                    onClick={() => goTo(Math.max(1, safePage - 1))}
                    disabled={safePage === 1}
                    aria-label={tx('Önceki sayfa')}
                  >
                    <ChevronLeft />
                  </Button>
                  {pageWindow(safePage, totalPages).map((entry, index) =>
                    entry === 'gap' ? (
                      <span key={`gap-${index}`} className="px-1 text-muted-foreground">
                        …
                      </span>
                    ) : (
                      <Button
                        key={entry}
                        variant={entry === safePage ? 'default' : 'outline'}
                        size="icon-sm"
                        className="tabular cursor-pointer"
                        onClick={() => goTo(entry)}
                        aria-label={tx('{0}. sayfa', [entry])}
                        aria-current={entry === safePage ? 'page' : undefined}
                      >
                        {entry}
                      </Button>
                    ),
                  )}
                  <Button
                    variant="outline"
                    size="icon-sm"
                    className="cursor-pointer"
                    onClick={() => goTo(Math.min(totalPages, safePage + 1))}
                    disabled={safePage === totalPages}
                    aria-label={tx('Sonraki sayfa')}
                  >
                    <ChevronRight />
                  </Button>
                </div>
              )}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
