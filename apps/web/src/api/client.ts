import { getValidToken, keycloak, readTenantSlug } from '@/auth/keycloak'
import { PLATFORM_TENANT_HEADER, hasPlatformGrant, needsPlatformGrant, platformTenantFor, readGrantTenant } from '@/auth/platformAccess'
import { env } from '@/lib/env'
import { lang, translateServerData, tx } from '@/lib/i18n'
import { QueuedOfflineError, enqueueOffline, isQueueable } from '@/lib/push'

/**
 * `?optional=true` ile çağrılan `/me` uçları, hesaba bağlı çalışan kaydı yoksa
 * 404 yerine 204 döner (tarayıcı konsolunda kırmızı hata oluşmasın). Mevcut
 * çağıranlar "kayıt yok" durumunu 404 ApiError olarak beklediği için yanıt
 * burada aynı hataya çevrilir.
 */
export function requireLinked<T>(v: T | undefined): T {
  if (v === undefined || v === null) throw new ApiError(404, tx('Bu hesaba bağlı çalışan kaydı bulunamadı'))
  return v
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public detail?: unknown,
  ) {
    super(message)
    this.name = 'ApiError'
  }

  /** Sıralı onay kuralı ihlali gibi iş kuralı hataları. */
  get isBusinessRule() {
    return this.status === 400 || this.status === 409
  }
}

/** ASP.NET Core'un durum koduna göre ürettiği ProblemDetails başlıkları (küçük harf). */
const ASPNET_DEFAULT_TITLES = new Set([
  'not found', 'bad request', 'unauthorized', 'forbidden', 'conflict', 'internal server error',
  'service unavailable', 'bad gateway', 'payload too large', 'request entity too large', 'method not allowed',
  'an error occurred while processing your request.',
])

/** Backend hata gövdesinden okunabilir bir Türkçe mesaj çıkarır. */
async function toApiError(res: Response): Promise<ApiError> {
  let detail: unknown
  let message = ''

  try {
    const text = await res.text()
    if (text) {
      try {
        detail = JSON.parse(text)
        if (typeof detail === 'string') {
          // ASP.NET Core'da BadRequest("düz metin") gövdeyi {detail:...}
          // gibi bir nesne değil, DOĞRUDAN bir JSON string olarak döner
          // (örn. "Kalem tutarı sıfırdan büyük olmalı"). Aşağıdaki nesne
          // alanı okuması (d.detail/d.title/...) bunu hiç yakalamıyordu -
          // backend'in verdiği anlamlı, alan-bazlı hata mesajı kullanıcıya
          // hiç ulaşmıyor, "İstek geçersiz" jenerik mesajına düşüyordu.
          message = detail
        } else {
          const d = (detail ?? {}) as Record<string, unknown>
          // FastAPI (ML servisi) doğrulama hatası: {"detail": [{"msg": "...", "loc": [...]}]}
          const fastApiList = Array.isArray(d.detail)
            ? (d.detail as Array<{ msg?: unknown }>).map((x) => (typeof x?.msg === 'string' ? x.msg : '')).filter(Boolean).join('; ')
            : ''
          message =
            (typeof d.detail === 'string' && d.detail) ||
            fastApiList ||
            (typeof d.title === 'string' && d.title) ||
            (typeof d.message === 'string' && d.message) ||
            (typeof d.error === 'string' && d.error) ||
            ''
        }
      } catch {
        // JSON değil: nginx'in HTML hata sayfası (413, 502…) kullanıcıya gösterilmez, durum koduna göre ileti seçilir.
        if (!/^\s*</.test(text)) message = text.slice(0, 300)
      }
    }
  } catch {
    /* gövde okunamadı */
  }

  // ASP.NET'in varsayılan İngilizce iletileri (NotFound()/ProblemDetails title, model doğrulama)
  // kullanıcıya ham gösterilmez: doğrulama özel iletiyle, diğerleri durum koduna göre Türkçeleşir.
  if (/^One or more validation errors occurred\.?$/i.test(message.trim())) message = tx('Gönderilen bilgiler geçersiz.')
  else if (ASPNET_DEFAULT_TITLES.has(message.trim().toLowerCase())) message = ''

  if (!message) {
    const byStatus: Record<number, string> = {
      400: tx('İstek geçersiz. Girdiğiniz bilgileri kontrol edin.'),
      401: tx('Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.'),
      403: tx('Bu işlem için yetkiniz yok.'),
      404: tx('Kayıt bulunamadı.'),
      409: tx('İşlem mevcut durumla çakışıyor.'),
      413: tx('Dosya çok büyük. Daha küçük bir dosya seçin.'),
      500: tx('Sunucu hatası oluştu. Lütfen daha sonra tekrar deneyin.'),
      502: tx('Servise ulaşılamıyor (gateway).'),
      503: tx('Servis geçici olarak kullanılamıyor.'),
    }
    message = byStatus[res.status] ?? tx('Beklenmeyen hata (HTTP {0}).', [res.status])
  }

  return new ApiError(res.status, message, detail)
}

/**
 * Güvenlik dalgası 2A: platform yöneticisinin seçtiği kiracı başlığı (yoksa boş). apiFetch dışında
 * doğrudan fetch yapan yardımcılar (yükleme, indirme, olay akışı) da eklemeli.
 */
export function platformTenantHeaders(): Record<string, string> {
  const roles = (keycloak.tokenParsed as { realm_access?: { roles?: string[] } } | undefined)?.realm_access?.roles ?? []
  const target = platformTenantFor(roles, readTenantSlug(), readGrantTenant())
  return target ? { [PLATFORM_TENANT_HEADER]: target } : {}
}

const GRANT_REQUIRED = 'platform_access_grant_required'

