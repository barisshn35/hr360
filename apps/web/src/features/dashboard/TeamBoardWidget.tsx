import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { BookX, Cake, CalendarOff, Inbox, MessagesSquare, PartyPopper, Sparkles, Users } from 'lucide-react'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { leaveApi } from '@/api/leave'
import { engagementApi } from '@/api/engagement'
import { learningW11Api } from '@/api/learningW11'
import { useModules } from '@/lib/modules'
import { useAuth } from '@/auth/useAuth'
import { useMyTodos } from '@/features/todos/useMyTodos'
import { localISODate } from '@/lib/dates'
import { formatDateTime } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { Panel } from '@/components/ui/panel'

function Row({ icon: Icon, label, children, to }: { icon: React.ElementType; label: string; children: React.ReactNode; to?: string }) {
  const body = (
    <div className="flex items-start gap-3 px-5 py-2.5">
      <Icon className="mt-0.5 size-4 shrink-0 text-primary" />
      <div className="min-w-0 flex-1">
        <p className="text-[11.5px] text-muted-foreground">{label}</p>
        <div className="text-[13px]">{children}</div>
      </div>
    </div>
  )
  return to ? <Link to={to} className="block transition-colors hover:bg-accent/60">{body}</Link> : body
}

/**
 * Ekip panosu (yenilik haritası #7): yöneticinin sabah ilk baktığı kart. Bugün izinde olanlar, yaklaşan
 * 1:1'ler, ekipteki kutlamalar, gecikmiş eğitimler ve bekleyen onaylar. Her bilgi kendi modülünün
 * yetki kuralıyla gelir (ör. izin türü yalnızca yöneticinin ekibinde görünür).
 */
export function TeamBoardWidget() {
  const { can } = useAuth()
  const { isEnabled, canQuery } = useModules()
  const today = localISODate()
  const cal = useQuery({ queryKey: ['leave', 'team-calendar', today, today, ''], queryFn: ({ signal }) => leaveApi.teamCalendar(today, today, undefined, signal), retry: false, staleTime: 5 * 60_000 })
  const ones = useQuery({ queryKey: ['engagement', 'one-on-ones', 'mine'], queryFn: ({ signal }) => engagementApi.oneOnOnes(undefined, signal), retry: false, staleTime: 5 * 60_000 })
  const cel = useQuery({ queryKey: ['engagement', 'celebrations', 7], queryFn: ({ signal }) => engagementApi.celebrations(7, signal), retry: false, staleTime: 30 * 60_000 })
  const due = useQuery({ queryKey: ['learning', 'overdue', 'team'], queryFn: ({ signal }) => learningW11Api.overdue(undefined, 0, signal), enabled: can('learning:view') && canQuery('learning'), retry: false, staleTime: 10 * 60_000 })
  const { items } = useMyTodos()

  const team = new Map((cal.data?.people ?? []).map((p) => [p.id, p]))
  const offToday = (cal.data?.entries ?? []).filter((e) => e.startDate <= today && e.endDate >= today && e.status === 'Approved')
  const othersOff = cal.data?.others.find((o) => o.date === today)
  const now = Date.now()
  const upcoming = (ones.data ?? []).filter((o) => o.status === 'Planned' && new Date(o.scheduledAt).getTime() >= now - 3600_000)
    .sort((a, b) => a.scheduledAt.localeCompare(b.scheduledAt)).slice(0, 3)
  const celebrations = (cel.data ?? []).filter((c) => team.has(c.employeeId))
  const approvals = items.filter((t) => t.kind === 'approval').length
  const overdue = due.data?.summary.overdue ?? 0

  return (
    <Panel className="h-full gap-0 overflow-hidden py-0">
      <div className="flex items-center justify-between px-5 pt-5 pb-2">
        <span className="flex items-center gap-2 text-[14px] font-semibold"><Users className="size-4 text-primary" />{tx('Ekibim bugün')}</span>
        {cal.data && <span className="text-[12px] text-muted-foreground">{tx('{0} kişilik ekip', [cal.data.teamSize])}</span>}
      </div>
      <div className="divide-y divide-border">
        <Row icon={CalendarOff} label={tx('Bugün izinde')} to="/panel/izin">
          {offToday.length === 0 && !othersOff ? <span className="text-muted-foreground">{tx('Herkes görevde')}</span> : (
            <span className="flex flex-wrap items-center gap-1.5">
              {offToday.map((e) => { const p = team.get(e.employeeId); return p ? (
                <span key={e.employeeId} className="flex items-center gap-1 rounded-full border border-border py-0.5 pr-2 pl-0.5"><EmployeeAvatar id={p.id} name={p.name} size={18} /><span className="text-[12px]">{p.name}</span></span>
              ) : null })}
              {othersOff && <span className="text-[12px] text-muted-foreground">{othersOff.count == null ? tx('ve diğer ekip arkadaşları') : tx('+{0} kişi', [othersOff.count])}</span>}
            </span>
          )}
        </Row>
        <Row icon={MessagesSquare} label={tx('Yaklaşan 1:1')} to="/panel/birebir">
          {upcoming.length === 0 ? <span className="text-muted-foreground">{tx('Planlı görüşme yok')}</span> : (
            <ul className="space-y-0.5">{upcoming.map((o) => <li key={o.id}>{o.employeeName} <span className="text-muted-foreground">· {formatDateTime(o.scheduledAt)}</span></li>)}</ul>
          )}
        </Row>
        {celebrations.length > 0 && (
          <Row icon={PartyPopper} label={tx('Bu hafta ekipte')} to="/panel/kutlamalar">
            <ul className="space-y-0.5">{celebrations.slice(0, 4).map((c) => (
              <li key={`${c.kind}:${c.employeeId}`} className="flex items-center gap-1.5">
                {c.kind === 'birthday' ? <Cake className="size-3.5 text-pink-400" /> : c.kind === 'newcomer' ? <Sparkles className="size-3.5 text-amber-400" /> : <PartyPopper className="size-3.5 text-primary" />}
                {c.name} <span className="text-muted-foreground">· {c.kind === 'birthday' ? tx('doğum günü') : c.kind === 'newcomer' ? tx('aramıza katılıyor') : tx('{0}. yıl', [c.years ?? ''])}{c.inDays === 0 ? ` · ${tx('bugün')}` : ''}</span>
              </li>
            ))}</ul>
          </Row>
        )}
        <div className={isEnabled('learning') ? 'grid grid-cols-2 divide-x divide-border' : 'grid grid-cols-1'}>
          <Row icon={Inbox} label={tx('Onay bekleyen')} to="/panel/onaylar"><span className="text-[18px] font-semibold tabular-nums">{approvals}</span></Row>
          {isEnabled('learning') && <Row icon={BookX} label={tx('Gecikmiş eğitim')} to={can('learning:view') ? '/panel/egitim-takibi' : undefined}><span className={overdue ? 'text-[18px] font-semibold text-destructive tabular-nums' : 'text-[18px] font-semibold tabular-nums'}>{overdue}</span></Row>}
        </div>
      </div>
    </Panel>
  )
}
