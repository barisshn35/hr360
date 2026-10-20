import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, ArrowUpCircle, History, Upload } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { StatusBadge, type StatusTone } from '@/components/ui/status-badge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useConfirm } from '@/components/ui/confirm'
import {
  mlModelApi,
  type FairnessReport,
  type ModelFreshness,
  type ModelVersionRow,
  type ReliabilityBin,
} from '@/api/mlModel'
import { formatDate, formatDateTime, formatNumber, formatPercent } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { appLocale, tx, txServer } from '@/lib/i18n'
import { FAIRNESS_CSV_HEADER, freshnessState, nearestRow, polyline, reliabilityPoints } from './modelQuality'

/**
 * Model kartının kalite bölümleri (ML dalgası 1): güncellik rozeti, kalibrasyon + inceleme eşiği,
 * champion/aday sürümler (insan onayıyla yayına alma, geri alma) ve adillik denetimi.
 * Skor ve eşik KARAR değildir: eşik üstü "insan incelemesi önerilir" anlamına gelir.
 */

const dec3 = (n: number | null | undefined) =>
  typeof n === 'number' ? new Intl.NumberFormat(appLocale, { minimumFractionDigits: 3, maximumFractionDigits: 3 }).format(n) : '—'
const pct = (n: number | null | undefined, digits = 1) => (typeof n === 'number' ? formatPercent(n, digits) : '—')

/* ------------------------------------------------------------------ güncellik */

export function FreshnessBadge({ freshness }: { freshness?: ModelFreshness }) {
  const state = freshnessState(freshness)
  if (state === 'unknown' || !freshness) return null
  const view: Record<Exclude<typeof state, 'unknown'>, { tone: StatusTone; label: string }> = {
    fresh: { tone: 'success', label: tx('Güncel') },
    expiring: { tone: 'warning', label: tx('Geçerliliği yakında doluyor') },
    stale: { tone: 'danger', label: tx('Geçerliliği doldu: yeniden eğitin') },
  }
  const v = view[state]
  return (
    <StatusBadge tone={v.tone}>
      {state !== 'fresh' && <AlertTriangle className="size-3.5" />}
      {v.label}
    </StatusBadge>
  )
}

/* ------------------------------------------------------------------ kalibrasyon + eşik */

function ReliabilityChart({ bins }: { bins: ReliabilityBin[] }) {
  const size = 220
  const pts = reliabilityPoints(bins, size)
  return (
    <figure className="space-y-2">
      <svg
        viewBox={`-28 -8 ${size + 40} ${size + 40}`}
        className="h-auto w-full max-w-[280px] text-foreground"
        role="img"
        aria-label={tx('Güvenilirlik eğrisi: tahmin edilen olasılık ile gözlenen ayrılma oranı')}
      >
        <rect x="0" y="0" width={size} height={size} className="fill-muted/40 stroke-border" />
        {[0.25, 0.5, 0.75].map((g) => (
          <g key={g} className="stroke-border">
            <line x1={g * size} y1="0" x2={g * size} y2={size} strokeDasharray="2 3" />
            <line x1="0" y1={g * size} x2={size} y2={g * size} strokeDasharray="2 3" />
          </g>
        ))}
        <line x1="0" y1={size} x2={size} y2="0" className="stroke-muted-foreground" strokeDasharray="4 4" />
        {pts.length > 1 && <polyline points={polyline(pts)} fill="none" className="stroke-primary" strokeWidth="2" />}
        {pts.map((p) => (
          <circle key={p.bin.lo} cx={p.x} cy={p.y} r="3.5" className="fill-primary">
            <title>{tx('{0}–{1}: tahmin {2}, gözlenen {3}, {4} kayıt', [p.bin.lo, p.bin.hi, dec3(p.bin.mean_predicted), dec3(p.bin.observed_rate), p.bin.count])}</title>
          </circle>
        ))}
        <text x={size / 2} y={size + 24} textAnchor="middle" className="fill-muted-foreground text-[10px]">{tx('Tahmin edilen olasılık')}</text>
        <text x="-16" y={size / 2} textAnchor="middle" transform={`rotate(-90 -16 ${size / 2})`} className="fill-muted-foreground text-[10px]">
          {tx('Gözlenen oran')}
        </text>
      </svg>
      <figcaption className="text-[12px] text-muted-foreground">
        {tx('Kesikli çizgi mükemmel kalibrasyondur. 5\'ten az kayıtlı aralıklar gösterilmez.')}
      </figcaption>
    </figure>
  )
}

