/**
 * SCORM 1.2 çalışma zamanı dolgusu (window.API). Paket aynı kökenden bir iframe'de açılır ve
 * API'yi window.parent.API üzerinden bulur. Kalıcı değerler (lesson_status, score.raw,
 * suspend_data, lesson_location) LMSCommit/LMSFinish'te sunucuya yazılır; diğer öğeler oturum
 * içinde tutulur. Hata kodları SCORM 1.2 RTE'ye uygundur.
 */
import { learningContentApi, type ScormRuntimeState } from '@/api/learningContent'

const ERRORS: Record<string, string> = {
  '0': 'No error',
  '101': 'General exception',
  '201': 'Invalid argument error',
  '202': 'Element cannot have children',
  '203': 'Element not an array - cannot have count',
  '301': 'Not initialized',
  '401': 'Not implemented error',
  '402': 'Invalid set value, element is a keyword',
  '403': 'Element is read only',
  '404': 'Element is write only',
  '405': 'Incorrect data type',
}

const PERSISTED = ['cmi.core.lesson_status', 'cmi.core.score.raw', 'cmi.suspend_data', 'cmi.core.lesson_location'] as const
const READ_ONLY = new Set([
  'cmi.core.student_id', 'cmi.core.student_name', 'cmi.core.credit', 'cmi.core.entry', 'cmi.core.total_time',
  'cmi.core.lesson_mode', 'cmi.launch_data', 'cmi.core._children', 'cmi.core.score._children', 'cmi._version',
])
const WRITE_ONLY = new Set(['cmi.core.exit', 'cmi.core.session_time'])
const STATUSES = new Set(['passed', 'completed', 'failed', 'incomplete', 'browsed', 'not attempted'])
const EXITS = new Set(['time-out', 'suspend', 'logout', ''])

export interface ScormShimOptions {
  courseId: string
  moduleId: string
  initial: ScormRuntimeState
  studentId: string
  studentName: string
  /** Sunucu yanıtından sonra (ör. eğitim tamamlandı) çağrılır. */
  onSaved?: (r: { enrollmentStatus: string; certificateId: string | null; lessonStatus: string }) => void
  onError?: (e: unknown) => void
}

export interface ScormApi {
  LMSInitialize(arg: string): string
  LMSFinish(arg: string): string
  LMSGetValue(element: string): string
  LMSSetValue(element: string, value: string): string
  LMSCommit(arg: string): string
  LMSGetLastError(): string
  LMSGetErrorString(code: string): string
  LMSGetDiagnostic(code: string): string
}

declare global {
  interface Window {
    API?: ScormApi
  }
}

