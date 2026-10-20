import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Eye, ShieldAlert } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { SelectField, TextAreaField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { RowsSkeleton } from '@/components/ui/states'
import { governanceApi, type AccessLogRow, type AnalysisObjection } from '@/api/governance'
import { platformGovApi } from '@/api/platformGov'
import { formatDate, formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { appLocale, tx } from '@/lib/i18n'
import { auditActionLabel } from '@/features/governance/auditLabels'

/** Erişim kaydındaki teknik alan adlarının okunur karşılıkları (anahtar küçük harfle). */
const FIELD_LABELS: Record<string, string> = {
  iban: tx('IBAN'),
  nationalid: tx('T.C. kimlik no'),
  passport: tx('pasaport bilgisi'),
  contact: tx('iletişim bilgileri'),
  salary: tx('ücret bilgisi'),
  salarylist: tx('ücret listesi'),
  grosssalary: tx('brüt ücret'),
  salarydistribution: tx('ücret dağılımı'),
  salaryworksheet: tx('zam çalışma tablosu'),
  payrolllist: tx('bordro listesi'),
  payslip: tx('ücret pusulası'),
  payslipsummary: tx('ücret pusulası özeti'),
  bank: tx('banka ödeme dosyası'),
  sgkaphb: tx('SGK APHB bildirgesi'),
  profile: tx('özel profil bilgileri (adres, doğum tarihi, acil durum kişisi)'),
  attritionrisk: tx('işten ayrılma riski'),
  competencyheatmap: tx('ekip yetkinlik ısı haritası'),
  competencygaps: tx('yetkinlik açıkları'),
  courseprogress: tx('eğitim ilerlemesi'),
  courseresults: tx('eğitim sonuçları'),
  ninebox: tx('9 kutu değerlendirmesi'),
  disciplinarycase: tx('disiplin dosyası'),
  signatureevidence: tx('e-imza kanıtı'),
  document: tx('belge'),
  report: tx('rapor'),
  surveycomments: tx('anket yorumları'),
  assetholder: tx('zimmet sahibi bilgisi'),
  healthnotes: tx('sağlık notları'),
  employeerecord: tx('çalışan kaydı'),
  employeelist: tx('çalışan listesi'),
}

/**
 * Alan adını okunur hâle getirir; "custom:<anahtar>" → "Özel alan: <etiket>" (etiket bilinmiyorsa anahtar).
 * customLabels: özel alan anahtarı → ekrandaki etiket.
 */
function fieldLabel(f: string | null | undefined, customLabels?: ReadonlyMap<string, string>): string {
  if (!f) return ''
  const m = /^custom:(.+)$/i.exec(f)
  if (m) return tx('Özel alan: {0}', [customLabels?.get(m[1]) ?? m[1]])
  return FIELD_LABELS[f.toLowerCase()] ?? f
}

/** Kayıtlarda özel alan erişimi var mı (etiketleri yalnızca o zaman çekmek için). */
export const hasCustomFieldRows = (rows: AccessLogRow[] | undefined) => (rows ?? []).some((r) => /^custom:/i.test(r.field ?? ''))

/** Erişim kaydı satırının okunur açıklaması (çalışan ve İK ekranları ortak kullanır). */
export function accessLabel(r: AccessLogRow, customLabels?: ReadonlyMap<string, string>): string {
  const field = fieldLabel(r.field, customLabels)
  const text = (() => {
    switch (r.action) {
      case 'Revealed': return tx('{0} açıldı', [field])
      case 'SensitiveViewed': return tx('{0} görüntülendi', [field])
      case 'Exported': return r.field ? tx('{0} dışa aktarıldı', [field]) : tx('Kişisel veri dökümü indirildi')
      case 'AutomatedAnalysis': return tx('Otomatik analiz yapıldı: {0}', [field])
      // Diğer işlemler denetim ekranıyla aynı etiketle (ör. "Platform yöneticisi erişimi: çalışan kaydı").
      default: return field ? `${auditActionLabel(String(r.action))}: ${field}` : auditActionLabel(String(r.action))
    }
  })()
  return text.charAt(0).toLocaleUpperCase(appLocale) + text.slice(1)
}

export function MyAccessLog() {
  const q = useQuery({ queryKey: ['privacy', 'access-log', 'me'], queryFn: ({ signal }) => governanceApi.myAccessLog(signal) })
  // Özel alan erişimlerinde teknik anahtar yerine alan etiketi (Ek bilgiler paneliyle aynı sorgu).
  const custom = useQuery({
    queryKey: ['custom-fields', 'values', 'me'],
    queryFn: ({ signal }) => platformGovApi.myFieldValues(signal),
    enabled: hasCustomFieldRows(q.data),
    retry: false,
  })
  const labels = useMemo(() => new Map((custom.data?.fields ?? []).map((f) => [f.key, f.label])), [custom.data])
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Eye className="size-4" />{' '}{tx('Verilerime erişenler')}</span>}
        note={tx('Son bir yılda T.C. kimlik no, IBAN, ücret ve özel profil bilgilerinizi kimin, ne zaman görüntülediği.')} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-4"><RowsSkeleton rows={3} /></div>
          : !q.data?.length ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Kayıt yok.')}</p> : (
            <ul className="max-h-80 divide-y divide-border overflow-auto">
              {q.data.map((r, i) => (
                <li key={i} className="px-5 py-2.5 text-[13px]">
                  <div className="flex flex-wrap items-baseline justify-between gap-2">
                    <span>{accessLabel(r, labels)}</span>
                    <span className="text-[11.5px] text-muted-foreground">{formatDateTime(r.at)}</span>
                  </div>
                  <p className="text-[12px] text-muted-foreground">{tx('Erişen: {0}', [String(r.action) === 'PlatformAccess' ? tx('Platform yöneticisi (destek)') : r.viewer])}{r.reason ? tx(' · Gerekçe: {0}', [r.reason]) : ''}</p>
                </li>
              ))}
            </ul>
          )}
      </PanelBody>
    </Panel>
  )
}

