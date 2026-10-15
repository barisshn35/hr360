/**
 * Y20 eğitim oynatıcı: modüller (video bağlantısı, metin, sınav, SCORM 1.2 iframe), ilerleme,
 * sertifika; İK için içerik yönetimi (modül, sınav soruları, SCORM paketi, yetkinlik etiketi)
 * ve sonuçlar (İK + departman yöneticisi).
 */
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  ArrowLeft, Award, BookOpen, Check, ExternalLink, FileText, ListChecks, Package, PlayCircle, Plus, Printer, Trash2, Upload,
} from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/api/client'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { ProgressBar } from '@/components/ui/Progress'
import { Tabs, useTabParam, type TabDef } from '@/components/ui/Tabs'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useAuth } from '@/auth/useAuth'
import { learningApi } from '@/api/learning'
import {
  learningContentApi, competencyLevelLabels, moduleKindLabels,
  type CourseModule, type CourseProgress, type ModuleInput, type ModuleKind, type ModuleStatus, type QuizQuestionInput,
} from '@/api/learningContent'
import { useMyEmployeeId } from '@/api/queries'
import { errMsg, useAction } from '@/features/shared/kit'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { installScormApi } from './scormShim'

type TabKey = 'icerik' | 'sonuclar' | 'yonetim'

const statusTone: Record<ModuleStatus, StatusTone> = { NotStarted: 'neutral', InProgress: 'info', Completed: 'success', Failed: 'danger' }
const statusLabel: Record<ModuleStatus, string> = {
  NotStarted: tx('Başlanmadı'), InProgress: tx('Sürüyor'), Completed: tx('Tamamlandı'), Failed: tx('Başarısız'),
}
const kindIcon: Record<ModuleKind, React.ElementType> = { Video: PlayCircle, Text: FileText, Quiz: ListChecks, Scorm: Package }

/* ================================================================== öğrenen görünümleri */

function TextModule({ m, done, onComplete, busy }: { m: CourseModule; done: boolean; onComplete: () => void; busy: boolean }) {
  return (
    <div className="space-y-4">
      <article className="max-w-none whitespace-pre-wrap text-[14px] leading-relaxed">{m.textBody}</article>
      {!done && <Button onClick={onComplete} disabled={busy}><Check className="size-4" />{' '}{tx('Okudum, tamamla')}</Button>}
    </div>
  )
}

function VideoModule({ m, done, onComplete, busy }: { m: CourseModule; done: boolean; onComplete: () => void; busy: boolean }) {
  return (
    <div className="space-y-4">
      <InfoNote>{tx('Video harici bir adreste yeni sekmede açılır (güvenlik politikası gereği uygulama içine gömülmez). İzledikten sonra modülü tamamlayın.')}</InfoNote>
      <div className="flex flex-wrap gap-2">
        <Button asChild variant="outline">
          <a href={m.videoUrl ?? '#'} target="_blank" rel="noopener noreferrer"><ExternalLink className="size-4" />{' '}{tx('Videoyu aç')}</a>
        </Button>
        {!done && <Button onClick={onComplete} disabled={busy}><Check className="size-4" />{' '}{tx('İzledim, tamamla')}</Button>}
      </div>
    </div>
  )
}