/** window.API'yi kurar; dönen işlev kaldırır (bekleyen değişiklikleri de gönderir). */
export function installScormApi(opts: ScormShimOptions): () => void {
  let initialized = false
  let finished = false
  let lastError = '0'
  let diagnostic = ''
  const dirty = new Set<string>()
  let chain: Promise<unknown> = Promise.resolve()

  const values: Record<string, string> = {
    'cmi._version': '3.4',
    'cmi.core._children': 'student_id,student_name,lesson_location,credit,lesson_status,entry,score,total_time,lesson_mode,exit,session_time',
    'cmi.core.score._children': 'raw,min,max',
    'cmi.core.student_id': opts.studentId,
    'cmi.core.student_name': opts.studentName,
    'cmi.core.credit': 'credit',
    'cmi.core.lesson_mode': 'normal',
    'cmi.core.entry': opts.initial.entry,
    'cmi.core.total_time': '0000:00:00.00',
    'cmi.launch_data': '',
    'cmi.core.lesson_status': opts.initial.lessonStatus || 'not attempted',
    'cmi.core.lesson_location': opts.initial.lessonLocation ?? '',
    'cmi.suspend_data': opts.initial.suspendData ?? '',
    'cmi.core.score.raw': opts.initial.scoreRaw == null ? '' : String(opts.initial.scoreRaw),
    'cmi.core.score.min': '',
    'cmi.core.score.max': '',
    'cmi.core.exit': '',
    'cmi.core.session_time': '',
    'cmi.comments': '',
  }

  const fail = (code: string, diag = ''): string => {
    lastError = code
    diagnostic = diag
    return 'false'
  }
  const ok = (v = 'true'): string => {
    lastError = '0'
    diagnostic = ''
    return v
  }

  function send(finish: boolean) {
    const payload: Record<string, string> = {}
    for (const k of PERSISTED) if (dirty.has(k)) payload[k] = values[k]!
    dirty.clear()
    if (!finish && Object.keys(payload).length === 0) return
    chain = chain
      .then(() => learningContentApi.saveRuntime(opts.courseId, opts.moduleId, payload, finish))
      .then((r) => opts.onSaved?.({ enrollmentStatus: r.enrollmentStatus, certificateId: r.certificateId, lessonStatus: r.runtime.lessonStatus }))
      .catch((e) => opts.onError?.(e))
  }

  const api: ScormApi = {
    LMSInitialize(arg) {
      if (arg !== '' && arg != null) return fail('201', 'LMSInitialize argümanı boş olmalı')
      if (initialized) return fail('101', 'Zaten başlatıldı')
      if (finished) return fail('101', 'Oturum bitti')
      initialized = true
      if (values['cmi.core.lesson_status'] === 'not attempted') {
        values['cmi.core.lesson_status'] = 'incomplete'
        dirty.add('cmi.core.lesson_status')
      }
      return ok()
    },
    LMSFinish(arg) {
      if (arg !== '' && arg != null) return fail('201')
      if (!initialized) return fail('301')
      initialized = false
      finished = true
      send(true)
      return ok()
    },
    LMSGetValue(element) {
      if (!initialized) return fail('301') && ''
      if (!element) return fail('201') && ''
      if (WRITE_ONLY.has(element)) return fail('404') && ''
      if (element.endsWith('._count')) return fail('203') && ''
      if (!(element in values)) return fail('401', element) && ''
      return ok(values[element]!)
    },
    LMSSetValue(element, value) {
      if (!initialized) return fail('301')
      const v = value == null ? '' : String(value)
      if (element.endsWith('._children')) return fail('402')
      if (READ_ONLY.has(element)) return fail('403')
      if (!(element in values)) return fail('401', element)
      switch (element) {
        case 'cmi.core.lesson_status':
          if (!STATUSES.has(v) || v === 'not attempted') return fail('405', v)
          break
        case 'cmi.core.score.raw':
        case 'cmi.core.score.min':
        case 'cmi.core.score.max':
          if (v !== '' && (Number.isNaN(Number(v)) || Number(v) < 0 || Number(v) > 100)) return fail('405', v)
          break
        case 'cmi.core.exit':
          if (!EXITS.has(v)) return fail('405', v)
          break
        case 'cmi.suspend_data':
          if (v.length > 4096) return fail('405', 'suspend_data > 4096')
          break
        case 'cmi.core.lesson_location':
          if (v.length > 255) return fail('405', 'lesson_location > 255')
          break
      }
      values[element] = v
      if ((PERSISTED as readonly string[]).includes(element)) dirty.add(element)
      return ok()
    },
    LMSCommit(arg) {
      if (arg !== '' && arg != null) return fail('201')
      if (!initialized) return fail('301')
      send(false)
      return ok()
    },
    LMSGetLastError: () => lastError,
    LMSGetErrorString: (code) => ERRORS[String(code)] ?? '',
    LMSGetDiagnostic: (code) => (code && code !== lastError ? ERRORS[String(code)] ?? '' : diagnostic || ERRORS[lastError] || ''),
  }

  window.API = api
  return () => {
    if (initialized && !finished) send(false)
    if (window.API === api) delete window.API
  }
}
