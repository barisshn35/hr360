/**
 * Y19 yetkinlik matrisi: kişisel açık analizi + öz değerlendirme + eğitim önerisi (yalnızca öneri),
 * ekip ısı haritası (departman yöneticisi kendi departmanını, İK herkesi), tanımlar ve rol profilleri (İK).
 */
import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { GraduationCap, Grid3x3, Plus, Sparkles, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { useMyEmployeeId } from '@/api/queries'
import { learningContentApi, competencyLevelLabels, type GapView } from '@/api/learningContent'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/Confirm'
import { isHr as isHrRole } from '@/auth/roles'
import { SkillGraphPanel } from './SkillGraphPanel'
import { learningW11Api } from '@/api/learningW11'

type TabKey = 'benim' | 'ekip' | 'tanimlar' | 'harita'

const sourceLabel: Record<string, string> = { Self: tx('öz'), Manager: tx('yönetici'), Hr: tx('İK') }
const levelOptions = [1, 2, 3, 4, 5].map((l) => ({ value: String(l), label: competencyLevelLabels[l]! }))

function gapClass(gap: number, current: number | null) {
  if (gap === 0) return 'bg-[hsl(var(--success))]/15 text-[hsl(var(--success))]'
  if (current === null) return 'bg-muted text-muted-foreground'
  if (gap === 1) return 'bg-[hsl(var(--warning))]/15 text-[hsl(var(--warning))]'
  if (gap === 2) return 'bg-[hsl(var(--warning))]/30 text-foreground'
  return 'bg-destructive/20 text-destructive'
}

/* ---------------------------------------------------------------- açık + öneri */

function GapPanel({ employee, canAssess, self }: { employee: string; canAssess: boolean; self: boolean }) {
  const gaps = useQuery({ queryKey: ['learning', 'gaps', employee], queryFn: ({ signal }) => learningContentApi.gaps(employee, signal) })
  const recs = useQuery({ queryKey: ['learning', 'recs', employee], queryFn: ({ signal }) => learningContentApi.recommendations(employee, signal) })
  const [assess, setAssess] = useState<{ competencyId: string; name: string; level: string; note: string } | null>(null)
  const save = useAction(
    () => learningContentApi.assess({ employeeId: gaps.data!.employeeId, competencyId: assess!.competencyId, level: Number(assess!.level), note: assess!.note || undefined }),
    { success: tx('Değerlendirme kaydedildi'), invalidate: [['learning', 'gaps'], ['learning', 'recs'], ['learning', 'team']], onDone: () => setAssess(null) },
  )
  // Dalga 11 (madde 82): öneriden kendini kaydetme (yalnızca kendi açığında; son tarih yok, otomatik kayıt yok).
  const enroll = useAction((courseId: string) => learningW11Api.enrollSelf(courseId, gaps.data!.employeeId), {
    success: tx('Eğitime kaydoldunuz'), invalidate: [['learning', 'recs'], ['learning', 'career']],
  })
  if (gaps.isPending) return <RowsSkeleton rows={4} columns={3} />
  if (gaps.isError) return <ErrorState message={errMsg(gaps.error)} onRetry={() => void gaps.refetch()} />
  const g: GapView = gaps.data
  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={self ? tx('Yetkinlik açığım') : tx('{0} · yetkinlik açığı', [g.name])}
          note={[g.positionTitle, g.department].filter(Boolean).join(' · ') || tx('Pozisyon/departman bilgisi yok')} />
        {g.items.length === 0 ? (
          <EmptyState title={tx('Rol profili tanımlı değil')} detail={tx('Pozisyonunuz ya da departmanınız için beklenen yetkinlik seviyeleri İK tarafından tanımlanınca burada görünür.')} />
        ) : (
          <ul className="divide-y divide-border">
            {g.items.map((i) => (
              <li key={i.competencyId} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13.5px]">
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{i.competency}</span>
                  <span className="block text-[12px] text-muted-foreground">
                    {tx('Beklenen {0} ({1})', [i.required, i.basis === 'Position' ? tx('pozisyon') : tx('departman')])}
                    {' · '}
                    {i.current === null ? tx('değerlendirilmedi') : tx('güncel {0} — {1}, {2}, {3}', [i.current, sourceLabel[i.source ?? ''] ?? '', i.assessedBy ?? '', i.assessedAt ? formatDate(i.assessedAt) : ''])}
                  </span>
                </span>
                {i.gap > 0 && (recs.data?.items ?? []).some((r) => r.closes.some((c) => c.competencyId === i.competencyId)) && (
                  <span className="flex flex-wrap gap-1.5" aria-label={tx('Bu açığı kapatan eğitimler')}>
                    {(recs.data?.items ?? []).filter((r) => r.closes.some((c) => c.competencyId === i.competencyId)).slice(0, 3).map((r) => (
                      <Link key={r.courseId} to={`/panel/egitim/${r.courseId}`}
                        className="inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 text-[11.5px] text-primary hover:bg-primary/5">
                        <GraduationCap className="size-3" aria-hidden />{r.title}
                      </Link>
                    ))}
                  </span>
                )}
                <span className={cn('tabular rounded-full px-2.5 py-0.5 text-[12px] font-semibold', gapClass(i.gap, i.current))}>
                  {i.gap === 0 ? tx('Karşılıyor') : tx('Açık {0}', [i.gap])}
                </span>
                {canAssess && (
                  <Button size="sm" variant="outline" onClick={() => setAssess({ competencyId: i.competencyId, name: i.competency, level: String(i.current ?? 3), note: '' })}>
                    {self ? tx('Kendimi değerlendir') : tx('Değerlendir')}
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><Sparkles className="size-4 text-primary" />{' '}{tx('Açığı kapatabilecek eğitimler')}</span>}
          action={self ? <Link to="/panel/kariyer-yollari" className="text-[12px] text-primary hover:underline">{tx('Kariyer yolum')}</Link> : undefined} />
        <PanelBody className="space-y-3">
          <InfoNote>{recs.data?.notice ?? tx('Bu liste yalnızca bir öneridir: otomatik eğitim kaydı yapılmaz ve kişi hakkında otomatik karar verilmez.')}</InfoNote>
          {recs.isPending ? <RowsSkeleton rows={2} columns={2} /> : !recs.data?.items.length ? (
            <p className="text-[13px] text-muted-foreground">{tx('Açığı kapatan etiketli bir eğitim bulunamadı.')}</p>
          ) : (
            <ul className="divide-y divide-border rounded-xl border border-border">
              {recs.data.items.map((r) => (
                <li key={r.courseId} className="flex flex-wrap items-center gap-3 px-4 py-3 text-[13.5px]">
                  <GraduationCap className="size-4 text-primary" aria-hidden />
                  <span className="min-w-0 flex-1">
                    <span className="font-medium">{r.title}</span>
                    <span className="block text-[12px] text-muted-foreground">
                      {r.closes.map((c) => tx('{0}: {1} → {2}', [c.competency, c.from, c.to])).join(' · ')}
                    </span>
                  </span>
                  {r.isMandatory && <StatusBadge tone="warning">{tx('Zorunlu')}</StatusBadge>}
                  {r.enrollmentStatus && <StatusBadge tone={r.enrollmentStatus === 'Completed' ? 'success' : 'info'}>{r.enrollmentStatus === 'Completed' ? tx('Tamamlandı') : tx('Kayıtlı')}</StatusBadge>}
                  {!r.enrollmentStatus && self && (
                    <Button size="sm" variant="outline" disabled={enroll.isPending} onClick={() => enroll.mutate(r.courseId)}>{tx('Kendimi kaydet')}</Button>
                  )}
                  <Button asChild size="sm" variant="outline"><Link to={`/panel/egitim/${r.courseId}`}>{tx('İncele')}</Link></Button>
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>

      {assess && (
        <Modal open onClose={() => setAssess(null)} title={tx('Değerlendir: {0}', [assess.name])}
          note={self ? tx('Öz değerlendirmeniz kaydedilir; yöneticiniz de değerlendirebilir. Güncel seviye en son değerlendirmedir.') : tx('Yönetici değerlendirmesi olarak kaydedilir; en son değerlendirme geçerlidir.')}
          footer={<><Button variant="outline" onClick={() => setAssess(null)}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending}>{tx('Kaydet')}</Button></>}>
          <div className="space-y-3">
            <SelectField label={tx('Seviye')} value={assess.level} onChange={(v) => setAssess({ ...assess, level: v })} options={levelOptions} />
            <TextAreaField label={tx('Not (isteğe bağlı)')} rows={2} maxLength={500} value={assess.note} onChange={(e) => setAssess({ ...assess, note: e.target.value })} hint={tx('Gözleme dayalı kısa not; sağlık gibi özel bilgi yazmayın.')} />
          </div>
        </Modal>
      )}
    </div>
  )
}

/* ---------------------------------------------------------------- ısı haritası */

function TeamHeatmap() {
  const [dept, setDept] = useState<string>('')
  const q = useQuery({ queryKey: ['learning', 'team', dept], queryFn: ({ signal }) => learningContentApi.team(dept || undefined, signal) })
  const [detail, setDetail] = useState<string | null>(null)
  if (q.isPending) return <RowsSkeleton rows={5} columns={5} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const d = q.data
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-72"><SelectField label={tx('Departman')} value={dept || '__all__'} onChange={(v) => setDept(v === '__all__' ? '' : v)}
          options={[{ value: '__all__', label: tx('Görebildiğim tüm departmanlar') }, ...d.departments.map((x) => ({ value: x.id, label: x.name }))]} /></div>
        <p className="pb-2 text-[12px] text-muted-foreground">{tx('Hücre: güncel / beklenen seviye. Gri: değerlendirilmedi.')}</p>
      </div>
      {d.rows.length === 0 || d.competencies.length === 0 ? (
        <EmptyState icon={Grid3x3} title={tx('Gösterilecek veri yok')} detail={tx('Departmanda rol profili tanımlı çalışan bulunmuyor.')} />
      ) : (
        <Panel className="overflow-x-auto">
          <table className="w-full min-w-max border-collapse text-[12.5px]">
            <thead>
              <tr className="border-b border-border">
                <th className="sticky left-0 bg-card px-4 py-2.5 text-left font-medium">{tx('Çalışan')}</th>
                {d.competencies.map((c) => <th key={c.id} className="px-2 py-2.5 text-center font-medium" title={c.category ?? ''}>{c.name}</th>)}
                <th className="px-3 py-2.5 text-right font-medium">{tx('Toplam açık')}</th>
              </tr>
            </thead>
            <tbody>
              {d.rows.map((r) => (
                <tr key={r.employeeId} className="border-b border-border last:border-0">
                  <td className="sticky left-0 bg-card px-4 py-2">
                    <button type="button" className="cursor-pointer text-left font-medium hover:underline" onClick={() => setDetail(r.employeeId)}>{r.name}</button>
                    <span className="block text-[11.5px] text-muted-foreground">{r.positionTitle ?? r.department ?? ''}</span>
                  </td>
                  {d.competencies.map((c) => {
                    const cell = r.cells.find((x) => x.competencyId === c.id)
                    return (
                      <td key={c.id} className="px-1.5 py-1.5 text-center">
                        {cell ? (
                          <span className={cn('tabular inline-block min-w-14 rounded-md px-2 py-1 font-semibold', gapClass(cell.gap, cell.current))}
                            title={tx('Beklenen {0}, güncel {1}, açık {2}', [cell.required, cell.current ?? '—', cell.gap])}>
                            {cell.current ?? '—'} / {cell.required}
                          </span>
                        ) : <span className="text-muted-foreground">·</span>}
                      </td>
                    )
                  })}
                  <td className="tabular px-3 py-2 text-right font-semibold">{r.totalGap}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>
      )}
      <InfoNote>{tx('Isı haritası gelişim planlaması için bir görünümdür; kişi hakkında otomatik karar üretmez. Görüntülemeler erişim kaydına yazılır.')}</InfoNote>
      {detail && (
        <Modal open onClose={() => setDetail(null)} size="xl" title={tx('Yetkinlik ayrıntısı')}>
          <GapPanel employee={detail} canAssess self={false} />
        </Modal>
      )}
    </div>
  )
}

/* ---------------------------------------------------------------- tanımlar (İK) */

function Definitions() {
  const comps = useQuery({ queryKey: ['learning', 'competencies'], queryFn: ({ signal }) => learningContentApi.competencies(signal) })
  const profiles = useQuery({ queryKey: ['learning', 'role-profiles'], queryFn: ({ signal }) => learningContentApi.roleProfiles(signal) })
  const org = useQuery({ queryKey: ['learning', 'org-options'], queryFn: ({ signal }) => learningContentApi.orgOptions(signal) })
  const [nc, setNc] = useState({ name: '', category: '', description: '' })
  const add = useAction(() => learningContentApi.createCompetency({ name: nc.name.trim(), category: nc.category || undefined, description: nc.description || undefined }), {
    success: tx('Yetkinlik eklendi'), invalidate: [['learning', 'competencies']], onDone: () => setNc({ name: '', category: '', description: '' }),
  })
  const del = useAction((id: string) => learningContentApi.deleteCompetency(id), { success: tx('Yetkinlik kaldırıldı'), invalidate: [['learning', 'competencies'], ['learning', 'role-profiles']] })
  const [rp, setRp] = useState({ competencyId: '', target: 'position' as 'position' | 'department', positionTitle: '', departmentId: '', requiredLevel: '3' })
  const addRp = useAction(() => learningContentApi.upsertRoleProfile({
    competencyId: rp.competencyId, requiredLevel: Number(rp.requiredLevel),
    ...(rp.target === 'position' ? { positionTitle: rp.positionTitle } : { departmentId: rp.departmentId }),
  }), { success: tx('Rol profili kaydedildi'), invalidate: [['learning', 'role-profiles'], ['learning', 'gaps'], ['learning', 'team']] })
  const delRp = useAction((id: string) => learningContentApi.deleteRoleProfile(id), { success: tx('Kaldırıldı'), invalidate: [['learning', 'role-profiles']] })
  const compName = useMemo(() => new Map((comps.data ?? []).map((c) => [c.id, c.name])), [comps.data])
  const confirm = useConfirm()
  const askDel = async (id: string, name: string) => {
    if (await confirm({ title: tx('“{0}” yetkinliği kaldırılsın mı?', [name]), note: tx('Değerlendirmesi olan yetkinlik pasifleştirilir; değerlendirmesi yoksa kalıcı olarak silinir.'), action: tx('Kaldır') })) del.mutate(id)
  }
  const askDelRp = async (id: string, name: string) => {
    if (await confirm({ title: tx('Rol profili kaldırılsın mı?'), note: tx('“{0}” için beklenen seviye tanımı silinir; açık analizleri buna göre güncellenir.', [name]), action: tx('Kaldır') })) delRp.mutate(id)
  }
  const deptName = useMemo(() => new Map((org.data?.departments ?? []).map((d) => [d.id, d.name])), [org.data])

  return (
    <div className="grid gap-5 xl:grid-cols-2">
      <Panel>
        <PanelHead title={tx('Yetkinlikler')} note={tx('Seviye ölçeği 1 (başlangıç) – 5 (uzman).')} />
        <PanelBody className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={tx('Ad')} value={nc.name} maxLength={150} onChange={(e) => setNc({ ...nc, name: e.target.value })} />
            <TextField label={tx('Kategori')} value={nc.category} maxLength={80} onChange={(e) => setNc({ ...nc, category: e.target.value })} hint={tx('ör. Teknik, Davranışsal')} />
          </div>
          <TextAreaField label={tx('Açıklama')} rows={2} value={nc.description} onChange={(e) => setNc({ ...nc, description: e.target.value })} />
          <Button size="sm" onClick={() => add.mutate(undefined)} disabled={add.isPending || nc.name.trim().length < 2}><Plus className="size-4" />{' '}{tx('Ekle')}</Button>
          <ul className="divide-y divide-border rounded-xl border border-border">
            {(comps.data ?? []).map((c) => (
              <li key={c.id} className="flex items-center gap-3 px-4 py-2.5 text-[13.5px]">
                <span className="min-w-0 flex-1">{c.name}<span className="block text-[12px] text-muted-foreground">{c.category ?? '—'}{c.description ? ` · ${c.description}` : ''}</span></span>
                <Button size="sm" variant="ghost" aria-label={tx('Kaldır')} onClick={() => askDel(c.id, c.name)}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Rol profilleri')} note={tx('Pozisyon unvanı ya da departman için beklenen seviye. Aynı yetkinlikte pozisyon tanımı departmanınkini geçersiz kılar.')} />
        <PanelBody className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <SelectField label={tx('Yetkinlik')} value={rp.competencyId} onChange={(v) => setRp({ ...rp, competencyId: v })} options={(comps.data ?? []).map((c) => ({ value: c.id, label: c.name }))} />
            <SelectField label={tx('Hedef')} value={rp.target} onChange={(v) => setRp({ ...rp, target: v as 'position' | 'department' })}
              options={[{ value: 'position', label: tx('Pozisyon unvanı') }, { value: 'department', label: tx('Departman') }]} />
            {rp.target === 'position' ? (
              <SelectField label={tx('Pozisyon')} value={rp.positionTitle} onChange={(v) => setRp({ ...rp, positionTitle: v })} options={(org.data?.positions ?? []).map((p) => ({ value: p, label: p }))} />
            ) : (
              <SelectField label={tx('Departman')} value={rp.departmentId} onChange={(v) => setRp({ ...rp, departmentId: v })} options={(org.data?.departments ?? []).map((d) => ({ value: d.id, label: d.name }))} />
            )}
            <SelectField label={tx('Beklenen seviye')} value={rp.requiredLevel} onChange={(v) => setRp({ ...rp, requiredLevel: v })} options={levelOptions} />
          </div>
          <Button size="sm" onClick={() => addRp.mutate(undefined)} disabled={addRp.isPending || !rp.competencyId || (rp.target === 'position' ? !rp.positionTitle : !rp.departmentId)}>
            <Plus className="size-4" />{' '}{tx('Kaydet')}
          </Button>
          <ul className="divide-y divide-border rounded-xl border border-border">
            {(profiles.data ?? []).map((p) => (
              <li key={p.id} className="flex items-center gap-3 px-4 py-2.5 text-[13.5px]">
                <span className="min-w-0 flex-1">{compName.get(p.competencyId) ?? '—'}<span className="block text-[12px] text-muted-foreground">
                  {p.positionTitle ? tx('Pozisyon: {0}', [p.positionTitle]) : tx('Departman: {0}', [deptName.get(p.departmentId ?? '') ?? '—'])}</span></span>
                <StatusBadge tone="info">{competencyLevelLabels[p.requiredLevel]}</StatusBadge>
                <Button size="sm" variant="ghost" aria-label={tx('Kaldır')} onClick={() => askDelRp(p.id, compName.get(p.competencyId) ?? '—')}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        </PanelBody>
      </Panel>
    </div>
  )
}

/* ---------------------------------------------------------------- sayfa */

export function CompetencyMatrixPage() {
  const { can, roles } = useAuth()
  const isHr = can('learning:manage')
  const isManager = can('employee:viewAll')
  // Beceri haritası governance'ın İK politikasıyla korunur (İK, şirket yöneticisi, yönetişim ek izni).
  const hrAdmin = isHrRole(roles, 'ext-governance-manage')
  const { employeeId, notLinked } = useMyEmployeeId()
  const [tab, setTab] = useTabParam<TabKey>('gorunum', notLinked && isManager ? 'ekip' : 'benim')
  const tabs: Array<TabDef<TabKey>> = [
    ...(!notLinked ? [{ key: 'benim' as TabKey, label: tx('Benim yetkinliklerim') }] : []),
    ...(isManager ? [{ key: 'ekip' as TabKey, label: tx('Ekip ısı haritası') }] : []),
    ...(isHr ? [{ key: 'tanimlar' as TabKey, label: tx('Tanımlar ve rol profilleri') }] : []),
    ...(hrAdmin ? [{ key: 'harita' as TabKey, label: tx('Beceri haritası') }] : []),
  ]
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Yetkinlikler')} description={tx('Rol profiline göre yetkinlik açıkları, ekip ısı haritası ve açığı kapatabilecek eğitim önerileri.')} />
      <Tabs tabs={tabs} value={tab} onChange={setTab} label={tx('Yetkinlik görünümü')} />
      {tab === 'benim' && (employeeId ? <GapPanel employee="me" canAssess self /> : notLinked ? <EmptyState title={tx('Çalışan kaydı yok')} detail={tx('Bu hesap bir çalışan kaydına bağlı değil.')} /> : <RowsSkeleton rows={4} />)}
      {tab === 'ekip' && isManager && <TeamHeatmap />}
      {tab === 'tanimlar' && isHr && <Definitions />}
      {tab === 'harita' && hrAdmin && <SkillGraphPanel />}
    </div>
  )
}