function QuizModule({ courseId, m, onChanged }: { courseId: string; m: CourseModule; onChanged: () => void }) {
  const q = useQuery({ queryKey: ['learning', 'quiz', m.id], queryFn: ({ signal }) => learningContentApi.quiz(courseId, m.id, signal) })
  const [answers, setAnswers] = useState<Record<string, string[]>>({})
  const submit = useAction(() => learningContentApi.attempt(courseId, m.id, answers), {
    invalidate: [['learning', 'quiz', m.id]],
    onDone: () => { setAnswers({}); onChanged() },
  })
  const result = submit.data
  if (q.isPending) return <RowsSkeleton rows={3} columns={1} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const quiz = q.data
  const locked = quiz.passed || (quiz.attemptsLeft !== null && quiz.attemptsLeft <= 0)
  const toggle = (qid: string, oid: string, multiple: boolean) =>
    setAnswers((a) => {
      const cur = a[qid] ?? []
      return { ...a, [qid]: multiple ? (cur.includes(oid) ? cur.filter((x) => x !== oid) : [...cur, oid]) : [oid] }
    })
  const perQ = new Map(result?.perQuestion.map((p) => [p.questionId, p.correct]) ?? [])
  return (
    <div className="space-y-4">
      <p className="text-[13px] text-muted-foreground">
        {tx('Geçme notu %{0}', [quiz.passMarkPercent])}
        {' · '}
        {quiz.maxAttempts === null ? tx('sınırsız deneme') : tx('{0} / {1} deneme kullanıldı', [quiz.attemptsUsed, quiz.maxAttempts])}
      </p>
      {result && (
        <div className={cn('rounded-xl border px-4 py-3 text-[13.5px]', result.passed ? 'border-[hsl(var(--success))]/40 bg-[hsl(var(--success))]/8' : 'border-destructive/30 bg-destructive/5')}>
          <strong>{result.passed ? tx('Geçtiniz') : tx('Geçemediniz')}</strong>{' '}
          {tx('Puan %{0} ({1}/{2} doğru).', [result.scorePercent, result.correctCount, result.total])}{' '}
          {!result.passed && result.attemptsLeft !== null && tx('Kalan deneme: {0}.', [result.attemptsLeft])}
          <span className="mt-1 block text-[12px] text-muted-foreground">{tx('Doğru cevaplar paylaşılmaz; yalnızca hangi soruları doğru yanıtladığınız gösterilir.')}</span>
        </div>
      )}
      {quiz.passed && !result && <InfoNote>{tx('Bu sınavı geçtiniz.')}</InfoNote>}
      {!quiz.passed && locked && !result && <InfoNote>{tx('Deneme hakkınız bitti. Yeniden kayıt için İK ile görüşün.')}</InfoNote>}
      <ol className="space-y-4">
        {quiz.questions.map((qq, i) => (
          <li key={qq.id} className="rounded-xl border border-border p-4">
            <p className="text-[14px] font-medium">
              {i + 1}. {qq.text}
              {qq.kind === 'Multiple' && <span className="ml-2 text-[12px] font-normal text-muted-foreground">{tx('(birden fazla seçilebilir)')}</span>}
              {perQ.has(qq.id) && <StatusBadge className="ml-2" tone={perQ.get(qq.id) ? 'success' : 'danger'}>{perQ.get(qq.id) ? tx('Doğru') : tx('Yanlış')}</StatusBadge>}
            </p>
            <div className="mt-2 space-y-1.5">
              {qq.options.map((o) => (
                <label key={o.id} className="flex min-h-9 cursor-pointer items-center gap-2.5 text-[13.5px]">
                  <input
                    type={qq.kind === 'Multiple' ? 'checkbox' : 'radio'}
                    name={`q-${qq.id}`}
                    disabled={locked || submit.isPending}
                    checked={(answers[qq.id] ?? []).includes(o.id)}
                    onChange={() => toggle(qq.id, o.id, qq.kind === 'Multiple')}
                    className="size-4 accent-[hsl(var(--primary))]"
                  />
                  {o.text}
                </label>
              ))}
            </div>
          </li>
        ))}
      </ol>
      {!locked && (
        <Button onClick={() => submit.mutate(undefined)} disabled={submit.isPending || Object.keys(answers).length === 0}>
          {tx('Yanıtları gönder')}
        </Button>
      )}
    </div>
  )
}

function ScormModule({ courseId, m, onChanged }: { courseId: string; m: CourseModule; onChanged: () => void }) {
  const toast = useToast()
  const [url, setUrl] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const uninstall = useRef<(() => void) | null>(null)
  const changed = useRef(onChanged)
  changed.current = onChanged
  useEffect(() => () => uninstall.current?.(), [])
  useEffect(() => {
    uninstall.current?.()
    uninstall.current = null
    setUrl(null)
  }, [m.id])

  const start = async () => {
    try {
      const l = await learningContentApi.launch(courseId, m.id)
      uninstall.current?.()
      uninstall.current = installScormApi({
        courseId, moduleId: m.id, initial: l.runtime, studentId: l.studentId, studentName: l.studentName,
        onSaved: (r) => { setStatus(r.lessonStatus); changed.current() },
        onError: (e) => toast.stop(errMsg(e, tx('SCORM ilerlemesi kaydedilemedi.'))),
      })
      setStatus(l.runtime.lessonStatus)
      setUrl(l.launchUrl)
    } catch (e) {
      toast.stop(errMsg(e))
    }
  }
  return (
    <div className="space-y-3">
      {!url ? (
        <Button onClick={() => void start()}><PlayCircle className="size-4" />{' '}{tx('Dersi başlat')}</Button>
      ) : (
        <>
          <p className="text-[12.5px] text-muted-foreground">{tx('Ders durumu: {0}. İlerlemeniz otomatik kaydedilir.', [status ?? '—'])}</p>
          <iframe
            title={m.title}
            src={url}
            className="h-[70vh] w-full rounded-xl border border-border bg-white"
            allow="fullscreen"
          />
        </>
      )}
    </div>
  )
}

