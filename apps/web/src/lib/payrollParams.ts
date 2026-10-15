import { appLocale, tx, txServer } from '@/lib/i18n'
import { parseDecimal } from '@/lib/format'
import type { PayrollParameterRow, PayrollParametersInput } from '@/api/payroll'

/**
 * Bordro › Parametreler formu (saf; Vitest: payrollParams.test.ts). Yıllık parametreleri bordro yetkilisi
 * girer; uygulama değer varsaymaz. Oranlar formda YÜZDE (14 = %14) girilir, API'ye kesir (0,14) gider.
 * Kurallar sunucudaki PayrollParameterResolver.ValidateInput ile aynıdır (alan adları da aynı).
 */

export interface BracketForm { upTo: string; ratePct: string }

export interface ParamForm {
  minimumWageGross: string
  minimumWageNet: string
  sgkEmployeePct: string
  unemploymentEmployeePct: string
  sgkEmployerPct: string
  incentivePoints: string
  unemploymentEmployerPct: string
  stampPct: string
  ceilingMultiplier: string
  brackets: BracketForm[]
  minimumWageExemption: boolean
  agiMonthly: string
  severanceH1: string
  severanceH2: string
  source: string
}

/** Hata anahtarları sunucunun alan adlarıyla aynı; dilim satırları "brackets.<i>.upTo|rate". */
export type ParamErrors = Record<string, string>

export function emptyForm(): ParamForm {
  return {
    minimumWageGross: '', minimumWageNet: '', sgkEmployeePct: '', unemploymentEmployeePct: '', sgkEmployerPct: '',
    incentivePoints: '', unemploymentEmployerPct: '', stampPct: '', ceilingMultiplier: '',
    brackets: [{ upTo: '', ratePct: '' }, { upTo: '', ratePct: '' }],
    minimumWageExemption: true, agiMonthly: '', severanceH1: '', severanceH2: '', source: '',
  }
}

/** Para/tutar: "33.030,00", "33030,5", "33,030.50" (format.parseDecimal). Boşsa null, sayı değilse NaN. */
export function parseAmount(s: string): number | null {
  if (!s.trim()) return null
  return parseDecimal(s) ?? Number.NaN
}

/**
 * Yüzde/oran: tek ayırıcı her zaman ondalıktır ("0,759", "0.759", "%14", "20,75"); oranlarda binlik
 * ayırıcı olmaz (format.parseDecimal "0.759"u 759 okurdu). Boşsa null, sayı değilse NaN.
 */
export function parsePercent(s: string): number | null {
  const t = s.trim().replace(/^%|%$/g, '').trim()
  if (!t) return null
  if (!/^[+-]?\d*[.,]?\d*$/.test(t) || !/\d/.test(t)) return Number.NaN
  return Number(t.replace(',', '.'))
}

const round = (v: number, digits: number) => Math.round(v * 10 ** digits) / 10 ** digits

/** Formda gösterim: yerel ondalık ayırıcı, binlik ayırıcısız (düzenlemesi kolay). */
export function formatInput(v: number | null | undefined, maxFraction = 6, locale = appLocale): string {
  if (v == null || !Number.isFinite(v)) return ''
  return v.toLocaleString(locale, { useGrouping: false, maximumFractionDigits: maxFraction })
}

const pctOf = (fraction: number, locale?: string) => formatInput(round(fraction * 100, 6), 6, locale)

/** Kayıtlı satırdan form (önceki yıldan doldurma ve düzenleme). Kaynak notu taşınmaz (yeni kaynak yazılmalı). */
export function formFromRow(r: PayrollParameterRow, opts: { keepSource?: boolean; locale?: string } = {}): ParamForm {
  const l = opts.locale
  return {
    minimumWageGross: formatInput(r.minimumWageGross, 2, l),
    minimumWageNet: formatInput(r.minimumWageNet, 2, l),
    sgkEmployeePct: pctOf(r.sgkEmployeeRate, l),
    unemploymentEmployeePct: pctOf(r.unemploymentEmployeeRate, l),
    sgkEmployerPct: pctOf(r.sgkEmployerRate, l),
    incentivePoints: formatInput(r.employerIncentivePoints, 4, l),
    unemploymentEmployerPct: pctOf(r.unemploymentEmployerRate, l),
    stampPct: pctOf(r.stampTaxRate, l),
    ceilingMultiplier: formatInput(r.sgkCeilingMultiplier, 4, l),
    brackets: r.brackets.length
      ? r.brackets.map((b) => ({ upTo: b.upTo == null ? '' : formatInput(b.upTo, 2, l), ratePct: pctOf(b.rate, l) }))
      : emptyForm().brackets,
    minimumWageExemption: r.minimumWageExemption,
    agiMonthly: formatInput(r.agiMonthly, 2, l),
    severanceH1: formatInput(r.severanceCeilingH1, 2, l),
    severanceH2: formatInput(r.severanceCeilingH2, 2, l),
    source: opts.keepSource ? r.source ?? '' : '',
  }
}

