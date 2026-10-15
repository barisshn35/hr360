/**
 * Dalga 11 (madde 83) kariyer yolları: rol basamakları ve basamak başına beklenen yetkinlik seviyeleri.
 * Çalışan kendi basamağını (güncel unvanından), sonraki basamağın açığını ve açığı kapatabilecek eğitimleri görür;
 * İK yolları düzenler. Hazırlık oranı yalnızca bilgi amaçlıdır — terfi ya da atama otomatik yapılmaz.
 */
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ChevronRight, GraduationCap, Plus, Route as RouteIcon, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { useMyEmployeeId } from '@/api/queries'
import { learningContentApi, competencyLevelLabels } from '@/api/learningContent'
import { learningW11Api, type CareerPath, type CareerPathInput, type StepProgress } from '@/api/learningW11'
import { errMsg, useAction } from '@/features/shared/kit'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

type TabKey = 'benim' | 'yollar'

const levelOptions = [1, 2, 3, 4, 5].map((l) => ({ value: String(l), label: competencyLevelLabels[l]! }))

/** Basamakta yetkinlik beklentisi tanımlı değil mi (ayrıntısı yüklü olan hedef/güncel basamak için bilinir). */
function noExpectations(p: { target: { id: string; items: unknown[] } | null; currentStep: { id: string; items: unknown[] } | null }, stepId: string) {
  const s = p.target?.id === stepId ? p.target : p.currentStep?.id === stepId ? p.currentStep : null
  return s !== null && s.items.length === 0
}

function readinessTone(r: number) {
  return r >= 100 ? 'success' : r >= 60 ? 'info' : 'warning'
}

/* ---------------------------------------------------------------- basamak ayrıntısı */

