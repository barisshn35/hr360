import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRight, LoaderCircle, Trash2, Waypoints } from 'lucide-react'
import { organizationApi } from '@/api/organization'
import type { Department, DepartmentLink, DepartmentLinkKind } from '@/api/types'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { errorText } from '@/features/performance/components/controls'
import { buildTree } from './DepartmentTree'
import { tx } from '@/lib/i18n'

export const departmentLinksKey = (companyId: string) => ['organization', 'department-links', companyId] as const

/** Şirketin matris bağları. Tablo henüz kurulmamışsa (migration uygulanmadı) hata durumuna düşer. */
export function useDepartmentLinks(companyId: string, enabled = true) {
  return useQuery({
    queryKey: departmentLinksKey(companyId),
    queryFn: ({ signal }) => organizationApi.listDepartmentLinks(companyId, signal),
    enabled: enabled && Boolean(companyId),
    staleTime: 60_000,
    retry: 1,
  })
}

export const linkKindLabel = (k: DepartmentLinkKind) => (k === 'Project' ? tx('Proje') : tx('Fonksiyonel'))

function flatten(departments: Department[]) {
  const out: Array<{ value: string; label: string }> = []
  const walk = (nodes: ReturnType<typeof buildTree>, depth: number) => {
    for (const n of nodes) {
      out.push({ value: n.id, label: `${'— '.repeat(depth)}${n.name}` })
      walk(n.children, depth + 1)
    }
  }
  walk(buildTree(departments), 0)
  return out
}

/**
 * Matris (noktalı çizgi) bağlarını yönetir: listeler, ekler, siler. Yalnızca İK ve
 * kiracı yöneticisine açılır (sunucu da RequireHrAdmin ile korur). `fromId` verilirse
 * yalnızca o departmanın bağları listelenir ve yeni bağ ondan başlar.
 */
