import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Building2, Medal, Trophy } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { SelectField } from '@/components/ui/Field'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { eomApi, eomKeys, periodLabel, type EomLeaderRow } from '@/api/eom'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const MEDAL = ['text-amber-400', 'text-slate-300', 'text-orange-400']
const PODIUM_H = ['h-24', 'h-16', 'h-12']

/**
 * İlk üç için kürsü (21st.dev "Leaderboard Podium" kalıbı: ortada birinci, solda ikinci, sağda üçüncü).
 * Eşit kazanma sayısında sıra paylaşılır; kürsüde sıraya göre madalya rengi kullanılır.
 */
function Podium({ rows }: { rows: EomLeaderRow[] }) {
  const top = rows.slice(0, 3)
  const order = top.length === 3 ? [top[1], top[0], top[2]] : top.length === 2 ? [top[1], top[0]] : top
  return (
    <div className="flex items-end justify-center gap-3 pt-2 sm:gap-6">
      {order.map((r) => {
        const place = Math.min(r.rank, 3) - 1
        return (
          <motion.div key={r.person.id} initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.08 * (2 - place) }}
            className="flex w-24 flex-col items-center gap-1.5 text-center sm:w-32">
            <span className="relative">
              <EmployeeAvatar id={r.person.id} name={r.person.name} version={r.person.photoVersion} size={place === 0 ? 72 : 56}
                className={cn('ring-2', place === 0 ? 'ring-amber-400' : place === 1 ? 'ring-slate-300' : 'ring-orange-400')} />
              <Medal className={cn('absolute -right-1 -bottom-1 size-5 drop-shadow', MEDAL[place])} />
            </span>
            <span className="line-clamp-2 text-[12.5px] font-semibold leading-tight">{r.person.name}</span>
            <div className={cn('flex w-full flex-col items-center justify-start rounded-t-xl border border-b-0 border-border bg-gradient-to-b pt-2',
              PODIUM_H[place], place === 0 ? 'from-amber-400/20' : place === 1 ? 'from-slate-300/15' : 'from-orange-400/15', 'to-transparent')}>
              <span className="text-[18px] font-bold tabular-nums">{r.wins}</span>
              <span className="text-[10.5px] text-muted-foreground">{tx('kez')}</span>
            </div>
          </motion.div>
        )
      })}
    </div>
  )
}

function Rows({ rows, showSub }: { rows: EomLeaderRow[]; showSub: boolean }) {
  return (
    <table className="w-full text-[13px]">
      <thead className="text-left text-[11.5px] text-muted-foreground">
        <tr>
          <th className="w-10 py-1.5 font-normal">#</th>
          <th className="py-1.5 font-normal">{tx('Kişi')}</th>
          <th className="py-1.5 text-right font-normal">{tx('Kazanma')}</th>
          {showSub && <th className="py-1.5 text-right font-normal" title={tx('İki aşamalı oylamada alt birim kazanma sayısı')}>{tx('Alt birim')}</th>}
          <th className="hidden py-1.5 text-right font-normal sm:table-cell">{tx('Son')}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.person.id} className="border-t border-border">
            <td className="py-2">
              {r.rank <= 3 ? <Medal className={cn('size-4', MEDAL[r.rank - 1])} aria-label={String(r.rank)} /> : <span className="text-muted-foreground tabular-nums">{r.rank}</span>}
            </td>
            <td className="py-2">
              <span className="flex items-center gap-2">
                <EmployeeAvatar id={r.person.id} name={r.person.name} version={r.person.photoVersion} size={26} />
                <span className="min-w-0">
                  <span className="block truncate font-medium">{r.person.name}</span>
                  <span className="block truncate text-[11.5px] text-muted-foreground">{r.person.department ?? r.person.position ?? ''}</span>
                </span>
              </span>
            </td>
            <td className="py-2 text-right font-semibold tabular-nums">{r.wins}</td>
            {showSub && <td className="py-2 text-right tabular-nums text-muted-foreground">{r.subWins || '—'}</td>}
            <td className="hidden py-2 text-right text-muted-foreground sm:table-cell">{periodLabel(r.lastWonKey)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

/** Kazananlar sıralaması: şirket geneli (ayın çalışanı seçilme sayısı) ve departman içi. */
export function EomLeaderboard() {
  const q = useQuery({ queryKey: eomKeys.leaderboard, queryFn: ({ signal }) => eomApi.leaderboard(signal) })
  const [tab, setTab] = useState<'company' | 'department'>('company')
  const [dept, setDept] = useState<string>('')
  const d = q.data
  if (!d?.enabled || !d.company?.length) return null
  const departments = d.departments ?? []
  const current = departments.find((x) => x.departmentId === dept) ?? departments.find((x) => x.isMine) ?? departments[0]
  const rows = tab === 'company' ? d.company : current?.rows ?? []
  const showSub = tab === 'company' && rows.some((r) => r.subWins > 0)

  return (
    <Panel>
      <PanelHead
        title={<span className="flex items-center gap-2"><Trophy className="size-4 text-amber-400" />{tx('Kazananlar sıralaması')}</span>}
        note={tx('{0} sonuçlanmış oylamaya göre.', [d.periods])}
        action={
          <div role="tablist" aria-label={tx('Sıralama kapsamı')} className="inline-flex rounded-lg border border-border p-0.5 text-[12.5px]">
            {([['company', tx('Şirket geneli')], ['department', tx('Departman')]] as const).map(([k, label]) => (
              <button key={k} type="button" role="tab" aria-selected={tab === k} onClick={() => setTab(k)}
                className={cn('cursor-pointer rounded-md px-2.5 py-1 transition-colors', tab === k ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:text-foreground')}>
                {label}
              </button>
            ))}
          </div>
        } />
      <PanelBody className="space-y-5">
        {tab === 'department' && departments.length > 0 && (
          <div className="max-w-xs">
            <SelectField label={tx('Departman')} value={current?.departmentId ?? ''} onChange={setDept}
              options={departments.map((x) => ({ value: x.departmentId, label: x.isMine ? tx('{0} (sizin)', [x.name]) : x.name }))} />
          </div>
        )}
        {rows.length === 0 ? (
          <p className="flex items-center gap-2 text-[13px] text-muted-foreground"><Building2 className="size-4" />{tx('Bu departmanda henüz kazanan yok.')}</p>
        ) : (
          <>
            <Podium rows={rows} />
            <Rows rows={rows} showSub={showSub} />
          </>
        )}
      </PanelBody>
    </Panel>
  )
}
