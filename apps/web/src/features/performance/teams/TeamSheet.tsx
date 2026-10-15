/**
 * Diyagramdan açılan yan panel — ekip (ya da "ekipte olmayanlar") ayrıntısı.
 * Herkes görür; `team:manage` yetkisi olan "Yönetimde aç" ile düzenlemeye geçer.
 */

import { motion } from 'motion/react'
import { Crown, Settings2 } from 'lucide-react'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Button } from '@/components/ui/button'
import { formatDate } from '@/lib/format'
import { EASE } from '@/motion/primitives'
import { Chip } from '../components/controls'
import { PersonAvatar } from '../components/people'
import type { SheetTarget } from './OrgDiagram'
import { tx, appLocale } from '@/lib/i18n'

export function TeamSheet({
  target,
  onClose,
  canManage,
  onManage,
}: {
  target: SheetTarget | null
  onClose: () => void
  canManage: boolean
  onManage: (teamId: string) => void
}) {
  const open = target !== null
  const team = target?.kind === 'team' ? target.team : null
  const dept = target?.department

  return (
    <Sheet open={open} onOpenChange={(v) => !v && onClose()}>
      <SheetContent className="w-full gap-0 overflow-y-auto sm:max-w-md">
        {target && (
          <>
            <SheetHeader className="border-b border-border p-5">
              <p className="text-[12px] font-medium text-primary">{dept?.path}</p>
              <SheetTitle className="flex flex-wrap items-center gap-2 text-[18px]">
                {team ? team.team.name : tx('Ekipte olmayanlar')}
                {team && !team.team.isActive && <Chip>{tx('Pasif')}</Chip>}
              </SheetTitle>
              <SheetDescription className="text-[13px]">
                {team
                  ? (team.team.description ?? tx('{0} üye', [team.members.length]))
                  : tx('{0} departmanında olup hiçbir etkin ekibe üye olmayan çalışanlar.', [dept?.name])}
              </SheetDescription>
              {team && canManage && (
                <Button size="sm" variant="outline" className="mt-2 w-fit" onClick={() => onManage(team.team.id)}>
                  <Settings2 aria-hidden />
                  {tx('Yönetimde aç')}
                </Button>
              )}
            </SheetHeader>

            <div className="p-5">
              {team && (
                <div className="mb-5">
                  <p className="mb-2 text-[12px] font-semibold text-muted-foreground">{tx('Takım lideri')}</p>
                  {team.lead ? (
                    <div className="flex items-center gap-3 rounded-xl border border-primary/25 bg-primary/5 p-3">
                      <PersonAvatar id={team.lead.id} name={team.lead.name} size="md" />
                      <div className="min-w-0">
                        <p className="flex items-center gap-1.5 truncate text-[14px] font-semibold">
                          {team.lead.name}
                          <Crown className="size-3.5 text-[hsl(var(--warning))]" aria-hidden />
                        </p>
                        <p className="truncate text-[12px] text-muted-foreground">{team.lead.title ?? tx('Takım lideri')}</p>
                      </div>
                    </div>
                  ) : (
                    <p className="rounded-xl border border-dashed border-border p-3 text-[13px] text-muted-foreground">
                      {tx('Bu ekibin lideri yok. Takım lideri opsiyoneldir.')}
                    </p>
                  )}
                </div>
              )}

              <p className="mb-2 text-[12px] font-semibold text-muted-foreground">
                {team ? tx('Üyeler ({0})', [team.members.length]) : tx('Çalışanlar ({0})', [dept?.unassigned?.length ?? 0])}
              </p>
              <ul className="flex flex-col gap-1">
                {(team ? team.members : (dept?.unassigned ?? [])).map((p, i) => {
                  const member = team ? team.members.find((m) => m.id === p.id) : null
                  return (
                    <motion.li
                      key={p.id}
                      initial={{ opacity: 0, x: 12 }}
                      animate={{ opacity: 1, x: 0 }}
                      transition={{ duration: 0.4, ease: EASE, delay: 0.03 * i }}
                      className="flex items-center gap-3 rounded-lg px-2 py-2 hover:bg-muted/50"
                    >
                      <PersonAvatar id={p.id} name={p.name} />
                      <div className="min-w-0 flex-1">
                        <p className="flex items-center gap-1.5 truncate text-[13px] font-medium">
                          {p.name}
                          {member?.isLead && <Crown className="size-3 text-[hsl(var(--warning))]" aria-label={tx('Takım lideri')} />}
                        </p>
                        <p className="truncate text-[11px] text-muted-foreground">
                          {[p.title, member?.roleInTeam]
                            .filter((v, idx, arr): v is string => Boolean(v) && arr.findIndex((x) => x?.toLocaleLowerCase(appLocale) === v?.toLocaleLowerCase(appLocale)) === idx)
                            .join(' · ') || (member ? tx('Üye') : '')}
                        </p>
                      </div>
                      {member && <span className="shrink-0 text-[11px] text-muted-foreground">{formatDate(member.joinedOn)}</span>}
                    </motion.li>
                  )
                })}
                {team && team.members.length === 0 && <li className="py-6 text-center text-[13px] text-muted-foreground">{tx('Henüz üye yok.')}</li>}
              </ul>
            </div>
          </>
        )}
      </SheetContent>
    </Sheet>
  )
}