export function CalibrationPanel({ canEdit, noTenant }: { canEdit: boolean; noTenant: boolean }) {
  const report = useQuery({ queryKey: ['ml-model', 'calibration'], queryFn: ({ signal }) => mlModelApi.calibration(signal) })
  const settings = useQuery({
    queryKey: ['ml-model', 'settings'],
    queryFn: ({ signal }) => mlModelApi.settings(signal),
    enabled: !noTenant,
  })
  const [selected, setSelected] = useState<number | null>(null)
  useEffect(() => {
    if (settings.data && selected === null) setSelected(settings.data.riskThreshold)
  }, [settings.data, selected])
  const save = useAction((t: number) => mlModelApi.saveThreshold(t), {
    success: (r) => tx('İnceleme eşiği kaydedildi: {0}', [dec3(r.riskThreshold)]),
    invalidate: [['ml-model', 'settings']],
  })

  const r = report.data
  const rows = r?.thresholds ?? []
  const current = settings.data ? nearestRow(rows, settings.data.riskThreshold) : undefined
  const chosen = selected !== null ? nearestRow(rows, selected) : undefined
  const methodLabel = (m?: string) =>
    m === 'isotonic' ? tx('İzotonik') : m === 'sigmoid' ? tx('Sigmoid (Platt)') : tx('Kalibrasyonsuz (ham olasılık)')

  return (
    <Panel>
      <PanelHead
        title={tx('Kalibrasyon ve inceleme eşiği')}
        note={tx('Skorun gerçek ayrılma oranıyla uyumu ve şirketinizin hangi skordan itibaren insan incelemesi önereceği.')}
      />
      <PanelBody className="space-y-4">
        {report.isPending ? (
          <RowsSkeleton rows={4} columns={3} />
        ) : report.isError ? (
          <ErrorState message={errMsg(report.error)} onRetry={() => void report.refetch()} />
        ) : !r?.available ? (
          <EmptyState title={tx('Kalibrasyon bilgisi yok')} detail={txServer(r?.note ?? '')} />
        ) : (
          <>
            <div className="grid gap-5 md:grid-cols-[minmax(0,280px)_1fr]">
              <ReliabilityChart bins={r.reliability ?? []} />
              <div className="space-y-1 text-[13px]">
                <div className="flex justify-between border-b border-border py-1.5"><span className="text-muted-foreground">{tx('Yöntem')}</span><span>{methodLabel(r.method)}</span></div>
                {r.method === 'none' || !r.method ? (
                  <>
                    <div className="flex justify-between border-b border-border py-1.5"><span className="text-muted-foreground">{tx('Brier (ham olasılık)')}</span><span className="tabular">{dec3(r.brier_raw ?? r.brier)}</span></div>
                    <div className="flex justify-between border-b border-border py-1.5"><span className="text-muted-foreground">{tx('ECE (ham olasılık)')}</span><span className="tabular">{dec3(r.ece_raw ?? r.ece)}</span></div>
                  </>
                ) : (
                  <>
                    <div className="flex justify-between border-b border-border py-1.5"><span className="text-muted-foreground">{tx('Brier (kalibre / ham)')}</span><span className="tabular">{dec3(r.brier)} / {dec3(r.brier_raw)}</span></div>
                    <div className="flex justify-between border-b border-border py-1.5"><span className="text-muted-foreground">{tx('ECE (kalibre / ham)')}</span><span className="tabular">{dec3(r.ece)} / {dec3(r.ece_raw)}</span></div>
                  </>
                )}
                <div className="flex justify-between border-b border-border py-1.5"><span className="text-muted-foreground">{tx('Değerlendirme')}</span><span className="tabular">{formatNumber(r.evaluation_rows)} {tx('satır')}</span></div>
                <div className="flex justify-between py-1.5"><span className="text-muted-foreground">{tx('Bilgi amaçlı öneri (en yüksek F1)')}</span><span className="tabular">{dec3(r.recommended_threshold)}</span></div>
                <p className="pt-2 text-[12px] leading-relaxed text-muted-foreground">
                  {tx('Brier ve ECE düştükçe skor gerçek orana yaklaşır. Yöntem, ayrı bir kalibrasyon kümesinde Brier skoruna göre seçilir.')}
                </p>
              </div>
            </div>

            {noTenant ? (
              <InfoNote>{tx('İnceleme eşiği şirket bazında seçilir; şirket İK yöneticisi bu ekranda belirler.')}</InfoNote>
            ) : (
              <div className="space-y-3">
                <InfoNote>
                  {tx('Eşik bir karar değildir: üstündeki skorlar yalnızca "insan incelemesi önerilir" olarak işaretlenir. Düşük eşik daha çok kişiyi (daha çok yanlış alarmla) işaretler; yüksek eşik daha azını işaretler ama bazı ayrılmaları kaçırır.')}
                </InfoNote>
                <div className="overflow-x-auto">
                  <table className="w-full text-[13px]">
                    <caption className="sr-only">{tx('Eşik seçimi')}</caption>
                    <thead>
                      <tr className="text-left text-[11px] tracking-[0.08em] text-muted-foreground uppercase">
                        <th className="py-2 pr-3 font-medium">{tx('Eşik')}</th>
                        <th className="py-2 pr-3 text-right font-medium">{tx('İşaretlenme oranı')}</th>
                        <th className="py-2 pr-3 text-right font-medium">{tx('Kesinlik')}</th>
                        <th className="py-2 pr-3 text-right font-medium">{tx('Duyarlılık')}</th>
                        <th className="py-2 text-right font-medium">{tx('Yanlış alarm oranı')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((row) => {
                        const isChosen = chosen?.threshold === row.threshold
                        return (
                          <tr key={row.threshold} className={`border-t border-border ${isChosen ? 'bg-muted/60' : ''}`}>
                            <td className="py-1.5 pr-3">
                              <label className="flex cursor-pointer items-center gap-2">
                                <input
                                  type="radio"
                                  name="risk-threshold"
                                  className="accent-[hsl(var(--primary))]"
                                  checked={isChosen}
                                  disabled={!canEdit}
                                  onChange={() => setSelected(row.threshold)}
                                />
                                <span className="tabular">{dec3(row.threshold)}</span>
                                {current?.threshold === row.threshold && <StatusBadge tone="info">{tx('Kayıtlı')}</StatusBadge>}
                              </label>
                            </td>
                            <td className="tabular py-1.5 pr-3 text-right">{pct(row.flag_rate)}</td>
                            <td className="tabular py-1.5 pr-3 text-right">{pct(row.precision)}</td>
                            <td className="tabular py-1.5 pr-3 text-right">{pct(row.recall)}</td>
                            <td className="tabular py-1.5 text-right">{pct(row.false_positive_rate)}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
                <div className="flex flex-wrap items-center justify-between gap-3 text-[12.5px] text-muted-foreground">
                  <span>
                    {settings.data?.isDefault
                      ? tx('Şirket henüz eşik seçmedi; varsayılan {0} kullanılıyor.', [dec3(settings.data.riskThreshold)])
                      : settings.data && tx('Kayıtlı eşik {0} · {1}, {2}', [dec3(settings.data.riskThreshold), settings.data.updatedBy ?? '—', formatDateTime(settings.data.updatedAt)])}
                  </span>
                  {canEdit && (
                    <Button
                      onClick={() => chosen && save.mutate(chosen.threshold)}
                      disabled={!chosen || save.isPending || chosen.threshold === current?.threshold}
                    >
                      {tx('Eşiği kaydet')}
                    </Button>
                  )}
                </div>
              </div>
            )}
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ champion / aday */

const statusView: Record<ModelVersionRow['status'], { tone: StatusTone; label: string }> = {
  champion: { tone: 'success', label: tx('Yayında (champion)') },
  challenger: { tone: 'warning', label: tx('Aday: onay bekliyor') },
  rejected: { tone: 'neutral', label: tx('Yayımlanmadı') },
  retired: { tone: 'info', label: tx('Eski yayın sürümü') },
}

export function VersionsPanel({ canPromote }: { canPromote: boolean }) {
  const confirm = useConfirm()
  const versions = useQuery({ queryKey: ['ml-model', 'versions'], queryFn: ({ signal }) => mlModelApi.versions(signal) })
  const promote = useAction((v: string) => mlModelApi.promote(v), {
    success: (r) => tx('v{0} yayına alındı (önceki v{1}).', [r.serving_version, r.previous_version ?? '—']),
    invalidate: [['ml-model']],
  })
  const rollback = useAction((v: string) => mlModelApi.rollback(v), {
    success: (r) => tx('v{0} sürümüne geri dönüldü.', [r.serving_version]),
    invalidate: [['ml-model']],
  })
  const champion = versions.data?.versions.find((v) => v.serving)

  async function approve(v: ModelVersionRow) {
    const ok = await confirm({
      title: tx('v{0} yayına alınsın mı?', [v.version]),
      note: tx('Aday, yayındaki v{0} ile aynı değerlendirme kümesinde karşılaştırıldı (AUC {1} / {2}, Brier {3} / {4}). Model tüm şirketlerce paylaşılır; onayınız denetim kaydına yazılır.', [
        champion?.version ?? '—', dec3(v.metrics.auc), dec3(champion?.metrics.auc), dec3(v.metrics.brier), dec3(champion?.metrics.brier),
      ]),
      action: tx('Yayına al'),
      destructive: false,
    })
    if (ok) promote.mutate(v.version)
  }

  async function revert(v: ModelVersionRow) {
    const ok = await confirm({
      title: tx('v{0} sürümüne geri dönülsün mü?', [v.version]),
      note: tx('Yayındaki sürüm hemen değişir; işlem denetim kaydına yazılır.'),
      action: tx('Geri dön'),
    })
    if (ok) rollback.mutate(v.version)
  }

  return (
    <Panel>
      <PanelHead
        title={tx('Sürümler: yayındaki model ve adaylar')}
        note={versions.data?.approval_required
          ? tx('Yeni eğitilen aday, yayındaki modelden kötü değilse bile kendiliğinden yayına girmez; platform yöneticisinin onayını bekler.')
          : tx('Onay şartı kapalı: karşılaştırmayı geçen aday doğrudan yayına girer.')}
      />
      <PanelBody>
        {versions.isPending ? (
          <RowsSkeleton rows={3} columns={5} />
        ) : versions.isError ? (
          <ErrorState message={errMsg(versions.error)} onRetry={() => void versions.refetch()} />
        ) : !versions.data?.versions.length ? (
          <EmptyState title={tx('Kayıtlı sürüm yok')} />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-[13px]">
              <thead>
                <tr className="text-left text-[11px] tracking-[0.08em] text-muted-foreground uppercase">
                  <th className="py-2 pr-3 font-medium">{tx('Sürüm')}</th>
                  <th className="py-2 pr-3 font-medium">{tx('Durum')}</th>
                  <th className="py-2 pr-3 font-medium">{tx('Eğitim')}</th>
                  <th className="py-2 pr-3 text-right font-medium">AUC</th>
                  <th className="py-2 pr-3 text-right font-medium">Brier</th>
                  <th className="py-2 pr-3 font-medium">{tx('Karşılaştırma')}</th>
                  {canPromote && <th className="py-2 font-medium"><span className="sr-only">{tx('İşlem')}</span></th>}
                </tr>
              </thead>
              <tbody>
                {versions.data.versions.map((v) => (
                  <tr key={v.version} className="border-t border-border align-top">
                    <td className="tabular py-2 pr-3 font-medium">v{v.version}</td>
                    <td className="py-2 pr-3"><StatusBadge tone={statusView[v.status]?.tone ?? 'neutral'}>{statusView[v.status]?.label ?? v.status}</StatusBadge></td>
                    <td className="py-2 pr-3 text-muted-foreground">{formatDate(v.trained_at)}</td>
                    <td className="tabular py-2 pr-3 text-right">{dec3(v.metrics.auc)}</td>
                    <td className="tabular py-2 pr-3 text-right">{dec3(v.metrics.brier)}</td>
                    <td className="max-w-[320px] py-2 pr-3 text-[12px] text-muted-foreground">
                      {v.compared_with_version ? tx('v{0} ile aynı kümede', [v.compared_with_version]) : '—'}
                      {v.status === 'challenger' && !v.comparison_current && (
                        <span className="block text-[hsl(var(--warning))]">{tx('Yayındaki sürüm değişti; yeniden eğitip karşılaştırın.')}</span>
                      )}
                      {v.reason && <span className="block">{versionReason(v.reason)}</span>}
                    </td>
                    {canPromote && (
                      <td className="py-2 text-right whitespace-nowrap">
                        {v.status === 'challenger' && v.comparison_current && (
                          <Button size="sm" onClick={() => void approve(v)} disabled={promote.isPending}>
                            <ArrowUpCircle />{tx('Yayına al')}
                          </Button>
                        )}
                        {v.was_champion && !v.serving && (
                          <Button size="sm" variant="outline" onClick={() => void revert(v)} disabled={rollback.isPending}>
                            <History />{tx('Geri dön')}
                          </Button>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ adillik denetimi */

function FairnessTable({ report }: { report: FairnessReport }) {
  return (
    <div className="space-y-4">
      <p className="text-[12.5px] text-muted-foreground">
        {tx('{0} çalışan · eşik {1} · model v{2} · {3}', [formatNumber(report.rows), dec3(report.threshold), report.model_version ?? '—', formatDateTime(report.computed_at)])}
      </p>
      {report.warnings.length > 0 && (
        <InfoNote>
          {tx('Dört-beşte bir kuralı: {0} grupta işaretlenme oranı en yüksek grubun %80\'inin altında. Bu bir karar değildir; farkın nedeni insan incelemesiyle değerlendirilmelidir.', [report.warnings.length])}
        </InfoNote>
      )}
      {report.attributes.map((a) => (
        <div key={a.attribute} className="space-y-1.5">
          <h3 className="text-[13px] font-semibold">{txServer(a.label)}</h3>
          {a.groups.length === 0 ? (
            <p className="text-[12.5px] text-muted-foreground">{tx('Gösterilecek grup yok (her grup 5 kişiden az).')}</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-[13px]">
                <thead>
                  <tr className="text-left text-[11px] tracking-[0.08em] text-muted-foreground uppercase">
                    <th className="py-1.5 pr-3 font-medium">{tx('Grup')}</th>
                    <th className="py-1.5 pr-3 text-right font-medium">{tx('Kişi')}</th>
                    <th className="py-1.5 pr-3 text-right font-medium">{tx('İşaretlenme')}</th>
                    {report.labels_used && <th className="py-1.5 pr-3 text-right font-medium">TPR</th>}
                    {report.labels_used && <th className="py-1.5 pr-3 text-right font-medium">FPR</th>}
                    <th className="py-1.5 text-right font-medium">{tx('Oran')}</th>
                  </tr>
                </thead>
                <tbody>
                  {a.groups.map((g) => (
                    <tr key={g.group} className="border-t border-border">
                      <td className="py-1.5 pr-3">{g.group}</td>
                      <td className="tabular py-1.5 pr-3 text-right">{formatNumber(g.n)}</td>
                      <td className="tabular py-1.5 pr-3 text-right">{pct(g.flag_rate)}</td>
                      {report.labels_used && <td className="tabular py-1.5 pr-3 text-right">{pct(g.tpr)}</td>}
                      {report.labels_used && <td className="tabular py-1.5 pr-3 text-right">{pct(g.fpr)}</td>}
                      <td className="py-1.5 text-right">
                        {g.four_fifths_warning
                          ? <StatusBadge tone="warning">{dec3(g.disparity_ratio)}</StatusBadge>
                          : <span className="tabular">{dec3(g.disparity_ratio)}</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {a.hidden_groups > 0 && (
            <p className="text-[12px] text-muted-foreground">{tx('{0} grup 5 kişiden az olduğu için gösterilmedi.', [a.hidden_groups])}</p>
          )}
        </div>
      ))}
      {report.absent_attributes.length > 0 && (
        <p className="text-[12px] text-muted-foreground">
          {tx('Raporlanmayan gruplar: {0}. Bu bilgiler veri modelinde tutulmaz (KVKK veri en aza indirme) ve modele hiçbir zaman girmez.', [report.absent_attributes.map((x) => txServer(x.label)).join(', ')])}
        </p>
      )}
      <p className="text-[12px] leading-relaxed text-muted-foreground">{txServer(report.note)}</p>
    </div>
  )
}

export function FairnessPanel({ noTenant }: { noTenant: boolean }) {
  const latest = useQuery({
    queryKey: ['ml-model', 'fairness'],
    queryFn: ({ signal }) => mlModelApi.latestFairness(signal),
    enabled: !noTenant,
  })
  const [csv, setCsv] = useState<string | null>(null)
  const [fileName, setFileName] = useState('')
  const run = useAction((text: string) => mlModelApi.runFairness(text), {
    success: tx('Adillik denetimi tamamlandı.'),
    invalidate: [['ml-model', 'fairness']],
    onDone: () => setCsv(null),
  })
  const report = useMemo(() => (latest.data?.available ? latest.data : null), [latest.data])

  return (
    <Panel>
      <PanelHead
        title={tx('Adillik denetimi')}
        note={tx('Gruplar arası işaretlenme oranı ve (etiket varsa) hata oranları. Grup bilgisi yalnızca denetim içindir, modele girmez.')}
      />
      <PanelBody className="space-y-4">
        {noTenant ? (
          <EmptyState title={tx('Adillik denetimi şirket bazında yapılır')} />
        ) : (
          <>
            <div className="space-y-2 text-[12.5px] text-muted-foreground">
              <p>{tx('Dönemsel toplu bir CSV yükleyin; çalışan kimliği yalnızca departman ve kıdem bandını eşlemek için kullanılır, model servisine gönderilmez. label: 1 = ayrıldı (isteğe bağlı).')}</p>
              <code className="block overflow-x-auto rounded-lg bg-muted px-3 py-2 text-[11.5px]">{FAIRNESS_CSV_HEADER}</code>
            </div>
            <div className="flex flex-wrap items-center gap-3">
              <label className="inline-flex cursor-pointer items-center gap-2 rounded-lg border border-border px-3 py-1.5 text-[13px] hover:bg-muted">
                <Upload className="size-4" />
                {fileName || tx('CSV seç')}
                <input
                  type="file"
                  accept=".csv,text/csv"
                  className="sr-only"
                  onChange={(e) => {
                    const f = e.target.files?.[0]
                    if (!f) return
                    setFileName(f.name)
                    void f.text().then(setCsv)
                  }}
                />
              </label>
              <Button onClick={() => csv && run.mutate(csv)} disabled={!csv || run.isPending}>
                {run.isPending ? tx('Hesaplanıyor…') : tx('Denetimi çalıştır')}
              </Button>
            </div>
            {latest.isPending ? (
              <RowsSkeleton rows={3} columns={4} />
            ) : latest.isError ? (
              <ErrorState message={errMsg(latest.error)} onRetry={() => void latest.refetch()} />
            ) : report?.report ? (
              <>
                <p className="text-[12px] text-muted-foreground">
                  {tx('Son denetim: {0}, {1}', [report.createdBy ?? '—', formatDateTime(report.createdAt)])}
                  {report.skipped ? ` · ${tx('{0} satır eşleşmedi ya da tekrarlandı', [report.skipped])}` : ''}
                </p>
                <FairnessTable report={report.report} />
              </>
            ) : (
              <EmptyState title={tx('Henüz adillik denetimi yok')} />
            )}
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

const DRY_RUN_PREFIX = /^Deneme eğitimi \(dry_run\): aday kaydedildi, yayımlanmadı\.\s*/

/** Sürüm notu: ML servisinin teknik "dry_run" ifadesi yerine okunur metin. */
export function versionReason(reason: string): string {
  const m = DRY_RUN_PREFIX.exec(reason)
  if (!m) return txServer(reason.replace(/\s*\(dry_run\)/g, ''))
  const rest = reason.slice(m[0].length).trim()
  return [tx('Deneme eğitimi: aday kaydedildi, yayımlanmadı.'), rest ? txServer(rest) : ''].filter(Boolean).join(' ')
}