/* ================================================================== İK: modül/sınav düzenleme */

function ModuleModal({ courseId, initial, onClose }: { courseId: string; initial?: CourseModule; onClose: () => void }) {
  const [f, setF] = useState<ModuleInput>(() => ({
    title: initial?.title ?? '', kind: initial?.kind ?? 'Text', videoUrl: initial?.videoUrl ?? '', textBody: initial?.textBody ?? '',
    passMarkPercent: initial?.passMarkPercent ?? 70, maxAttempts: initial?.maxAttempts ?? 3, scormPackageId: initial?.scormPackageId ?? '',
  }))
  const packages = useQuery({ queryKey: ['learning', 'scorm-packages'], queryFn: ({ signal }) => learningContentApi.scormPackages(signal), enabled: f.kind === 'Scorm' })
  const fileRef = useRef<HTMLInputElement>(null)
  const upload = useAction((file: File) => learningContentApi.uploadScorm(file, f.title || undefined), {
    success: (p) => tx('Paket yüklendi: {0} dosya', [p.fileCount]), invalidate: [['learning', 'scorm-packages']],
    onDone: (p) => setF((x) => ({ ...x, scormPackageId: p.id })),
  })
  const save = useAction(
    () => {
      // Sunucu ScormPackageId'yi Guid? bekler: boş metin JSON'da çözülemez ve tüm istek 400 olur.
      const body: ModuleInput = {
        ...f, maxAttempts: f.maxAttempts ? Number(f.maxAttempts) : null, passMarkPercent: Number(f.passMarkPercent),
        scormPackageId: f.kind === 'Scorm' && f.scormPackageId ? f.scormPackageId : null,
      }
      return initial ? learningContentApi.updateModule(courseId, initial.id, body) : learningContentApi.createModule(courseId, body)
    },
    { success: tx('Modül kaydedildi'), invalidate: [['learning', 'modules', courseId]], onDone: onClose },
  )
  return (
    <Modal open onClose={onClose} size="lg" title={initial ? tx('Modülü düzenle') : tx('Yeni modül')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || !f.title.trim()}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField label={tx('Başlık')} value={f.title} maxLength={200} onChange={(e) => setF({ ...f, title: e.target.value })} required />
          <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => setF({ ...f, kind: v as ModuleKind })}
            options={(Object.keys(moduleKindLabels) as ModuleKind[]).map((k) => ({ value: k, label: moduleKindLabels[k] }))} />
        </div>
        {f.kind === 'Video' && <TextField label={tx('Video bağlantısı')} type="url" placeholder="https://" value={f.videoUrl} onChange={(e) => setF({ ...f, videoUrl: e.target.value })} hint={tx('Öğrenen bağlantıyı yeni sekmede açar.')} />}
        {f.kind === 'Text' && <TextAreaField label={tx('Metin')} rows={10} value={f.textBody} onChange={(e) => setF({ ...f, textBody: e.target.value })} />}
        {f.kind === 'Quiz' && (
          <div className="grid gap-4 sm:grid-cols-2">
            <TextField label={tx('Geçme notu (%)')} type="number" min={1} max={100} value={String(f.passMarkPercent ?? '')} onChange={(e) => setF({ ...f, passMarkPercent: Number(e.target.value) })} />
            <TextField label={tx('En fazla deneme')} type="number" min={1} max={20} value={f.maxAttempts == null ? '' : String(f.maxAttempts)} onChange={(e) => setF({ ...f, maxAttempts: e.target.value ? Number(e.target.value) : null })} hint={tx('Boş: sınırsız')} />
          </div>
        )}
        {f.kind === 'Scorm' && (
          <div className="space-y-3">
            <SelectField label={tx('SCORM 1.2 paketi')} value={f.scormPackageId ?? ''} onChange={(v) => setF({ ...f, scormPackageId: v })}
              options={(packages.data ?? []).map((p) => ({ value: p.id, label: `${p.title} (${p.fileCount})` }))} hint={packages.isPending ? tx('Yükleniyor') : undefined} />
            <input ref={fileRef} type="file" accept=".zip,application/zip" className="hidden" onChange={(e) => { const file = e.target.files?.[0]; if (file) upload.mutate(file); e.target.value = '' }} />
            <Button variant="outline" onClick={() => fileRef.current?.click()} disabled={upload.isPending}><Upload className="size-4" />{' '}{upload.isPending ? tx('Yükleniyor…') : tx('Yeni paket yükle (.zip, en fazla 50 MB)')}</Button>
            <InfoNote>{tx('Paketin kökünde imsmanifest.xml olmalı; yalnızca SCORM 1.2 desteklenir. İçerik uygulamayla aynı adresten çalışır, yalnızca güvendiğiniz paketleri yükleyin.')}</InfoNote>
          </div>
        )}
      </div>
    </Modal>
  )
}

