import { useEffect, useMemo } from 'react'

/**
 * Herkese açık formlar (etik bildirimi, iş başvurusu) için bot koruması (güvenlik dalgası 2B).
 * Form açılınca sunucudan imzalı zaman jetonu alınır; gönderimde jeton eklenir. Sunucu jetonu en
 * erken birkaç saniye sonra ve bir kez kabul eder: kullanıcı çok hızlıysa gönderim kısa süre bekletilir,
 * kullanılan jetonun yerine hemen yenisi alınır (hata sonrası yeniden deneme için).
 */
export interface FormToken {
  token: string
  minSeconds: number
}

/** Jetonun kabul edileceği ana kadar kalan süre (ms); küçük bir pay eklenir. */
export function remainingWait(issuedAt: number, minSeconds: number, now = Date.now()): number {
  return Math.max(0, issuedAt + minSeconds * 1000 + 300 - now)
}

export class FormTokenHolder {
  private current: Promise<{ t: FormToken; at: number } | null> | null = null

  constructor(
    private readonly fetcher: () => Promise<FormToken>,
    private readonly sleep: (ms: number) => Promise<void> = (ms) => new Promise((r) => setTimeout(r, ms)),
    private readonly now: () => number = () => Date.now(),
  ) {}

  /** Jetonu önceden alır (form açılırken). Hata yutulur; gönderimde yeniden denenir. */
  prepare(): void {
    if (!this.current) {
      this.current = this.fetcher().then((t) => ({ t, at: this.now() }), () => null)
    }
  }

  /** Gönderim için jeton: gerekirse alır ve en az bekleme süresi dolana kadar bekler; sonra yenisini hazırlar. */
  async take(): Promise<string | undefined> {
    this.prepare()
    let got = await this.current
    if (!got) {
      this.current = null
      this.prepare()
      got = await this.current
    }
    this.current = null
    if (!got) return undefined
    const wait = remainingWait(got.at, got.t.minSeconds, this.now())
    if (wait > 0) await this.sleep(wait)
    this.prepare()
    return got.t.token
  }
}

/** Bileşen bağlanınca jetonu hazırlayan tutucu. `key` değişirse (ör. kiracı) yenisi oluşturulur. */
export function useFormToken(fetcher: () => Promise<FormToken>, key: string): FormTokenHolder {
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const holder = useMemo(() => new FormTokenHolder(fetcher), [key])
  useEffect(() => holder.prepare(), [holder])
  return holder
}
