import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { RefreshCw, ShieldCheck, ShieldAlert } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/modal'
import { ProgressBar } from '@/components/ui/progress'
import { StatusBadge, type StatusTone } from '@/components/ui/status-badge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { mlModelApi, type DriftLevel, type DriftReport, type RetrainResult } from '@/api/mlModel'
import { formatDate, formatDateTime, formatNumber, formatPercent } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { appLocale, tx, txServer } from '@/lib/i18n'
import { CalibrationPanel, FairnessPanel, FreshnessBadge, VersionsPanel, versionReason } from './ModelQualityPanels'
import { TenantTrainingPanel } from './TenantTrainingPanel'

/** AUC/PSI gibi 0–1 arası metrikler yerel ondalık ayırıcıyla, 3 basamak. */
const DEC3 = new Intl.NumberFormat(appLocale, { minimumFractionDigits: 3, maximumFractionDigits: 3 })
const dec3 = (n: number) => DEC3.format(n)
const dec = (n: number) => new Intl.NumberFormat(appLocale, { maximumFractionDigits: 3 }).format(n)

/**
 * Devir riski modelinin kartı (G5): ne için kullanılır, hangi özellikleri kullanır ve BİLEREK
 * hangilerini kullanmaz, ne zaman hangi veriyle eğitildi, küresel özellik önemi ve veri kayması.
 * Kişi bazlı skor bu sayfada yoktur (çalışan sayfasında, itiraz denetimiyle governance üzerinden).
 */

const driftView: Record<DriftLevel, { label: string; tone: StatusTone }> = {
  stable: { label: tx('Kararlı'), tone: 'success' },
  moderate: { label: tx('Orta kayma'), tone: 'warning' },
  significant: { label: tx('Belirgin kayma'), tone: 'danger' },
}

const sourceLabel = (s: string | null | undefined) =>
  s === 'synthetic' ? tx('Sentetik veri') : s === 'provided' ? tx('Toplu, takma adlı kiracı verisi') : '—'

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-wrap items-baseline justify-between gap-2 border-b border-border py-2 text-[13px] last:border-0">
      <span className="text-muted-foreground">{label}</span>
      <span className="tabular text-right font-medium">{children}</span>
    </div>
  )
}

