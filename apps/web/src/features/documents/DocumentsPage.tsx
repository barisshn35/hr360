import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle, Plus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Button } from '@/components/ui/button'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { InfoNote } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { documentTypeLabels, expenseApi, type DocumentType } from '@/api/expense'
import { useDocuments } from '@/api/queries'
import type { HrDocument } from '@/api/types'
import { formatDateTime } from '@/lib/format'
import { useEmployeeName } from '@/lib/useEmployeeName'
import { tx } from '@/lib/i18n'
import { docSignatureApi } from '@/api/docSignature'
import {
  DocumentSignaturesModal, MySignaturesPanel, SendForSignatureModal, SignatureStatusBadge, useSignatureStatus,
} from '@/features/documents/DocumentSignature'

function NewDocumentModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [employeeId, setEmployeeId] = useState('')
  const [type, setType] = useState<DocumentType | ''>('')
  const [name, setName] = useState('')
  const [storageKey, setStorageKey] = useState('')
  const [error, setError] = useState<string | undefined>()

  const mutation = useMutation({
    mutationFn: () =>
      expenseApi.createDocument({
        employeeId,
        type: type as DocumentType,
        fileName: name.trim(),
        storageKey: storageKey.trim() || undefined,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Doküman kaydedildi'))
      onClose()
      setType('')
      setName('')
      setStorageKey('')
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Doküman kaydedilemedi.')),
  })

  function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!employeeId) return setError(tx('Çalışan seçilmeli.'))
    if (!type) return setError(tx('Doküman türü zorunlu.'))
    if (name.trim().length < 3) return setError(tx('Doküman adı en az 3 karakter olmalı.'))
    setError(undefined)
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Yeni doküman kaydı')}
      note={tx('Bu kayıt dosyanın kendisini içermez; belgenin hangi çalışanda bulunduğunu not eder.')}
      size="lg"
      footer={
        <>
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
            form="new-doc"
            className="cursor-pointer"
            disabled={mutation.isPending}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      <form id="new-doc" onSubmit={submit} noValidate className="space-y-4">
        <EmployeePicker
          id="doc-employee"
          value={employeeId}
          onChange={setEmployeeId}
          hint={error?.includes('Çalışan') ? error : undefined}
        />
        <div className="grid gap-4 sm:grid-cols-2">
          <SelectField
            id="doc-type"
            label={tx('Tür')}
            required
            value={type}
            onChange={(v) => setType(v as DocumentType)}
            options={(Object.keys(documentTypeLabels) as DocumentType[]).map((t) => ({
              value: t,
              label: documentTypeLabels[t],
            }))}
            placeholder={tx('Tür seçin')}
            error={error?.includes('türü') ? error : undefined}
          />
          <TextField
            id="doc-name"
            label={tx('Doküman adı')}
            required
            value={name}
            onChange={(e) => setName(e.target.value)}
            error={error?.includes('adı') ? error : undefined}
          />
        </div>
        <TextField
          id="doc-key"
          label={tx('Depolama anahtarı')}
          hint={tx('İsteğe bağlı; dosya deposundaki karşılığı')}
          value={storageKey}
          onChange={(e) => setStorageKey(e.target.value)}
        />
      </form>
    </Modal>
  )
}

function DeleteConfirm({ doc, signed, onClose }: { doc: HrDocument | null; signed?: boolean; onClose: () => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()

  const mutation = useMutation({
    mutationFn: () => (signed ? docSignatureApi.deleteSigned(doc!.id) : expenseApi.deleteDocument(doc!.id)),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['expense'] })
      toast.ok(tx('Doküman kaydı silindi'))
      onClose()
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kayıt silinemedi.')),
  })

  if (!doc) return null

  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Doküman kaydını sil')}
      note={doc.fileName}
      footer={
        <>
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={onClose}
            disabled={mutation.isPending}
          >
            {tx('Vazgeç')}
          </Button>
          <Button
            variant="destructive"
            className="cursor-pointer"
            disabled={mutation.isPending}
            onClick={() => mutation.mutate()}
          >
            {mutation.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Sil')}
          </Button>
        </>
      }
    >
      <p className="text-[14px] leading-relaxed text-muted-foreground">
        {tx('Bu kayıt kalıcı olarak silinir ve geri alınamaz. Dosyanın kendisi varsa depoda kalmaya devam eder.')}
      </p>
      {signed && (
        <p className="mt-3 text-[13px] leading-relaxed text-destructive">
          {tx('Bu doküman imzalanmış. Silme, imza kanıtlarını da imha eder; yalnızca saklama süresi dolduysa silin.')}
        </p>
      )}
    </Modal>
  )
}

