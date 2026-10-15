import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Plane, Plus, Settings2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { useDirectory } from '@/api/directory'
import { expenseApi, expenseCategoryLabels, type CategoryLimit, type ExpenseCategory, type TravelRequest, type TravelStatus } from '@/api/expense'
import { formatDate, formatMoney } from '@/lib/format'
import { errMsg, isoDate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const statusView: Record<TravelStatus, { label: string; tone: 'neutral' | 'warning' | 'success' | 'danger' | 'info' }> = {
  Submitted: { label: tx('Onayda'), tone: 'warning' }, Approved: { label: tx('Onaylandı'), tone: 'success' },
  Rejected: { label: tx('Reddedildi'), tone: 'danger' }, Cancelled: { label: tx('İptal'), tone: 'neutral' }, Completed: { label: tx('Tamamlandı'), tone: 'info' },
}
const transportLabels: Record<string, string> = { Plane: tx('Uçak'), Bus: tx('Otobüs'), Train: tx('Tren'), Car: tx('Araç'), Other: tx('Diğer') }

function TravelModal({ onClose }: { onClose: () => void }) {
  const [f, setF] = useState({ destination: '', abroad: false, start: isoDate(), end: isoDate(), purpose: '', transport: 'Plane', acc: false, advance: '', passport: '' })
  const policy = useQuery({ queryKey: ['expense', 'policy'], queryFn: ({ signal }) => expenseApi.policy(signal) })
  const days = Math.max(0, Math.round((new Date(f.end).getTime() - new Date(f.start).getTime()) / 864e5) + 1)
  const rate = f.abroad ? policy.data?.perDiemAbroad ?? 0 : policy.data?.perDiemDomestic ?? 0
  const cur = f.abroad ? policy.data?.perDiemAbroadCurrency ?? 'EUR' : 'TRY'
  const save = useAction(() => expenseApi.createTravel({
    destination: f.destination, abroad: f.abroad, startDate: f.start, endDate: f.end, purpose: f.purpose, transport: f.transport,
    needsAccommodation: f.acc, advanceRequested: f.advance ? Number(f.advance) : null, passportNumber: f.abroad && f.passport ? f.passport : undefined,
  }), { success: tx('Seyahat talebi onaya gönderildi'), invalidate: [['travel']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Yeni seyahat talebi')} note={tx('Onaydan sonra harcırahı tek tıkla masraf beyanına dönüştürebilirsiniz.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !f.destination.trim() || !f.purpose.trim()}>{tx('Gönder')}</Button></>}>
      <div className="space-y-3">
        <div className="grid gap-3 md:grid-cols-[1fr_auto]">
          <TextField label={tx('Varış yeri')} value={f.destination} onChange={(e) => setF({ ...f, destination: e.target.value })} />
          <label className="flex h-9 items-end gap-2 text-[13px]"><Checkbox checked={f.abroad} onCheckedChange={(v) => setF({ ...f, abroad: v === true })} />{' '}{tx('Yurt dışı')}</label>
        </div>
        <div className="grid gap-3 md:grid-cols-3">
          <TextField label={tx('Gidiş')} type="date" value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} />
          <TextField label={tx('Dönüş')} type="date" value={f.end} onChange={(e) => setF({ ...f, end: e.target.value })} />
          <SelectField label={tx('Ulaşım')} value={f.transport} onChange={(v) => setF({ ...f, transport: v })} options={Object.entries(transportLabels).map(([value, label]) => ({ value, label }))} />
        </div>
        <TextAreaField label={tx('Amaç')} rows={2} value={f.purpose} onChange={(e) => setF({ ...f, purpose: e.target.value })} />
        <div className="grid gap-3 md:grid-cols-2">
          <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={f.acc} onCheckedChange={(v) => setF({ ...f, acc: v === true })} />{' '}{tx('Konaklama gerekli')}</label>
          <TextField label={tx('İş avansı (TL, isteğe bağlı)')} type="number" value={f.advance} onChange={(e) => setF({ ...f, advance: e.target.value })} />
        </div>
        {f.abroad && <TextField label={tx('Pasaport no (bilet için, isteğe bağlı)')} value={f.passport} onChange={(e) => setF({ ...f, passport: e.target.value })}
          hint={tx('Şifreli saklanır, yalnızca siz ve İK görebilir; seyahat bitiminden 7 gün sonra silinir.')} />}
        <InfoNote>{tx('Harcırah: {0} gün × {1} = {2}', [days, formatMoney(rate, cur), formatMoney(days * rate, cur)])}</InfoNote>
      </div>
    </Modal>
  )
}