function DriftTable({ report }: { report: DriftReport }) {
  if (!report.available || !report.features?.length) return null
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2 text-[13px]">
        <StatusBadge tone={driftView[report.status ?? 'stable'].tone}>{driftView[report.status ?? 'stable'].label}</StatusBadge>
        <span className="text-muted-foreground">
          {tx('{0} kayıt · en yüksek PSI {1} · {2}', [formatNumber(report.rows ?? 0), dec3(report.max_psi ?? 0), formatDateTime(report.computed_at)])}
        </span>
        {typeof report.prediction_psi === 'number' && (
          <span className="flex items-center gap-1.5 text-muted-foreground">
            {tx('Skor dağılımı PSI {0}', [dec3(report.prediction_psi)])}
            {report.prediction_level && report.prediction_level !== 'stable' && (
              <StatusBadge tone={driftView[report.prediction_level].tone}>{driftView[report.prediction_level].label}</StatusBadge>
            )}
          </span>
        )}
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-[13px]">
          <thead>
            <tr className="text-left text-[11px] tracking-[0.08em] text-muted-foreground uppercase">
              <th className="py-2 pr-3 font-medium">{tx('Özellik')}</th>
              <th className="py-2 pr-3 text-right font-medium">PSI</th>
              <th className="py-2 pr-3 text-right font-medium">{tx('Eğitim ort.')}</th>
              <th className="py-2 pr-3 text-right font-medium">{tx('Güncel ort.')}</th>
              <th className="py-2 font-medium">{tx('Durum')}</th>
            </tr>
          </thead>
          <tbody>
            {report.features.map((f) => (
              <tr key={f.feature} className="border-t border-border">
                <td className="py-2 pr-3">{tx(f.label)}</td>
                <td className="tabular py-2 pr-3 text-right">{dec3(f.psi)}</td>
                <td className="tabular py-2 pr-3 text-right">{f.reference_mean == null ? '—' : dec(f.reference_mean)}</td>
                <td className="tabular py-2 pr-3 text-right">{f.batch_mean == null ? '—' : dec(f.batch_mean)}</td>
                <td className="py-2"><StatusBadge tone={driftView[f.level].tone}>{driftView[f.level].label}</StatusBadge></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {report.recommendation && <InfoNote>{txServer(report.recommendation)}</InfoNote>}
    </div>
  )
}

export function ModelCardPage() {
  const { roles, tenantSlug } = useAuth()
  // Model kiracılar arası paylaşımlıdır; veri kayması ise şirket başına ölçülür. Kiracısız oturumda
  // (platform yöneticisi) kayma istekleri atılmaz.
  const noTenant = !tenantSlug
  const [confirm, setConfirm] = useState(false)
  const [lastRun, setLastRun] = useState<RetrainResult | null>(null)
  // Paylaşılan modeli yayımlama yalnızca platform yöneticisinde; şirket İK'sı yalnızca deneme eğitimi yapar.
  const canPromote = roles.includes('platform-admin')
  const [dryRun, setDryRun] = useState(false)
  const effectiveDryRun = !canPromote || dryRun
  const card = useQuery({ queryKey: ['ml-model', 'card'], queryFn: ({ signal }) => mlModelApi.card(signal) })
  const importance = useQuery({
    queryKey: ['ml-model', 'importance', card.data?.version],
    queryFn: ({ signal }) => mlModelApi.importance(signal),
    enabled: Boolean(card.data?.serving),
  })
  const recent = useQuery({ queryKey: ['ml-model', 'drift', 'recent'], queryFn: ({ signal }) => mlModelApi.recentDrift(signal), enabled: !noTenant })
  const last = useQuery({
    queryKey: ['ml-model', 'drift', 'last'],
    queryFn: ({ signal }) => mlModelApi.lastDrift(signal),
    enabled: !noTenant && recent.isSuccess && !recent.data.available,
  })
  const retrain = useAction(() => mlModelApi.retrainSynthetic(effectiveDryRun), {
    success: (r) => (r.promoted ? tx('Yeni sürüm yayımlandı: v{0}', [r.candidate_version])
      : r.awaiting_approval ? tx('Aday v{0} kaydedildi; yayına alma onay bekliyor', [r.candidate_version])
      : r.dry_run ? tx('Deneme eğitimi tamamlandı: aday v{0} yayımlanmadı', [r.candidate_version])
      : tx('Aday sürüm yayımlanmadı: v{0}', [r.candidate_version])),
    invalidate: [['ml-model']],
    onDone: (r) => {
      setLastRun(r)
      setConfirm(false)
    },
  })

  const c = card.data
  const drift = recent.data?.available ? recent.data : last.data?.available ? last.data : null

  return (
    <div className="space-y-6">
      <PageHeader
        title={tx('Model kartı: devir riski')}
        description={tx('Modelin amacı, kullandığı ve bilerek kullanmadığı bilgiler, eğitim verisi, başarımı ve veri kayması.')}
        actions={
          isHr(roles) ? (
            <Button variant="outline" className="cursor-pointer" onClick={() => setConfirm(true)}>
              <RefreshCw />
              {tx('Yeniden eğit')}
            </Button>
          ) : undefined
        }
      />

      {card.isPending ? (
        <Panel><RowsSkeleton rows={5} columns={2} /></Panel>
      ) : card.isError ? (
        <ErrorState message={errMsg(card.error)} onRetry={() => void card.refetch()} />
      ) : c && (
        <>
          {c.freshness?.stale && (
            <InfoNote>
              {tx('Model kartının geçerlilik süresi ({0} gün) doldu. Skorları yalnızca insan incelemesiyle kullanın ve modeli güncel veriyle yeniden eğitin.', [c.freshness.validity_days])}
            </InfoNote>
          )}
          {!c.serving && (
            <InfoNote>
              {c.blocked_reason
                ? tx('Model devre dışı: dışlanan nitelik denetiminden geçmedi ({0}).', [c.blocked_reason])
                : tx('Model henüz yüklenmedi; birkaç dakika sonra yeniden deneyin.')}
            </InfoNote>
          )}
          {lastRun && (
            <InfoNote>
              {tx('Son eğitim: aday v{0} - {1} (aday AUC {2}, mevcut AUC {3}).', [
                lastRun.candidate_version,
                versionReason(lastRun.decision.reason),
                dec3(lastRun.candidate.auc),
                lastRun.current ? dec3(lastRun.current.auc) : '—',
              ])}
            </InfoNote>
          )}
          <div className="grid gap-6 lg:grid-cols-2">
            <Panel>
              <PanelHead title={tx('Sürüm ve eğitim')} note={tx(c.intended_use)} action={<FreshnessBadge freshness={c.freshness} />} />
              <PanelBody>
                <Row label={tx('Model')}>{c.model}{c.version ? ` · v${c.version}` : ''}</Row>
                <Row label={tx('Eğitim tarihi')}>{formatDateTime(c.trained_at)}</Row>
                {c.freshness?.expires_on && (
                  <Row label={tx('Geçerlilik')}>
                    {tx('{0} güne kadar · {1}', [c.freshness.validity_days, formatDate(c.freshness.expires_on)])}
                  </Row>
                )}
                <Row label={tx('Eğitim verisi')}>{sourceLabel(c.training_source)} · {formatNumber(c.training_rows)} {tx('satır')}
                  {c.provenance ? ` · ${tx('kiracı izniyle, zaman bazlı doğrulama ({0} → {1})', [formatDate(c.provenance.train_snapshot), formatDate(c.provenance.eval_snapshot)])}` : ''}</Row>
                <Row label={tx('Veri dönemi')}>
                  {c.data_window ? `${formatDate(c.data_window.start)} – ${formatDate(c.data_window.end)}` : tx(c.data_window_note ?? '—')}
                </Row>
                <Row label={tx('Değerlendirme')}>{formatNumber(c.evaluation_rows)} {tx('satır (eğitimde kullanılmadı)')}</Row>
                <Row label="AUC">{c.metrics ? dec3(c.metrics.auc) : '—'}</Row>
                <Row label={tx('Doğruluk')}>{c.metrics ? formatPercent(c.metrics.accuracy, 1) : '—'}</Row>
                {c.calibration && (
                  // Kalibrasyon yöntemi "none" ise skor ham olasılıktır; etiket buna göre.
                  <Row label={c.calibration.method === 'none' ? tx('Brier (ham olasılık)') : tx('Brier (kalibre)')}>{dec3(c.calibration.brier)}</Row>
                )}
                <Row label={tx('Algoritma')}>{String(c.algorithm.name ?? '—')}</Row>
                {c.promotion && <Row label={tx('Yayın kararı')}>{versionReason(c.promotion.reason)}</Row>}
              </PanelBody>
            </Panel>

            <Panel>
              <PanelHead
                title={tx('Küresel özellik önemi')}
                note={importance.data?.method === 'impurity'
                  ? tx('Ağaç safsızlığına göre (SHAP kullanılamadı).')
                  : tx('Ortalama |SHAP| değeri: özelliğin skora ortalama etkisi. Kişi bazlı değildir.')}
              />
              <PanelBody className="space-y-3">
                {importance.isPending && c.serving ? (
                  <RowsSkeleton rows={6} columns={2} />
                ) : importance.isError ? (
                  <ErrorState message={errMsg(importance.error)} onRetry={() => void importance.refetch()} />
                ) : importance.data ? (
                  importance.data.features.map((f) => (
                    <div key={f.feature} className="space-y-1">
                      <div className="flex justify-between text-[13px]">
                        <span>{tx(f.label)}</span>
                        <span className="tabular text-muted-foreground">{formatPercent(f.share, 1)}</span>
                      </div>
                      <ProgressBar value={f.share * 100} label={tx(f.label)} />
                    </div>
                  ))
                ) : (
                  <EmptyState title={tx('Önem bilgisi yok')} detail={tx('Model yüklendiğinde hesaplanır.')} />
                )}
              </PanelBody>
            </Panel>
          </div>

          <div className="grid gap-6 lg:grid-cols-2">
            <Panel>
              <PanelHead title={tx('Kullanılan özellikler')} note={tx('Yalnızca işle ilgili, sayısal ve kimliksiz bilgiler.')} />
              <PanelBody>
                {c.features.map((f) => (
                  <Row key={f.name} label={tx(f.label)}>
                    <span className="font-normal text-muted-foreground">{tx(f.description)} ({tx(f.unit)})</span>
                  </Row>
                ))}
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead
                title={tx('Bilerek kullanılmayan bilgiler')}
                action={c.excluded_check.passed
                  ? <StatusBadge tone="success"><ShieldCheck className="size-3.5" />{tx('Denetim geçti')}</StatusBadge>
                  : <StatusBadge tone="danger"><ShieldAlert className="size-3.5" />{tx('Denetim başarısız')}</StatusBadge>}
              />
              <PanelBody className="space-y-3">
                <div className="flex flex-wrap gap-2">
                  {c.excluded_attributes.map((a) => <StatusBadge key={a.key}>{tx(a.label)}</StatusBadge>)}
                </div>
                <InfoNote>{tx(c.fairness.note)}</InfoNote>
                <p className="text-[13px] leading-relaxed text-muted-foreground">{tx(c.fairness.residual_risk)}</p>
              </PanelBody>
            </Panel>
          </div>

          <Panel>
            <PanelHead
              title={tx('Veri kayması (PSI)')}
              note={tx('Güncel girdilerin dağılımı eğitim verisiyle karşılaştırılır. PSI {0} üstü orta, {1} üstü belirgin kaymadır. Yalnızca toplu kova sayıları tutulur.', [dec(c.drift_thresholds.moderate), dec(c.drift_thresholds.significant)])}
            />
            <PanelBody>
              {noTenant ? (
                <EmptyState
                  title={tx('Veri kayması şirket bazında ölçülür')}
                  detail={tx('Platform görünümünde kayma gösterilmez; her şirketin İK yöneticisi kendi şirketinin ölçümünü bu ekranda görür.')}
                />
              ) : recent.isPending ? (
                <RowsSkeleton rows={4} columns={4} />
              ) : drift ? (
                <DriftTable report={drift} />
              ) : (
                <EmptyState
                  title={tx('Henüz kayma ölçümü yok')}
                  detail={tx('Bu şirket için en az {0} tahmin yapıldığında son tahminlerin toplu dağılımından hesaplanır (şu an {1}).', [recent.data?.min_rows ?? 30, recent.data?.rows ?? 0])}
                />
              )}
            </PanelBody>
          </Panel>

          <CalibrationPanel canEdit={isHr(roles) && !noTenant} noTenant={noTenant} />
          <VersionsPanel canPromote={canPromote} />
          {isHr(roles) && <FairnessPanel noTenant={noTenant} />}
          {isHr(roles) && !noTenant && <TenantTrainingPanel />}

          <Panel>
            <PanelHead title={tx('Sınırlamalar ve KVKK')} />
            <PanelBody className="space-y-2 text-[13px] leading-relaxed">
              <ul className="list-disc space-y-1 pl-5">
                {c.limitations.map((l) => <li key={l}>{tx(l)}</li>)}
              </ul>
              <InfoNote>{tx(c.kvkk)}</InfoNote>
            </PanelBody>
          </Panel>
        </>
      )}

      {confirm && (
        <Modal
          open
          onClose={() => setConfirm(false)}
          title={tx('Modeli yeniden eğit')}
          note={canPromote
            ? (c?.approval_required
              ? tx('Yeni tohumla sentetik veride aday model eğitilir ve mevcut modelle aynı değerlendirme kümesinde karşılaştırılır. AUC farkı -{0} eşiğinden kötü değilse aday olarak kaydedilir; yayına alma "Sürümler" bölümünden onayla yapılır.', [dec(c?.promotion_tolerance ?? 0.02)])
              : tx('Yeni tohumla sentetik veride aday model eğitilir ve mevcut modelle aynı değerlendirme kümesinde karşılaştırılır. Aday yalnızca AUC farkı -{0} eşiğinden kötü değilse yayımlanır; aksi halde kayıtta kalır.', [dec(c?.promotion_tolerance ?? 0.02)]))
            : tx('Deneme eğitimi: aday değerlendirilir, yayımlanmaz; yayımlama platform yöneticisindedir')}
          footer={
            <>
              <Button variant="outline" onClick={() => setConfirm(false)}>{tx('Vazgeç')}</Button>
              <Button onClick={() => retrain.mutate(undefined)} disabled={retrain.isPending}>
                {retrain.isPending ? tx('Eğitiliyor…') : tx('Eğit')}
              </Button>
            </>
          }
        >
          <div className="space-y-3">
            <label className="flex items-center gap-2 text-[13px]">
              <Checkbox checked={effectiveDryRun} disabled={!canPromote} onCheckedChange={(v) => setDryRun(v === true)} />
              {tx('Yalnızca deneme eğitimi (yayımlanmaz)')}
            </label>
            <InfoNote>{tx('Model tüm şirketlerce paylaşılır. Şirket verisiyle eğitim yalnızca platform düzeyinde, toplu ve takma adlı veriyle yapılır.')}</InfoNote>
          </div>
        </Modal>
      )}
    </div>
  )
}
