import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Bar, BarChart, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { BarChart3, CheckCircle2, Lock, MessagesSquare, Plus, ShieldCheck, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { Tabs } from '@/components/ui/tabs'
import { engagementApi, type QuestionType, type Survey, type SurveyAnswer, type SurveyQuestion } from '@/api/engagement'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Metric, TenantGate, useAction } from '@/features/shared/kit'
import { tx, pct } from '@/lib/i18n'
import { useConfirm } from '@/components/ui/confirm'
import { EnpsTrendPanel, SentimentSummary } from './SurveyInsights'
import { SurveyTopics } from './SurveyTopics'

/** Sunucudaki anket anonimlik eşiği (engagement-service HrControllers.AnonymityThreshold = 5, KVKK). */
const SURVEY_ANONYMITY_THRESHOLD = 5

const SCALE_FACES = ['😞', '🙁', '😐', '🙂', '😄']
const statusTone = { Draft: 'neutral', Open: 'success', Closed: 'info' } as const
const statusLabel = { Draft: tx('Taslak'), Open: tx('Açık'), Closed: tx('Kapandı') }

function AnswerModal({ survey, onClose }: { survey: Survey; onClose: () => void }) {
  const [answers, setAnswers] = useState<Record<string, SurveyAnswer>>({})
  const set = (q: SurveyQuestion, patch: Partial<SurveyAnswer>) => setAnswers((a) => ({ ...a, [q.id]: { ...a[q.id], ...patch, questionId: q.id } }))
  const missing = survey.questions.filter((q) => q.required && !(answers[q.id]?.score != null || answers[q.id]?.choice || answers[q.id]?.text?.trim()))
  const submit = useAction(() => engagementApi.respond(survey.id, Object.values(answers)), { success: tx('Teşekkürler! Yanıtınız kaydedildi.'), invalidate: [['surveys']], onDone: onClose })
  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={survey.title}
      note={survey.isAnonymous ? tx('Anonim anket: yanıtınız kimliğinizle eşleştirilmez; {0} yanıtın altındaki kırılımlar ve serbest metinler gizlenir.', [SURVEY_ANONYMITY_THRESHOLD]) : survey.description ?? undefined}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
          <Button disabled={missing.length > 0 || submit.isPending} onClick={() => submit.mutate(undefined)}>{tx('Gönder')}</Button>
        </>
      }
    >
      <div className="space-y-6">
        {survey.questions.map((q, i) => (
          <motion.div key={q.id} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }}>
            <p className="mb-2.5 text-[14px] font-medium">
              {i + 1}. {q.text} {q.required && <span className="text-destructive">*</span>}
            </p>
            {q.type === 'Nps' && (
              <div>
                <div className="grid grid-cols-11 gap-1">
                  {Array.from({ length: 11 }, (_, n) => (
                    <button
                      key={n}
                      type="button"
                      onClick={() => set(q, { score: n })}
                      className={cn(
                        'tabular h-10 cursor-pointer rounded-lg border text-[13px] font-medium transition',
                        answers[q.id]?.score === n
                          ? n <= 6 ? 'border-rose-500 bg-rose-500/20' : n <= 8 ? 'border-amber-400 bg-amber-400/20' : 'border-emerald-500 bg-emerald-500/20'
                          : 'border-border hover:border-primary/50',
                      )}
                    >
                      {n}
                    </button>
                  ))}
                </div>
                <div className="mt-1 flex justify-between text-[11px] text-muted-foreground"><span>{tx('Hiç önermem')}</span><span>{tx('Kesinlikle öneririm')}</span></div>
              </div>
            )}
            {q.type === 'Scale' && (
              <div className="flex gap-2">
                {SCALE_FACES.map((face, n) => (
                  <motion.button
                    key={n}
                    type="button"
                    whileHover={{ scale: 1.12 }}
                    whileTap={{ scale: 0.92 }}
                    onClick={() => set(q, { score: n + 1 })}
                    className={cn('grid size-12 cursor-pointer place-items-center rounded-xl border text-2xl transition', answers[q.id]?.score === n + 1 ? 'border-primary bg-primary/15' : 'border-border opacity-70 hover:opacity-100')}
                    aria-label={`${n + 1} / 5`}
                  >
                    {face}
                  </motion.button>
                ))}
              </div>
            )}
            {q.type === 'Choice' && (
              <div className="flex flex-wrap gap-2">
                {q.options.map((o) => (
                  <button key={o} type="button" onClick={() => set(q, { choice: o })} className={cn('cursor-pointer rounded-full border px-3 py-1.5 text-[13px] transition', answers[q.id]?.choice === o ? 'border-primary bg-primary/15' : 'border-border hover:border-primary/40')}>
                    {o}
                  </button>
                ))}
              </div>
            )}
            {q.type === 'Text' && (
              <TextAreaField label="" aria-label={q.text} rows={3} maxLength={2000} value={answers[q.id]?.text ?? ''} onChange={(e) => set(q, { text: e.target.value })} />
            )}
          </motion.div>
        ))}
      </div>
    </Modal>
  )
}