function StepDetail({ step, employeeId, self }: { step: StepProgress; employeeId: string; self: boolean }) {
  const enroll = useAction((courseId: string) => learningW11Api.enrollSelf(courseId, employeeId), {
    success: tx('Eğitime kaydoldunuz'),
    invalidate: [['learning', 'career'], ['learning', 'recs']],
  })
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2 text-[13.5px]">
        <span className="font-medium">{step.positionTitle}</span>
        {/* Beklenti tanımlanmamış basamakta "%100 hazır" yanıltıcı olur. */}
        {step.items.length === 0
          ? <StatusBadge tone="neutral">{tx('Beklenti tanımlanmadı')}</StatusBadge>
          : <StatusBadge tone={readinessTone(step.readiness)}>{tx('Hazırlık %{0}', [step.readiness])}</StatusBadge>}
        {step.minMonths ? <span className="text-[12px] text-muted-foreground">{tx('Önerilen asgari süre: {0} ay', [step.minMonths])}</span> : null}
      </div>
      {step.description && <p className="text-[13px] text-muted-foreground">{step.description}</p>}
      {step.items.length === 0 ? (
        <p className="text-[13px] text-muted-foreground">{tx('Bu basamak için yetkinlik beklentisi tanımlı değil.')}</p>
      ) : (
        <ul className="divide-y divide-border rounded-xl border border-border">
          {step.items.map((g) => (
            <li key={g.competencyId} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-[13.5px]">
              <span className="min-w-0 flex-1">
                <span className="font-medium">{g.competency}</span>
                <span className="block text-[12px] text-muted-foreground">
                  {tx('Beklenen {0}', [g.required])} · {g.current === null ? tx('değerlendirilmedi') : tx('güncel {0}', [g.current])}
                </span>
              </span>
              <span className={cn('tabular rounded-full px-2.5 py-0.5 text-[12px] font-semibold',
                g.gap === 0 ? 'bg-[hsl(var(--success))]/15 text-[hsl(var(--success))]' : 'bg-[hsl(var(--warning))]/15 text-[hsl(var(--warning))]')}>
                {g.gap === 0 ? tx('Karşılıyor') : tx('Açık {0}', [g.gap])}
              </span>
            </li>
          ))}
        </ul>
      )}
      {step.courses && (
        <div className="space-y-2">
          <p className="flex items-center gap-2 text-[13px] font-medium"><GraduationCap className="size-4 text-primary" aria-hidden />{' '}{tx('Açığı kapatabilecek eğitimler')}</p>
          {step.courses.length === 0 ? (
            <p className="text-[13px] text-muted-foreground">{tx('Açığı kapatan etiketli bir eğitim bulunamadı.')}</p>
          ) : (
            <ul className="divide-y divide-border rounded-xl border border-border">
              {step.courses.map((c) => (
                <li key={c.courseId} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-[13.5px]">
                  <span className="min-w-0 flex-1">
                    <span className="font-medium">{c.title}</span>
                    <span className="block text-[12px] text-muted-foreground">{c.closes.map((x) => tx('{0}: {1} → {2}', [x.competency, x.from, x.to])).join(' · ')}</span>
                  </span>
                  {c.isMandatory && <StatusBadge tone="warning">{tx('Zorunlu')}</StatusBadge>}
                  {c.enrollmentStatus ? (
                    <StatusBadge tone={c.enrollmentStatus === 'Completed' ? 'success' : 'info'}>{c.enrollmentStatus === 'Completed' ? tx('Tamamlandı') : tx('Kayıtlı')}</StatusBadge>
                  ) : self ? (
                    <Button size="sm" variant="outline" disabled={enroll.isPending} onClick={() => enroll.mutate(c.courseId)}>{tx('Kendimi kaydet')}</Button>
                  ) : null}
                  <Button asChild size="sm" variant="ghost"><Link to={`/panel/egitim/${c.courseId}`}>{tx('İncele')}</Link></Button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}

/* ---------------------------------------------------------------- benim yolum */

function MyCareer() {
  const [explore, setExplore] = useState<{ pathId?: string; stepId?: string }>({})
  const q = useQuery({
    queryKey: ['learning', 'career', 'me', explore.pathId ?? '', explore.stepId ?? ''],
    queryFn: ({ signal }) => learningW11Api.careerProgress('me', explore, signal),
  })
  const paths = useQuery({ queryKey: ['learning', 'career', 'paths'], queryFn: ({ signal }) => learningW11Api.careerPaths(false, signal) })
  if (q.isPending) return <RowsSkeleton rows={4} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const d = q.data
  const chosenPath = paths.data?.find((p) => p.id === explore.pathId)
  return (
    <div className="space-y-5">
      <InfoNote>{d.notice}</InfoNote>
      <Panel>
        <PanelHead title={tx('Bir yolu incele')} note={d.positionTitle ? tx('Güncel unvanınız: {0}', [d.positionTitle]) : tx('Pozisyon bilgisi yok')} />
        <PanelBody className="grid gap-3 sm:grid-cols-2">
          <SelectField label={tx('Kariyer yolu')} value={explore.pathId ?? ''} onChange={(v) => setExplore({ pathId: v })}
            options={(paths.data ?? []).map((p) => ({ value: p.id, label: p.name }))} />
          <SelectField label={tx('Hedef basamak')} value={explore.stepId ?? ''} disabled={!chosenPath}
            onChange={(v) => setExplore({ pathId: explore.pathId, stepId: v })}
            options={(chosenPath?.steps ?? []).map((s) => ({ value: s.id, label: `${s.stepOrder}. ${s.positionTitle}` }))} />
        </PanelBody>
      </Panel>
      {d.matched.length === 0 ? (
        <EmptyState icon={RouteIcon} title={tx('Unvanınız bir kariyer yolunda yer almıyor')}
          detail={d.available.length ? tx('Yukarıdan bir yol ve hedef basamak seçerek açığınızı görebilirsiniz.') : tx('İK henüz kariyer yolu tanımlamadı.')} />
      ) : d.matched.map((p) => (
        <Panel key={p.pathId}>
          <PanelHead title={p.name} note={p.description ?? undefined} />
          <PanelBody className="space-y-4">
            <ol className="flex flex-wrap items-center gap-1.5 text-[12.5px]" aria-label={tx('Basamaklar')}>
              {p.steps.map((s, i) => (
                <li key={s.id} className="flex items-center gap-1.5">
                  <span className={cn('rounded-full border px-2.5 py-1',
                    s.id === p.currentStepId ? 'border-primary bg-primary/10 font-semibold text-primary'
                      : s.id === p.target?.id ? 'border-[hsl(var(--warning))] font-medium' : 'border-border text-muted-foreground')}>
                    {s.positionTitle}{noExpectations(p, s.id) ? '' : ` · %${s.readiness}`}
                  </span>
                  {i < p.steps.length - 1 && <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />}
                </li>
              ))}
            </ol>
            {p.target ? (
              <div>
                <p className="mb-2 text-[12px] uppercase tracking-wide text-muted-foreground">
                  {p.currentStepId ? (p.target.id === explore.stepId ? tx('Seçilen basamak') : tx('Sonraki basamak')) : tx('Hedef basamak')}
                </p>
                <StepDetail step={p.target} employeeId={d.employeeId} self />
              </div>
            ) : (
              <p className="text-[13px] text-muted-foreground">{tx('Bu yolun en üst basamağındasınız.')}</p>
            )}
          </PanelBody>
        </Panel>
      ))}
    </div>
  )
}

/* ---------------------------------------------------------------- tanımlar (İK) */

type Draft = CareerPathInput & { id?: string }

function emptyDraft(): Draft {
  return { name: '', description: '', isActive: true, steps: [{ positionTitle: '', description: '', minMonths: null, requirements: [] }] }
}

function toDraft(p: CareerPath): Draft {
  return {
    id: p.id, name: p.name, description: p.description ?? '', isActive: p.isActive,
    steps: p.steps.map((s) => ({
      positionTitle: s.positionTitle, description: s.description ?? '', minMonths: s.minMonths,
      requirements: s.requirements.map((r) => ({ competencyId: r.competencyId, requiredLevel: r.requiredLevel })),
    })),
  }
}

function PathEditor({ draft, onClose }: { draft: Draft; onClose: () => void }) {
  const [d, setD] = useState<Draft>(draft)
  const comps = useQuery({ queryKey: ['learning', 'competencies'], queryFn: ({ signal }) => learningContentApi.competencies(signal) })
  const save = useAction(() => {
    const body: CareerPathInput = { name: d.name, description: d.description || null, isActive: d.isActive, steps: d.steps }
    return d.id ? learningW11Api.updateCareerPath(d.id, body) : learningW11Api.createCareerPath(body)
  }, { success: tx('Kariyer yolu kaydedildi'), invalidate: [['learning', 'career']], onDone: onClose })
  const compOptions = (comps.data ?? []).map((c) => ({ value: c.id, label: c.name }))
  const setStep = (i: number, patch: Partial<Draft['steps'][number]>) =>
    setD({ ...d, steps: d.steps.map((s, j) => (j === i ? { ...s, ...patch } : s)) })
  const move = (i: number, dir: -1 | 1) => {
    const steps = [...d.steps]
    const [s] = steps.splice(i, 1)
    steps.splice(i + dir, 0, s!)
    setD({ ...d, steps })
  }
  return (
    <Modal open onClose={onClose} size="xl" title={d.id ? tx('Kariyer yolunu düzenle') : tx('Yeni kariyer yolu')}
      note={tx('Basamaklar alttan üste sıralanır; çalışanın basamağı güncel pozisyon unvanıyla eşleştirilir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={tx('Ad')} value={d.name} maxLength={150} onChange={(e) => setD({ ...d, name: e.target.value })} />
          <SelectField label={tx('Durum')} value={d.isActive === false ? 'pasif' : 'etkin'} onChange={(v) => setD({ ...d, isActive: v === 'etkin' })}
            options={[{ value: 'etkin', label: tx('Etkin') }, { value: 'pasif', label: tx('Pasif') }]} />
        </div>
        <TextAreaField label={tx('Açıklama')} rows={2} maxLength={1000} value={d.description ?? ''} onChange={(e) => setD({ ...d, description: e.target.value })} />
        {d.steps.map((s, i) => (
          <div key={i} className="space-y-3 rounded-xl border border-border p-3">
            <div className="flex items-center gap-2">
              <span className="text-[12px] font-semibold text-muted-foreground">{tx('Basamak {0}', [i + 1])}</span>
              <span className="flex-1" />
              <Button size="icon" variant="ghost" disabled={i === 0} onClick={() => move(i, -1)} aria-label={tx('Yukarı taşı')}><ArrowUp className="size-4" /></Button>
              <Button size="icon" variant="ghost" disabled={i === d.steps.length - 1} onClick={() => move(i, 1)} aria-label={tx('Aşağı taşı')}><ArrowDown className="size-4" /></Button>
              <Button size="icon" variant="ghost" disabled={d.steps.length === 1} onClick={() => setD({ ...d, steps: d.steps.filter((_, j) => j !== i) })} aria-label={tx('Basamağı sil')}><Trash2 className="size-4" /></Button>
            </div>
            <div className="grid gap-3 sm:grid-cols-[1fr_10rem]">
              <TextField label={tx('Pozisyon unvanı')} value={s.positionTitle} maxLength={150} onChange={(e) => setStep(i, { positionTitle: e.target.value })} />
              <TextField label={tx('Asgari süre (ay)')} type="number" min={0} max={240} value={s.minMonths ?? ''}
                onChange={(e) => setStep(i, { minMonths: e.target.value === '' ? null : Number(e.target.value) })} />
            </div>
            <TextAreaField label={tx('Basamak açıklaması')} rows={2} maxLength={1000} value={s.description ?? ''} onChange={(e) => setStep(i, { description: e.target.value })} />
            {s.requirements.map((r, k) => (
              <div key={k} className="grid items-end gap-2 sm:grid-cols-[1fr_12rem_auto]">
                <SelectField label={tx('Yetkinlik')} value={r.competencyId} options={compOptions}
                  onChange={(v) => setStep(i, { requirements: s.requirements.map((x, m) => (m === k ? { ...x, competencyId: v } : x)) })} />
                <SelectField label={tx('Beklenen seviye')} value={String(r.requiredLevel)} options={levelOptions}
                  onChange={(v) => setStep(i, { requirements: s.requirements.map((x, m) => (m === k ? { ...x, requiredLevel: Number(v) } : x)) })} />
                <Button size="icon" variant="ghost" onClick={() => setStep(i, { requirements: s.requirements.filter((_, m) => m !== k) })} aria-label={tx('Yetkinliği kaldır')}><Trash2 className="size-4" /></Button>
              </div>
            ))}
            <div className="flex flex-wrap items-center gap-2">
              <Button size="sm" variant="outline" disabled={!compOptions.length}
                title={!compOptions.length ? tx('Önce Yetkinlik matrisi ekranından yetkinlik tanımlayın.') : undefined}
                onClick={() => setStep(i, { requirements: [...s.requirements, { competencyId: compOptions.find((o) => !s.requirements.some((x) => x.competencyId === o.value))?.value ?? compOptions[0]!.value, requiredLevel: 3 }] })}>
                <Plus className="size-4" />{' '}{tx('Yetkinlik ekle')}
              </Button>
              {!compOptions.length && (
                <span className="text-[12px] text-muted-foreground">{tx('Yetkinlik eklemek için önce Yetkinlik matrisi ekranından yetkinlik tanımlayın.')}</span>
              )}
            </div>
          </div>
        ))}
        <Button variant="outline" onClick={() => setD({ ...d, steps: [...d.steps, { positionTitle: '', description: '', minMonths: null, requirements: [] }] })}>
          <Plus className="size-4" />{' '}{tx('Basamak ekle')}
        </Button>
      </div>
    </Modal>
  )
}

function PathList({ canEdit }: { canEdit: boolean }) {
  const q = useQuery({ queryKey: ['learning', 'career', 'paths', canEdit], queryFn: ({ signal }) => learningW11Api.careerPaths(canEdit, signal) })
  const [edit, setEdit] = useState<Draft | null>(null)
  const confirm = useConfirm()
  const del = useAction((id: string) => learningW11Api.deleteCareerPath(id), { success: tx('Kariyer yolu silindi'), invalidate: [['learning', 'career']] })
  if (q.isPending) return <RowsSkeleton rows={4} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  return (
    <div className="space-y-4">
      {canEdit && <div className="flex justify-end"><Button onClick={() => setEdit(emptyDraft())}><Plus className="size-4" />{' '}{tx('Yeni kariyer yolu')}</Button></div>}
      {q.data.length === 0 ? (
        <EmptyState icon={RouteIcon} title={tx('Kariyer yolu yok')} detail={canEdit ? tx('Rol basamaklarını ve beklenen yetkinlikleri tanımlayın.') : tx('İK henüz kariyer yolu tanımlamadı.')} />
      ) : q.data.map((p) => (
        <Panel key={p.id}>
          <PanelHead title={<span className="flex items-center gap-2">{p.name}{!p.isActive && <StatusBadge>{tx('Pasif')}</StatusBadge>}</span>} note={p.description ?? undefined}
            action={canEdit ? (
              <span className="flex gap-1.5">
                <Button size="sm" variant="outline" onClick={() => setEdit(toDraft(p))}>{tx('Düzenle')}</Button>
                <Button size="sm" variant="ghost" onClick={async () => {
                  if (await confirm({ title: tx('Kariyer yolu silinsin mi?'), note: tx('"{0}" yolu ve basamakları kalıcı olarak silinir.', [p.name]), action: tx('Sil') })) del.mutate(p.id)
                }} aria-label={tx('Sil')}><Trash2 className="size-4" /></Button>
              </span>
            ) : undefined} />
          <PanelBody>
            <ol className="space-y-2">
              {p.steps.map((s) => (
                <li key={s.id} className="rounded-xl border border-border px-4 py-2.5 text-[13.5px]">
                  <span className="font-medium">{s.stepOrder}. {s.positionTitle}</span>
                  {s.minMonths ? <span className="ml-2 text-[12px] text-muted-foreground">{tx('asgari {0} ay', [s.minMonths])}</span> : null}
                  {s.requirements.length > 0 && (
                    <span className="mt-1 flex flex-wrap gap-1.5">
                      {s.requirements.map((r) => <StatusBadge key={r.competencyId} tone={r.isActive ? 'info' : 'neutral'}>{tx('{0} ≥ {1}', [r.competency, r.requiredLevel])}</StatusBadge>)}
                    </span>
                  )}
                </li>
              ))}
            </ol>
          </PanelBody>
        </Panel>
      ))}
      {edit && <PathEditor draft={edit} onClose={() => setEdit(null)} />}
    </div>
  )
}

/* ---------------------------------------------------------------- sayfa */

export function CareerPathsPage() {
  const { can } = useAuth()
  const isHr = can('learning:manage')
  const { employeeId, notLinked } = useMyEmployeeId()
  const [tab, setTab] = useTabParam<TabKey>('gorunum', notLinked ? 'yollar' : 'benim')
  const tabs: Array<TabDef<TabKey>> = [
    ...(!notLinked ? [{ key: 'benim' as TabKey, label: tx('Benim yolum') }] : []),
    { key: 'yollar', label: isHr ? tx('Kariyer yolları ve düzenleme') : tx('Kariyer yolları') },
  ]
  const active = notLinked && tab === 'benim' ? 'yollar' : tab
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Kariyer yolları')} description={tx('Rol basamakları, her basamakta beklenen yetkinlikler, bulunduğunuz yer ve sonraki adım için açığınız.')} />
      <Tabs tabs={tabs} value={active} onChange={setTab} label={tx('Kariyer yolu görünümü')} />
      {active === 'benim' && (employeeId ? <MyCareer /> : <RowsSkeleton rows={4} />)}
      {active === 'yollar' && <PathList canEdit={isHr} />}
    </div>
  )
}
