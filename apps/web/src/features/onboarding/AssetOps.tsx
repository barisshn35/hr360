/**
 * G16: zimmet QR etiketleri (yazdırma), QR okutma sayfası, iade tarihi ve bakım kayıtları.
 * Etiket yalnızca opak demirbaş kodu ve okutma adresini taşır — kişisel veri yoktur.
 */
import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import QRCode from 'qrcode'
import { AlertTriangle, CalendarClock, Printer, QrCode, ScanLine, Trash2, Wrench } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { assetStatusLabels, assetTypeLabels, type Asset, type AssetStatus } from '@/api/onboarding'
import { maintenanceTypeLabels, opsApi, type MaintenanceType } from '@/api/opsPlus'
import { formatDate, formatMoney } from '@/lib/format'
import { isoDate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

export const scanUrl = (code: string) => `${window.location.origin}/panel/zimmet/tara?kod=${encodeURIComponent(code)}`

/* ------------------------------------------------------------------ etiketler */

function escapeHtml(s: string) {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)
}

export function QrLabelsModal({ assets, onClose }: { assets: Asset[]; onClose: () => void }) {
  const withCode = assets.filter((a) => a.qrCode)
  const [svgs, setSvgs] = useState<Record<string, string>>({})
  useEffect(() => {
    let stop = false
    void Promise.all(withCode.map(async (a) => [a.id, await QRCode.toString(scanUrl(a.qrCode!), { type: 'svg', margin: 1, errorCorrectionLevel: 'M' })] as const))
      .then((list) => { if (!stop) setSvgs(Object.fromEntries(list)) })
    return () => { stop = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [withCode.map((a) => a.id).join(',')])

  const print = () => {
    const w = window.open('', '_blank', 'width=900,height=700')
    if (!w) return
    const cells = withCode.map((a) => `<div class="l"><div class="q">${svgs[a.id] ?? ''}</div><div class="t"><b>${escapeHtml(a.assetTag)}</b><br>${escapeHtml(assetTypeLabels[a.type])}<br><small>${escapeHtml(a.qrCode!)}</small></div></div>`).join('')
    w.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>${escapeHtml(tx('Zimmet etiketleri'))}</title><style>
      body{font-family:system-ui,sans-serif;margin:8mm} .g{display:grid;grid-template-columns:repeat(3,1fr);gap:4mm}
      .l{display:flex;gap:3mm;align-items:center;border:1px dashed #999;padding:3mm;break-inside:avoid} .q{width:26mm;height:26mm} .q svg{width:100%;height:100%}
      .t{font-size:10pt;line-height:1.3} small{color:#555;font-family:monospace}</style></head><body><div class="g">${cells}</div>
      <script>window.onload=function(){window.print()}</script></body></html>`)
    w.document.close()
  }

  return (
    <Modal open size="lg" onClose={onClose} title={tx('QR etiketleri')} note={tx('Etikette yalnızca demirbaş no ve opak kod bulunur; kişisel veri yoktur. Okutulduğunda zimmet sayfası açılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button><Button onClick={print} disabled={withCode.length === 0}><Printer className="size-4" />{' '}{tx('Yazdır')}</Button></>}>
      {withCode.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Seçili demirbaşlarda etiket kodu yok.')}</p> : (
        <div className="grid max-h-[60vh] grid-cols-2 gap-3 overflow-auto sm:grid-cols-3">
          {withCode.map((a) => (
            <div key={a.id} className="flex flex-col items-center gap-1 rounded-xl border border-border bg-white p-3 text-black">
              {/* qrcode kütüphanesinin ürettiği SVG; içerik yalnızca opak kod ve adres */}
              <div className="size-28" dangerouslySetInnerHTML={{ __html: svgs[a.id] ?? '' }} />
              <p className="text-[12.5px] font-semibold">{a.assetTag}</p>
              <p className="font-mono text-[10.5px] text-neutral-600">{a.qrCode}</p>
            </div>
          ))}
        </div>
      )}
    </Modal>
  )
}

/* ------------------------------------------------------------------ bakım */

export function MaintenanceModal({ asset, onClose }: { asset: Asset; onClose: () => void }) {
  const q = useQuery({ queryKey: ['onboarding', 'maintenance', asset.id], queryFn: ({ signal }) => opsApi.maintenance(asset.id, signal) })
  const [f, setF] = useState({ date: isoDate(), type: 'Periodic' as MaintenanceType, cost: '', vendor: '', notes: '', next: '' })
  const add = useAction(() => opsApi.addMaintenance(asset.id, { date: f.date, type: f.type, cost: f.cost ? Number(f.cost) : null, vendor: f.vendor || null, notes: f.notes || null, nextMaintenanceOn: f.next || null }),
    { success: tx('Bakım kaydedildi'), invalidate: [['onboarding', 'maintenance']], onDone: () => setF({ ...f, cost: '', vendor: '', notes: '', next: '' }) })
  const del = useAction((id: string) => opsApi.deleteMaintenance(asset.id, id), { success: tx('Kayıt silindi'), invalidate: [['onboarding', 'maintenance']] })
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Bakım kayıtları — {0}', [asset.assetTag])} note={`${assetTypeLabels[asset.type]}${asset.model ? `, ${asset.model}` : ''}`}>
      <div className="space-y-4">
        <div className="grid gap-3 rounded-xl border border-border p-3 md:grid-cols-3">
          <TextField label={tx('Tarih')} type="date" value={f.date} onChange={(e) => setF({ ...f, date: e.target.value })} />
          <SelectField label={tx('Tür')} value={f.type} onChange={(v) => setF({ ...f, type: v as MaintenanceType })}
            options={(Object.keys(maintenanceTypeLabels) as MaintenanceType[]).map((k) => ({ value: k, label: maintenanceTypeLabels[k] }))} />
          <TextField label={tx('Maliyet (TL)')} type="number" min={0} step="0.01" value={f.cost} onChange={(e) => setF({ ...f, cost: e.target.value })} />
          <TextField label={tx('Servis / tedarikçi')} value={f.vendor} onChange={(e) => setF({ ...f, vendor: e.target.value })} />
          <TextField label={tx('Sonraki bakım')} type="date" value={f.next} onChange={(e) => setF({ ...f, next: e.target.value })} />
          <TextAreaField label={tx('Not')} rows={1} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} />
          <div className="md:col-span-3"><Button size="sm" onClick={() => add.mutate(undefined)} disabled={add.isPending || !f.date}><Wrench className="size-4" />{' '}{tx('Bakım ekle')}</Button></div>
        </div>
        {q.isPending ? <RowsSkeleton rows={2} /> : !q.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Henüz bakım kaydı yok.')}</p> : (
          <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
            {q.data.map((m) => (
              <li key={m.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                <span className="min-w-0 flex-1">{formatDate(m.date)} · {maintenanceTypeLabels[m.type]}{m.vendor ? ` · ${m.vendor}` : ''}
                  <span className="block text-[12px] text-muted-foreground">{m.cost != null ? formatMoney(m.cost) : '—'}{m.nextMaintenanceOn ? ` · ${tx('sonraki: {0}', [formatDate(m.nextMaintenanceOn)])}` : ''}{m.notes ? ` · ${m.notes}` : ''}</span></span>
                <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => del.mutate(m.id)}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Modal>
  )
}

export function ExpectedReturnModal({ asset, onClose }: { asset: Asset; onClose: () => void }) {
  const [date, setDate] = useState(asset.expectedReturnOn ?? '')
  const save = useAction(() => opsApi.setExpectedReturn(asset.id, date || null), { success: tx('İade tarihi kaydedildi'), invalidate: [['onboarding']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Beklenen iade tarihi')} note={tx('Tarihten önce ve geçince zimmet sahibine ve İK sorumlusuna birer hatırlatma gider.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending}>{tx('Kaydet')}</Button></>}>
      <TextField label={tx('Tarih')} type="date" value={date} onChange={(e) => setDate(e.target.value)} hint={tx('Boş bırakılırsa hatırlatma yapılmaz.')} />
    </Modal>
  )
}

/* ------------------------------------------------------------------ İK/BT panelleri */

export function AssetDuePanels() {
  const returns = useQuery({ queryKey: ['onboarding', 'returns-due'], queryFn: ({ signal }) => opsApi.returnsDue(14, signal) })
  const maint = useQuery({ queryKey: ['onboarding', 'maintenance', 'due'], queryFn: ({ signal }) => opsApi.maintenanceDue(30, signal) })
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><CalendarClock className="size-4 text-primary" />{' '}{tx('İadesi yaklaşan / geciken')}</span>} note={tx('Önümüzdeki 14 gün')} />
        <PanelBody className="p-0">
          {returns.isPending ? <div className="p-4"><RowsSkeleton rows={2} /></div> : !returns.data?.length ? <p className="p-4 text-[13px] text-muted-foreground">{tx('Yaklaşan iade yok.')}</p> : (
            <ul className="divide-y divide-border text-[13px]">
              {returns.data.map((r) => (
                <li key={r.assignmentId} className="flex items-center gap-3 px-4 py-2.5">
                  <span className="min-w-0 flex-1"><b>{r.assetTag}</b> · {assetTypeLabels[r.type]}<span className="block text-[12px] text-muted-foreground">{r.holder ?? '—'} · {formatDate(r.expectedReturnOn)}</span></span>
                  {r.overdue ? <StatusBadge tone="danger">{tx('Gecikti')}</StatusBadge> : <StatusBadge tone="warning">{tx('Yaklaşıyor')}</StatusBadge>}
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><Wrench className="size-4 text-primary" />{' '}{tx('Bakımı gelenler')}</span>} note={tx('Önümüzdeki 30 gün')} />
        <PanelBody className="p-0">
          {maint.isPending ? <div className="p-4"><RowsSkeleton rows={2} /></div> : !maint.data?.length ? <p className="p-4 text-[13px] text-muted-foreground">{tx('Bakımı gelen demirbaş yok.')}</p> : (
            <ul className="divide-y divide-border text-[13px]">
              {maint.data.map((m) => (
                <li key={m.assetId} className="flex items-center gap-3 px-4 py-2.5">
                  <span className="min-w-0 flex-1"><b>{m.assetTag}</b> · {assetTypeLabels[m.type]}<span className="block text-[12px] text-muted-foreground">{tx('Son: {0} ({1})', [formatDate(m.lastDate), maintenanceTypeLabels[m.lastType]])}{m.vendor ? ` · ${m.vendor}` : ''}</span></span>
                  <StatusBadge tone={m.overdue ? 'danger' : 'warning'}>{formatDate(m.nextMaintenanceOn)}</StatusBadge>
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}

/* ------------------------------------------------------------------ okutma sayfası */

export function AssetScanPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const { can } = useAuth()
  const code = params.get('kod') ?? ''
  const [manual, setManual] = useState(code)
  const q = useQuery({ queryKey: ['onboarding', 'scan', code], queryFn: ({ signal }) => opsApi.scan(code, signal), enabled: code.length >= 8, retry: false })
  const r = q.data
  return (
    <div className="mx-auto max-w-2xl space-y-5">
      <PageHeader title={tx('Zimmet okut')} description={tx('Etiketteki QR kodu telefon kamerasıyla okutun ya da kodu yazın.')}
        actions={can('onboarding:view') && <Button variant="outline" asChild><Link to="/panel/zimmet">{tx('Zimmet listesi')}</Link></Button>} />
      <Panel>
        <PanelBody>
          <form className="flex items-end gap-2" onSubmit={(e) => { e.preventDefault(); navigate(`/panel/zimmet/tara?kod=${encodeURIComponent(manual.trim())}`) }}>
            <div className="flex-1"><TextField label={tx('Etiket kodu')} value={manual} onChange={(e) => setManual(e.target.value.toUpperCase())} className="font-mono" /></div>
            <Button type="submit" disabled={manual.trim().length < 8}><ScanLine className="size-4" />{' '}{tx('Göster')}</Button>
          </form>
        </PanelBody>
      </Panel>
      {!code ? null : q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? (
        <ErrorState title={tx('Demirbaş bulunamadı')} message={tx('Kod hatalı olabilir ya da bu demirbaşı görme yetkiniz yok (çalışanlar yalnızca kendi zimmetlerini görür).')} />
      ) : r && (
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><QrCode className="size-4 text-primary" />{' '}{r.asset.assetTag}</span>}
            note={`${assetTypeLabels[r.asset.type]}${r.asset.model ? `, ${r.asset.model}` : ''}`}
            action={<StatusBadge tone={r.asset.status === 'Assigned' ? 'info' : r.asset.status === 'Available' ? 'success' : 'warning'}>{assetStatusLabels[r.asset.status as AssetStatus] ?? r.asset.status}</StatusBadge>} />
          <PanelBody className="space-y-3 text-[13.5px]">
            {r.asset.serialNumber && <p>{tx('Seri no:')}{' '}<span className="tabular">{r.asset.serialNumber}</span></p>}
            {r.holder ? (
              <div className="rounded-xl border border-border p-3">
                <p className="font-medium">{r.holder.name ?? tx('Zimmetli çalışan')}{r.holder.department ? ` · ${r.holder.department}` : ''}</p>
                <p className="text-[12.5px] text-muted-foreground">{tx('Zimmet tarihi {0}', [formatDate(r.holder.assignedOn)])}{r.holder.expectedReturnOn ? ` · ${tx('beklenen iade {0}', [formatDate(r.holder.expectedReturnOn)])}` : ''}</p>
                {r.holder.overdue && <p className="mt-1 flex items-center gap-1.5 text-[12.5px] text-destructive"><AlertTriangle className="size-3.5" />{' '}{tx('İade tarihi geçti')}</p>}
              </div>
            ) : <p className="text-muted-foreground">{tx('Bu demirbaş şu an kimseye zimmetli değil.')}</p>}
            {r.lastMaintenance && <p className="text-[12.5px] text-muted-foreground">{tx('Son bakım {0} ({1})', [formatDate(r.lastMaintenance.date), maintenanceTypeLabels[r.lastMaintenance.type]])}{r.lastMaintenance.nextMaintenanceOn ? ` · ${tx('sonraki {0}', [formatDate(r.lastMaintenance.nextMaintenanceOn)])}` : ''}</p>}
            {r.canManage && r.holder && <InfoNote>{tx('Zimmetli kişinin görüntülenmesi erişim kaydına yazıldı.')}</InfoNote>}
          </PanelBody>
        </Panel>
      )}
      {!code && <EmptyState icon={ScanLine} title={tx('Kod bekleniyor')} detail={tx('Telefonunuzun kamerası etiketi okuttuğunda bu sayfa kodla açılır.')} />}
    </div>
  )
}