/** Formu doğrular; hatasızsa API girdisi döner. */
export function validateForm(f: ParamForm, validFromMonth: number): { errors: ParamErrors; input: PayrollParametersInput | null } {
  const e: ParamErrors = {}
  const required = tx('Zorunlu alan')
  const notNumber = tx('Sayı girin')

  const amount = (key: string, s: string, opt: { required?: boolean; positive?: boolean } = {}) => {
    const v = parseAmount(s)
    if (v == null) { if (opt.required) e[key] = required; return null }
    if (Number.isNaN(v)) { e[key] = notNumber; return null }
    if (opt.positive && v <= 0) { e[key] = tx('Sıfırdan büyük olmalı'); return null }
    if (v < 0) { e[key] = tx('Negatif olamaz'); return null }
    return v
  }
  /** Yüzde alanı: 0–maxPct arası; kesir döner. */
  const pct = (key: string, s: string, maxPct: number) => {
    const v = parsePercent(s)
    if (v == null) { e[key] = required; return null }
    if (Number.isNaN(v)) { e[key] = notNumber; return null }
    if (v < 0 || v > maxPct) { e[key] = tx('%0 ile %{0} arasında olmalı', [maxPct]); return null }
    return round(v / 100, 8)
  }

  const gross = amount('minimumWageGross', f.minimumWageGross, { required: true, positive: true })
  const net = amount('minimumWageNet', f.minimumWageNet, { positive: true })
  if (net != null && gross != null && net > gross) e.minimumWageNet = tx('Net asgari ücret brütten büyük olamaz')

  const sgkEmployee = pct('sgkEmployeeRate', f.sgkEmployeePct, 50)
  const unEmployee = pct('unemploymentEmployeeRate', f.unemploymentEmployeePct, 10)
  const sgkEmployer = pct('sgkEmployerRate', f.sgkEmployerPct, 50)
  const unEmployer = pct('unemploymentEmployerRate', f.unemploymentEmployerPct, 10)
  const stamp = pct('stampTaxRate', f.stampPct, 10)

  let points: number | null = parsePercent(f.incentivePoints)
  if (points == null) { e.employerIncentivePoints = tx('Zorunlu alan (teşvik yoksa 0)') }
  else if (Number.isNaN(points)) { e.employerIncentivePoints = notNumber; points = null }
  else if (points < 0 || points > 20) { e.employerIncentivePoints = tx('0–20 puan olmalı'); points = null }
  else if (sgkEmployer != null && points / 100 > sgkEmployer + 1e-12) { e.employerIncentivePoints = tx('Teşvik indirimi işveren SGK oranını aşamaz'); points = null }

  let mult: number | null = parsePercent(f.ceilingMultiplier)
  if (mult == null) e.sgkCeilingMultiplier = required
  else if (Number.isNaN(mult)) { e.sgkCeilingMultiplier = notNumber; mult = null }
  else if (mult < 1) { e.sgkCeilingMultiplier = tx('SGK tavanı tabandan (asgari ücret) düşük olamaz'); mult = null }
  else if (mult > 20) { e.sgkCeilingMultiplier = tx('En fazla 20 kat olabilir'); mult = null }

  // Gelir vergisi dilimleri: son dilim "üzeri" (sınırsız); üst sınırlar artan, oranlar azalmayan.
  const brackets: Array<{ upTo: number | null; rate: number }> = []
  if (f.brackets.length < 1 || f.brackets.length > 10) e.brackets = tx('1–10 gelir vergisi dilimi girin')
  f.brackets.forEach((b, i) => {
    const last = i === f.brackets.length - 1
    let upTo: number | null = null
    if (!last) {
      const v = parseAmount(b.upTo)
      if (v == null) e[`brackets.${i}.upTo`] = required
      else if (Number.isNaN(v)) e[`brackets.${i}.upTo`] = notNumber
      else if (v <= 0) e[`brackets.${i}.upTo`] = tx('Sıfırdan büyük olmalı')
      else {
        const prev = brackets[i - 1]?.upTo
        if (prev != null && v <= prev) e[`brackets.${i}.upTo`] = tx('Bir önceki dilimin üst sınırından büyük olmalı')
        upTo = v
      }
    }
    const r = parsePercent(b.ratePct)
    let rate = 0
    if (r == null) e[`brackets.${i}.rate`] = required
    else if (Number.isNaN(r)) e[`brackets.${i}.rate`] = notNumber
    else if (r < 0 || r > 100) e[`brackets.${i}.rate`] = tx('%0 ile %{0} arasında olmalı', [100])
    else {
      rate = round(r / 100, 8)
      const prev = brackets[i - 1]
      if (prev && rate < prev.rate) e[`brackets.${i}.rate`] = tx('Oran bir önceki dilimden düşük olamaz')
    }
    brackets.push({ upTo, rate })
  })

  const h1 = amount('severanceCeilingH1', f.severanceH1, { positive: true })
  const h2 = amount('severanceCeilingH2', f.severanceH2, { positive: true })
  const agi = amount('agiMonthly', f.agiMonthly)
  if (f.source.length > 300) e.source = tx('En fazla 300 karakter')
  if (validFromMonth < 1 || validFromMonth > 12) e.validFromMonth = tx('Yürürlük ayı 1–12 olmalı')

  if (Object.keys(e).length) return { errors: e, input: null }
  return {
    errors: e,
    input: {
      validFromMonth, minimumWageGross: gross!, minimumWageNet: net, sgkEmployeeRate: sgkEmployee!, unemploymentEmployeeRate: unEmployee!,
      sgkEmployerRate: sgkEmployer!, employerIncentivePoints: points!, unemploymentEmployerRate: unEmployer!, stampTaxRate: stamp!,
      sgkCeilingMultiplier: mult!, brackets, minimumWageExemption: f.minimumWageExemption, agiMonthly: agi,
      severanceCeilingH1: h1, severanceCeilingH2: h2, source: f.source.trim() || null,
    },
  }
}

