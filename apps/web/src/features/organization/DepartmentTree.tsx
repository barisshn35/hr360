import { useMemo, useState, type ReactNode } from 'react'
import { ChevronRight, MoreHorizontal, Pencil, Trash2 } from 'lucide-react'
import { EmptyState } from '@/components/ui/States'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { cn } from '@/lib/utils'
import type { Department } from '@/api/types'
import { tx } from '@/lib/i18n'

interface TreeNode extends Department {
  children: TreeNode[]
}

/**
 * Düz listeyi parentDepartmentId üzerinden ağaca çevirir. Üstü listede
 * olmayan düğümler kök seviyede gösterilir — hiçbir kayıt kaybolmaz.
 */
export function buildTree(departments: Department[]): TreeNode[] {
  const byId = new Map<string, TreeNode>()
  for (const d of departments) byId.set(d.id, { ...d, children: [] })

  const roots: TreeNode[] = []
  for (const node of byId.values()) {
    const parent = node.parentDepartmentId ? byId.get(node.parentDepartmentId) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }

  const sortRec = (nodes: TreeNode[]) => {
    nodes.sort((a, b) => a.name.localeCompare(b.name, 'tr-TR'))
    nodes.forEach((n) => sortRec(n.children))
  }
  sortRec(roots)
  return roots
}

function countDescendants(node: TreeNode): number {
  return node.children.reduce((sum, c) => sum + 1 + countDescendants(c), 0)
}

