import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, ArchiveRestore, BellRing, Building2, CalendarClock, CheckCircle2, CircleAlert, DownloadCloud, HardDrive, History, Info, LoaderCircle, Puzzle, ExternalLink, RefreshCw, Rewind, ShieldCheck, Trash2, Undo2, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Callout } from '@/components/ui/callout'
import { Modal } from '@/components/ui/modal'
import { Checkbox } from '@/components/ui/checkbox'
import { TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useConfirm } from '@/components/ui/confirm'
import { useAction } from '@/features/shared/kit'
import { newerThan, pitrWindow, platformOpsApi, validPitrTarget, type OpsBackup, type OpsJob, type OpsKind, type OpsModules, type OpsMonitoring, type OpsPitr, type OpsSecurity, type OpsStatus, type OpsTenancy } from '@/api/platformOps'
import { publicSiteKey } from '@/features/site/usePublicSite'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { appLocale, tx } from '@/lib/i18n'

const KEY = ['platform-ops'] as const
const RESTORE_CONFIRM = 'GERİ YÜKLE'
const ROLLBACK_CONFIRM = 'GERİ AL'
const PITR_CONFIRM = 'GERİ DÖN'
const MODULE_OFF_CONFIRM = 'MODÜLÜ KAPAT'
const MULTI_CONFIRM = 'ÇOK ŞİRKET'
/** Bitince menüleri değiştiren işler: herkese açık site bilgisi (modül listesi, şirket kipi) tazelenir. */
const SITE_KINDS: ReadonlySet<OpsKind> = new Set(['modules-set', 'tenancy-multi', 'tenancy-single'])
// Geri dönüş anı sunucuda İstanbul saatiyle yorumlanır; ekranda da o saatle gösterilir.
const istanbulFmt = new Intl.DateTimeFormat(appLocale, { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Istanbul' })
const istanbul = (iso: string | null) => (iso && !Number.isNaN(Date.parse(iso)) ? istanbulFmt.format(new Date(iso)) : '—')

const kindLabels = (): Record<OpsKind, string> => ({
  backup: tx('Yedek al'), 'backup-verify': tx('Yedeği doğrula'), schedule: tx('Yedek zamanla'), unschedule: tx('Zamanlamayı kaldır'),
  'check-updates': tx('Güncellemeleri denetle'), update: tx('Güncelle'), rollback: tx('Önceki sürüme dön'), restore: tx('Yedekten geri yükle'),
  'db-isolation': tx('Veritabanı yalıtımını aç'),
  'pitr-enable': tx('Anlık geri dönüşü aç'), 'pitr-disable': tx('Anlık geri dönüşü kapat'), 'pitr-restore': tx('Bir ana geri dön'),
  'pitr-cleanup': tx('Eski veriyi sil'),
  'modules-set': tx('Modül seçimini uygula'), 'tenancy-multi': tx('Çok şirketli kipe geç'), 'tenancy-single': tx('Tek şirket kipine geç'),
  'monitoring-enable': tx('İzlemeyi aç'), 'monitoring-disable': tx('İzlemeyi kapat'), 'monitoring-alerts': tx('Alarm kanalını değiştir'),
  'monitoring-test': tx('Deneme alarmı gönder'), 'monitoring-tracing': tx('Dağıtık izlemeyi değiştir'),
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
                  {Array.isArray(j.args.modules) && <span className="ml-1.5 text-muted-foreground">{j.args.modules.length ? j.args.modules.join(', ') : tx('yalnızca çekirdek')}</span>}
                  {typeof j.args.channel === 'string' && <span className="ml-1.5 text-muted-foreground">{j.args.channel === 'email' ? tx('E-posta') : j.args.channel === 'slack' ? 'Slack' : 'Teams'}{j.args.value ? '' : ` · ${tx('kapatıldı')}`}</span>}
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
          <Callout tone="warning">
            {tx('Arşivleme hatası: {0} başarısız deneme (son: {1}). Disk doluluğunu ve veritabanı günlüğünü denetleyin; arşivlenemeyen değişikliklere geri dönülemez.', [String(pitr.failedCount), pitr.lastFailedWal ?? '—'])}
          </Callout>
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

/** Kartın altındaki "son işlem" satırı; tıklayınca çıktı açılır. */
function LastJobLine({ job, onOpen }: { job?: OpsJob; onOpen: (id: string) => void }) {
  if (!job) return null
  return (
    <button type="button" onClick={() => onOpen(job.id)} className="flex w-full cursor-pointer items-center gap-2 text-left text-[12px] text-muted-foreground">
      <span className="min-w-0 flex-1">{tx('Son işlem: {0} · {1}', [kindLabels()[job.kind], formatRelativeToNow(job.requestedAt)])}</span>
      {job.status === 'Running' && <LoaderCircle className="size-4 animate-spin text-primary" />}
      <StatusBadge tone={STATUS[job.status].tone}>{STATUS[job.status].label()}</StatusBadge>
    </button>
  )
}

/** Modüllerin arayüz adı ve kısa açıklaması (sunucu özetindeki ad yalnızca yedek olarak kullanılır). */
const moduleInfo = (): Record<string, { name: string; desc: string }> => ({
  recruitment: { name: tx('İşe alım'), desc: tx('İş ilanları, aday havuzu, mülakat planlama ve teklif süreci.') },
  onboarding: { name: tx('İşe alıştırma ve demirbaş'), desc: tx('Yeni çalışan karşılama planları, görev listeleri ve zimmetli demirbaş takibi.') },
  timeshift: { name: tx('Puantaj ve vardiya'), desc: tx('Vardiya planlama, giriş-çıkış kayıtları, fazla mesai ve aylık puantaj.') },
  performance: { name: tx('Performans'), desc: tx('Hedefler, değerlendirme dönemleri, geri bildirim ve yetkinlikler.') },
  learning: { name: tx('Eğitim'), desc: tx('Eğitim kataloğu, atamalar, katılım ve sertifika takibi.') },
  compensation: { name: tx('Ücret ve bordro'), desc: tx('Ücret yapıları, bordro hesaplama ve ücret değişikliği süreçleri.') },
  ml: { name: tx('Yapay zekâ'), desc: tx('Ayrılma riski tahmini, CV ve ilan analizi, aday eşleştirme ve model kartı. En çok belleği bu modül kullanır.') },
  expense: { name: tx('Masraf ve seyahat'), desc: tx('Masraf beyanı, seyahat talepleri ve onayları. Ayrı servis çalıştırmaz; yalnızca ekranları açar.') },
})

/** İsteğe bağlı modüller (scripts/modules.sh): istenen seçim işaretlenir, "Uygula" tek işte uygular. */
export function ModulesPanel({ modules, lastJob, disabled, busy, onOpenJob }: {
  modules: OpsModules; lastJob?: OpsJob; disabled: boolean; busy: boolean; onOpenJob: (id: string) => void
}) {
  const [want, setWant] = useState<Set<string>>(() => new Set(modules.enabled))
  const [open, setOpen] = useState(false)
  const run = useEnqueue(tx('İş sıraya alındı; çalıştırıcı bir dakika içinde başlatır.'), () => setOpen(false))
  const info = moduleInfo()
  const detail = (id: string) => modules.details.find((d) => d.id === id)
  const name = (id: string) => info[id]?.name ?? detail(id)?.name ?? id
  const enabled = new Set(modules.enabled)
  const order = modules.all
  const toOn = order.filter((id) => want.has(id) && !enabled.has(id))
  const toOff = order.filter((id) => !want.has(id) && enabled.has(id))
  const changed = toOn.length + toOff.length > 0
  const memOf = (ids: string[]) => ids.reduce((n, id) => n + (detail(id)?.memoryMb ?? 0), 0)
  const toggle = (id: string, on: boolean) => setWant((w) => { const n = new Set(w); if (on) n.add(id); else n.delete(id); return n })
  const submit = () => run.mutate({ kind: 'modules-set', args: { modules: order.filter((id) => want.has(id)) }, confirm: toOff.length ? MODULE_OFF_CONFIRM : undefined })
  const close = () => setOpen(false)
  const title = tx('Modül seçimini uygula')

  const summary = (
    <div className="space-y-3 text-[13px] leading-relaxed">
      {toOn.length > 0 && (
        <Callout tone="info" title={tx('Açılacak: {0}', [toOn.map(name).join(', ')])}>
          {tx('Modül imajları derlenir (modül başına birkaç dakika) ve servisler yeniden başlatılır; bu sırada uygulama kısa süre yavaşlayabilir. Ek bellek: yaklaşık {0} MB.', [String(memOf(toOn))])}
        </Callout>
      )}
      {toOff.length > 0 && (
        <Callout tone="warning" title={tx('Kapatılacak: {0}', [toOff.map(name).join(', ')])}>
          {tx('Ekranları menülerden kalkar ve servisleri durdurulur. Veriler silinmez; modül istendiği zaman yeniden açılabilir ve kaldığı yerden devam eder.')}
        </Callout>
      )}
    </div>
  )

  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Puzzle className="size-4 text-primary" />{tx('Modüller')}</span>}
        note={tx('Kullanmadığınız modülleri kapatarak sunucu belleğinden tasarruf edin. Kapatılan modülün verisi silinmez.')}
        action={<StatusBadge tone="neutral">{tx('{0}/{1} açık', [String(modules.enabled.length), String(order.length)])}</StatusBadge>} />
      <PanelBody className="space-y-4">
        <p className="flex items-start gap-2 text-[13px] text-muted-foreground"><CheckCircle2 className="mt-0.5 size-4 shrink-0 text-primary" />
          {tx('Çekirdek: çalışanlar, organizasyon, izin, onaylar, bildirimler, belgeler, KVKK — her zaman açık')}</p>
        <ul className="divide-y divide-border rounded-xl border border-border">
          {order.map((id) => {
            const on = enabled.has(id)
            const mem = detail(id)?.memoryMb
            return (
              <li key={id} className="flex items-start gap-3 px-4 py-2.5 text-[13px]">
                <Checkbox checked={want.has(id)} onCheckedChange={(v) => toggle(id, v === true)} disabled={disabled || busy || run.isPending} aria-label={name(id)} className="mt-0.5" />
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{name(id)}</span>
                  <span className="block text-[12px] text-muted-foreground">{info[id]?.desc ?? ''}</span>
                  <span className="block text-[12px] text-muted-foreground">{mem ? tx('Bellek: yaklaşık {0} MB', [String(mem)]) : tx('Ek bellek gerekmez')}</span>
                </span>
                <StatusBadge tone={on ? 'success' : 'neutral'}>{on ? tx('Açık') : tx('Kapalı')}</StatusBadge>
              </li>
            )
          })}
        </ul>
        <LastJobLine job={lastJob} onOpen={onOpenJob} />
        <div className="flex flex-wrap items-center gap-2">
          <Button disabled={!changed || disabled || busy || run.isPending} onClick={() => setOpen(true)}>{tx('Uygula')}</Button>
          {changed && <Button variant="ghost" onClick={() => setWant(new Set(modules.enabled))}>{tx('Seçimi geri al')}</Button>}
          {busy && <span className="text-[12px] text-muted-foreground">{tx('Sürmekte olan iş bitince uygulanabilir.')}</span>}
        </div>
      </PanelBody>

      {open && (toOff.length > 0 ? (
        <TypedConfirm title={title} word={MODULE_OFF_CONFIRM} pending={run.isPending} onClose={close} onConfirm={submit}
          note={tx('Seçim tek işte uygulanır; servisler yeniden başlatılır.')}>{summary}</TypedConfirm>
      ) : (
        <Modal open onClose={close} title={title} note={tx('Seçim tek işte uygulanır; servisler yeniden başlatılır.')}
          footer={<><Button variant="outline" onClick={close}>{tx('Vazgeç')}</Button><Button disabled={run.isPending} onClick={submit}>{title}</Button></>}>
          {summary}
        </Modal>
      ))}
    </Panel>
  )
}

/** Şirket kipi (scripts/tenancy.sh): tek şirket ya da çok şirketli. */
export function TenancyPanel({ tenancy, lastJob, disabled, busy, onOpenJob }: {
  tenancy: OpsTenancy; lastJob?: OpsJob; disabled: boolean; busy: boolean; onOpenJob: (id: string) => void
}) {
  const confirm = useConfirm()
  const [multi, setMulti] = useState(false)
  const run = useEnqueue(tx('İş sıraya alındı; çalıştırıcı bir dakika içinde başlatır.'), () => setMulti(false))
  const { mode, tenants } = tenancy
  const off = disabled || busy || run.isPending
  const modeLabel = { single: tx('Tek şirket'), multi: tx('Çok şirketli'), unknown: tx('Bilinmiyor') }[mode]

  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Building2 className="size-4 text-primary" />{tx('Şirket kipi')}</span>}
        note={tx('Şirket sayısı: {0}', [tenants === null ? '?' : String(tenants)])}
        action={<StatusBadge tone={mode === 'unknown' ? 'danger' : 'info'}>{modeLabel}</StatusBadge>} />
      <PanelBody className="space-y-4 text-[13px] leading-relaxed">
        {mode === 'single' && (
          <>
            <p className="text-muted-foreground">{tx('Kurulum tek bir şirket için çalışıyor: şirket listesi, yeni şirket ekleme ve herkese açık şirket kaydı gizli.')}</p>
            <Button variant="outline" disabled={off} onClick={() => setMulti(true)}>{tx('Çok şirketli kipe geç')}</Button>
          </>
        )}
        {mode === 'multi' && (
          <>
            <p className="text-muted-foreground">{tx('Kurulum birden çok şirketi barındırabilir: platform yönetimi ile şirket yönetimi ayrıdır.')}</p>
            {tenants === 1 ? (
              <Button variant="outline" disabled={off} onClick={async () => {
                if (await confirm({ title: tx('Tek şirket kipine geçilsin mi?'), note: tx('Arayüz tek şirket için sadeleşir: şirket listesi, yeni şirket ekleme ve herkese açık şirket kaydı gizlenir. Veriler değişmez; servisler yeniden başlatılır.'), action: tx('Tek şirket kipine geç'), destructive: false }))
                  run.mutate({ kind: 'tenancy-single' })
              }}>{tx('Tek şirket kipine geç')}</Button>
            ) : tenants === null ? (
              <InfoNote>{tx('Şirket sayısı okunamadı; veritabanı çalışmıyor olabilir.')}</InfoNote>
            ) : (
              <InfoNote>{tx('Bu kurulumda {0} şirket var. Tek şirket kipine yalnızca tam olarak bir şirket varken geçilebilir.', [String(tenants)])}</InfoNote>
            )}
          </>
        )}
        {mode === 'unknown' && <InfoNote>{tx('Şirket kipi okunamadı.')}</InfoNote>}
        <LastJobLine job={lastJob} onOpen={onOpenJob} />
      </PanelBody>

      {multi && (
        <TypedConfirm title={tx('Çok şirketli kipe geç')} word={MULTI_CONFIRM} pending={run.isPending} onClose={() => setMulti(false)}
          note={tx('Şirket listesi, yeni şirket ekleme ve herkese açık şirket kaydı kullanılabilir hâle gelir; platform yönetimi ile şirket yönetimi yeniden ayrılır. Servisler yeniden başlatılır.')}
          onConfirm={() => run.mutate({ kind: 'tenancy-multi', confirm: MULTI_CONFIRM })}>
          <p className="text-[12px] text-muted-foreground">{tx('Pratikte tek yönlüdür: ikinci bir şirket eklendikten sonra tek şirket kipine dönülemez.')}</p>
        </TypedConfirm>
      )}
    </Panel>
  )
}