function QuizEditor({ courseId, m, onClose }: { courseId: string; m: CourseModule; onClose: () => void }) {
  const key = useQuery({ queryKey: ['learning', 'answer-key', m.id], queryFn: ({ signal }) => learningContentApi.answerKey(courseId, m.id, signal) })
  const [qs, setQs] = useState<QuizQuestionInput[] | null>(null)
  useEffect(() => {
    if (key.data && qs === null) setQs(key.data.length ? key.data.map(({ text, kind, options, correct }) => ({ text, kind, options, correct })) : [])
  }, [key.data, qs])
  const list = qs ?? []
  const save = useAction(() => learningContentApi.saveQuiz(courseId, m.id, list), {
    success: tx('Sorular kaydedildi'), invalidate: [['learning', 'modules', courseId], ['learning', 'answer-key', m.id]], onDone: onClose,
  })
  const set = (i: number, q: QuizQuestionInput) => setQs(list.map((x, j) => (j === i ? q : x)))
  const letters = 'abcdefghij'
  return (
    <Modal open onClose={onClose} size="xl" title={tx('Sınav soruları: {0}', [m.title])} note={tx('Doğru cevaplar yalnızca burada (İK) görünür; öğrenene gönderilmez, puanlama sunucuda yapılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || list.length === 0}>{tx('Kaydet')}</Button></>}>
      {key.isPending ? <RowsSkeleton rows={3} columns={1} /> : (
        <div className="space-y-4">
          {list.map((q, i) => (
            <div key={i} className="space-y-2 rounded-xl border border-border p-3">
              <div className="flex items-start gap-2">
                <div className="flex-1"><TextField label={tx('{0}. soru', [i + 1])} value={q.text} onChange={(e) => set(i, { ...q, text: e.target.value })} /></div>
                <div className="w-40"><SelectField label={tx('Tür')} value={q.kind} onChange={(v) => set(i, { ...q, kind: v as 'Single' | 'Multiple', correct: v === 'Single' ? q.correct.slice(0, 1) : q.correct })}
                  options={[{ value: 'Single', label: tx('Tek seçim') }, { value: 'Multiple', label: tx('Çoklu seçim') }]} /></div>
                <Button size="sm" variant="ghost" className="mt-6" aria-label={tx('Soruyu sil')} onClick={() => setQs(list.filter((_, j) => j !== i))}><Trash2 className="size-4" /></Button>
              </div>
              {q.options.map((o, k) => (
                <div key={o.id} className="flex items-center gap-2">
                  <Checkbox checked={q.correct.includes(o.id)} aria-label={tx('Doğru seçenek')}
                    onCheckedChange={(v) => set(i, { ...q, correct: v === true ? (q.kind === 'Single' ? [o.id] : [...q.correct, o.id]) : q.correct.filter((c) => c !== o.id) })} />
                  <input className="h-9 flex-1 rounded-lg border border-input bg-background/60 px-2.5 text-[13.5px]" value={o.text}
                    onChange={(e) => set(i, { ...q, options: q.options.map((x, j) => (j === k ? { ...x, text: e.target.value } : x)) })} aria-label={tx('Seçenek {0}', [o.id])} />
                  <Button size="sm" variant="ghost" aria-label={tx('Seçeneği sil')} disabled={q.options.length <= 2}
                    onClick={() => set(i, { ...q, options: q.options.filter((_, j) => j !== k), correct: q.correct.filter((c) => c !== o.id) })}><Trash2 className="size-3.5" /></Button>
                </div>
              ))}
              {q.options.length < 10 && (
                <Button size="sm" variant="outline" onClick={() => {
                  const id = [...letters].find((l) => !q.options.some((o) => o.id === l)) ?? String(q.options.length)
                  set(i, { ...q, options: [...q.options, { id, text: '' }] })
                }}><Plus className="size-3.5" />{' '}{tx('Seçenek')}</Button>
              )}
            </div>
          ))}
          <Button variant="outline" onClick={() => setQs([...list, { text: '', kind: 'Single', options: [{ id: 'a', text: '' }, { id: 'b', text: '' }], correct: [] }])}>
            <Plus className="size-4" />{' '}{tx('Soru ekle')}
          </Button>
        </div>
      )}
    </Modal>
  )
}

function ManagePanel({ courseId, modules, validityMonths }: { courseId: string; modules: CourseModule[]; validityMonths?: number | null }) {
  const [edit, setEdit] = useState<CourseModule | 'new' | null>(null)
  const [quizFor, setQuizFor] = useState<CourseModule | null>(null)
  const del = useAction((id: string) => learningContentApi.deleteModule(courseId, id), { success: tx('Modül silindi'), invalidate: [['learning', 'modules', courseId]] })
  const comps = useQuery({ queryKey: ['learning', 'competencies'], queryFn: ({ signal }) => learningContentApi.competencies(signal) })
  const tags = useQuery({ queryKey: ['learning', 'course-tags', courseId], queryFn: ({ signal }) => learningContentApi.courseTags(courseId, signal) })
  const [draft, setDraft] = useState<{ competencyId: string; targetLevel: number }[] | null>(null)
  const current = draft ?? tags.data?.map((t) => ({ competencyId: t.competencyId, targetLevel: t.targetLevel })) ?? []
  const saveTags = useAction(() => learningContentApi.saveCourseTags(courseId, current), {
    success: tx('Yetkinlik etiketleri kaydedildi'), invalidate: [['learning', 'course-tags', courseId]], onDone: () => setDraft(null),
  })
  // Kullanıcı yazana kadar kurs verisindeki değer gösterilir (veri sonradan gelse de dolar).
  const [validityDraft, setValidity] = useState<string | null>(null)
  const validity = validityDraft ?? (validityMonths != null ? String(validityMonths) : '')
  const saveValidity = useAction(() => learningContentApi.courseSettings(courseId, validity ? Number(validity) : null), {
    success: tx('Sertifika geçerliliği kaydedildi'), invalidate: [['learning', 'courses', 'detail', courseId]], onDone: () => setValidity(null),
  })
  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={tx('Modüller')} note={tx('Tüm modüller tamamlanınca (sınav geçildi, SCORM passed/completed) kayıt tamamlanır ve sertifika verilir.')}
          action={<Button size="sm" onClick={() => setEdit('new')}><Plus className="size-4" />{' '}{tx('Modül ekle')}</Button>} />
        <PanelBody className="p-0">
          {modules.length === 0 ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Henüz modül yok.')}</p> : (
            <ul className="divide-y divide-border">
              {modules.map((m) => {
                const Icon = kindIcon[m.kind]
                return (
                  <li key={m.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13.5px]">
                    <Icon className="size-4 text-primary" aria-hidden />
                    <span className="min-w-0 flex-1">{m.title}<span className="block text-[12px] text-muted-foreground">{moduleKindLabels[m.kind]}{m.kind === 'Quiz' ? ` · ${tx('{0} soru, geçme %{1}', [m.questionCount, m.passMarkPercent ?? 0])}` : ''}</span></span>
                    {m.kind === 'Quiz' && <Button size="sm" variant="outline" onClick={() => setQuizFor(m)}>{tx('Sorular')}</Button>}
                    <Button size="sm" variant="ghost" onClick={() => setEdit(m)}>{tx('Düzenle')}</Button>
                    <Button size="sm" variant="ghost" aria-label={tx('Sil')} onClick={() => del.mutate(m.id)}><Trash2 className="size-4" /></Button>
                  </li>
                )
              })}
            </ul>
          )}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Geliştirdiği yetkinlikler')} note={tx('Yetkinlik açığı olan çalışanlara bu eğitim öneri olarak gösterilir (otomatik kayıt yapılmaz).')}
          action={<Button size="sm" onClick={() => saveTags.mutate(undefined)} disabled={saveTags.isPending || draft === null}>{tx('Kaydet')}</Button>} />
        <PanelBody className="space-y-2">
          {current.map((t, i) => (
            <div key={i} className="flex items-end gap-2">
              <div className="flex-1"><SelectField label={tx('Yetkinlik')} value={t.competencyId} onChange={(v) => setDraft(current.map((x, j) => (j === i ? { ...x, competencyId: v } : x)))}
                options={(comps.data ?? []).map((c) => ({ value: c.id, label: c.name }))} /></div>
              <div className="w-44"><SelectField label={tx('Hedef seviye')} value={String(t.targetLevel)} onChange={(v) => setDraft(current.map((x, j) => (j === i ? { ...x, targetLevel: Number(v) } : x)))}
                options={[1, 2, 3, 4, 5].map((l) => ({ value: String(l), label: competencyLevelLabels[l]! }))} /></div>
              <Button variant="ghost" aria-label={tx('Kaldır')} onClick={() => setDraft(current.filter((_, j) => j !== i))}><Trash2 className="size-4" /></Button>
            </div>
          ))}
          <Button size="sm" variant="outline" disabled={!comps.data?.length} onClick={() => setDraft([...current, { competencyId: comps.data![0]!.id, targetLevel: 3 }])}><Plus className="size-4" />{' '}{tx('Yetkinlik ekle')}</Button>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Sertifika')} note={tx('Tamamlayan çalışana doğrulama kodlu sertifika verilir. Geçerlilik girilirse bitişten 30 ve 7 gün önce hatırlatma gider.')} />
        <PanelBody className="flex flex-wrap items-end gap-3">
          <div className="w-56"><TextField label={tx('Geçerlilik (ay)')} type="number" min={1} max={120} value={validity} onChange={(e) => setValidity(e.target.value)} hint={tx('Boş: süresiz')} /></div>
          <Button variant="outline" onClick={() => saveValidity.mutate(undefined)} disabled={saveValidity.isPending}>{tx('Kaydet')}</Button>
        </PanelBody>
      </Panel>
      {edit && <ModuleModal courseId={courseId} initial={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} />}
      {quizFor && <QuizEditor courseId={courseId} m={quizFor} onClose={() => setQuizFor(null)} />}
    </div>
  )
}