function TreeItem({
  node,
  depth,
  selectedId,
  onSelect,
  onRename,
  onDelete,
}: {
  node: TreeNode
  depth: number
  selectedId?: string | null
  onSelect?: (department: Department) => void
  onRename?: (department: Department, name: string) => void
  onDelete?: (department: Department) => void
}) {
  const [expanded, setExpanded] = useState(depth < 2)
  const [renaming, setRenaming] = useState(false)
  const [draftName, setDraftName] = useState(node.name)
  const hasChildren = node.children.length > 0
  const isSelected = selectedId === node.id

  const commitRename = () => {
    const trimmed = draftName.trim()
    setRenaming(false)
    if (trimmed && trimmed !== node.name) onRename?.(node, trimmed)
    else setDraftName(node.name)
  }

  return (
    <li>
      <div
        className={cn(
          'group relative flex min-h-11 items-center gap-1 pr-3 transition-colors',
          isSelected ? 'bg-accent' : 'hover:bg-muted/50',
        )}
      >
        {/* Seçili departman kenarda bir işaret bırakır. */}
        {isSelected && (
          <span aria-hidden="true" className="absolute inset-y-0 left-0 w-[3px] bg-primary" />
        )}

        {hasChildren ? (
          <button
            type="button"
            onClick={() => setExpanded((e) => !e)}
            aria-expanded={expanded}
            aria-label={
              expanded
                ? tx('{0} alt departmanlarını gizle', [node.name])
                : tx('{0} alt departmanlarını göster', [node.name])
            }
            className="flex size-7 shrink-0 cursor-pointer items-center justify-center rounded text-muted-foreground transition-colors hover:text-foreground"
          >
            <ChevronRight
              aria-hidden="true"
              className={cn('size-3.5 transition-transform duration-200', expanded && 'rotate-90')}
            />
          </button>
        ) : (
          <span aria-hidden="true" className="size-7 shrink-0" />
        )}

        {renaming ? (
          <input
            autoFocus
            value={draftName}
            onChange={(e) => setDraftName(e.target.value)}
            onBlur={commitRename}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commitRename()
              if (e.key === 'Escape') {
                setDraftName(node.name)
                setRenaming(false)
              }
            }}
            className="min-w-0 flex-1 rounded border border-primary bg-background px-1.5 py-1 text-[14px] outline-none"
          />
        ) : (
          <button
            type="button"
            onClick={() => onSelect?.(node)}
            disabled={!onSelect}
            className={cn(
              'min-w-0 flex-1 truncate py-2 text-left text-[14px]',
              onSelect ? 'cursor-pointer' : 'cursor-default',
              isSelected ? 'font-semibold' : 'font-normal',
            )}
          >
            {node.name}
          </button>
        )}

        {hasChildren && (
          <span
            className="tabular shrink-0 text-[11px] text-muted-foreground"
            title={tx('{0} alt departman', [countDescendants(node)])}
          >
            {countDescendants(node)}
          </span>
        )}

        {(onRename || onDelete) && !renaming && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <button
                type="button"
                aria-label={tx('{0} için işlemler', [node.name])}
                className="flex size-7 shrink-0 cursor-pointer items-center justify-center rounded text-muted-foreground opacity-0 transition-opacity hover:text-foreground focus-visible:opacity-100 group-hover:opacity-100"
                onClick={(e) => e.stopPropagation()}
              >
                <MoreHorizontal aria-hidden="true" className="size-4" />
              </button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              {onRename && (
                <DropdownMenuItem
                  onSelect={() => {
                    setDraftName(node.name)
                    setRenaming(true)
                  }}
                >
                  <Pencil aria-hidden="true" className="mr-2 size-3.5" />
                  {tx('Adını değiştir')}
                </DropdownMenuItem>
              )}
              {onDelete && (
                <DropdownMenuItem
                  onSelect={() => onDelete(node)}
                  className="text-destructive focus:text-destructive"
                >
                  <Trash2 aria-hidden="true" className="mr-2 size-3.5" />
                  {tx('Sil')}
                </DropdownMenuItem>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>

      {hasChildren && expanded && (
        <ul className="ml-[13px] border-l border-border pl-2">
          {node.children.map((child) => (
            <TreeItem
              key={child.id}
              node={child}
              depth={depth + 1}
              selectedId={selectedId}
              onSelect={onSelect}
              onRename={onRename}
              onDelete={onDelete}
            />
          ))}
        </ul>
      )}
    </li>
  )
}

export function DepartmentTree({
  departments,
  selectedId,
  onSelect,
  onRename,
  onDelete,
  emptyAction,
}: {
  departments: Department[]
  selectedId?: string | null
  onSelect?: (department: Department) => void
  /** Ad değişikliği tehlikesizdir, onay istemeden doğrudan çağrılır. */
  onRename?: (department: Department, name: string) => void
  /**
   * Silme isteği. Backend içinde ekip/alt departman/atanmış çalışan
   * varsa 409 ile reddeder - o mesajı olduğu gibi göstermek için hata
   * fırlatması beklenir (rejected promise).
   */
  onDelete?: (department: Department) => Promise<void>
  emptyAction?: ReactNode
}) {
  const tree = useMemo(() => buildTree(departments), [departments])
  const [pendingDelete, setPendingDelete] = useState<Department | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)

  const requestDelete = (department: Department) => {
    setDeleteError(null)
    setPendingDelete(department)
  }

  const confirmDelete = async () => {
    if (!pendingDelete || !onDelete) return
    setDeleting(true)
    setDeleteError(null)
    try {
      await onDelete(pendingDelete)
      setPendingDelete(null)
    } catch (err) {
      // Backend'in 409 mesajı ("önce şu kayıtların taşınması gerekiyor...")
      // olduğu gibi gösterilir - dialog kapanmaz, kullanıcı okuyup kapatır.
      setDeleteError(err instanceof Error ? err.message : tx('Departman silinemedi.'))
    } finally {
      setDeleting(false)
    }
  }

  if (tree.length === 0) {
    return (
      <EmptyState
        title={tx('Departman yok')}
        detail={tx('Bu şirket için henüz departman tanımlanmamış.')}
        action={emptyAction}
      />
    )
  }

  return (
    <div>
      <ul aria-label={tx('Departman hiyerarşisi')} className="py-1">
        {tree.map((node) => (
          <TreeItem
            key={node.id}
            node={node}
            depth={0}
            selectedId={selectedId}
            onSelect={onSelect}
            onRename={onRename}
            onDelete={onDelete ? requestDelete : undefined}
          />
        ))}
      </ul>

      <AlertDialog
        open={pendingDelete !== null}
        onOpenChange={(open) => {
          if (!open) {
            setPendingDelete(null)
            setDeleteError(null)
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{tx('Departmanı sil')}</AlertDialogTitle>
            <AlertDialogDescription>
              <strong>{pendingDelete?.name}</strong>{' '}{tx('departmanını silmek üzeresiniz. Departmana bağlı pasif ekipler ve ekip üyelik geçmişleri de silinir; geçmiş görev atamaları kayıtlarda kalır. Bu işlem geri alınamaz.')}
            </AlertDialogDescription>
          </AlertDialogHeader>

          {deleteError && (
            <p role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-[13px] text-destructive">
              {deleteError}
            </p>
          )}

          <AlertDialogFooter>
            {/* role="alertdialog": odak "Vazgeç"te başlar. "Sil" kendisi kapatmaz (AlertDialogAction
                değil): 409 iletisi pencerede kalsın diye kapanmayı confirmDelete yönetir. */}
            <AlertDialogCancel
              className="cursor-pointer rounded-md border border-border px-3.5 py-2 text-[13px] font-medium transition-colors hover:bg-muted"
            >
              {tx('Vazgeç')}
            </AlertDialogCancel>
            <button
              type="button"
              onClick={confirmDelete}
              disabled={deleting}
              className="cursor-pointer rounded-md bg-destructive px-3.5 py-2 text-[13px] font-medium text-destructive-foreground transition-colors hover:opacity-90 disabled:opacity-50"
            >
              {deleting ? tx('Siliniyor…') : tx('Evet, sil')}
            </button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}