/** İzleme yığınının yaklaşık bellek kullanımı (docker-compose.yml bellek sınırlarına göre gerçekte daha az). */
const MONITORING_MB = 1500
const EMAIL_RX = /^[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}$/
/** Arayüzdeki ön denetim; sunucu ve çalıştırıcı ayrıca doğrular. */
export function validAlertEmails(v: string): boolean {
  const list = v.split(',').map((x) => x.trim()).filter(Boolean)
  return list.length >= 1 && list.length <= 10 && list.every((x) => EMAIL_RX.test(x))
}
export const validWebhook = (v: string) => /^https:\/\/[^\s'"#$`\\]{8,500}$/.test(v.trim())

/** Tek alarm kanalı satırı: mevcut değer (webhook için yalnızca kökeni), değiştir / kapat. */
function AlertChannel({ label, hint, current, placeholder, kind, off, onSave }: {
  label: string; hint: string; current: string | null; placeholder: string; kind: 'email' | 'webhook'; off: boolean
  onSave: (value: string | null) => void
}) {
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState(kind === 'email' && current ? current : '')
  const ok = kind === 'email' ? validAlertEmails(value) : validWebhook(value)
  return (
    <li className="space-y-2 px-4 py-3 text-[13px]">
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 flex-1">
          <span className="font-medium">{label}</span>
          <span className="block truncate text-[12px] text-muted-foreground">{current ?? tx('Kapalı')}</span>
        </span>
        {!editing && <Button size="sm" variant="outline" disabled={off} aria-label={current ? tx('{0} kanalını değiştir', [label]) : tx('{0} kanalını ayarla', [label])}
          onClick={() => setEditing(true)}>{current ? tx('Değiştir') : tx('Ayarla')}</Button>}
        {!editing && current && <Button size="sm" variant="ghost" disabled={off} aria-label={tx('{0} kanalını kapat', [label])} onClick={() => onSave(null)}>{tx('Kapat')}</Button>}
      </div>
      {editing && (
        <div className="space-y-2">
          <TextField label={label} hint={hint} placeholder={placeholder} value={value} autoComplete="off" spellCheck={false}
            type={kind === 'email' ? 'text' : 'url'} onChange={(e) => setValue(e.target.value)}
            error={value && !ok ? (kind === 'email' ? tx('En fazla 10 geçerli e-posta adresi, virgülle ayrılmış') : tx('https:// ile başlayan geçerli bir adres girin')) : undefined} />
          <div className="flex gap-2">
            <Button size="sm" disabled={!ok || off} onClick={() => { onSave(value.trim()); setEditing(false) }}>{tx('Kaydet')}</Button>
            <Button size="sm" variant="ghost" onClick={() => { setEditing(false); setValue(kind === 'email' && current ? current : '') }}>{tx('Vazgeç')}</Button>
          </div>
        </div>
      )}
    </li>
  )
}

/** İzleme ve alarmlar (scripts/monitoring.sh): varsayılan kapalı; buradan açılır, kanallar ayarlanır, deneme alarmı gönderilir. */
export function MonitoringPanel({ mon, memory, lastJob, disabled, busy, onOpenJob }: {
  mon: OpsMonitoring; memory?: { total: number; available: number }; lastJob?: OpsJob; disabled: boolean; busy: boolean; onOpenJob: (id: string) => void
}) {
  const confirm = useConfirm()
  const [closing, setClosing] = useState(false)
  const [purge, setPurge] = useState(false)
  const [ratio, setRatio] = useState(mon.tracing.ratio ?? '0.1')
  const run = useEnqueue(tx('İş sıraya alındı; çalıştırıcı bir dakika içinde başlatır.'), () => setClosing(false))
  const off = disabled || run.isPending
  const lowMemory = memory ? memory.available < (MONITORING_MB + 512) * 1024 * 1024 : false
  const ratioOk = /^(0(\.\d{1,3})?|1(\.0{1,3})?)$/.test(ratio) && Number(ratio) >= 0.001
  const save = (channel: 'email' | 'slack' | 'teams') => (value: string | null) => run.mutate({ kind: 'monitoring-alerts', args: { channel, value } })
  const noChannel = !mon.alerts.email.length && !mon.alerts.slack && !mon.alerts.teams

  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Activity className="size-4 text-primary" />{tx('İzleme ve alarmlar')}</span>}
        note={tx('Servis sağlığını, disk ve bellek kullanımını, yedeklerin tazeliğini ve sertifika süresini izler; sorun olduğunda seçtiğiniz kanaldan haber verir.')}
        action={<StatusBadge tone={mon.enabled ? 'success' : 'neutral'}>{mon.enabled ? tx('Açık') : tx('Kapalı')}</StatusBadge>} />
      <PanelBody className="space-y-4 text-[13px] leading-relaxed">
        {!mon.enabled ? (
          <>
            <p className="text-muted-foreground">{tx('İzleme kapalı: bir servis çöktüğünde ya da disk dolduğunda kimseye haber verilmez. Açıldığında yaklaşık {0} GB ek bellek kullanır.', [(MONITORING_MB / 1024).toFixed(1)])}</p>
            {memory && (lowMemory ? (
              <Callout tone="warning">{tx('Sunucuda kullanılabilir bellek: {0} / {1}.', [bytes(memory.available), bytes(memory.total)])} {tx('Bellek az; izlemeyi açmadan önce kullanmadığınız modülleri kapatmayı düşünün.')}</Callout>
            ) : (
              <p className="text-muted-foreground">{tx('Sunucuda kullanılabilir bellek: {0} / {1}.', [bytes(memory.available), bytes(memory.total)])}</p>
            ))}
            <Button disabled={off || busy} onClick={async () => {
              if (await confirm({ title: tx('İzleme açılsın mı?'), note: tx('Prometheus, Grafana, Loki ve Alertmanager başlatılır (ilk açılışta imajlar indirilir). Uygulama çalışmaya devam eder.'), action: tx('Aç'), destructive: false }))
                run.mutate({ kind: 'monitoring-enable' })
            }}>{tx('İzlemeyi aç')}</Button>
          </>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <span className="min-w-0 flex-1 text-muted-foreground">
                {mon.running === null ? tx('Bileşen durumu okunamadı.') : tx('{0}/{1} bileşen çalışıyor.', [String(mon.running), String(mon.total)])}
                {' '}{mon.sso ? tx('Grafana\'ya HR360 hesabınızla girersiniz (yalnızca platform yöneticisi).') : tx('Grafana\'ya yönetici parolasıyla girilir.')}
              </span>
              <Button asChild variant="outline" size="sm"><a href="/grafana/" target="_blank" rel="noopener noreferrer"><ExternalLink className="size-4" />{tx('Grafana\'yı aç')}</a></Button>
            </div>
            {mon.running !== null && mon.running < mon.total && (
              <Callout tone="warning">{tx('Bazı izleme bileşenleri çalışmıyor. "İzlemeyi aç" ile yeniden başlatabilirsiniz.')}</Callout>
            )}
          </>
        )}

        <div className="space-y-2">
          <h3 className="flex items-center gap-2 text-[13px] font-medium"><BellRing className="size-4 text-primary" />{tx('Alarm kanalları')}</h3>
          {mon.enabled && noChannel && <InfoNote>{tx('Hiçbir alarm kanalı ayarlı değil; alarmlar yalnızca Grafana\'da görünür.')}</InfoNote>}
          <ul className="divide-y divide-border rounded-xl border border-border">
            <AlertChannel label={tx('E-posta')} kind="email" off={off} current={mon.alerts.email.length ? mon.alerts.email.join(', ') : null}
              placeholder="ops@sirket.com, it@sirket.com" hint={tx('Virgülle ayrılmış en fazla 10 adres. Platformun e-posta sunucusuyla (Sistem ayarları) gönderilir.')} onSave={save('email')} />
            <AlertChannel label="Slack" kind="webhook" off={off} current={mon.alerts.slack}
              placeholder="https://hooks.slack.com/services/…" hint={tx('Slack\'te "Incoming Webhook" uygulamasının verdiği adres.')} onSave={save('slack')} />
            <AlertChannel label="Microsoft Teams" kind="webhook" off={off} current={mon.alerts.teams}
              placeholder="https://….logic.azure.com/workflows/…" hint={tx('Teams kanalında Workflows › "Post to a channel when a webhook request is received" şablonunun verdiği adres.')} onSave={save('teams')} />
          </ul>
          <p className="text-[12px] text-muted-foreground">{tx('Webhook adresleri gizli tutulur; burada yalnızca alan adı görünür.')}{!mon.enabled ? ` ${tx('Kanallar şimdi kaydedilebilir; izleme açıldığında geçerli olur.')}` : ''}</p>
          {mon.enabled && (
            <Button size="sm" variant="outline" disabled={off || noChannel} onClick={() => run.mutate({ kind: 'monitoring-test' })}>
              <BellRing className="size-4" />{tx('Deneme alarmı gönder')}</Button>
          )}
        </div>

        {mon.enabled && (
          <div className="space-y-2">
            <h3 className="text-[13px] font-medium">{tx('Dağıtık izleme')}</h3>
            <p className="text-[12px] text-muted-foreground">{tx('Bir isteğin servisler arasındaki yolunu ve süresini kaydeder; yavaşlık incelemesi için. Kişisel veriler maskelenir, izler 3 gün sonra silinir. Açıp kapatmak servisleri yeniden başlatır.')}</p>
            {mon.tracing.on ? (
              <div className="flex flex-wrap items-center gap-3">
                <span className="flex-1">{tx('Açık (örnekleme oranı {0})', [mon.tracing.ratio ?? '1'])}</span>
                <Button size="sm" variant="ghost" disabled={off || busy} onClick={() => run.mutate({ kind: 'monitoring-tracing', args: { on: false } })}>{tx('Kapat')}</Button>
              </div>
            ) : (
              <div className="flex flex-wrap items-end gap-3">
                <div className="w-40"><TextField label={tx('Örnekleme oranı (0–1)')} inputMode="decimal" value={ratio} onChange={(e) => setRatio(e.target.value.replace(',', '.'))}
                  error={ratioOk ? undefined : tx('0,001 ile 1 arası')} /></div>
                <Button size="sm" variant="outline" disabled={off || busy || !ratioOk} onClick={() => run.mutate({ kind: 'monitoring-tracing', args: { on: true, ratio: Number(ratio) } })}>{tx('Aç')}</Button>
              </div>
            )}
          </div>
        )}

        <LastJobLine job={lastJob} onOpen={onOpenJob} />
        {mon.enabled && <Button variant="ghost" disabled={off || busy} onClick={() => { setPurge(false); setClosing(true) }}>{tx('İzlemeyi kapat')}</Button>}
      </PanelBody>

      {closing && (
        <Modal open onClose={() => setClosing(false)} title={tx('İzleme kapatılsın mı?')} note={tx('Alarmlar gönderilmez; servis çökmesi ya da disk dolması bildirilmez. Uygulama etkilenmez.')}
          footer={<><Button variant="outline" onClick={() => setClosing(false)}>{tx('Vazgeç')}</Button><Button variant="destructive" disabled={run.isPending} onClick={() => run.mutate({ kind: 'monitoring-disable', args: { purge } })}>{tx('İzlemeyi kapat')}</Button></>}>
          <label className="flex cursor-pointer items-start gap-2 text-[13px]">
            <Checkbox checked={purge} onCheckedChange={(v) => setPurge(v === true)} className="mt-0.5" />
            <span>{tx('Toplanan ölçümleri ve günlükleri de sil')}<span className="block text-[12px] text-muted-foreground">{tx('Disk açılır; geçmiş grafikler kaybolur. Alarm kanalı ayarları korunur.')}</span></span>
          </label>
        </Modal>
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
    { ok: sec.monitoring, title: tx('İzleme ve alarmlar'), note: sec.monitoring ? tx('Ölçümler toplanıyor, alarmlar açık.') : tx('İzleme kapalı; servis çökmesi ya da disk dolması bildirilmez. "İzleme ve alarmlar" bölümünden açın.') },
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
  // Modül / şirket kipi işi başarıyla bitince menüler yeni seçime göre çizilsin.
  const qc = useQueryClient()
  const seen = useRef(new Map<string, OpsStatus>())
  useEffect(() => {
    let refresh = false
    for (const j of jobs.data ?? []) {
      if (!SITE_KINDS.has(j.kind)) continue
      const prev = seen.current.get(j.id)
      if (j.status === 'Succeeded' && prev !== undefined && prev !== 'Succeeded') refresh = true
      seen.current.set(j.id, j.status)
    }
    if (refresh) void qc.invalidateQueries({ queryKey: publicSiteKey })
  }, [jobs.data, qc])

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
        <Callout tone="warning">
          {tx('Bakım çalıştırıcısı çalışmıyor{0}. Kurulumdan sonra bir kez sunucuda "scripts/ops-runner.sh install" çalıştırılmalıdır; sonraki tüm işlemler bu ekrandan yapılır.', [updatedAt ? ` (${tx('son tur {0}', [formatRelativeToNow(updatedAt)])})` : ''])}
        </Callout>
      )}
      {busy && <InfoNote>{tx('Sürmekte olan bir iş var. Güncelleme ve geri yükleme sırasında uygulama kısa süre erişilemez olabilir; sayfa kendini yeniler.')}</InfoNote>}

      <div className="grid gap-5 lg:grid-cols-2">
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><DownloadCloud className="size-4 text-primary" />{tx('Sürüm')}</span>}
            note={s ? tx('Kurulu: {0} ({1}, {2})', [s.version ?? '—', s.commit, s.commitDate]) : undefined} />
          <PanelBody className="space-y-4">
            {latest ? (
              <Callout tone="info" icon={CircleAlert} title={tx('Yeni sürüm var: {0}', [latest])}
                action={<Button disabled={disabled || busy} onClick={async () => {
                  if (await confirm({ title: tx('{0} sürümüne güncellensin mi?', [latest]), note: tx('Önce yedek alınır. Güncelleme birkaç dakika sürer; bu sırada uygulama kısa süre erişilemez olabilir.'), action: tx('Güncelle'), destructive: false }))
                    run.mutate({ kind: 'update', args: { ref: latest } })
                }}>{tx('Güncelle')}</Button>}>
                {tx('Güncellemeden önce otomatik yedek alınır; sağlık denetimi geçmezse önceki sürüme dönülür.')}
              </Callout>
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

      {s?.modules && <ModulesPanel key={s.modules.enabled.join(',')} modules={s.modules} lastJob={jobs.data?.find((j) => j.kind === 'modules-set')}
        disabled={disabled} busy={busy} onOpenJob={setOutput} />}

      {s?.tenancy && <TenancyPanel tenancy={s.tenancy} lastJob={jobs.data?.find((j) => j.kind.startsWith('tenancy-'))} disabled={disabled} busy={busy} onOpenJob={setOutput} />}

      {s?.monitoring && <MonitoringPanel key={JSON.stringify(s.monitoring)} mon={s.monitoring} memory={s.memory}
        lastJob={jobs.data?.find((j) => j.kind.startsWith('monitoring-'))} disabled={disabled} busy={busy} onOpenJob={setOutput} />}

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
