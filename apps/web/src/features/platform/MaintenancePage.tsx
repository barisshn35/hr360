import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArchiveRestore, CalendarClock, CheckCircle2, CircleAlert, DownloadCloud, HardDrive, History, Info, LoaderCircle, RefreshCw, Rewind, ShieldCheck, Trash2, Undo2, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { Checkbox } from '@/components/ui/checkbox'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { useAction } from '@/features/shared/kit'
import { newerThan, pitrWindow, platformOpsApi, validPitrTarget, type OpsBackup, type OpsJob, type OpsKind, type OpsPitr, type OpsSecurity, type OpsStatus } from '@/api/platformOps'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { appLocale, tx } from '@/lib/i18n'

const KEY = ['platform-ops'] as const
const RESTORE_CONFIRM = 'GERİ YÜKLE'
const ROLLBACK_CONFIRM = 'GERİ AL'
const PITR_CONFIRM = 'GERİ DÖN'
// Geri dönüş anı sunucuda İstanbul saatiyle yorumlanır; ekranda da o saatle gösterilir.
const istanbulFmt = new Intl.DateTimeFormat(appLocale, { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Istanbul' })
const istanbul = (iso: string | null) => (iso && !Number.isNaN(Date.parse(iso)) ? istanbulFmt.format(new Date(iso)) : '—')

const kindLabels = (): Record<OpsKind, string> => ({
  backup: tx('Yedek al'), 'backup-verify': tx('Yedeği doğrula'), schedule: tx('Yedek zamanla'), unschedule: tx('Zamanlamayı kaldır'),
  'check-updates': tx('Güncellemeleri denetle'), update: tx('Güncelle'), rollback: tx('Önceki sürüme dön'), restore: tx('Yedekten geri yükle'),
  'db-isolation': tx('Veritabanı yalıtımını aç'),
  'pitr-enable': tx('Anlık geri dönüşü aç'), 'pitr-disable': tx('Anlık geri dönüşü kapat'), 'pitr-restore': tx('Bir ana geri dön'),
  'pitr-cleanup': tx('Eski veriyi sil'),
})
const STATUS: Record<OpsStatus, { tone: 'neutral' | 'info' | 'success' | 'danger'; label: () => string }> = {
  Pending: { tone: 'neutral', label: () => tx('Sırada') }, Running: { tone: 'info', label: () => tx('Çalışıyor') },
  Succeeded: { tone: 'success', label: () => tx('Tamamlandı') }, Failed: { tone: 'danger', label: () => tx('Başarısız') }, Cancelled: { tone: 'neutral', label: () => tx('İptal edildi') },
}

function bytes(n: number): string {
  const u = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++ }
  return `${n.toFixed(i > 1 ? 1 : 0)} ${u[i]}`
}

function useEnqueue(success: string, onDone?: () => void) {
  return useAction((v: { kind: OpsKind; args?: Record<string, unknown>; confirm?: string }) => platformOpsApi.enqueue(v.kind, v.args, v.confirm),
    { success, invalidate: [KEY], onDone })
}

/** Yazılı onay isteyen işlem (geri yükleme, geri alma). */
function TypedConfirm({ title, note, word, children, onConfirm, onClose, pending, ready = true }: {
  title: string; note: string; word: string; children?: React.ReactNode; onConfirm: () => void; onClose: () => void; pending: boolean
  /** Diğer alanlar geçerli mi (ör. geri dönüş anı). */
  ready?: boolean
}) {
  const [text, setText] = useState('')
  return (
    <Modal open onClose={onClose} title={title} note={note}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button variant="destructive" disabled={text !== word || pending || !ready} onClick={onConfirm}>{title}</Button></>}>
      <div className="space-y-4">
        {children}
        <TextField label={tx('Onay için "{0}" yazın', [word])} value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" />
      </div>
    </Modal>
  )
}