function objectionTone(o: AnalysisObjection) {
  return o.status === 'Upheld' ? 'success' : o.status === 'Rejected' ? 'neutral' : o.overdue ? 'danger' : 'warning'
}

export function objectionStatusLabel(o: AnalysisObjection) {
  return o.status === 'Upheld' ? tx('Kabul edildi') : o.status === 'Rejected' ? tx('Reddedildi') : tx('Değerlendiriliyor')
}

export function MyObjections() {
  const analyses = useQuery({ queryKey: ['privacy', 'analyses'], queryFn: ({ signal }) => governanceApi.analyses(signal) })
  const list = useQuery({ queryKey: ['privacy', 'objections'], queryFn: ({ signal }) => governanceApi.objections(signal) })
  const [analysis, setAnalysis] = useState('AttritionRisk')
  const [reason, setReason] = useState('')
  const create = useAction(() => governanceApi.createObjection(analysis, reason), {
    success: tx('İtirazınız alındı. Değerlendirilene kadar bu analiz sizin için yapılmaz.'),
    invalidate: [['privacy']], onDone: () => setReason(''),
  })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><ShieldAlert className="size-4" />{' '}{tx('Otomatik analize itiraz')}</span>}
        note={tx('KVKK m.11/1-g: Yalnızca otomatik sistemlerle yapılan bir analizin aleyhinize sonuç doğurmasına itiraz edebilirsiniz. İtiraz değerlendirilene kadar analiz sizin için yapılmaz.')} />
      <PanelBody className="space-y-3">
        <SelectField label={tx('Analiz')} value={analysis} onChange={setAnalysis}
          options={(analyses.data ?? []).map((a) => ({ value: a.value, label: a.label }))} />
        <TextAreaField label={tx('Gerekçe (isteğe bağlı)')} rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />
        <Button onClick={() => create.mutate(undefined)} disabled={create.isPending}>{tx('İtiraz et')}</Button>
        {list.isPending ? <RowsSkeleton rows={2} /> : !list.data?.length ? null : (
          <ul className="mt-2 divide-y divide-border rounded-xl border border-border">
            {list.data.map((o) => (
              <li key={o.id} className="px-3.5 py-2.5 text-[13px]">
                <div className="flex items-center justify-between gap-2">
                  <span>{o.analysisLabel}</span>
                  <StatusBadge tone={objectionTone(o)}>{objectionStatusLabel(o)}</StatusBadge>
                </div>
                <p className="text-[12px] text-muted-foreground">{formatDate(o.createdAt)}{o.response ? ' · ' + tx('Yanıt: {0}', [o.response]) : ''}</p>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}