function ResultsPanel({ courseId }: { courseId: string }) {
  const q = useQuery({ queryKey: ['learning', 'results', courseId], queryFn: ({ signal }) => learningContentApi.results(courseId, signal) })
  if (q.isPending) return <RowsSkeleton rows={3} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  if (!q.data.length) return <EmptyState title={tx('Sonuç yok')} detail={tx('Görebileceğiniz bir kayıt bulunmuyor.')} />
  return (
    <Panel>
      <PanelHead title={tx('Sonuçlar')} note={tx('Yalnızca İK ve çalışanın departman yöneticisi görür; her görüntüleme erişim kaydına yazılır.')} />
      <PanelBody className="p-0">
        <ul className="divide-y divide-border">
          {q.data.map((r) => (
            <li key={r.progress.enrollmentId} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13.5px]">
              <span className="min-w-0 flex-1 font-medium">{r.name}<span className="block text-[12px] font-normal text-muted-foreground">
                {tx('{0}/{1} modül', [r.progress.completedModules, r.progress.totalModules])}{r.progress.score != null ? ` · ${tx('puan {0}', [r.progress.score])}` : ''}{r.progress.completedAt ? ` · ${formatDate(r.progress.completedAt)}` : ''}</span></span>
              <EnrollmentBadge status={r.progress.status} />
              {r.progress.certificate && <Button asChild size="sm" variant="ghost"><Link to={`/panel/egitim/sertifika/${r.progress.certificate.id}`}><Award className="size-4" />{' '}{tx('Sertifika')}</Link></Button>}
            </li>
          ))}
        </ul>
      </PanelBody>
    </Panel>
  )
}

