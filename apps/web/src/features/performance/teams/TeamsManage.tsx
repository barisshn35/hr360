/**
 * Ekip yönetimi (team:manage): solda hiyerarşi gezgini, sağda seçili ekip.
 *
 * Üye çıkarmak kaydı silmez — ayrılma tarihi yazılır; "Eski üyeleri göster"
 * ile geçmiş üyelikler görünür. Lider opsiyoneldir; atanınca otomatik üye olur.
 * Bir çalışan birden fazla ekipte olabilir; aynı ekibe ikinci kez eklenemez.
 */

import { useMemo, useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { ChevronDown, CornerDownRight, Crown, Network, Pencil, Plus, Power, Search, UserMinus, UserPlus, Users } from 'lucide-react'
import { useTeam, useUpdateTeam, type Team, type TeamMember } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { Chip, Switch, errorText } from '../components/controls'
import { PersonAvatar } from '../components/people'
import { usePeople, type DeptNode } from '../hooks'
import { AddMemberDialog, LeadDialog, RemoveMemberDialog, TeamFormDialog } from './TeamDialogs'
import { walkDepartments, type OrgCompany, type OrgDepartment } from './useOrgTree'
import { tx, appLocale } from '@/lib/i18n'

type Dialog =
  | { kind: 'create'; departmentId?: string | null }
  | { kind: 'edit'; team: Team }
  | { kind: 'lead'; team: Team; members: TeamMember[] }
  | { kind: 'add'; team: Team; members: TeamMember[] }
  | { kind: 'remove'; team: Team; member: TeamMember }

export function TeamsManage({
  tree,
  departments,
  selectedId,
  onSelect,
  showInactive,
  onShowInactive,
  onShowInDiagram,
}: {
  tree: OrgCompany[]
  departments: DeptNode[]
  selectedId: string | null
  onSelect: (id: string | null) => void
  showInactive: boolean
  onShowInactive: (v: boolean) => void
  onShowInDiagram: (teamId: string) => void
}) {
  const [q, setQ] = useState('')
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const people = usePeople()

  const needle = q.trim().toLocaleLowerCase(appLocale)
  const allDepts = tree.flatMap((c) => walkDepartments(c.departments))
  const allTeams = allDepts.flatMap((d) => d.teams)
  const pathOf = (id: string | null) => departments.find((d) => d.id === id)?.path ?? null
  const detailOf = (id: string) => [people.titleOf(id), pathOf(people.departmentOf(id))].filter(Boolean).join(' · ') || null
  const otherTeamOf = (teamId: string) => (employeeId: string) => {
    const t = allTeams.find((x) => x.team.id !== teamId && x.team.isActive && x.members.some((m) => m.id === employeeId))
    return t ? `${t.team.name} ekibinde` : null
  }

  const matches = (d: OrgDepartment): boolean =>
    !needle || d.teams.some((t) => t.team.name.toLocaleLowerCase(appLocale).includes(needle)) || d.children.some(matches)

  const toggle = (id: string) =>
    setCollapsed((s) => {
      const n = new Set(s)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
    })

  const navDept = (d: OrgDepartment): React.ReactNode => {
    if (!matches(d)) return null
    const teams = d.teams.filter((t) => !needle || t.team.name.toLocaleLowerCase(appLocale).includes(needle))
    const isCollapsed = collapsed.has(d.id)
    return (
      <div key={d.id} className="mb-0.5">
        <div className="group flex items-center">
          <button
            type="button"
            onClick={() => toggle(d.id)}
            aria-expanded={!isCollapsed}
            className="flex min-w-0 flex-1 items-center gap-1.5 rounded-md px-2 py-1.5 text-left text-[13px] font-medium hover:bg-muted/60"
          >
            <ChevronDown className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform', isCollapsed && '-rotate-90')} aria-hidden />
            {d.depth > 0 && <CornerDownRight className="size-3 shrink-0 text-muted-foreground" aria-label={tx('Alt departman')} />}
            <span className="truncate">{d.name}</span>
            <span className="tabular ml-auto text-[11px] font-normal text-muted-foreground">{d.teamCount}</span>
          </button>
          <button
            type="button"
            onClick={() => setDialog({ kind: 'create', departmentId: d.id })}
            className="rounded p-1 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100 hover:bg-muted focus-visible:opacity-100"
            aria-label={tx('{0} departmanına ekip ekle', [d.name])}
            title={tx('Bu departmana ekip ekle')}
          >
            <Plus className="size-3.5" />
          </button>
        </div>
        <AnimatePresence initial={false}>
          {!isCollapsed && (
            <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} className="overflow-hidden pl-4">
              {d.children.map((c) => navDept(c))}
              <ul>
                {teams.length === 0 && d.children.length === 0 && <li className="px-2 py-1.5 text-[12px] text-muted-foreground">{tx('Ekip yok')}</li>}
                {teams.map((t) => {
                  const on = t.team.id === selectedId
                  return (
                    <li key={t.team.id}>
                      <button
                        type="button"
                        onClick={() => onSelect(t.team.id)}
                        className={cn(
                          'relative flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-[13px] transition-colors',
                          on ? 'text-foreground' : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground',
                        )}
                      >
                        {on && (
                          <motion.span layoutId="team-nav-sel" className="absolute inset-0 rounded-md bg-primary/10 ring-1 ring-primary/25" transition={{ type: 'spring', stiffness: 500, damping: 38 }} />
                        )}
                        <span className={cn('relative truncate', !t.team.isActive && 'line-through decoration-muted-foreground/50')}>{t.team.name}</span>
                        {!t.team.isActive && <Chip className="relative">{tx('Pasif')}</Chip>}
                        <span className="relative ml-auto flex items-center gap-1 text-[11px]">
                          {t.lead && <Crown className="size-3 text-[hsl(var(--warning))]" aria-label={tx('Lideri var')} />}
                          <span className="tabular">{t.members.length}</span>
                        </span>
                      </button>
                    </li>
                  )
                })}
              </ul>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    )
  }

  return (
    <div className="grid items-start gap-5 lg:grid-cols-[300px_minmax(0,1fr)]">
      {/* ------------------------------ hiyerarşi gezgini ------------------------------ */}
      <Panel className="lg:sticky lg:top-20">
        <div className="flex items-center justify-between gap-2 border-b border-border px-4 py-3">
          <p className="text-[13px] font-semibold">{tx('Hiyerarşi')}</p>
          <Button size="xs" onClick={() => setDialog({ kind: 'create' })}>
            <Plus aria-hidden />
            {tx('Yeni ekip')}
          </Button>
        </div>
        <div className="flex flex-col gap-3 border-b border-border p-3">
          <label className="relative block">
            <span className="sr-only">{tx('Ekip ara')}</span>
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden />
            <input
              value={q}
              onChange={(e) => setQ(e.target.value)}
              placeholder={tx('Ekip ara')}
              className="h-8 w-full rounded-md border border-input bg-background pr-2 pl-8 text-[12px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
            />
          </label>
          <Switch checked={showInactive} onChange={onShowInactive} label={<span className="text-[12px]">{tx('Pasif ekipleri göster')}</span>} />
        </div>
        <nav aria-label={tx('Ekipler')} className="max-h-[60vh] overflow-y-auto p-2">
          {tree.map((c) => (
            <div key={c.id}>
              <p className="px-2 pt-1 pb-2 text-[11px] font-semibold tracking-wide text-muted-foreground">{c.name}</p>
              {c.departments.map((d) => navDept(d))}
            </div>
          ))}
        </nav>
      </Panel>

      {/* ------------------------------ seçili ekip ------------------------------ */}
      <div className="min-w-0">
        {selectedId ? (
          <TeamDetail
            key={selectedId}
            teamId={selectedId}
            departmentPath={(id) => pathOf(id) ?? '—'}
            onEdit={(team) => setDialog({ kind: 'edit', team })}
            onLead={(team, members) => setDialog({ kind: 'lead', team, members })}
            onAdd={(team, members) => setDialog({ kind: 'add', team, members })}
            onRemove={(team, member) => setDialog({ kind: 'remove', team, member })}
            onShowInDiagram={onShowInDiagram}
          />
        ) : allTeams.length === 0 ? (
          <Panel>
            <EmptyState
              icon={Users}
              title={tx('Henüz ekip yok — ilk ekibi oluşturun')}
              detail={tx('Ekipler departmanlara bağlıdır. Takım lideri opsiyoneldir; lider atanınca kişi otomatik olarak ekibe üye olur.')}
              action={
                <Button onClick={() => setDialog({ kind: 'create' })}>
                  <Plus aria-hidden />
                  {tx('Yeni ekip')}
                </Button>
              }
            />
          </Panel>
        ) : (
          <Panel>
            <EmptyState icon={Network} title={tx('Soldan bir ekip seçin')} detail={tx('Seçtiğiniz ekibin lideri, üyeleri ve geçmiş üyelikleri burada görünür.')} />
          </Panel>
        )}
      </div>

      {/* ------------------------------ diyaloglar ------------------------------ */}
      {dialog?.kind === 'create' && (
        <TeamFormDialog team={null} departments={departments} defaultDepartmentId={dialog.departmentId} onClose={() => setDialog(null)} onSaved={(t) => onSelect(t.id)} />
      )}
      {dialog?.kind === 'edit' && <TeamFormDialog team={dialog.team} departments={departments} onClose={() => setDialog(null)} />}
      {dialog?.kind === 'lead' && (
        <LeadDialog team={dialog.team} members={dialog.members} people={people.list} nameOf={people.nameOf} detailOf={detailOf} onClose={() => setDialog(null)} />
      )}
      {dialog?.kind === 'add' && (
        <AddMemberDialog
          team={dialog.team}
          members={dialog.members}
          people={people.list}
          nameOf={people.nameOf}
          detailOf={detailOf}
          otherTeamOf={otherTeamOf(dialog.team.id)}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog?.kind === 'remove' && (
        <RemoveMemberDialog team={dialog.team} member={dialog.member} name={people.nameOf(dialog.member.employeeId, dialog.member.employeeName)} onClose={() => setDialog(null)} />
      )}
    </div>
  )
}

/* ---------------------------------- ekip ayrıntısı --------------------------------- */

function TeamDetail({
  teamId,
  departmentPath,
  onEdit,
  onLead,
  onAdd,
  onRemove,
  onShowInDiagram,
}: {
  teamId: string
  departmentPath: (id: string) => string
  onEdit: (team: Team) => void
  onLead: (team: Team, members: TeamMember[]) => void
  onAdd: (team: Team, members: TeamMember[]) => void
  onRemove: (team: Team, member: TeamMember) => void
  onShowInDiagram: (teamId: string) => void
}) {
  const toast = useToast()
  const [showFormer, setShowFormer] = useState(false)
  const q = useTeam(teamId, true)
  const update = useUpdateTeam()
  const people = usePeople()

  const members = useMemo(() => q.data?.members ?? [], [q.data])
  const active = members.filter((m) => !m.leftOn)
  const former = members.filter((m) => m.leftOn)
  const visible = (showFormer ? [...active, ...former] : active).slice().sort((a, b) => {
    if (Boolean(a.leftOn) !== Boolean(b.leftOn)) return a.leftOn ? 1 : -1
    const la = a.employeeId === q.data?.leadEmployeeId
    const lb = b.employeeId === q.data?.leadEmployeeId
    if (la !== lb) return la ? -1 : 1
    return people.nameOf(a.employeeId, a.employeeName).localeCompare(people.nameOf(b.employeeId, b.employeeName), 'tr-TR')
  })

  if (q.isPending) {
    return (
      <Panel className="p-5" aria-busy="true">
        <Skeleton className="h-3 w-24" />
        <Skeleton className="mt-2 h-6 w-56" />
        <Skeleton className="mt-6 h-16 w-full rounded-xl" />
        {[0, 1, 2, 3].map((i) => (
          <div key={i} className="mt-4 flex items-center gap-3">
            <Skeleton className="size-8 rounded-full" />
            <Skeleton className="h-4 flex-1" />
          </div>
        ))}
      </Panel>
    )
  }
  if (q.isError || !q.data) {
    return (
      <Panel>
        <ErrorState title={tx('Ekip alınamadı')} message={errorText(q.error)} onRetry={() => void q.refetch()} />
      </Panel>
    )
  }

  const team = q.data
  const lead = team.leadEmployeeId
  const toggleActive = () =>
    update.mutate(
      { id: team.id, input: { name: team.name, description: team.description, isActive: !team.isActive } },
      {
        onSuccess: () => toast.ok(team.isActive ? tx('«{0}» pasife alındı. Üyelik geçmişi korunuyor.', [team.name]) : tx('«{0}» yeniden etkin.', [team.name])),
        onError: (e) => toast.stop(errorText(e)),
      },
    )

  return (
    <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.45, ease: EASE }}>
      <Panel>
        {/* başlık */}
        <div className="flex flex-col gap-3 border-b border-border p-5 sm:flex-row sm:items-start sm:justify-between">
          <div className="min-w-0">
            <p className="text-[12px] font-medium text-primary">{departmentPath(team.departmentId)}</p>
            <h2 className="mt-0.5 flex flex-wrap items-center gap-2 text-[20px] font-semibold tracking-tight">
              {team.name}
              {team.isActive ? <Chip tone="success">{tx('Etkin')}</Chip> : <Chip>{tx('Pasif')}</Chip>}
            </h2>
            {team.description && <p className="mt-1 max-w-xl text-[13px] text-muted-foreground">{team.description}</p>}
            <p className="tabular mt-2 text-[12px] text-muted-foreground">
              {tx('{0} etkin üye · {1} eski üye', [active.length, former.length])}</p>
          </div>
          <div className="flex shrink-0 flex-wrap gap-2">
            <Button size="sm" variant="ghost" onClick={() => onShowInDiagram(team.id)}>
              <Network aria-hidden />
              {tx('Diyagramda göster')}
            </Button>
            <Button size="sm" variant="outline" onClick={() => onEdit(team)}>
              <Pencil aria-hidden />
              {tx('Düzenle')}
            </Button>
            <Button size="sm" variant="outline" onClick={toggleActive} disabled={update.isPending}>
              <Power aria-hidden />
              {team.isActive ? tx('Pasife al') : tx('Etkinleştir')}
            </Button>
          </div>
        </div>

        {/* lider */}
        <div className="border-b border-border p-5">
          <p className="mb-2 text-[12px] font-semibold text-muted-foreground">{tx('Takım lideri')}</p>
          {lead ? (
            <div className="flex flex-col gap-3 rounded-xl border border-primary/25 bg-primary/5 p-3 sm:flex-row sm:items-center">
              <PersonAvatar id={lead} name={people.nameOf(lead)} size="lg" />
              <div className="min-w-0 flex-1">
                <p className="flex items-center gap-1.5 text-[15px] font-semibold">
                  {people.nameOf(lead)}
                  <Crown className="size-4 text-[hsl(var(--warning))]" aria-hidden />
                </p>
                <p className="text-[12px] text-muted-foreground">{people.titleOf(lead) ?? tx('Takım lideri')}</p>
              </div>
              <Button size="sm" variant="outline" onClick={() => onLead(team, members)} disabled={!team.isActive}>
                {tx('Lideri değiştir')}
              </Button>
            </div>
          ) : (
            <div className="flex flex-col gap-3 rounded-xl border border-dashed border-border p-4 sm:flex-row sm:items-center sm:justify-between">
              <p className="text-[13px] text-muted-foreground">
                {tx('Bu ekibin lideri yok.')}{' '}<span className="text-foreground">{tx('Takım lideri opsiyoneldir;')}</span>{' '}{tx('atanan kişi otomatik olarak üye olur.')}
              </p>
              <Button size="sm" onClick={() => onLead(team, members)} disabled={!team.isActive}>
                <Crown aria-hidden />
                {tx('Lider ata')}
              </Button>
            </div>
          )}
        </div>

        {/* üyeler */}
        <div className="p-5">
          <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
            <p className="text-[13px] font-semibold">{tx('Üyeler')}</p>
            <div className="flex items-center gap-4">
              <Switch checked={showFormer} onChange={setShowFormer} label={<span className="text-[12px]">{tx('Eski üyeleri göster ({0})', [former.length])}</span>} />
              <Button size="sm" onClick={() => onAdd(team, members)} disabled={!team.isActive} title={team.isActive ? undefined : tx('Pasif ekibe üye eklenemez')}>
                <UserPlus aria-hidden />
                {tx('Üye ekle')}
              </Button>
            </div>
          </div>

          {visible.length === 0 ? (
            <EmptyState
              icon={Users}
              title={tx('Bu ekipte henüz üye yok')}
              detail={tx('Üye ekleyin ya da bir lider atayın; lider otomatik olarak üye olur.')}
              action={
                team.isActive ? (
                  <Button size="sm" onClick={() => onAdd(team, members)}>
                    <UserPlus aria-hidden />
                    {tx('Üye ekle')}
                  </Button>
                ) : undefined
              }
            />
          ) : (
            <ul className="divide-y divide-border rounded-xl border border-border">
              <AnimatePresence initial={false}>
                {visible.map((m) => {
                  const name = people.nameOf(m.employeeId, m.employeeName)
                  const isLead = m.employeeId === lead && !m.leftOn
                  return (
                    <motion.li
                      key={m.id}
                      layout
                      initial={{ opacity: 0, height: 0 }}
                      animate={{ opacity: 1, height: 'auto' }}
                      exit={{ opacity: 0, height: 0 }}
                      transition={{ duration: 0.3, ease: EASE }}
                      className={cn('group overflow-hidden', m.leftOn && 'bg-muted/30')}
                    >
                      <div className="flex items-center gap-3 px-3 py-2.5">
                        <PersonAvatar id={m.employeeId} name={name} className={cn(m.leftOn && 'opacity-50 grayscale')} />
                        <div className="min-w-0 flex-1">
                          <p className={cn('flex flex-wrap items-center gap-1.5 text-[13px] font-medium', m.leftOn && 'text-muted-foreground')}>
                            {name}
                            {isLead && (
                              <Chip tone="warning">
                                <Crown className="size-3" aria-hidden />
                                {tx('Lider')}
                              </Chip>
                            )}
                            {m.roleInTeam && !isLead && <Chip>{m.roleInTeam}</Chip>}
                          </p>
                          <p className="truncate text-[11px] text-muted-foreground">
                            {tx('{0}{1} tarihinden{2}', [people.titleOf(m.employeeId) ? `${people.titleOf(m.employeeId)} · ` : '', formatDate(m.joinedOn), m.leftOn ? tx(' {0} tarihine kadar', [formatDate(m.leftOn)]) : tx(' beri')])}
                          </p>
                        </div>
                        {m.leftOn ? (
                          <Chip>{tx('Ayrıldı · {0}', [formatDate(m.leftOn)])}</Chip>
                        ) : (
                          <Button
                            size="sm"
                            variant="ghost"
                            onClick={() => onRemove(team, m)}
                            className="text-muted-foreground sm:opacity-0 sm:transition-opacity sm:group-focus-within:opacity-100 sm:group-hover:opacity-100"
                            aria-label={tx('{0} kişisini ekipten çıkar', [name])}
                          >
                            <UserMinus aria-hidden />
                            <span className="hidden sm:inline">{tx('Ekipten çıkar')}</span>
                          </Button>
                        )}
                      </div>
                    </motion.li>
                  )
                })}
              </AnimatePresence>
            </ul>
          )}
          <p className="mt-3 text-[11px] text-muted-foreground">{tx('Ekipten çıkarmak kaydı silmez; ayrılma tarihi yazılır ve geçmiş analizler korunur.')}</p>
        </div>
      </Panel>
    </motion.div>
  )
}
