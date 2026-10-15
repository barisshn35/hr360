/**
 * G7: puantaj — geç kalma, erken çıkış ve olası fazla mesai (giriş-çıkış hareketleri ile
 * vardiya ya da varsayılan mesai karşılaştırması). Bilgilendirme amaçlıdır; tespit edilen fazla
 * mesai yalnızca kullanıcı talep oluşturursa mevcut onay akışına girer.
 */
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlarmClock, Settings2 } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { Checkbox } from '@/components/ui/checkbox'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useMyEmployeeId } from '@/api/queries'
import { overtimeApi } from '@/api/timeclock'
import { opsApi, type AttendanceRow } from '@/api/opsPlus'
import { formatDate, parseDecimal } from '@/lib/format'
import { overtimeHoursError } from './OvertimePanel'
import { isoDate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const hm = (m?: number) => (!m ? '—' : m >= 60 ? `${Math.floor(m / 60)}s${m % 60 ? ` ${m % 60}dk` : ''}` : `${m}dk`)
/** Sunucu yerel saat (Europe/Istanbul) döndürür, saat dilimi eki yok: yalnızca saat kısmı gösterilir. */
const localClock = (s?: string | null) => (s ? s.slice(11, 16) : '—')

const statusView: Record<AttendanceRow['status'], { label: string; tone: 'neutral' | 'warning' | 'success' | 'danger' | 'info' }> = {
  Ok: { label: tx('Uygun'), tone: 'success' }, Late: { label: tx('Geç'), tone: 'warning' }, EarlyLeave: { label: tx('Erken çıkış'), tone: 'warning' },
  Absent: { label: tx('Kayıt yok'), tone: 'danger' }, MissingOut: { label: tx('Çıkış eksik'), tone: 'warning' }, Off: { label: tx('Tatil'), tone: 'neutral' },
  OffDayWork: { label: tx('Tatilde çalışma'), tone: 'info' }, Leave: { label: tx('İzinli'), tone: 'neutral' }, Holiday: { label: tx('Resmî tatil'), tone: 'neutral' },
  NotYet: { label: tx('Bugün'), tone: 'neutral' },
}

function OvertimeDraftModal({ row, self, onClose }: { row: AttendanceRow; self: boolean; onClose: () => void }) {
  const [hours, setHours] = useState(String(row.suggestedOvertimeHours ?? 0))
  const [reason, setReason] = useState(tx('Puantajdan tespit edilen fazla mesai ({0})', [hm(row.overtimeMinutes)]))
  const hoursErr = overtimeHoursError(hours)
  const create = useAction(() => overtimeApi.create({ employeeId: self ? undefined : row.employeeId, date: row.date, hours: parseDecimal(hours) ?? 0, reason: reason.trim() || undefined }),
    { success: self ? tx('Fazla mesai talebi onaya gönderildi') : tx('Fazla mesai kaydedildi'), invalidate: [['attendance'], ['overtime']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Fazla mesai talebi — {0}', [formatDate(row.date)])}
      note={self ? tx('Talep, mevcut fazla mesai onay akışına girer (yılda en çok 270 saat, günde en çok 4 saat).') : tx('Yönetici/İK olarak çalışan adına girilen fazla mesai doğrudan onaylı sayılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => create.mutate(undefined)} disabled={create.isPending || !!hoursErr}>{tx('Oluştur')}</Button></>}>
      <div className="space-y-3">
        <p className="text-[13px]">{row.name} · {tx('tespit: {0}', [hm(row.overtimeMinutes)])}</p>
        <TextField label={tx('Saat (0,5 adım)')} inputMode="decimal" value={hours} error={hoursErr} onChange={(e) => setHours(e.target.value)} />
        <TextAreaField label={tx('Gerekçe')} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
      </div>
    </Modal>
  )
}

type SettingsDraft = {
  grace: string; start: string; end: string; brk: string
  rest: string; weekly: string; daily: string; night: string; consec: string
  geoPolicy: 'Block' | 'Flag'; geoRaw: boolean; geoDays: string
}

const num = (v: string) => Number(v.replace(',', '.'))

/** Puantaj ayarları + çalışma süresi kuralları (madde 66) + konum doğrulama (madde 67). */
function SettingsModal({ onClose }: { onClose: () => void }) {
  const q = useQuery({ queryKey: ['attendance', 'settings'], queryFn: ({ signal }) => opsApi.timesheetSettings(signal) })
  const [d, setD] = useState<SettingsDraft | null>(null)
  const v: SettingsDraft | null = d ?? (q.data ? {
    grace: String(q.data.lateGraceMinutes), start: q.data.defaultStart.slice(0, 5), end: q.data.defaultEnd.slice(0, 5), brk: String(q.data.defaultBreakMinutes),
    rest: String(q.data.minRestHours ?? 11), weekly: String(q.data.weeklyMaxHours ?? 45), daily: String(q.data.dailyMaxHours ?? 11),
    night: String(q.data.nightMaxHours ?? 7.5), consec: String(q.data.maxConsecutiveDays ?? 6),
    geoPolicy: q.data.geoOutsidePolicy ?? 'Block', geoRaw: q.data.geoStoreRaw ?? false, geoDays: String(q.data.geoRawRetentionDays ?? 30),
  } : null)
  const bad = !v ? true : [num(v.rest) >= 8 && num(v.rest) <= 24, num(v.weekly) >= 1 && num(v.weekly) <= 72, num(v.daily) >= 1 && num(v.daily) <= 16,
    num(v.night) >= 1 && num(v.night) <= 16, Number.isInteger(num(v.consec)) && num(v.consec) >= 1 && num(v.consec) <= 14,
    Number.isInteger(num(v.geoDays)) && num(v.geoDays) >= 1 && num(v.geoDays) <= 90].some((ok) => !ok)
  const save = useAction(() => opsApi.saveTimesheetSettings({
    lateGraceMinutes: Number(v!.grace), defaultStart: `${v!.start}:00`, defaultEnd: `${v!.end}:00`, defaultBreakMinutes: Number(v!.brk),
    minRestHours: num(v!.rest), weeklyMaxHours: num(v!.weekly), dailyMaxHours: num(v!.daily), nightMaxHours: num(v!.night), maxConsecutiveDays: num(v!.consec),
    geoOutsidePolicy: v!.geoPolicy, geoStoreRaw: v!.geoRaw, geoRawRetentionDays: num(v!.geoDays),
  }), { success: tx('Puantaj ayarları kaydedildi'), invalidate: [['attendance'], ['timeshift', 'rules']], onDone: onClose })
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Puantaj ayarları')} note={tx('Vardiyası olmayan günlerde varsayılan mesai (hafta içi) kullanılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={!v || bad || save.isPending}>{tx('Kaydet')}</Button></>}>
      {!v ? <RowsSkeleton rows={2} /> : (
        <div className="space-y-5">
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Geç kalma toleransı (dk)')} type="number" min={0} max={60} value={v.grace} onChange={(e) => setD({ ...v, grace: e.target.value })} />
            <TextField label={tx('Mola (dk)')} type="number" min={0} max={180} value={v.brk} onChange={(e) => setD({ ...v, brk: e.target.value })} />
            <TextField label={tx('Mesai başlangıcı')} type="time" value={v.start} onChange={(e) => setD({ ...v, start: e.target.value })} />
            <TextField label={tx('Mesai bitişi')} type="time" value={v.end} onChange={(e) => setD({ ...v, end: e.target.value })} />
          </div>
          <div className="space-y-2 border-t border-border pt-4">
            <p className="text-[13px] font-medium">{tx('Çalışma süresi kuralları')}</p>
            <p className="text-[12px] text-muted-foreground">{tx('Varsayılanlar yasal değerlerdir (İş Kanunu m.63/m.69, Çalışma Süreleri Yönetmeliği). Sektörünüzde farklı bir düzenleme varsa değiştirin. Vardiya atamasında uyarı olarak gösterilir; takas onayında dinlenme ve haftalık süre ihlali takası reddeder.')}</p>
            <div className="grid gap-3 sm:grid-cols-3">
              <TextField label={tx('Vardiyalar arası dinlenme (saat)')} inputMode="decimal" value={v.rest} onChange={(e) => setD({ ...v, rest: e.target.value })} />
              <TextField label={tx('Haftalık en çok (saat)')} inputMode="decimal" value={v.weekly} onChange={(e) => setD({ ...v, weekly: e.target.value })} />
              <TextField label={tx('Günlük en çok (saat)')} inputMode="decimal" value={v.daily} onChange={(e) => setD({ ...v, daily: e.target.value })} />
              <TextField label={tx('Gece çalışması en çok (saat)')} inputMode="decimal" value={v.night} onChange={(e) => setD({ ...v, night: e.target.value })} />
              <TextField label={tx('Ardışık çalışma günü en çok')} inputMode="numeric" value={v.consec} onChange={(e) => setD({ ...v, consec: e.target.value })} />
            </div>
          </div>
          <div className="space-y-2 border-t border-border pt-4">
            <p className="text-[13px] font-medium">{tx('Konum doğrulamalı giriş-çıkış')}</p>
            <SelectField label={tx('Nokta dışından giriş-çıkış')} value={v.geoPolicy} onChange={(x) => setD({ ...v, geoPolicy: x as 'Block' | 'Flag' })}
              options={[{ value: 'Block', label: tx('Reddet') }, { value: 'Flag', label: tx('Kaydet, "noktada değil" olarak işaretle') }]} />
            <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={v.geoRaw} onCheckedChange={(x) => setD({ ...v, geoRaw: x === true })} /> {tx('Ham koordinatı da sakla (varsayılan kapalı)')}</label>
            {v.geoRaw && <TextField label={tx('Ham koordinat saklama süresi (gün, 1-90)')} inputMode="numeric" value={v.geoDays} onChange={(e) => setD({ ...v, geoDays: e.target.value })} />}
            <InfoNote>{tx('KVKK: varsayılan olarak yalnızca "noktada mı" ve uzaklık aralığı (ör. 50-100 m) saklanır. Ham koordinat saklamak için meşru amaç ve aydınlatma metni gerekir; süre dolunca otomatik silinir, kapatınca mevcut koordinatlar hemen silinir.')}</InfoNote>
          </div>
        </div>
      )}
    </Modal>
  )
}

export function AttendanceReportPanel() {
  const { roles } = useAuth()
  const hr = isHr(roles, 'ext-timeshift-manage')
  const { employeeId: me } = useMyEmployeeId()
  const [from, setFrom] = useState(isoDate(new Date(Date.now() - 6 * 864e5)))
  const [to, setTo] = useState(isoDate())
  const [draft, setDraft] = useState<AttendanceRow | null>(null)
  const [settings, setSettings] = useState(false)
  const q = useQuery({ queryKey: ['attendance', from, to], queryFn: ({ signal }) => opsApi.attendance({ from, to }, signal), retry: false })
  const r = q.data
  const rows = (r?.rows ?? []).filter((x) => !['Off', 'Leave', 'Holiday'].includes(x.status))
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><AlarmClock className="size-4 text-primary" />{' '}{tx('Geç kalma ve fazla mesai')}</span>}
        note={r ? (r.scope === 'self' ? tx('Yalnızca kendi kayıtlarınız') : r.scope === 'department' ? tx('Bölümünüzdeki çalışanlar') : tx('Tüm çalışanlar')) + ` · ${tx('tolerans {0} dk', [r.graceMinutes])}` : undefined}
        action={hr && <Button size="sm" variant="ghost" onClick={() => setSettings(true)}><Settings2 className="size-4" />{' '}{tx('Ayarlar')}</Button>} />
      <PanelBody className="space-y-4">
        <div className="flex flex-wrap items-end gap-3">
          <div className="w-40"><TextField label={tx('Başlangıç')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></div>
          <div className="w-40"><TextField label={tx('Bitiş')} type="date" value={to} onChange={(e) => setTo(e.target.value)} /></div>
          <Button variant="outline" size="sm" onClick={() => { setFrom(isoDate()); setTo(isoDate()) }}>{tx('Bugün')}</Button>
          <Button variant="outline" size="sm" onClick={() => { setFrom(isoDate(new Date(Date.now() - 6 * 864e5))); setTo(isoDate()) }}>{tx('Son 7 gün')}</Button>
        </div>
        <InfoNote>{tx('Bilgilendirme amaçlıdır; otomatik kesinti yapılmaz. Fazla mesai yalnızca talep ve onayla bordroya girer.')}</InfoNote>
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <p className="text-[13px] text-destructive">{(q.error as Error).message}</p> : r && (
          <>
            {r.totals.length > 1 && (
              <div className="overflow-x-auto">
                <table className="w-full text-[13px]">
                  <thead><tr className="text-left text-muted-foreground"><th className="py-1.5">{tx('Çalışan')}</th><th>{tx('Geç (gün)')}</th><th>{tx('Geç toplam')}</th><th>{tx('Erken çıkış')}</th><th>{tx('Kayıt yok')}</th><th>{tx('Olası fazla mesai')}</th></tr></thead>
                  <tbody>{r.totals.map((t) => (
                    <tr key={t.employeeId} className="border-t border-border"><td className="py-1.5">{t.name}<span className="block text-[11.5px] text-muted-foreground">{t.department ?? ''}</span></td>
                      <td className="tabular">{t.lateDays}</td><td className="tabular">{hm(t.lateMinutes)}</td><td className="tabular">{hm(t.earlyLeaveMinutes)}</td><td className="tabular">{t.absentDays}</td><td className="tabular">{hm(t.overtimeMinutes)}</td></tr>
                  ))}</tbody>
                </table>
              </div>
            )}
            {rows.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Bu aralıkta değerlendirilecek gün yok.')}</p> : (
              <div className="overflow-x-auto">
                <table className="w-full text-[13px]">
                  <thead><tr className="text-left text-muted-foreground">
                    {r.scope !== 'self' && <th className="py-1.5">{tx('Çalışan')}</th>}<th className="py-1.5">{tx('Gün')}</th><th>{tx('Plan')}</th><th>{tx('Giriş')}</th><th>{tx('Çıkış')}</th>
                    <th>{tx('Geç')}</th><th>{tx('Erken')}</th><th>{tx('Çalışılan')}</th><th>{tx('Olası FM')}</th><th>{tx('Durum')}</th><th />
                  </tr></thead>
                  <tbody>{rows.map((x) => (
                    <tr key={`${x.employeeId}-${x.date}`} className="border-t border-border">
                      {r.scope !== 'self' && <td className="py-1.5">{x.name}</td>}
                      <td className="tabular py-1.5">{formatDate(x.date)}</td>
                      <td className="tabular text-muted-foreground">{x.plannedStart ? `${localClock(x.plannedStart)}–${localClock(x.plannedEnd)}` : '—'}{x.source === 'Default' ? '*' : ''}</td>
                      <td className="tabular">{localClock(x.firstIn)}</td>
                      <td className="tabular">{localClock(x.lastOut)}</td>
                      <td className="tabular">{hm(x.lateMinutes)}</td><td className="tabular">{hm(x.earlyLeaveMinutes)}</td>
                      <td className="tabular">{hm(x.workedMinutes)}</td><td className="tabular">{hm(x.overtimeMinutes)}</td>
                      <td><StatusBadge tone={statusView[x.status].tone}>{statusView[x.status].label}</StatusBadge></td>
                      <td className="text-right">
                        {x.overtimeRequest ? <StatusBadge tone="info">{tx('FM talebi: {0}', [x.overtimeRequest.status])}</StatusBadge>
                          : (x.suggestedOvertimeHours ?? 0) > 0 && (x.employeeId === me || r.scope !== 'self') && (
                            <Button size="sm" variant="outline" onClick={() => setDraft(x)}>{tx('FM talebi oluştur')}</Button>
                          )}
                      </td>
                    </tr>
                  ))}</tbody>
                </table>
                <p className="mt-1 text-[11.5px] text-muted-foreground">{tx('* Vardiya atanmamış günlerde varsayılan mesai ({0}–{1}).', [r.defaultStart.slice(0, 5), r.defaultEnd.slice(0, 5)])}</p>
              </div>
            )}
          </>
        )}
      </PanelBody>
      {draft && <OvertimeDraftModal row={draft} self={draft.employeeId === me} onClose={() => setDraft(null)} />}
      {settings && <SettingsModal onClose={() => setSettings(false)} />}
    </Panel>
  )
}
