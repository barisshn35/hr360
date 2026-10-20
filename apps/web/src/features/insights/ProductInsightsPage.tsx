import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { EyeOff, MessageSquareText, ThumbsDown, ThumbsUp } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { SelectField } from '@/components/ui/field'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { apiFetch } from '@/api/client'
import { flattenItems, navGroups } from '@/components/layout/nav-config'
import { formatNumber, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { useModules } from '@/lib/modules'

interface Insights {
  days: number
  screens: { path: string; views: number; up: number; down: number }[]
  comments: { path: string; rating: number; comment: string; roleGroup: string; createdAt: string }[]
}

const ROLE: Record<string, string> = { employee: tx('Çalışan'), manager: tx('Yönetici'), hr: tx('İK') }

/**
 * Ekran içgörüleri (yenilik haritası #33): hangi ekran ne kadar açılıyor, işe yarıyor mu, kullanıcılar ne
 * diyor. Veriler isimsizdir; ekran adları menüden çözülür.
 */
export function ProductInsightsPage() {
  const [days, setDays] = useState('30')
  const q = useQuery({ queryKey: ['product-insights', days], queryFn: ({ signal }) => apiFetch<Insights>(`/api/engagement/product/insights?days=${days}`, { signal }) })
  const menu = useMemo(() => flattenItems(navGroups).filter((i) => i.path?.startsWith('/panel')), [])
  const title = (path: string) => menu.find((m) => m.path === path)?.title ?? path
  const d = q.data
  const visited = new Set((d?.screens ?? []).filter((s) => s.views > 0).map((s) => s.path))
  // Kurulumda kapalı modüllerin ekranları "hiç açılmayan" sayılmaz.
  const { isEnabled } = useModules()
  const unused = menu.filter((m) => !visited.has(m.path!) && isEnabled(m.module ?? m.parent?.module))
  const max = Math.max(1, ...(d?.screens ?? []).map((s) => s.views))

  return (
    <div className="space-y-5">
      <PageHeader title={tx('Ekran içgörüleri')} eyebrow={[tx('İçgörü')]}
        description={tx('Hangi ekran ne kadar kullanılıyor ve çalışanlar ne diyor? Veriler isimsiz toplanır.')}
        actions={<div className="w-40"><SelectField label={tx('Dönem')} value={days} onChange={setDays}
          options={[{ value: '7', label: tx('Son 7 gün') }, { value: '30', label: tx('Son 30 gün') }, { value: '90', label: tx('Son 90 gün') }]} /></div>} />
      <InfoNote>{tx('Kişi bilgisi tutulmaz: açılışlar gün ve ekran başına sayılır, geri bildirimler isimsizdir (yalnızca çalışan, yönetici ya da İK grubu).')}</InfoNote>
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : (
        <div className="grid items-start gap-5 xl:grid-cols-[1.4fr_1fr]">
          <Panel>
            <PanelHead title={tx('Ekranlar')} note={tx('Açılma ve "işimi gördü" oyları')} />
            <PanelBody className="p-0">
              <table className="w-full text-[13px]">
                <thead className="text-left text-[11.5px] text-muted-foreground">
                  <tr><th className="px-5 py-2 font-normal">{tx('Ekran')}</th><th className="px-3 py-2 font-normal">{tx('Açılma')}</th><th className="px-3 py-2 text-right font-normal">👍</th><th className="px-5 py-2 text-right font-normal">👎</th></tr>
                </thead>
                <tbody>
                  {d!.screens.map((s) => (
                    <tr key={s.path} className="border-t border-border">
                      <td className="px-5 py-2"><span className="block font-medium">{title(s.path)}</span><span className="block text-[11.5px] text-muted-foreground">{s.path}</span></td>
                      <td className="w-48 px-3 py-2"><span className="flex items-center gap-2 tabular-nums"><span className="h-1.5 rounded-full bg-primary/70" style={{ width: `${Math.max(4, (s.views / max) * 100)}%` }} />{formatNumber(s.views)}</span></td>
                      <td className="px-3 py-2 text-right tabular-nums">{s.up || '—'}</td>
                      <td className={cn('px-5 py-2 text-right tabular-nums', s.down > s.up && 'text-destructive')}>{s.down || '—'}</td>
                    </tr>
                  ))}
                  {!d!.screens.length && <tr><td colSpan={4} className="px-5 py-6 text-center text-muted-foreground">{tx('Bu dönemde veri yok.')}</td></tr>}
                </tbody>
              </table>
            </PanelBody>
          </Panel>
          <div className="space-y-5">
            <Panel>
              <PanelHead title={<span className="flex items-center gap-2"><MessageSquareText className="size-4 text-primary" />{tx('Yorumlar')}</span>} />
              <PanelBody className="max-h-[28rem] space-y-2 overflow-y-auto">
                {!d!.comments.length ? <p className="text-[13px] text-muted-foreground">{tx('Henüz yorum yok.')}</p> : d!.comments.map((c, i) => (
                  <div key={i} className="rounded-xl bg-muted/40 px-3 py-2 text-[13px]">
                    <p className="mb-0.5 flex items-center gap-1.5 text-[11.5px] text-muted-foreground">
                      {c.rating > 0 ? <ThumbsUp className="size-3.5" /> : <ThumbsDown className="size-3.5 text-destructive" />}
                      {title(c.path)} · {ROLE[c.roleGroup] ?? c.roleGroup} · {formatRelativeToNow(c.createdAt)}
                    </p>
                    {c.comment}
                  </div>
                ))}
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={<span className="flex items-center gap-2"><EyeOff className="size-4 text-muted-foreground" />{tx('Hiç açılmayan ekranlar')}</span>}
                note={tx('Menüde olup bu dönemde açılmayanlar; sadeleştirme ya da tanıtım adayı.')} />
              <PanelBody>
                <ul className="flex flex-wrap gap-1.5">{unused.map((m) => <li key={m.path} className="rounded-full border border-border px-2.5 py-1 text-[12px]">{m.title}</li>)}</ul>
              </PanelBody>
            </Panel>
          </div>
        </div>
      )}
    </div>
  )
}