/** Platform yöneticisi izinsiz kiracı verisi isterse istek gönderilmeden 403 verilir. */
async function assertPlatformGrant(path: string) {
  const roles = (keycloak.tokenParsed as { realm_access?: { roles?: string[] } } | undefined)?.realm_access?.roles ?? []
  if (!roles.includes('platform-admin') || !needsPlatformGrant(path)) return
  const target = platformTenantFor(roles, readTenantSlug(), readGrantTenant())
  const ok = target
    ? await hasPlatformGrant(target, () =>
        apiFetch<{ tenantSlug: string; active: boolean; expiresAt: string }[]>('/api/tenant/platform-access/grants/mine'))
    : false
  if (!ok)
    throw new ApiError(403, tx('Bu şirketin verisini görmek için Kiracılar ekranından süreli erişim izni açın.'), { code: GRANT_REQUIRED })
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
  body?: unknown
  signal?: AbortSignal
  /** Auth gerektirmeyen uçlar (ör. /gateway/health) için. */
  anonymous?: boolean
  /** Çevrimdışı kuyruğa alma (kuyruğu boşaltırken kullanılır). */
  noQueue?: boolean
  /** Ek başlıklar (ör. kiosk tabletinin X-Device-Key'i; oturumsuz uçlarla birlikte). */
  headers?: Record<string, string>
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, signal, anonymous = false, noQueue = false, headers: extraHeaders } = options

  // Çevrimdışıyken izinli talepler (izin, fazla mesai) cihazda sıraya alınır (PWA).
  if (!noQueue && typeof navigator !== 'undefined' && !navigator.onLine && isQueueable(path, method)) {
    enqueueOffline(path, body)
    throw new QueuedOfflineError()
  }

  // Sunucu yanıt metinlerini (rapor asistanı, hata iletileri…) arayüz dilinde üretsin.
  const headers: Record<string, string> = { Accept: 'application/json', 'X-HR360-Lang': lang, ...extraHeaders }

  if (!anonymous) {
    const token = await getValidToken()
    if (!token) {
      // Refresh başarısız — oturumu yenilemek için Keycloak'a yönlendir.
      throw new ApiError(401, tx('Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.'))
    }
    headers.Authorization = `Bearer ${token}`
    // Platform yöneticisi: seçili kiracı (süreli erişim izni servislerde denetlenir).
    Object.assign(headers, platformTenantHeaders())
    await assertPlatformGrant(path)
  }

  if (body !== undefined) headers['Content-Type'] = 'application/json'

  let res: Response
  try {
    res = await fetch(`${env.apiBase}${path}`, {
      method,
      headers,
      signal,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch (e) {
    if (!noQueue && e instanceof TypeError && isQueueable(path, method)) {
      enqueueOffline(path, body)
      throw new QueuedOfflineError()
    }
    throw e
  }

  if (res.status === 401 && !anonymous) {
    keycloak.clearToken()
    throw new ApiError(401, tx('Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.'))
  }

  if (!res.ok) throw await toApiError(res)

  if (res.status === 204) return undefined as T

  /*
   * Gateway'de route'u olmayan bir /api yolu nginx'e düşüyor ve arayüzün
   * kendi index.html'i 200 ile dönüyor. JSON.parse bunu "Unexpected token <"
   * diye patlatırdı — kullanıcı ne olduğunu anlamaz. Açık bir hataya çevir.
   */
  const contentType = res.headers.get('content-type') ?? ''
  if (contentType.includes('text/html')) {
    throw new ApiError(
      503,
      tx('Servis bu adreste yanıt vermiyor. Gateway yönlendirmesi tanımlı olmayabilir.'),
      { path },
    )
  }

  const text = await res.text()
  return (text ? translateServerData(JSON.parse(text)) : undefined) as T
}

/** Sunucu tarafı sayfalı liste yanıtı (G24): `?page=` verilen liste uçları döner. */
export interface Paged<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/**
 * Sorgu dizesi kurar; tanımsız ve boş değerleri atar.
 * Modül servisleri bunu paylaşır, her dosyada tekrarlanmaz.
 */
export function qs(params: object): string {
  const search = new URLSearchParams()
  // Arayüzlerde index imzası olmadığı için Record yerine object alınır.
  for (const [key, value] of Object.entries(params) as [string, unknown][]) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const out = search.toString()
  return out ? `?${out}` : ''
}

/**
 * Dosya yükleme uçları için — apiFetch'ten ayrı, çünkü apiFetch body'yi
 * her zaman JSON'a çevirir. FormData'da Content-Type header'ını TARAYICI
 * kendi koyar (boundary içerir); elle eklemek isteği bozar.
 */
export async function apiUploadFile<T>(
  path: string,
  file: File,
  fieldName = 'file',
): Promise<T> {
  const token = await getValidToken()
  if (!token) {
    throw new ApiError(401, tx('Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.'))
  }

  const formData = new FormData()
  formData.append(fieldName, file)

  const res = await fetch(`${env.apiBase}${path}`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'X-HR360-Lang': lang, ...platformTenantHeaders() },
    body: formData,
  })

  if (res.status === 401) {
    keycloak.clearToken()
    throw new ApiError(401, tx('Oturumunuzun süresi doldu. Lütfen yeniden giriş yapın.'))
  }

  // Hata gövdesi JSON olmayabilir (nginx 413 HTML sayfası); ortak ayrıştırıcı güvenli okur.
  if (!res.ok) throw await toApiError(res)

  const text = await res.text()
  try {
    return (text ? JSON.parse(text) : undefined) as T
  } catch {
    throw new ApiError(502, tx('Sunucudan beklenmeyen yanıt alındı.'))
  }
}