/** Sunucunun alan hatalarını (400 invalid_parameters) form hatalarına çevirir (İngilizce arayüzde txServer ile). */
export function serverErrors(detail: unknown): ParamErrors {
  const list = (detail as { errors?: Array<{ field?: string; message?: string }> } | null)?.errors
  const out: ParamErrors = {}
  if (Array.isArray(list)) for (const x of list) if (x.field && x.message && !out[x.field]) out[x.field] = txServer(x.message)
  return out
}

/** Yeni dilim: "üzeri" diliminin önüne boş satır. */
export function addBracket(list: BracketForm[]): BracketForm[] {
  if (list.length >= 10) return list
  const at = Math.max(0, list.length - 1)
  return [...list.slice(0, at), { upTo: '', ratePct: '' }, ...list.slice(at)]
}

export function removeBracket(list: BracketForm[], i: number): BracketForm[] {
  return list.length <= 1 ? list : list.filter((_, j) => j !== i)
}

/** Formdaki asgari ücret ve tavan çarpanından SGK taban/tavan tutarları (geçersizse null). */
export function sgkFloorCeiling(f: ParamForm): { floor: number; ceiling: number } | null {
  const g = parseAmount(f.minimumWageGross)
  const m = parsePercent(f.ceilingMultiplier)
  if (g == null || m == null || Number.isNaN(g) || Number.isNaN(m) || g <= 0) return null
  return { floor: g, ceiling: round(g * m, 2) }
}

/** Satırın durum metni: "Girildi" ya da eski kurulum tohumu ("örnek değer"). */
export function rowStatus(r: Pick<PayrollParameterRow, 'seed'>): 'entered' | 'seed' {
  return r.seed ? 'seed' : 'entered'
}

/**
 * "Önceki değerlerle doldur" kaynağı: aynı yılda bu aydan önce yürürlüğe giren en son satır, yoksa
 * önceki yılların en son satırı.
 */
export function prefillSource(rows: PayrollParameterRow[], previous: PayrollParameterRow | null | undefined, validFromMonth: number) {
  const sameYear = rows.filter((r) => r.validFromMonth < validFromMonth).sort((a, b) => b.validFromMonth - a.validFromMonth)[0]
  return sameYear ?? previous ?? null
}