function EnrollmentBadge({ status }: { status: string }) {
  const map: Record<string, [StatusTone, string]> = {
    Enrolled: ['neutral', tx('Kayıtlı')], InProgress: ['info', tx('Sürüyor')], Completed: ['success', tx('Tamamlandı')],
    Failed: ['danger', tx('Başarısız')], Dropped: ['neutral', tx('Bırakıldı')],
  }
  const [tone, label] = map[status] ?? ['neutral', status]
  return <StatusBadge tone={tone}>{label}</StatusBadge>
}

/* ================================================================== sayfa */

export function CoursePlayerPage() {
  const { courseId = '' } = useParams()
  const navigate = useNavigate()
  const { can } = useAuth()
  const isHr = can('learning:manage')
  const isManager = can('employee:viewAll')
  const [tab, setTab] = useTabParam<TabKey>('gorunum', 'icerik')
  const course = useQuery({ queryKey: ['learning', 'courses', 'detail', courseId], queryFn: ({ signal }) => learningApi.getCourse(courseId, signal) })
  const modules = useQuery({ queryKey: ['learning', 'modules', courseId], queryFn: ({ signal }) => learningContentApi.modules(courseId, signal) })
  const { employeeId } = useMyEmployeeId()
  // Çalışan kaydı olmayan kullanıcıda (ör. yalnızca İK/platform hesabı) kendi ilerlemesi yoktur; istek atılmaz (404 önlenir).
  const progress = useQuery({ queryKey: ['learning', 'progress', courseId], queryFn: ({ signal }) => learningContentApi.progress(courseId, undefined, signal), retry: false, enabled: Boolean(employeeId) })
  const [active, setActive] = useState<string | null>(null)
  const list = useMemo(() => modules.data ?? [], [modules.data])
  const current = list.find((m) => m.id === active) ?? list[0]
  const p: CourseProgress | undefined = progress.data?.progress
  const statusOf = (id: string): ModuleStatus => p?.modules.find((x) => x.moduleId === id)?.status ?? 'NotStarted'
  const refresh = () => { void progress.refetch(); void course.refetch() }

  const enroll = useAction(() => learningApi.enroll(courseId, employeeId!), { success: tx('Eğitime kaydoldunuz'), invalidate: [['learning']] })
  const complete = useAction((id: string) => learningContentApi.completeModule(courseId, id), {
    onDone: (r) => { refresh(); if (r.certificateId) navigate(`/panel/egitim/sertifika/${r.certificateId}`) },
  })

  const tabs: Array<TabDef<TabKey>> = [
    { key: 'icerik', label: tx('İçerik') },
    ...(isManager ? [{ key: 'sonuclar' as TabKey, label: tx('Sonuçlar') }] : []),
    ...(isHr ? [{ key: 'yonetim' as TabKey, label: tx('İçerik yönetimi') }] : []),
  ]

  if (course.error instanceof ApiError && (course.error.status === 404 || course.error.status === 400))
    return <EmptyState title={tx('Eğitim bulunamadı')} detail={tx('Bağlantı hatalı olabilir ya da eğitim kaldırılmış.')} action={<Button asChild size="sm" variant="outline"><Link to="/panel/egitim">{tx('Eğitimler')}</Link></Button>} />
  if (course.isError) return <ErrorState message={errMsg(course.error)} onRetry={() => void course.refetch()} />
  const finished = p && ['Completed', 'Failed', 'Dropped'].includes(p.status)

  return (
    <div className="space-y-5">
      <PageHeader
        title={course.data?.title ?? tx('Eğitim')}
        description={course.data?.description ?? undefined}
        actions={<Button asChild variant="outline"><Link to="/panel/egitim"><ArrowLeft className="size-4" />{' '}{tx('Kataloğa dön')}</Link></Button>}
      />
      <Tabs tabs={tabs} value={tab} onChange={setTab} label={tx('Eğitim görünümü')} />

      {tab === 'icerik' && (
        <>
          {p ? (
            <Panel>
              <PanelBody className="flex flex-wrap items-center gap-4">
                <div className="min-w-48 flex-1">
                  <ProgressBar value={p.completedModules} max={p.totalModules || 1} tone={p.status === 'Completed' ? 'success' : 'info'} label={tx('Eğitim ilerlemesi')} />
                  <p className="mt-1.5 text-[12.5px] text-muted-foreground">{tx('{0}/{1} modül tamamlandı', [p.completedModules, p.totalModules])}</p>
                </div>
                <EnrollmentBadge status={p.status} />
                {p.certificate && (
                  <Button asChild size="sm"><Link to={`/panel/egitim/sertifika/${p.certificate.id}`}><Printer className="size-4" />{' '}{tx('Sertifikayı yazdır')}</Link></Button>
                )}
              </PanelBody>
            </Panel>
          ) : progress.data && !progress.data.enrolled && employeeId ? (
            <InfoNote>
              {tx('Bu eğitime kayıtlı değilsiniz.')}{' '}
              <Button size="sm" className="ml-2" onClick={() => enroll.mutate(undefined)} disabled={enroll.isPending}>{tx('Kaydol')}</Button>
            </InfoNote>
          ) : null}

          {modules.isPending ? <RowsSkeleton rows={4} columns={2} /> : list.length === 0 ? (
            <EmptyState icon={BookOpen} title={tx('İçerik henüz eklenmedi')} detail={isHr ? tx('İçerik yönetimi sekmesinden modül ekleyin.') : tx('Bu eğitim için çevrim içi içerik yok.')} />
          ) : (
            <div className="grid gap-5 lg:grid-cols-[280px_1fr]">
              <Panel className="h-fit">
                <PanelBody className="p-2">
                  <ol className="space-y-1">
                    {list.map((m, i) => {
                      const Icon = kindIcon[m.kind]
                      const st = statusOf(m.id)
                      return (
                        <li key={m.id}>
                          <button type="button" onClick={() => setActive(m.id)}
                            className={cn('flex w-full cursor-pointer items-center gap-2.5 rounded-lg px-3 py-2.5 text-left text-[13.5px] hover:bg-muted', current?.id === m.id && 'bg-primary/10 text-primary')}>
                            <Icon className="size-4 shrink-0" aria-hidden />
                            <span className="min-w-0 flex-1 truncate">{i + 1}. {m.title}</span>
                            {st !== 'NotStarted' && <StatusBadge tone={statusTone[st]}>{statusLabel[st]}</StatusBadge>}
                          </button>
                        </li>
                      )
                    })}
                  </ol>
                </PanelBody>
              </Panel>
              {current && (
                <Panel>
                  <PanelHead title={current.title} note={moduleKindLabels[current.kind]} />
                  <PanelBody>
                    {!p ? (
                      <p className="text-[13px] text-muted-foreground">{isHr ? tx('Önizleme: öğrenen görünümü için eğitime kayıtlı olmak gerekir.') : tx('İçeriği görmek için önce kaydolun.')}</p>
                    ) : current.kind === 'Text' ? (
                      <TextModule m={current} done={statusOf(current.id) === 'Completed' || !!finished} busy={complete.isPending} onComplete={() => complete.mutate(current.id)} />
                    ) : current.kind === 'Video' ? (
                      <VideoModule m={current} done={statusOf(current.id) === 'Completed' || !!finished} busy={complete.isPending} onComplete={() => complete.mutate(current.id)} />
                    ) : current.kind === 'Quiz' ? (
                      <QuizModule key={current.id} courseId={courseId} m={current} onChanged={refresh} />
                    ) : (
                      <ScormModule courseId={courseId} m={current} onChanged={refresh} />
                    )}
                  </PanelBody>
                </Panel>
              )}
            </div>
          )}
        </>
      )}

      {tab === 'sonuclar' && isManager && <ResultsPanel courseId={courseId} />}
      {tab === 'yonetim' && isHr && <ManagePanel courseId={courseId} modules={list} validityMonths={course.data?.certificateValidityMonths} />}
    </div>
  )
}
