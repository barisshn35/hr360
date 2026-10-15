import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { Briefcase, CalendarDays, FileSignature, Radio, Users, Workflow } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote } from '@/components/ui/States'
import { governanceApi, streamEvents, type RadarEvent } from '@/api/governance'
import { formatDateTime, formatNumber, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { TenantGate } from '@/features/shared/kit'
import { appLocale, tx, txServer } from '@/lib/i18n'

const TOPIC: Record<string, { label: string; color: string; angle: number; icon: React.ElementType }> = {
  'hr360.employee.events': { label: tx('Çalışan'), color: '#10b981', angle: 30, icon: Users },
  'hr360.leave.events': { label: tx('İzin'), color: '#38bdf8', angle: 150, icon: CalendarDays },
  'hr360.workflow.events': { label: tx('Onay akışı'), color: '#f59e0b', angle: 270, icon: Workflow },
  'governance.internal': { label: tx('Belge imzası'), color: '#a78bfa', angle: 90, icon: FileSignature },
}

/** Olay türlerinin okunur adları (radar listesi ve akış satırı). */
const EVENT_LABEL: Record<string, string> = {
  'employee.hired': tx('İşe alım'),
  'employee.assigned': tx('Görevlendirme'),
  'employee.status-changed': tx('Çalışan durumu değişti'),
  'workflow.submitted': tx('Onaya gönderildi'),
  'workflow.approved': tx('Onaylandı'),
  'workflow.rejected': tx('Reddedildi'),
  'workflow.step-approved': tx('Onay adımı geçti'),
  'leave.approved': tx('İzin onaylandı'),
  'leave.rejected': tx('İzin reddedildi'),
  'leave.cancelled': tx('İzin iptal edildi'),
  'document.signed': tx('Belge imzalandı'),
}
const eventLabel = (t: string) => EVENT_LABEL[t] ?? t

// Diğer servislerin özetleri (ör. "1 saat fazla mesai (01.10.2026)") gg.aa.yyyy kullanır; ISO tarihler de aynı sayısal biçime çevrilir.
const NUMERIC_DATE = new Intl.DateTimeFormat(appLocale, { day: '2-digit', month: '2-digit', year: 'numeric' })
const isoToLocal = (iso: string) => {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return NUMERIC_DATE.format(new Date(y, m - 1, d))
}

const STATUS: Record<string, string> = { Active: tx('Aktif'), OnLeave: tx('İzinde'), Terminated: tx('Ayrıldı') }

/**
 * Sunucu özetleri farklı servislerden gelir: ISO tarih (2026-10-04), noktalı ondalık ("1.5 saat") ve
 * İngilizce durum adları (Active → Terminated) yerel biçime çevrilir.
 */
function localizeSummary(s: string): string {
  return s
    .replace(/\b(\d{4}-\d{2}-\d{2})T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?/g, (m) => formatDateTime(m))
    .replace(/\b\d{4}-\d{2}-\d{2}\b/g, (m) => isoToLocal(m))
    .replace(/\b(\d+\.\d+)(?=\s*(?:saat|gün|hours?|days?)\b)/g, (m) => formatNumber(Number(m)))
    .replace(/\b(Active|OnLeave|Terminated)\b/g, (m) => STATUS[m] ?? m)
}
const topicOf = (t: string) => TOPIC[t] ?? { label: t.replace('hr360.', '').replace('.events', ''), color: '#a78bfa', angle: (t.length * 47) % 360, icon: Briefcase }

function Radar({ blips }: { blips: Array<RadarEvent & { r: number; a: number }> }) {
  const reduced = useReducedMotion()
  return (
    <div className="relative mx-auto aspect-square w-full max-w-[420px]">
      <svg viewBox="-110 -110 220 220" className="absolute inset-0">
        {[30, 60, 90, 105].map((r) => <circle key={r} r={r} fill="none" stroke="hsl(var(--border))" strokeWidth={0.6} />)}
        <line x1={-105} y1={0} x2={105} y2={0} stroke="hsl(var(--border))" strokeWidth={0.4} />
        <line x1={0} y1={-105} x2={0} y2={105} stroke="hsl(var(--border))" strokeWidth={0.4} />
        {Object.values(TOPIC).map((t) => (
          <text key={t.label} x={Math.cos((t.angle * Math.PI) / 180) * 96} y={Math.sin((t.angle * Math.PI) / 180) * 96} fontSize={6} textAnchor="middle" fill={t.color} opacity={0.8}>{t.label}</text>
        ))}
      </svg>
      {!reduced && (
        <motion.div className="absolute inset-[2.5%] rounded-full" animate={{ rotate: 360 }} transition={{ repeat: Infinity, duration: 4, ease: 'linear' }}
          style={{ background: 'conic-gradient(from 0deg, hsl(var(--primary)/0.35), transparent 70deg, transparent 360deg)' }} />
      )}
      <AnimatePresence>
        {blips.map((b) => {
          const t = topicOf(b.topic)
          const x = 50 + Math.cos((b.a * Math.PI) / 180) * b.r * 0.45
          const y = 50 + Math.sin((b.a * Math.PI) / 180) * b.r * 0.45
          return (
            <motion.span key={b.id} initial={{ scale: 0, opacity: 0 }} animate={{ scale: [0, 1.8, 1], opacity: 1 }} exit={{ opacity: 0, scale: 0 }} transition={{ duration: 0.8 }}
              className="absolute size-2.5 -translate-x-1/2 -translate-y-1/2 rounded-full" style={{ left: `${x}%`, top: `${y}%`, background: t.color, boxShadow: `0 0 14px ${t.color}` }} title={localizeSummary(txServer(b.summary))}>
              <span className="absolute inset-0 animate-ping rounded-full" style={{ background: t.color, opacity: 0.4 }} />
            </motion.span>
          )
        })}
      </AnimatePresence>
      <div className="absolute inset-0 grid place-items-center"><span className="size-2 rounded-full bg-primary shadow-[0_0_20px_hsl(var(--primary))]" /></div>
    </div>
  )
}

/** Radar noktalarının renk açıklaması. */
function Legend() {
  return (
    <ul className="mt-3 flex flex-wrap justify-center gap-x-4 gap-y-1 text-[12px] text-muted-foreground" aria-label={tx('Renk açıklaması')}>
      {Object.values(TOPIC).map((t) => (
        <li key={t.label} className="flex items-center gap-1.5"><span className="size-2.5 rounded-full" style={{ background: t.color }} />{t.label}</li>
      ))}
    </ul>
  )
}

export function EventRadarPage() {
  const recent = useQuery({ queryKey: ['events', 'recent'], queryFn: ({ signal }) => governanceApi.recentEvents(60, signal) })
  const stats = useQuery({ queryKey: ['events', 'stats'], queryFn: ({ signal }) => governanceApi.eventStats(signal), refetchInterval: 30_000 })
  const [live, setLive] = useState<RadarEvent[]>([])
  const [state, setState] = useState<'connecting' | 'live' | 'error'>('connecting')
  const retry = useRef(0)

  useEffect(() => {
    const ctrl = new AbortController()
    let stopped = false
    const connect = async () => {
      while (!stopped) {
        try {
          setState('connecting')
          await streamEvents((e) => setLive((l) => [e, ...l].slice(0, 100)), ctrl.signal, () => { setState('live'); retry.current = 0 })
        } catch {
          if (stopped) return
          setState('error')
        }
        await new Promise((r) => setTimeout(r, Math.min(15000, 1000 * 2 ** retry.current++)))
      }
    }
    void connect()
    return () => { stopped = true; ctrl.abort() }
  }, [])

  const all = [...live, ...(recent.data ?? []).filter((r) => !live.some((l) => l.id === r.id))]
  const blips = all.slice(0, 24).map((e, i) => ({ ...e, a: topicOf(e.topic).angle + ((i * 37) % 50) - 25, r: 25 + ((i * 53) % 70) }))

  return (
    <TenantGate>
      <PageHeader title={tx('Canlı olay radarı')} description={tx('Kafka\'daki iş olayları gerçek zamanlı: işe alım, görevlendirme, izin onayı, onay akışı… Bir talep oluşturun, burada belirdiğini görün.')} />
      <div className="mb-5 flex flex-wrap items-center gap-3">
        <StatusBadge tone={state === 'live' ? 'success' : state === 'error' ? 'danger' : 'warning'}><Radio className="size-3" /> {state === 'live' ? tx('Canlı bağlantı') : state === 'error' ? tx('Bağlantı koptu — yeniden deneniyor') : tx('Bağlanıyor')}</StatusBadge>
        {stats.data && <span className="text-[12.5px] text-muted-foreground">{tx('Son 7 gün {0} olay · {1} canlı izleyici', [formatNumber(stats.data.byType.reduce((a, b) => a + b.count, 0)), stats.data.listeners])}</span>}
      </div>
      <div className="grid gap-6 xl:grid-cols-[440px_1fr]">
        <div className="space-y-5">
          <Panel><PanelBody><Radar blips={blips} /><Legend /></PanelBody></Panel>
          <Panel>
            <PanelHead title={tx('Olay türleri (7 gün)')} />
            <PanelBody className="space-y-2">
              {(stats.data?.byType ?? []).map((t) => {
                const max = Math.max(...(stats.data?.byType ?? []).map((x) => x.count), 1)
                return (
                  <div key={t.type} className="text-[12.5px]">
                    <div className="mb-0.5 flex justify-between"><span title={t.type}>{eventLabel(t.type)}</span><span className="tabular">{formatNumber(t.count)}</span></div>
                    <div className="h-1.5 overflow-hidden rounded-full bg-muted"><motion.div initial={{ width: 0 }} animate={{ width: `${(100 * t.count) / max}%` }} className="h-full rounded-full bg-primary" /></div>
                  </div>
                )
              })}
            </PanelBody>
          </Panel>
        </div>
        <Panel>
          <PanelHead title={tx('Akış')} note={tx('En yeni üstte')} />
          <PanelBody className="max-h-[720px] space-y-2 overflow-y-auto">
            {all.length === 0 && <InfoNote>{tx('Henüz olay yok. Örneğin bir izin talebi oluşturun ya da bir çalışanın görevlendirmesini değiştirin.')}</InfoNote>}
            <AnimatePresence initial={false}>
              {all.map((e) => {
                const t = topicOf(e.topic)
                const isNew = live.some((l) => l.id === e.id)
                return (
                  <motion.div key={e.id} layout initial={{ opacity: 0, x: 40, scale: 0.96 }} animate={{ opacity: 1, x: 0, scale: 1 }} transition={{ type: 'spring', stiffness: 300, damping: 28 }}
                    className={cn('flex items-start gap-3 rounded-xl border p-3', isNew ? 'border-primary/40 bg-primary/5' : 'border-border')}>
                    <span className="mt-0.5 grid size-8 shrink-0 place-items-center rounded-lg" style={{ background: `${t.color}22`, color: t.color }}><t.icon className="size-4" /></span>
                    <div className="min-w-0 flex-1">
                      <p className="text-[13.5px]">{localizeSummary(txServer(e.summary))}</p>
                      <p className="text-[11.5px] text-muted-foreground"><span title={e.eventType}>{eventLabel(e.eventType)}</span> · <time dateTime={e.occurredAt} title={formatDateTime(e.occurredAt)}>{formatRelativeToNow(e.occurredAt)}</time>{e.tenantSlug ? ` · ${e.tenantSlug}` : ''}</p>
                    </div>
                    {isNew && <span className="rounded-full bg-primary px-1.5 py-px text-[10px] font-semibold text-primary-foreground">{tx('YENİ')}</span>}
                  </motion.div>
                )
              })}
            </AnimatePresence>
          </PanelBody>
        </Panel>
      </div>
    </TenantGate>
  )
}