export function DepartmentLinksModal({
  open,
  onClose,
  companyId,
  departments,
  fromId,
}: {
  open: boolean
  onClose: () => void
  companyId: string
  departments: Department[]
  fromId?: string | null
}) {
  const toast = useToast()
  const confirm = useConfirm()
  const qc = useQueryClient()
  const links = useDepartmentLinks(companyId, open)
  const nameOf = useMemo(() => {
    const m = new Map(departments.map((d) => [d.id, d.name]))
    return (id: string) => m.get(id) ?? tx('Başka şirkette')
  }, [departments])
  const options = useMemo(() => flatten(departments), [departments])

  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [kind, setKind] = useState<DepartmentLinkKind>('Functional')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setFrom(fromId ?? '')
    setTo('')
    setKind('Functional')
    setNote('')
    setError(null)
  }, [open, fromId])

  const invalidate = () => qc.invalidateQueries({ queryKey: departmentLinksKey(companyId) })

  const create = useMutation({
    mutationFn: () =>
      organizationApi.createDepartmentLink({ fromDepartmentId: from, toDepartmentId: to, kind, note: note.trim() || undefined }),
    onSuccess: () => {
      toast.ok(tx('Matris bağı eklendi.'))
      setTo('')
      setNote('')
      setError(null)
      void invalidate()
    },
    onError: (e) => setError(errorText(e)),
  })

  const remove = useMutation({
    mutationFn: (id: string) => organizationApi.deleteDepartmentLink(id),
    onSuccess: () => {
      toast.ok(tx('Matris bağı silindi.'))
      void invalidate()
    },
    onError: (e) => toast.stop(errorText(e)),
  })

  const submit = () => {
    if (!from || !to) return setError(tx('İki departmanı da seçin.'))
    if (from === to) return setError(tx('Bir departman kendisine bağlanamaz.'))
    create.mutate()
  }

  const rows: DepartmentLink[] = (links.data ?? []).filter(
    (l) => !fromId || l.fromDepartmentId === fromId || l.toDepartmentId === fromId,
  )

  return (
    <Modal
      open={open}
      onClose={onClose}
      size="lg"
      title={fromId ? tx('{0}: matris bağları', [nameOf(fromId)]) : tx('Matris bağları')}
      note={tx('Ana hiyerarşiye ek olarak, bir departmanın başka bir departmana fonksiyonel ya da proje bazında noktalı çizgiyle raporlamasını tanımlar. Şemada kesikli eğri olarak görünür.')}
      footer={
        <Button variant="outline" onClick={onClose}>
          {tx('Kapat')}
        </Button>
      }
    >
      <div className="space-y-5">
        <section aria-labelledby="dl-list-h">
          <h3 id="dl-list-h" className="mb-2 text-[13px] font-semibold">
            {tx('Mevcut bağlar')}
          </h3>
          {links.isPending ? (
            <p className="text-[13px] text-muted-foreground">{tx('Yükleniyor')}</p>
          ) : links.isError ? (
            <p role="alert" className="text-[13px] text-destructive">
              {tx('Bağlar okunamadı. {0}', [errorText(links.error)])}
            </p>
          ) : rows.length === 0 ? (
            <p className="text-[13px] text-muted-foreground">{tx('Henüz matris bağı yok.')}</p>
          ) : (
            <ul className="divide-y divide-border rounded-lg border border-border">
              {rows.map((l) => (
                <li key={l.id} className="flex items-center gap-2 px-3 py-2 text-[13px]">
                  <Waypoints className="size-4 shrink-0 text-muted-foreground" aria-hidden />
                  <span className="min-w-0 flex-1">
                    <span className="flex flex-wrap items-center gap-1 font-medium">
                      {nameOf(l.fromDepartmentId)}
                      <ArrowRight className="size-3.5 text-muted-foreground" aria-label={tx('raporlar')} />
                      {nameOf(l.toDepartmentId)}
                    </span>
                    <span className="block text-[12px] text-muted-foreground">
                      {linkKindLabel(l.kind)}
                      {l.note ? ` · ${l.note}` : ''}
                    </span>
                  </span>
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={tx('{0} → {1} bağını sil', [nameOf(l.fromDepartmentId), nameOf(l.toDepartmentId)])}
                    disabled={remove.isPending}
                    onClick={async () => {
                      if (await confirm({ title: tx('Matris bağı silinsin mi?'), note: `${nameOf(l.fromDepartmentId)} → ${nameOf(l.toDepartmentId)}`, action: tx('Sil') }))
                        remove.mutate(l.id)
                    }}
                  >
                    <Trash2 className="size-4" />
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section aria-labelledby="dl-new-h" className="space-y-3 rounded-lg border border-dashed border-border p-3">
          <h3 id="dl-new-h" className="text-[13px] font-semibold">
            {tx('Yeni bağ')}
          </h3>
          <div className="grid gap-3 sm:grid-cols-2">
            <SelectField id="dl-from" label={tx('Raporlayan departman')} value={from} onChange={setFrom} options={options} required />
            <SelectField id="dl-to" label={tx('Noktalı çizgiyle raporladığı')} value={to} onChange={setTo} options={options.map((o) => ({ ...o, disabled: o.value === from }))} required />
            <SelectField
              id="dl-kind"
              label={tx('Bağ türü')}
              value={kind}
              onChange={(v) => setKind(v as DepartmentLinkKind)}
              options={[
                { value: 'Functional', label: tx('Fonksiyonel') },
                { value: 'Project', label: tx('Proje') },
              ]}
            />
            <TextField id="dl-note" label={tx('Not')} value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} placeholder={tx('İsteğe bağlı, ör. proje adı')} />
          </div>
          {error && (
            <p role="alert" className="text-[12.5px] text-destructive">
              {error}
            </p>
          )}
          <div className="flex justify-end">
            <Button onClick={submit} disabled={create.isPending}>
              {create.isPending && <LoaderCircle className="size-4 animate-spin" aria-hidden />}
              {tx('Bağ ekle')}
            </Button>
          </div>
        </section>
      </div>
    </Modal>
  )
}