function PolicyPanel() {
  const q = useQuery({ queryKey: ['expense', 'policy'], queryFn: ({ signal }) => expenseApi.policy(signal) })
  const [draft, setDraft] = useState<typeof q.data | null>(null)
  const p = draft ?? q.data
  const [fxf, setFxf] = useState({ currency: 'USD', date: isoDate(), rate: '' })
  const save = useAction(() => expenseApi.savePolicy({ limits: p!.limits, kmRate: p!.kmRate, perDiemDomestic: p!.perDiemDomestic, perDiemAbroad: p!.perDiemAbroad, perDiemAbroadCurrency: p!.perDiemAbroadCurrency }),
    { success: tx('Masraf politikası kaydedildi'), invalidate: [['expense', 'policy']], onDone: () => setDraft(null) })
  const saveFx = useAction(() => expenseApi.setFx({ currency: fxf.currency, date: fxf.date, rate: Number(fxf.rate) }), { success: tx('Kur kaydedildi') })
  if (q.isPending || !p) return <RowsSkeleton />
  const setLimit = (cat: string, k: keyof CategoryLimit, v: string) =>
    setDraft({ ...p, limits: { ...p.limits, [cat]: { ...(p.limits[cat] ?? { perItem: null, monthly: null, receiptAbove: null }), [k]: v === '' ? null : Number(v) } } })
  const cats = (Object.keys(expenseCategoryLabels) as ExpenseCategory[]).filter((c) => c !== 'PerDiem' && c !== 'Mileage')
  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={tx('Masraf politikası')} note={tx('Boş bırakılan limit uygulanmaz. Beyan onaya gönderilirken denetlenir.')} />
        <PanelBody className="space-y-3">
          <div className="grid gap-3 md:grid-cols-4">
            <TextField label={tx('Km ücreti (TL)')} type="number" value={p.kmRate} onChange={(e) => setDraft({ ...p, kmRate: Number(e.target.value) })} />
            <TextField label={tx('Yurt içi harcırah (TL/gün)')} type="number" value={p.perDiemDomestic} onChange={(e) => setDraft({ ...p, perDiemDomestic: Number(e.target.value) })} />
            <TextField label={tx('Yurt dışı harcırah (gün)')} type="number" value={p.perDiemAbroad} onChange={(e) => setDraft({ ...p, perDiemAbroad: Number(e.target.value) })} />
            <SelectField label={tx('Yurt dışı para birimi')} value={p.perDiemAbroadCurrency} onChange={(v) => setDraft({ ...p, perDiemAbroadCurrency: v })} options={['EUR', 'USD', 'GBP'].map((c) => ({ value: c, label: c }))} />
          </div>
          <table className="w-full text-[13px]">
            <thead><tr className="text-left text-[12px] text-muted-foreground"><th className="py-1">{tx('Kategori')}</th><th>{tx('Kalem limiti')}</th><th>{tx('Aylık limit')}</th><th>{tx('Fiş zorunlu (üzeri)')}</th></tr></thead>
            <tbody>
              {cats.map((c) => (
                <tr key={c}>
                  <td className="py-1 pr-2">{expenseCategoryLabels[c]}</td>
                  {(['perItem', 'monthly', 'receiptAbove'] as const).map((k) => (
                    <td key={k} className="pr-2"><input aria-label={`${expenseCategoryLabels[c]} ${k}`} type="number" min={0} className="h-8 w-full rounded-md border border-border bg-background px-2 text-right" value={p.limits[c]?.[k] ?? ''} onChange={(e) => setLimit(c, k, e.target.value)} /></td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
          <Button onClick={() => save.mutate(undefined)} disabled={!draft || save.isPending}>{tx('Kaydet')}</Button>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Elle döviz kuru')} note={tx('Kurlar TCMB döviz alış kurundan otomatik alınır; erişilemezse ya da şirket farklı kur kullanıyorsa buradan girin.')} />
        <PanelBody className="flex flex-wrap items-end gap-3">
          <div className="w-28"><SelectField label={tx('Para birimi')} value={fxf.currency} onChange={(v) => setFxf({ ...fxf, currency: v })} options={['USD', 'EUR', 'GBP', 'CHF'].map((c) => ({ value: c, label: c }))} /></div>
          <div className="w-44"><TextField label={tx('Tarih')} type="date" value={fxf.date} onChange={(e) => setFxf({ ...fxf, date: e.target.value })} /></div>
          <div className="w-36"><TextField label={tx('Kur (TL)')} type="number" value={fxf.rate} onChange={(e) => setFxf({ ...fxf, rate: e.target.value })} /></div>
          <Button variant="outline" onClick={() => saveFx.mutate(undefined)} disabled={!(Number(fxf.rate) > 0)}>{tx('Kaydet')}</Button>
        </PanelBody>
      </Panel>
    </div>
  )
}

export function TravelPage() {
  const { hasRole } = useAuth()
  const hr = hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')
  const q = useQuery({ queryKey: ['travel'], queryFn: ({ signal }) => expenseApi.travels(signal) })
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const [creating, setCreating] = useState(false)
  const [settings, setSettings] = useState(false)
  const toast = useToast()
  const inv = [['travel'], ['expense']]
  const cancel = useAction((id: string) => expenseApi.cancelTravel(id), { success: tx('Seyahat iptal edildi'), invalidate: inv })
  const confirm = useConfirm()
  const askCancel = async (t: TravelRequest) => {
    if (await confirm({
      title: tx('Seyahat talebi iptal edilsin mi?'),
      note: t.status === 'Submitted'
        ? tx('Bekleyen onay akışı da kapatılır. Pasaport bilgisi hemen silinir.')
        : tx('Onaylanmış seyahat iptal edilir. Pasaport bilgisi hemen silinir.'),
      action: tx('İptal et'),
      destructive: true,
    })) cancel.mutate(t.id)
  }
  const decide = useAction((a: { id: string; approve: boolean }) => expenseApi.decideTravel(a.id, a.approve), { success: tx('Karar kaydedildi'), invalidate: inv })
  const claim = useAction((id: string) => expenseApi.perDiemClaim(id), { success: (r) => tx('Harcırah beyanı taslağı oluşturuldu ({0})', [formatMoney(r.amount)]), invalidate: inv })
  const showPassport = async (id: string) => {
    try { const r = await expenseApi.passport(id); toast.ok(tx('Pasaport no: {0}', [r.passportNumber])) } catch (e) { toast.stop(errMsg(e)) }
  }
  return (
    <>
      <PageHeader title={tx('Seyahat')} description={tx('İş seyahati talebi, onay ve harcırah.')}
        actions={<div className="flex gap-2">{hr && <Button variant="outline" onClick={() => setSettings(!settings)}><Settings2 className="size-4" />{' '}{settings ? tx('Seyahatler') : tx('Politika')}</Button>}<Button onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Yeni seyahat')}</Button></div>} />
      {settings && hr ? <PolicyPanel /> : q.isPending ? <RowsSkeleton /> : !q.data?.length ? (
        <EmptyState icon={Plane} title={tx('Seyahat talebi yok')} detail={tx('İş seyahatinizi buradan talep edin; onaylanınca harcırahınız hesaplanır.')} />
      ) : (
        <Panel><PanelBody className="p-0"><ul className="divide-y divide-border">
          {q.data.map((t: TravelRequest) => (
            <li key={t.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
              <span className="min-w-0 flex-1"><span className="font-medium">{t.destination}</span>{hr && <span className="text-muted-foreground"> · {nameOf(t.employeeId)}</span>}
                <span className="block text-[12px] text-muted-foreground">{formatDate(t.startDate)} – {formatDate(t.endDate)} · {transportLabels[t.transport]} · {tx('harcırah {0}', [formatMoney(t.perDiemTotal, t.perDiemCurrency)])}{t.passportPurged ? ` · ${tx('pasaport bilgisi silindi')}` : ''}</span></span>
              {t.abroad && <StatusBadge tone="info">{tx('Yurt dışı')}</StatusBadge>}
              <StatusBadge tone={statusView[t.status].tone}>{statusView[t.status].label}</StatusBadge>
              {t.hasPassport && <Button size="sm" variant="ghost" onClick={() => void showPassport(t.id)}>{tx('Pasaport')}</Button>}
              {t.status === 'Approved' && <Button size="sm" variant="outline" onClick={() => claim.mutate(t.id)}>{tx('Harcırahı beyan et')}</Button>}
              {hr && t.status === 'Submitted' && !t.workflowRequestId && <><Button size="sm" onClick={() => decide.mutate({ id: t.id, approve: true })}>{tx('Onayla')}</Button><Button size="sm" variant="outline" onClick={() => decide.mutate({ id: t.id, approve: false })}>{tx('Reddet')}</Button></>}
              {(t.status === 'Submitted' || t.status === 'Approved') && <Button size="sm" variant="ghost" disabled={cancel.isPending} onClick={() => void askCancel(t)}>{tx('İptal')}</Button>}
            </li>
          ))}
        </ul></PanelBody></Panel>
      )}
      {creating && <TravelModal onClose={() => setCreating(false)} />}
    </>
  )
}