function ResultsModal({ survey, onClose }: { survey: Survey; onClose: () => void }) {
  const q = useQuery({ queryKey: ['surveys', survey.id, 'results'], queryFn: ({ signal }) => engagementApi.surveyResults(survey.id, signal) })
  const r = q.data
  const nps = r?.questions.find((x) => x.type === 'Nps')
  return (
    <Modal open onClose={onClose} size="xl" title={tx('Sonuçlar — {0}', [survey.title])} note={tx('Anonimlik eşiği: {0} yanıtın altındaki kırılımlar ve serbest metinler gizlenir.', [r?.anonymityThreshold ?? SURVEY_ANONYMITY_THRESHOLD])}>
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState message={(q.error as Error).message} /> : r?.hidden ? (
        <div className="space-y-3">
          <Metric label={tx('Yanıt')} value={r.responseCount} hint={tx('{0} kişiden', [r.eligible])} />
          <InfoNote>{tx('Anonimliği korumak için sonuçlar en az {0} yanıt toplandığında gösterilir.', [r.anonymityThreshold])}</InfoNote>
        </div>
      ) : r && (
        <div className="space-y-6">
          <div className="grid gap-3 sm:grid-cols-3">
            <Metric label={tx('Yanıt')} value={r.responseCount} hint={tx('{0} kişiden', [r.eligible])} />
            <Metric label={tx('Katılım')} value={pct(r.participation)} tone={r.participation >= 60 ? 'good' : r.participation >= 30 ? 'warn' : 'bad'} />
            {nps && <Metric label="eNPS" value={nps.enps ?? '—'} hint={tx('{0} destekçi · {1} pasif · {2} eleştirmen', [nps.promoters ?? 0, nps.passives ?? 0, nps.detractors ?? 0])} tone={(nps.enps ?? 0) >= 20 ? 'good' : (nps.enps ?? 0) >= 0 ? 'warn' : 'bad'} />}
          </div>
          {r.questions.map((x) => (
            <div key={x.id} className="rounded-2xl border border-border p-4">
              <p className="mb-3 text-[13.5px] font-medium">{x.text}</p>
              {(x.type === 'Nps' || x.type === 'Scale') && x.distribution && (
                <div className="h-36">
                  <ResponsiveContainer>
                    <BarChart data={x.distribution.map((v, i) => ({ k: x.type === 'Nps' ? String(i) : SCALE_FACES[i], v, i }))}>
                      <XAxis dataKey="k" tickLine={false} axisLine={false} fontSize={11} />
                      <YAxis allowDecimals={false} width={24} fontSize={11} tickLine={false} axisLine={false} />
                      <Tooltip cursor={{ fill: 'hsl(var(--muted)/0.4)' }} contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 12 }} />
                      <Bar dataKey="v" name={tx('Yanıt')} radius={[6, 6, 0, 0]}>
                        {x.distribution.map((_, i) => (
                          <Cell key={i} fill={x.type === 'Nps' ? (i <= 6 ? '#f43f5e' : i <= 8 ? '#f59e0b' : '#10b981') : `hsl(${150 - (4 - i) * 30} 60% 45%)`} />
                        ))}
                      </Bar>
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              )}
              {x.type === 'Scale' && <p className="mt-1 text-[12.5px] text-muted-foreground">{tx('Ortalama {0} / 5 · olumlu %{1}', [x.average ?? '—', x.favorable ?? '—'])}</p>}
              {x.type === 'Choice' && (
                <div className="space-y-1.5">
                  {x.options?.map((o) => (
                    <div key={o.option} className="flex items-center gap-2 text-[13px]">
                      <span className="w-48 truncate">{o.option}</span>
                      <div className="h-2 flex-1 overflow-hidden rounded-full bg-muted">
                        <motion.div initial={{ width: 0 }} animate={{ width: `${x.count ? (100 * o.count) / x.count : 0}%` }} className="h-full rounded-full bg-primary" />
                      </div>
                      <span className="tabular w-6 text-right">{o.count}</span>
                    </div>
                  ))}
                </div>
              )}
              {x.type === 'Text' && (x.hiddenForAnonymity ? (
                <p className="flex items-center gap-1.5 text-[12.5px] text-muted-foreground"><Lock className="size-3.5" />{' '}{tx('Anonimliği korumak için {0} yorum gizlendi (en az {1} gerekli).', [x.count, r.anonymityThreshold])}</p>
              ) : (
                <>
                  <SentimentSummary q={x} />
                  {x.count >= (r.anonymityThreshold ?? SURVEY_ANONYMITY_THRESHOLD) && <SurveyTopics surveyId={survey.id} questionId={x.id} />}
                  <ul className="space-y-1.5">{x.texts?.map((t, i) => <li key={i} className="rounded-lg bg-muted/50 px-3 py-2 text-[13px]">“{t}”</li>)}</ul>
                </>
              ))}
            </div>
          ))}
          <div>
            <p className="mb-2 text-[13.5px] font-medium">{tx('Departman kırılımı')}</p>
            <table className="w-full text-[13px]">
              <thead><tr className="text-left text-muted-foreground"><th className="py-1.5">{tx('Departman')}</th><th>{tx('Yanıt')}</th><th>{tx('eNPS')}</th><th>{tx('Olumlu')}</th></tr></thead>
              <tbody>
                {r.byDepartment.map((d) => (
                  <tr key={d.department} className="border-t border-border">
                    <td className="py-1.5">{d.department}</td><td className="tabular">{d.count ?? <span className="text-muted-foreground">{tx('gizli')}</span>}</td>
                    <td className="tabular">{d.hidden ? <span className="text-muted-foreground">{tx('gizli')}</span> : d.enps ?? '—'}</td>
                    <td className="tabular">{d.hidden ? <span className="text-muted-foreground">{tx('gizli')}</span> : d.favorable != null ? pct(d.favorable) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </Modal>
  )
}

function BuilderModal({ onClose }: { onClose: () => void }) {
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [questions, setQuestions] = useState<SurveyQuestion[]>([{ id: 'q1', text: '', type: 'Scale', options: [], required: true }])
  // Seçenekler ham metin olarak tutulur (her tuşta ayrıştırılırsa virgül yazılamaz); kaydederken bölünür.
  const [optionText, setOptionText] = useState<Record<string, string>>({})
  const optionsOf = (id: string) => [...new Set((optionText[id] ?? '').split(',').map((x) => x.trim()).filter(Boolean))]
  const optionError = (q: SurveyQuestion) => (q.type === 'Choice' && optionsOf(q.id).length < 2 ? tx('Seçenekli soruda en az 2 farklı seçenek olmalı.') : undefined)
  const invalid = questions.some((q) => !q.text.trim() || optionError(q))
  const create = useAction(() => engagementApi.createSurvey({ title, description, kind: 'Custom', isAnonymous: true, questions: questions.map((q) => ({ ...q, options: q.type === 'Choice' ? optionsOf(q.id) : [] })) }), {
    success: tx('Anket taslağı oluşturuldu'), invalidate: [['surveys']], onDone: onClose,
  })
  const upd = (i: number, patch: Partial<SurveyQuestion>) => setQuestions((qs) => qs.map((q, j) => (j === i ? { ...q, ...patch } : q)))
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Yeni anket')} note={tx('Anketler anonim oluşturulur. Taslak olarak kaydedilir; yayınlamak için listeden açın.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!title.trim() || invalid || create.isPending} onClick={() => create.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Başlık')} value={title} onChange={(e) => setTitle(e.target.value)} required />
        <TextAreaField label={tx('Açıklama')} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
        {questions.map((q, i) => (
          <div key={q.id} className="space-y-2 rounded-xl border border-border p-3">
            <div className="flex items-end gap-2">
              <div className="flex-1"><TextField label={tx('Soru {0}', [i + 1])} value={q.text} onChange={(e) => upd(i, { text: e.target.value })} /></div>
              <div className="w-40">
                <SelectField label={tx('Tip')} value={q.type} onChange={(v) => upd(i, { type: v as QuestionType })} options={[{ value: 'Nps', label: '0–10 (NPS)' }, { value: 'Scale', label: tx('1–5 ölçek') }, { value: 'Choice', label: tx('Seçenekli') }, { value: 'Text', label: tx('Serbest metin') }]} />
              </div>
              <Button variant="ghost" size="icon" aria-label={tx('Soruyu sil')} onClick={() => setQuestions((qs) => qs.filter((_, j) => j !== i))} disabled={questions.length === 1}><Trash2 className="size-4" /></Button>
            </div>
            {q.type === 'Choice' && <TextField label={tx('Seçenekler (virgülle)')} value={optionText[q.id] ?? ''} error={optionError(q)} onChange={(e) => setOptionText((m) => ({ ...m, [q.id]: e.target.value }))} />}
          </div>
        ))}
        <Button variant="outline" onClick={() => setQuestions((qs) => [...qs, { id: `q${Date.now().toString(36)}`, text: '', type: 'Scale', options: [], required: true }])}><Plus className="size-4" />{' '}{tx('Soru ekle')}</Button>
      </div>
    </Modal>
  )
}

/** Anket türü sunucudan İngilizce enum olarak gelir; kullanıcıya Türkçe etiket gösterilir. */
function surveyKindLabel(kind: string): string {
  if (kind === 'eNPS') return 'eNPS'
  if (kind === 'Pulse') return tx('Nabız anketi')
  if (kind === 'Custom') return tx('Özel anket')
  return kind
}

export function SurveysPage() {
  const { roles } = useAuth()
  const hr = isHr(roles, 'ext-engagement-manage')
  const [tab, setTab] = useState<'answer' | 'manage'>('answer')
  const [answering, setAnswering] = useState<Survey | null>(null)
  const [results, setResults] = useState<Survey | null>(null)
  const [building, setBuilding] = useState(false)
  const list = useQuery({ queryKey: ['surveys'], queryFn: ({ signal }) => engagementApi.surveys(signal) })
  const tpl = useAction((k: 'enps' | 'pulse') => engagementApi.surveyFromTemplate(k), { success: tx('Şablondan taslak oluşturuldu'), invalidate: [['surveys']] })
  const status = useAction(({ id, s }: { id: string; s: Survey['status'] }) => engagementApi.setSurveyStatus(id, s), { success: tx('Güncellendi'), invalidate: [['surveys']] })
  const del = useAction((id: string) => engagementApi.deleteSurvey(id), { success: tx('Silindi'), invalidate: [['surveys']] })
  const confirm = useConfirm()
  const askDelete = async (s: Survey) => {
    const note = s.responseCount > 0
      ? tx('Bu ankete verilmiş {0} yanıt ve sonuçları da kalıcı olarak silinir.', [s.responseCount])
      : tx('Anket ve soruları kalıcı olarak silinir.')
    if (await confirm({ title: tx('“{0}” silinsin mi?', [s.title]), note, action: tx('Sil') })) del.mutate(s.id)
  }

  const open = (list.data ?? []).filter((s) => s.status === 'Open')
  return (
    <TenantGate>
      <PageHeader title={tx('Anketler ve eNPS')} description={tx('Kısa, anonim nabız anketleri. Çalışan bağlılığını ölçün, eğilimi izleyin.')} actions={hr && <Button onClick={() => setBuilding(true)}><Plus className="size-4" />{' '}{tx('Yeni anket')}</Button>} />
      {hr && (
        <div className="mb-5">
          <Tabs label={tx('Görünüm')} value={tab} onChange={setTab} tabs={[{ key: 'answer', label: tx('Yanıtla'), count: open.filter((s) => !s.answered).length }, { key: 'manage', label: tx('Yönetim'), count: list.data?.length }]} />
        </div>
      )}
      {list.isPending ? <RowsSkeleton /> : list.isError ? <ErrorState message={(list.error as Error).message} onRetry={() => list.refetch()} /> : tab === 'answer' || !hr ? (
        open.length === 0 ? (
          <EmptyState icon={MessagesSquare} title={tx('Açık anket yok')} detail={tx('Yeni bir anket yayınlandığında burada görünecek.')} />
        ) : (
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {open.map((s, i) => (
              <motion.div key={s.id} initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.06 }} className="surface flex flex-col rounded-2xl border border-border p-5">
                <div className="flex items-center gap-2">
                  <StatusBadge tone={s.kind === 'eNPS' ? 'info' : 'neutral'}>{surveyKindLabel(s.kind)}</StatusBadge>
                  {s.isAnonymous && <span className="flex items-center gap-1 text-[12px] text-muted-foreground"><ShieldCheck className="size-3.5" />{' '}{tx('anonim')}</span>}
                </div>
                <h3 className="mt-3 text-[15.5px] font-semibold">{s.title}</h3>
                <p className="mt-1 flex-1 text-[13px] text-muted-foreground">{tx('{0} soru · {1}', [s.questions.length, s.closesAt ? tx('{0} tarihine kadar', [formatDate(s.closesAt)]) : tx('süresiz')])}</p>
                {s.answered ? (
                  <p className="mt-4 flex items-center gap-1.5 text-[13px] text-[hsl(var(--success))]"><CheckCircle2 className="size-4" />{' '}{tx('Yanıtladınız, teşekkürler')}</p>
                ) : (
                  <Button className="mt-4" onClick={() => setAnswering(s)}>{tx('Yanıtla (~1 dk)')}</Button>
                )}
              </motion.div>
            ))}
          </div>
        )
      ) : (
        <div className="space-y-4">
          <EnpsTrendPanel />
          <InfoNote>{tx('Hazır şablonlar:')}{' '}<button className="cursor-pointer font-medium text-primary underline-offset-2 hover:underline" onClick={() => tpl.mutate('enps')}>{tx('eNPS anketi')}</button> · <button className="cursor-pointer font-medium text-primary underline-offset-2 hover:underline" onClick={() => tpl.mutate('pulse')}>{tx('haftalık nabız')}</button>{tx('. Taslak oluşur, “Yayınla” ile açılır.')}</InfoNote>
          <Panel>
            <PanelHead title={tx('Tüm anketler')} />
            <PanelBody className="p-0">
              {(list.data ?? []).length === 0 ? <EmptyState title={tx('Henüz anket yok')} detail={tx('Bir şablonla başlayın.')} /> : (
                <ul className="divide-y divide-border">
                  {list.data!.map((s) => (
                    <li key={s.id} className="flex flex-wrap items-center gap-3 px-5 py-3.5">
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-[14px] font-medium">{s.title}</p>
                        <p className="text-[12px] text-muted-foreground">{tx('{0} · {1} soru · {2} yanıt · {3}', [surveyKindLabel(s.kind), s.questions.length, s.responseCount, s.createdByName])}</p>
                      </div>
                      <StatusBadge tone={statusTone[s.status]}>{statusLabel[s.status]}</StatusBadge>
                      {s.status !== 'Open' && <Button size="sm" variant="outline" onClick={() => status.mutate({ id: s.id, s: 'Open' })}>{tx('Yayınla')}</Button>}
                      {s.status === 'Open' && <Button size="sm" variant="outline" onClick={() => status.mutate({ id: s.id, s: 'Closed' })}>{tx('Kapat')}</Button>}
                      <Button size="sm" variant="outline" onClick={() => setResults(s)} disabled={s.responseCount === 0}><BarChart3 className="size-4" />{' '}{tx('Sonuçlar')}</Button>
                      <Button size="icon" variant="ghost" aria-label={tx('Sil')} onClick={() => askDelete(s)}><Trash2 className="size-4" /></Button>
                    </li>
                  ))}
                </ul>
              )}
            </PanelBody>
          </Panel>
        </div>
      )}
      {answering && <AnswerModal survey={answering} onClose={() => setAnswering(null)} />}
      {results && <ResultsModal survey={results} onClose={() => setResults(null)} />}
      {building && <BuilderModal onClose={() => setBuilding(false)} />}
    </TenantGate>
  )
}