export function DocumentsPage() {
  const [employeeId, setEmployeeId] = useState('all')
  const [modalOpen, setModalOpen] = useState(false)
  const [toDelete, setToDelete] = useState<HrDocument | null>(null)
  const [toSign, setToSign] = useState<HrDocument | null>(null)
  const [history, setHistory] = useState<HrDocument | null>(null)
  const signatures = useSignatureStatus()
  const nameOf = useEmployeeName()

  const documents = useDocuments({ employeeId: employeeId && employeeId !== 'all' ? employeeId : undefined })

  const columns: Array<Column<HrDocument>> = [
    {
      id: 'name',
      header: tx('Doküman'),
      searchText: (d) => `${d.fileName} ${documentTypeLabels[d.type] ?? d.type} ${d.storageKey}`,
      sortValue: (d) => d.fileName,
      cell: (d) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-foreground">{d.fileName}</p>
          <p className="mt-0.5 truncate text-[12px] text-muted-foreground">
            {d.storageKey || tx('dosya bağlı değil')}
          </p>
        </div>
      ),
    },
    {
      id: 'type',
      header: tx('Tür'),
      hideBelow: 'sm',
      sortValue: (d) => documentTypeLabels[d.type] ?? d.type,
      exportText: (d) => documentTypeLabels[d.type] ?? d.type,
      cell: (d) => <StatusBadge tone="neutral">{documentTypeLabels[d.type] ?? d.type}</StatusBadge>,
    },
    {
      id: 'employee',
      header: tx('Çalışan'),
      hideBelow: 'md',
      searchText: (d) => nameOf(d.employeeId),
      sortValue: (d) => nameOf(d.employeeId),
      exportText: (d) => nameOf(d.employeeId),
      cell: (d) => <span className="text-muted-foreground">{nameOf(d.employeeId)}</span>,
    },
    {
      id: 'signature',
      header: tx('İmza'),
      hideBelow: 'sm',
      sortValue: (d) => signatures.data?.get(d.id)?.status ?? '',
      exportText: (d) => signatures.data?.get(d.id)?.status ?? '',
      cell: (d) => {
        const s = signatures.data?.get(d.id)
        return s ? <SignatureStatusBadge status={s.status} /> : <span className="text-muted-foreground">—</span>
      },
    },
    {
      id: 'createdAt',
      header: tx('Kayıt'),
      align: 'right',
      hideBelow: 'lg',
      sortValue: (d) => new Date(d.uploadedAt).getTime(),
      exportText: (d) => formatDateTime(d.uploadedAt),
      cell: (d) => (
        <span className="tabular text-muted-foreground">{formatDateTime(d.uploadedAt)}</span>
      ),
    },
  ]

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Dokümanlar')}
        description={tx('Çalışan dosyalarına bağlı doküman kayıtları. Bu sayfa yalnızca İK yönetimine açıktır.')}
        actions={
          <Button className="cursor-pointer" onClick={() => setModalOpen(true)}>
            <Plus className="size-4" />
            {tx('Yeni kayıt')}
          </Button>
        }
      />

      <MySignaturesPanel compact />

      <div className="max-w-sm">
        <EmployeePicker
          id="doc-filter-employee"
          value={employeeId}
          onChange={setEmployeeId}
          includeAllOption
        />
      </div>

      <DataTable
        rows={documents.data}
        rowKey={(d) => d.id}
        columns={columns}
        isLoading={documents.isPending}
        error={documents.error}
        onRetry={() => void documents.refetch()}
        searchPlaceholder={tx('Doküman adı, tür veya çalışan')}
        exportFileName="dokuman-kayitlari"
        pageSize={12}
        initialSort={{ columnId: 'createdAt', dir: 'desc' }}
        emptyTitle={tx('Doküman kaydı yok')}
        emptyDetail={tx('Bu filtreye uyan kayıt bulunmuyor.')}
        emptyAction={
          <Button size="sm" className="cursor-pointer" onClick={() => setModalOpen(true)}>
            {tx('Yeni kayıt')}
          </Button>
        }
        rowActions={[
          { label: tx('İmzaya gönder'), onSelect: (d) => setToSign(d), hidden: (d) => signatures.data?.get(d.id)?.status === 'Pending' },
          { label: tx('İmza geçmişi'), onSelect: (d) => setHistory(d), hidden: (d) => !signatures.data?.get(d.id) },
          { label: tx('Kaydı sil'), destructive: true, onSelect: (d) => setToDelete(d) },
        ]}
        notice={
          <InfoNote>
            {tx('Bu ekran dosyanın kendisini tutmaz — yalnızca hangi çalışanda hangi belgenin bulunduğunu kayda geçirir. Dosyanın aslını özlük dosyasında ya da belge kütüphanesinde saklayın.')}
          </InfoNote>
        }
      />

      <NewDocumentModal open={modalOpen} onClose={() => setModalOpen(false)} />
      <DeleteConfirm doc={toDelete} signed={!!(toDelete && signatures.data?.get(toDelete.id)?.signedAt)} onClose={() => setToDelete(null)} />
      {toSign && <SendForSignatureModal doc={toSign} onClose={() => setToSign(null)} />}
      {history && <DocumentSignaturesModal doc={history} onClose={() => setHistory(null)} />}
    </div>
  )
}