function JobOutput({ id, onClose }: { id: string; onClose: () => void }) {
  const q = useQuery({ queryKey: [...KEY, 'job', id], queryFn: ({ signal }) => platformOpsApi.job(id, signal),
    refetchInterval: (s) => (s.state.data && (s.state.data.status === 'Pending' || s.state.data.status === 'Running') ? 4000 : false) })
  const j = q.data
  return (
    <Modal open onClose={onClose} size="xl" title={j ? kindLabels()[j.kind] : tx('İş')} note={j ? `${STATUS[j.status].label()} · ${formatDateTime(j.requestedAt)}${j.requestedBy ? ` · ${j.requestedBy}` : ''}` : undefined}>
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : (
        <pre className="max-h-[60vh] overflow-auto rounded-xl bg-muted/50 p-3 font-mono text-[12px] leading-relaxed whitespace-pre-wrap">{j!.output || (j!.status === 'Pending' ? tx('Sırada; çalıştırıcı bir dakika içinde başlatır.') : j!.status === 'Running' ? tx('Çalışıyor…') : '—')}</pre>
      )}
    </Modal>
  )
}

function JobsPanel({ jobs, onOpen }: { jobs: OpsJob[]; onOpen: (id: string) => void }) {
  const cancel = useAction((id: string) => platformOpsApi.cancel(id), { success: tx('İş iptal edildi.'), invalidate: [KEY] })
  const labels = kindLabels()
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><History className="size-4 text-primary" />{tx('İş geçmişi')}</span>} note={tx('Son 50 iş. Ayrıntı ve çıktı için satıra tıklayın.')} />
      <PanelBody className="p-0">
        {!jobs.length ? <p className="px-5 py-4 text-[13px] text-muted-foreground">{tx('Henüz iş yok.')}</p> : (
          <ul className="divide-y divide-border">
            {jobs.map((j) => (
              <li key={j.id} className="flex flex-wrap items-center gap-3 px-5 py-2.5 text-[13px]">
                <button type="button" onClick={() => onOpen(j.id)} className="min-w-0 flex-1 cursor-pointer text-left">
                  <span className="font-medium">{labels[j.kind]}</span>
                  {typeof j.args.ref === 'string' && <span className="ml-1.5 text-muted-foreground">{j.args.ref}</span>}
                  {typeof j.args.archive === 'string' && <span className="ml-1.5 truncate text-muted-foreground">{j.args.archive}</span>}
                  {typeof j.args.target === 'string' && <span className="ml-1.5 text-muted-foreground">{j.args.target}</span>}
                  <span className="block text-[12px] text-muted-foreground">{formatRelativeToNow(j.requestedAt)}{j.requestedBy ? ` · ${j.requestedBy}` : ''}</span>
                </button>
                {j.status === 'Running' && <LoaderCircle className="size-4 animate-spin text-primary" />}
                <StatusBadge tone={STATUS[j.status].tone}>{STATUS[j.status].label()}</StatusBadge>
                {j.status === 'Pending' && <Button size="sm" variant="ghost" aria-label={tx('İptal et')} onClick={() => cancel.mutate(j.id)}><X className="size-4" /></Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

const PITR_TONE: Record<OpsPitr['state'], 'success' | 'neutral' | 'danger'> = { on: 'success', off: 'neutral', 'not-ready': 'neutral', unknown: 'danger' }

/** Anlık geri dönüş (scripts/pitr.sh): aç/kapat, bir ana dön, geri dönüş öncesi veriyi sil. */
export function PitrPanel({ pitr, lastJob, disabled, busy, onOpenJob }: {
  pitr: OpsPitr; lastJob?: OpsJob; disabled: boolean; busy: boolean; onOpenJob: (id: string) => void
}) {
  const confirm = useConfirm()
  const [mode, setMode] = useState<'disable' | 'restore' | null>(null)
  const [purge, setPurge] = useState(false)
  const [date, setDate] = useState('')
  const [time, setTime] = useState('')
  const close = () => setMode(null)
  const run = useEnqueue(tx('İş sıraya alındı; çalıştırıcı bir dakika içinde başlatır.'), close)
  const labels = kindLabels()
  const w = pitrWindow(pitr.oldestRecoverable)
  const target = `${date} ${time}`
  const valid = validPitrTarget(target, w)
  const stateLabel = { on: tx('Açık'), off: tx('Kapalı'), 'not-ready': tx('Hazır değil'), unknown: tx('Bilinmiyor') }[pitr.state]
  const off = disabled || run.isPending

  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Rewind className="size-4 text-primary" />{tx('Anlık geri dönüş')}</span>}
        note={tx('Gece yedeği bir güne kadar veri kaybıyla korur; bu özellik açıkken veritabanı son 5 dakikaya kadar herhangi bir ana döndürülebilir. Belgeler ve fotoğraflar geri dönmez.')}
        action={<StatusBadge tone={PITR_TONE[pitr.state]}>{stateLabel}</StatusBadge>} />
      <PanelBody className="space-y-4">
        {pitr.state === 'not-ready' && <InfoNote>{tx('Veritabanı arşivlemeye hazır başlatılmamış; bir sonraki sürüm güncellemesinden sonra açılabilir.')}</InfoNote>}
        {pitr.state === 'unknown' && <InfoNote>{tx('Durum okunamadı; veritabanı çalışmıyor olabilir.')}</InfoNote>}
        <dl className="grid gap-3 text-[13px] sm:grid-cols-3">
          <div><dt className="text-[12px] text-muted-foreground">{tx('Arşiv boyutu')}</dt><dd className="font-medium">{pitr.walSize ?? '—'}{pitr.baseSize ? ` + ${tx('temel yedek {0}', [pitr.baseSize])}` : ''}</dd></div>
          <div className="sm:col-span-2"><dt className="text-[12px] text-muted-foreground">{tx('Geri dönülebilecek aralık (İstanbul saati)')}</dt>
            <dd className="font-medium">{pitr.oldestRecoverable ? `${istanbul(pitr.oldestRecoverable)} – ${pitr.lastArchivedAt ? istanbul(pitr.lastArchivedAt) : tx('şimdi')}` : '—'}</dd></div>
        </dl>
        {pitr.failedCount > 0 && (
          <p className="rounded-xl border border-amber-500/40 bg-amber-500/10 px-3.5 py-2.5 text-[13px] leading-relaxed">
            {tx('Arşivleme hatası: {0} başarısız deneme (son: {1}). Disk doluluğunu ve veritabanı günlüğünü denetleyin; arşivlenemeyen değişikliklere geri dönülemez.', [String(pitr.failedCount), pitr.lastFailedWal ?? '—'])}
          </p>
        )}
        {pitr.preRestore.length > 0 && (
          <div className="flex flex-wrap items-center gap-3 rounded-xl border border-border p-3 text-[13px]">
            <span className="min-w-0 flex-1">{tx('Geri dönüşten önceki veri sunucuda saklanıyor: {0}. Geri dönüşü doğruladıktan sonra silin.', [pitr.preRestore.map((p) => `${p.name}${p.size ? ` (${p.size})` : ''}`).join(', ')])}</span>
            <Button size="sm" variant="outline" disabled={off || busy} onClick={async () => {
              if (await confirm({ title: tx('Eski veri silinsin mi?'), note: tx('Geri dönüşten önceki veri kopyaları kalıcı olarak silinir; geri dönüş artık geri alınamaz.'), action: tx('Sil') }))
                run.mutate({ kind: 'pitr-cleanup' })
            }}><Trash2 className="size-4" />{tx('Eski veriyi sil')}</Button>
          </div>
        )}
        {lastJob && (
          <button type="button" onClick={() => onOpenJob(lastJob.id)} className="flex w-full cursor-pointer items-center gap-2 text-left text-[12px] text-muted-foreground">
            <span className="min-w-0 flex-1">{tx('Son işlem: {0} · {1}', [labels[lastJob.kind], formatRelativeToNow(lastJob.requestedAt)])}</span>
            {lastJob.status === 'Running' && <LoaderCircle className="size-4 animate-spin text-primary" />}
            <StatusBadge tone={STATUS[lastJob.status].tone}>{STATUS[lastJob.status].label()}</StatusBadge>
          </button>
        )}
        <div className="flex flex-wrap gap-2">
          {pitr.state === 'off' && (
            <Button disabled={off} onClick={async () => {
              if (await confirm({ title: tx('Anlık geri dönüş açılsın mı?'), note: tx('Veritabanı değişiklikleri sürekli arşivlenir ve ilk temel yedek alınır. Arşiv veritabanı diskinde yer kaplar ve yazma yoğunluğuyla büyür.'), action: tx('Aç'), destructive: false }))
                run.mutate({ kind: 'pitr-enable' })
            }}>{tx('Aç')}</Button>
          )}
          {w && pitr.bases.length > 0 && (
            <Button variant="outline" disabled={off || busy} onClick={() => { setDate(w.max.slice(0, 10)); setTime(w.max.slice(11)); setMode('restore') }}>
              <Rewind className="size-4" />{tx('Bir ana dön')}
            </Button>
          )}
          {pitr.state === 'on' && <Button variant="ghost" disabled={off} onClick={() => { setPurge(false); setMode('disable') }}>{tx('Kapat')}</Button>}
        </div>
      </PanelBody>

      {mode === 'disable' && (
        <Modal open onClose={close} title={tx('Anlık geri dönüş kapatılsın mı?')} note={tx('Yeni değişiklikler arşivlenmez; yalnızca gece yedeğine dönülebilir.')}
          footer={<><Button variant="outline" onClick={close}>{tx('Vazgeç')}</Button><Button variant="destructive" disabled={run.isPending} onClick={() => run.mutate({ kind: 'pitr-disable', args: { purge } })}>{tx('Anlık geri dönüşü kapat')}</Button></>}>
          <label className="flex cursor-pointer items-start gap-2 text-[13px]">
            <Checkbox checked={purge} onCheckedChange={(v) => setPurge(v === true)} className="mt-0.5" />
            <span>{tx('Arşivi de sil')}<span className="block text-[12px] text-muted-foreground">{tx('Arşiv ve temel yedekler silinir, disk açılır; daha önceki anlara artık geri dönülemez.')}</span></span>
          </label>
        </Modal>
      )}
      {mode === 'restore' && w && (
        <TypedConfirm title={tx('Bir ana dön')} word={PITR_CONFIRM} pending={run.isPending} ready={valid} onClose={close}
          note={tx('Tüm veritabanları seçilen ana döndürülür. O andan sonraki tüm değişiklikler kaybolur; servisler yeniden başlatılır ve uygulama birkaç dakika erişilemez.')}
          onConfirm={() => run.mutate({ kind: 'pitr-restore', args: { target }, confirm: PITR_CONFIRM })}>
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Tarih')} type="date" min={w.min.slice(0, 10)} max={w.max.slice(0, 10)} value={date} onChange={(e) => setDate(e.target.value)} />
            <TextField label={tx('Saat (İstanbul)')} type="time" value={time} onChange={(e) => setTime(e.target.value)} />
          </div>
          <p className="text-[12px] text-muted-foreground">{tx('Geri dönülebilecek aralık: {0} – {1}', [w.min, w.max])}</p>
          {!valid && date && time && <p className="text-[12px] text-destructive">{tx('Seçilen an geri dönülebilecek aralığın dışında.')}</p>}
          <p className="text-[12px] text-muted-foreground">{tx('Belgeler ve fotoğraflar geri dönmez. Mevcut veri sunucuda saklanır; doğruladıktan sonra "Eski veriyi sil" ile silebilirsiniz.')}</p>
        </TypedConfirm>
      )}
    </Panel>
  )
}

/** Sunucu güvenlik duruşu: önerilen ayarların açık/kapalı durumu (değerler değil). */
function SecurityPanel({ sec, pitr, disabled, onIsolate }: { sec: OpsSecurity; pitr?: OpsPitr; disabled: boolean; onIsolate: () => void }) {
  const [rlsOn, rlsAll] = sec.rlsTables.split('/').map((x) => Number(x) || 0)
  const isolated = !sec.dbIsolationOff && sec.dbRoles >= 15 && rlsAll > 0 && rlsOn === rlsAll
  const items: { ok: boolean; title: string; note: string; action?: React.ReactNode }[] = [
    { ok: isolated, title: tx('Veritabanı yalıtımı'),
      note: isolated ? tx('Her servis kendi en az yetkili rolüyle bağlanır; şirket verileri satır düzeyinde ayrılır ({0} tablo).', [String(rlsAll)])
        : tx('Servis rolleri: {0}, satır düzeyi güvenlik: {1} tablo.', [String(sec.dbRoles), sec.rlsTables]),
      action: isolated ? undefined : <Button size="sm" disabled={disabled} onClick={onIsolate}>{tx('Aç')}</Button> },
    { ok: sec.tls, title: tx('HTTPS'), note: sec.tls ? tx('Trafik şifreli.') : tx('Uygulama şifresiz HTTP ile sunuluyor.') },
    { ok: sec.backupScheduled, title: tx('Otomatik yedek'), note: sec.backupScheduled ? tx('Günlük yedek zamanlanmış.') : tx('Zamanlanmış yedek yok; "Otomatik yedek" bölümünden zamanlayın.') },
    { ok: sec.backupEncrypted, title: tx('Yedek şifreleme'), note: sec.backupEncrypted ? tx('Yedekler şifreli alınır.') : tx('Yedekler şifresiz.') },
    ...(pitr ? [{ ok: pitr.enabled, title: tx('Anlık geri dönüş'),
      note: pitr.enabled ? tx('Açık: veritabanı son 5 dakikaya kadar herhangi bir ana döndürülebilir.') : tx('Kapalı: yalnızca gece yedeğine dönülebilir; "Anlık geri dönüş" bölümünden açın.') }] : []),
    { ok: sec.backupOffsite, title: tx('Sunucu dışı yedek kopyası'), note: sec.backupOffsite ? tx('Yedekler ayrıca dış depoya gönderilir.') : tx('Yedekler yalnızca bu sunucuda; sunucu kaybında yedek de kaybolur.') },
    { ok: sec.redisPassword, title: tx('Önbellek parolası'), note: sec.redisPassword ? tx('Önbellek parolayla korunuyor.') : tx('Önbellek parolasız.') },
    { ok: sec.monitoring, title: tx('İzleme ve alarmlar'), note: sec.monitoring ? tx('Ölçümler toplanıyor, alarmlar açık.') : tx('İzleme kapalı; servis çökmesi ya da disk dolması bildirilmez.') },
  ]
  const okCount = items.filter((i) => i.ok).length
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><ShieldCheck className="size-4 text-primary" />{tx('Güvenlik durumu')}</span>}
        note={tx('{0}/{1} önerilen ayar açık. Herkese açık şirket kaydı: {2}.', [String(okCount), String(items.length), sec.publicSignup ? tx('açık') : tx('kapalı')])} />
      <PanelBody className="p-0">
        <ul className="divide-y divide-border">
          {items.map((i) => (
            <li key={i.title} className="flex items-center gap-3 px-5 py-2.5 text-[13px]">
              {i.ok ? <CheckCircle2 className="size-4 shrink-0 text-primary" /> : <Info className="size-4 shrink-0 text-amber-600 dark:text-amber-400" />}
              <span className="min-w-0 flex-1"><span className="font-medium">{i.title}</span><span className="block text-[12px] text-muted-foreground">{i.note}</span></span>
              {i.action}
            </li>
          ))}
        </ul>
      </PanelBody>
    </Panel>
  )
}

/** Platform > Bakım (yenilik haritası #34): sürüm güncelleme, yedek, doğrulama, zamanlama, geri yükleme. */
export function MaintenancePage() {
  const status = useQuery({ queryKey: [...KEY, 'status'], queryFn: ({ signal }) => platformOpsApi.status(signal),
    refetchInterval: (s) => (s.state.data?.activeJobs ? 5000 : 30_000) })
  const jobs = useQuery({ queryKey: [...KEY, 'jobs'], queryFn: ({ signal }) => platformOpsApi.jobs(signal),
    refetchInterval: () => (status.data?.activeJobs ? 5000 : 30_000) })
  const confirm = useConfirm()
  const [output, setOutput] = useState<string | null>(null)
  const [restoring, setRestoring] = useState<OpsBackup | null>(null)
  const [onlyDb, setOnlyDb] = useState(false)
  const [rollingBack, setRollingBack] = useState(false)
  const [sched, setSched] = useState({ at: '03:15', keep: '14', verifyWeekly: true })
  const queued = tx('İş sıraya alındı; çalıştırıcı bir dakika içinde başlatır.')
  const run = useEnqueue(queued)
  const restore = useEnqueue(queued, () => setRestoring(null))
  const rollback = useEnqueue(queued, () => setRollingBack(false))

  if (status.isPending) return <><PageHeader title={tx('Bakım')} eyebrow={[tx('Platform')]} /><RowsSkeleton /></>
  if (status.isError) return <><PageHeader title={tx('Bakım')} eyebrow={[tx('Platform')]} /><ErrorState onRetry={() => void status.refetch()} /></>
  const { runnerOk, snapshot: s, activeJobs, updatedAt } = status.data
  const newer = (s?.tags ?? []).filter((t) => newerThan(t, s?.version))
  const latest = newer[0]
  const busy = activeJobs > 0
  const disabled = !runnerOk || run.isPending

  return (
    <div className="space-y-5">
      <PageHeader title={tx('Bakım')} eyebrow={[tx('Platform')]}
        description={tx('Sürüm güncelleme, yedek alma, doğrulama ve geri yükleme. İşleri sunucudaki bakım çalıştırıcısı sırayla yürütür; her istek denetim kaydına yazılır.')} />
      {!runnerOk && (
        <p className="rounded-xl border border-amber-500/40 bg-amber-500/10 px-3.5 py-2.5 text-[13px] leading-relaxed">
          {tx('Bakım çalıştırıcısı çalışmıyor{0}. Kurulumdan sonra bir kez sunucuda "scripts/ops-runner.sh install" çalıştırılmalıdır; sonraki tüm işlemler bu ekrandan yapılır.', [updatedAt ? ` (${tx('son tur {0}', [formatRelativeToNow(updatedAt)])})` : ''])}
        </p>
      )}
      {busy && <InfoNote>{tx('Sürmekte olan bir iş var. Güncelleme ve geri yükleme sırasında uygulama kısa süre erişilemez olabilir; sayfa kendini yeniler.')}</InfoNote>}

      <div className="grid gap-5 lg:grid-cols-2">
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><DownloadCloud className="size-4 text-primary" />{tx('Sürüm')}</span>}
            note={s ? tx('Kurulu: {0} ({1}, {2})', [s.version ?? '—', s.commit, s.commitDate]) : undefined} />
          <PanelBody className="space-y-4">
            {latest ? (
              <div className="flex flex-wrap items-center gap-3 rounded-xl border border-primary/40 bg-primary/5 p-3">
                <CircleAlert className="size-5 text-primary" />
                <div className="min-w-0 flex-1 text-[13px]"><p className="font-medium">{tx('Yeni sürüm var: {0}', [latest])}</p><p className="text-[12px] text-muted-foreground">{tx('Güncellemeden önce otomatik yedek alınır; sağlık denetimi geçmezse önceki sürüme dönülür.')}</p></div>
                <Button disabled={disabled || busy} onClick={async () => {
                  if (await confirm({ title: tx('{0} sürümüne güncellensin mi?', [latest]), note: tx('Önce yedek alınır. Güncelleme birkaç dakika sürer; bu sırada uygulama kısa süre erişilemez olabilir.'), action: tx('Güncelle'), destructive: false }))
                    run.mutate({ kind: 'update', args: { ref: latest } })
                }}>{tx('Güncelle')}</Button>
              </div>
            ) : <p className="flex items-center gap-2 text-[13px] text-muted-foreground"><CheckCircle2 className="size-4 text-primary" />{tx('En güncel sürümdesiniz.')}</p>}
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" disabled={disabled} onClick={() => run.mutate({ kind: 'check-updates' })}><RefreshCw className="size-4" />{tx('Güncellemeleri denetle')}</Button>
              <Button variant="ghost" disabled={disabled || busy} onClick={() => setRollingBack(true)}><Undo2 className="size-4" />{tx('Önceki sürüme dön')}</Button>
            </div>
            {s?.updateStatus && <pre className="rounded-xl bg-muted/50 p-3 font-mono text-[12px] whitespace-pre-wrap">{s.updateStatus}</pre>}
          </PanelBody>
        </Panel>

        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><CalendarClock className="size-4 text-primary" />{tx('Otomatik yedek')}</span>}
            note={s ? tx('Boş disk: {0} / {1}', [bytes(s.diskFree), bytes(s.diskTotal)]) : undefined} />
          <PanelBody className="space-y-4">
            {s?.backupStatus && <pre className="rounded-xl bg-muted/50 p-3 font-mono text-[12px] whitespace-pre-wrap">{s.backupStatus}</pre>}
            <div className="grid gap-3 sm:grid-cols-2">
              <TextField label={tx('Her gün saat')} type="time" value={sched.at} onChange={(e) => setSched({ ...sched, at: e.target.value })} />
              <TextField label={tx('Saklanacak yedek sayısı')} type="number" min={1} max={365} value={sched.keep} onChange={(e) => setSched({ ...sched, keep: e.target.value })} />
            </div>
            <label className="flex cursor-pointer items-center gap-2 text-[13px]">
              <Checkbox checked={sched.verifyWeekly} onCheckedChange={(v) => setSched({ ...sched, verifyWeekly: v === true })} />{tx('Her hafta son yedeği geri yükleme testiyle doğrula')}
            </label>
            <div className="flex flex-wrap gap-2">
              <Button disabled={disabled || !/^\d{2}:\d{2}$/.test(sched.at) || !(Number(sched.keep) >= 1)} onClick={() => run.mutate({ kind: 'schedule', args: { at: sched.at, keep: Number(sched.keep), verifyWeekly: sched.verifyWeekly } })}>{tx('Zamanla')}</Button>
              <Button variant="ghost" disabled={disabled} onClick={() => run.mutate({ kind: 'unschedule' })}>{tx('Zamanlamayı kaldır')}</Button>
            </div>
          </PanelBody>
        </Panel>
      </div>

      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><HardDrive className="size-4 text-primary" />{tx('Yedekler')}</span>}
          note={tx('Yedekler sunucuda backups/ dizinindedir. Şifreli yedeğin anahtarı .env dosyasındadır; anahtarı yedekten ayrı saklayın.')}
          action={<Button size="sm" disabled={disabled} onClick={() => run.mutate({ kind: 'backup' })}><HardDrive className="size-4" />{tx('Şimdi yedek al')}</Button>} />
        <PanelBody className="p-0">
          {!s?.backups.length ? <p className="px-5 py-4 text-[13px] text-muted-foreground">{tx('Henüz yedek yok.')}</p> : (
            <ul className="divide-y divide-border">
              {s.backups.map((b) => (
                <li key={b.name} className="flex flex-wrap items-center gap-3 px-5 py-2.5 text-[13px]">
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12.5px]">{b.name}</span>
                    <span className="text-[12px] text-muted-foreground">{formatDateTime(b.createdAt)} · {bytes(b.size)}{b.encrypted ? ` · ${tx('şifreli')}` : ''}{b.manifest ? ` · ${tx('imzalı')}` : ''}</span>
                  </span>
                  <Button size="sm" variant="ghost" disabled={disabled} onClick={() => run.mutate({ kind: 'backup-verify', args: { archive: b.name } })}><ShieldCheck className="size-4" />{tx('Doğrula')}</Button>
                  <Button size="sm" variant="outline" disabled={disabled || busy} onClick={() => { setOnlyDb(false); setRestoring(b) }}><ArchiveRestore className="size-4" />{tx('Geri yükle')}</Button>
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>

      {s?.pitr && <PitrPanel pitr={s.pitr} lastJob={jobs.data?.find((j) => j.kind.startsWith('pitr-'))} disabled={disabled} busy={busy} onOpenJob={setOutput} />}

      {s?.security && <SecurityPanel sec={s.security} pitr={s.pitr} disabled={disabled || busy} onIsolate={async () => {
        if (await confirm({ title: tx('Veritabanı yalıtımı açılsın mı?'), note: tx('Her servis kendi rolüne geçer ve satır düzeyi güvenlik açılır. Servisler yeniden başlatılır; uygulama bir dakika kadar erişilemez olabilir.'), action: tx('Aç'), destructive: false }))
          run.mutate({ kind: 'db-isolation' })
      }} />}

      {jobs.data && <JobsPanel jobs={jobs.data} onOpen={setOutput} />}

      {restoring && (
        <TypedConfirm title={tx('Yedekten geri yükle')} word={RESTORE_CONFIRM} pending={restore.isPending} onClose={() => setRestoring(null)}
          note={tx('{0} yedeği geri yüklenir. Yedekten sonra girilen tüm veriler kalıcı olarak silinir; işlem sırasında uygulama erişilemez.', [restoring.name])}
          onConfirm={() => restore.mutate({ kind: 'restore', args: { archive: restoring.name, onlyDb }, confirm: RESTORE_CONFIRM })}>
          <label className="flex cursor-pointer items-start gap-2 text-[13px]">
            <Checkbox checked={onlyDb} onCheckedChange={(v) => setOnlyDb(v === true)} className="mt-0.5" />
            <span>{tx('Yalnızca veritabanları')}<span className="block text-[12px] text-muted-foreground">{tx('Belgeler ve logolar (nesne deposu) olduğu gibi kalır.')}</span></span>
          </label>
        </TypedConfirm>
      )}
      {rollingBack && (
        <TypedConfirm title={tx('Önceki sürüme dön')} word={ROLLBACK_CONFIRM} pending={rollback.isPending} onClose={() => setRollingBack(false)}
          note={tx('Son güncellemeden önceki sürüm yeniden kurulur. Veriler korunur (veritabanı geri yüklenmez).')}
          onConfirm={() => rollback.mutate({ kind: 'rollback', confirm: ROLLBACK_CONFIRM })} />
      )}
      {output && <JobOutput id={output} onClose={() => setOutput(null)} />}
    </div>
  )
}
