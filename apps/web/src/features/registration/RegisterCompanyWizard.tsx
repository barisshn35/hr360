/**
 * Şirket kaydı sihirbazı — çok kiracılılığın giriş kapısı.
 *
 * Kaynak: 21st.dev "Onboarding Wizard Form" (cnippet-dev, id 25064). Adım
 * göstergesi, adım adım ilerleme, özet ve başarı ekranı yapısı oradan.
 *
 * Uyarlamalar:
 *  - Şablonun alan bileşenleri Base UI üzerine kuruluydu; projede zaten Radix
 *    tabanlı shadcn primitifleri var. İkinci bir bileşen kitaplığı taşımamak
 *    için alanlar mevcut primitiflerle yeniden kuruldu.
 *  - Üç adım: şirket → yönetici → özet.
 *  - Slug alanında canlı müsaitlik kontrolü (debounce 400 ms).
 *
 * KRİTİK: `POST /api/tenant/registration` ANONİM bir uç — bu ekran oturum
 * açmadan çalışır, istek Authorization başlığı taşımaz (bkz. tenantApi.register).
 */

import { AmbientBackground } from '@/components/fx/ambient-background'
import { Lamp } from '@/components/fx/lamp'
import { BorderBeam } from '@/components/fx/border-beam'
import { TextReveal } from '@/components/fx/text-reveal'
import { GradientText } from '@/components/fx/shiny-text'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import {
  ArrowLeft,
  ArrowRight,
  Check,
  CircleCheck,
  LoaderCircle,
  X,
} from 'lucide-react'
import { ApiError } from '@/api/client'
import { slugify } from '@/api/tenant'
import { usePublicConfig, useRegisterTenant, useSlugAvailability } from '@/api/queries-tenant'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Callout } from '@/components/ui/callout'
import { EASE } from '@/motion/primitives'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'
import { PasswordLinkCard } from '@/features/access/PasswordLinkCard'
import { SiteFooter } from '@/features/site/SiteChrome'
import type { ThanksState } from '@/features/site/ThanksPage'

const STEPS = [tx('Şirket'), tx('Yönetici'), tx('Özet')] as const
type StepIndex = 0 | 1 | 2

interface FormState {
  companyName: string
  slug: string
  taxNumber: string
  emailDomain: string
  adminFullName: string
  adminEmail: string
}

type Errors = Partial<Record<keyof FormState, string>>

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/
const SLUG_RE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/
const DOMAIN_RE = /^[a-z0-9-]+(\.[a-z0-9-]+)+$/

function validate(step: StepIndex, values: FormState, slugTaken: boolean): Errors {
  const errors: Errors = {}

  if (step === 0) {
    if (values.companyName.trim().length < 2) errors.companyName = tx('Şirket adı en az 2 karakter olmalı.')
    if (!values.slug) errors.slug = tx('Kısa ad zorunlu.')
    else if (values.slug.length < 3) errors.slug = tx('Kısa ad en az 3 karakter olmalı.')
    else if (values.slug.length > 40) errors.slug = tx('Kısa ad en fazla 40 karakter olabilir.')
    else if (!SLUG_RE.test(values.slug))
      errors.slug = tx('Yalnızca küçük harf, rakam ve tire kullanın (tire başta/sonda olamaz).')
    else if (slugTaken) errors.slug = tx('Bu kısa ad kullanımda. Başka bir tane deneyin.')

    if (values.taxNumber && !/^\d{10,11}$/.test(values.taxNumber))
      errors.taxNumber = tx('Vergi numarası 10 veya 11 hane olmalı.')

    if (values.emailDomain && !DOMAIN_RE.test(values.emailDomain.toLowerCase()))
      errors.emailDomain = tx('Geçerli bir alan adı girin (ör. sirket.com.tr).')
  }

  if (step === 1) {
    if (values.adminFullName.trim().length < 3)
      errors.adminFullName = tx('Yöneticinin ad soyadını girin.')
    if (!EMAIL_RE.test(values.adminEmail.trim()))
      errors.adminEmail = tx('Geçerli bir e-posta adresi girin.')
  }

  return errors
}

function Field({
  id,
  label,
  hint,
  error,
  optional,
  children,
}: {
  id: string
  label: string
  hint?: string
  error?: string
  optional?: boolean
  children: React.ReactNode
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id} className="text-[13px]">
        {label}
        {optional && <span className="font-normal text-muted-foreground">{tx('(isteğe bağlı)')}</span>}
      </Label>
      {children}
      {error ? (
        <p role="alert" className="text-[12px] leading-relaxed text-destructive">
          {error}
        </p>
      ) : hint ? (
        <p className="text-[12px] leading-relaxed text-muted-foreground">{hint}</p>
      ) : null}
    </div>
  )
}

/**
 * Herkese açık kayıt kurulumda kapatılmışsa (PUBLIC_SIGNUP_ENABLED=false) sihirbaz yerine
 * bilgi gösterilir; şirketleri platform yöneticisi açar. Ayar alınamazsa sihirbaz açılır.
 */
export function RegisterCompanyWizard() {
  const config = usePublicConfig()
  if (config.data?.signupEnabled === false) return <RegistrationClosed />
  return <RegisterCompanyWizardForm />
}

function RegistrationClosed() {
  return (
    <main className="flex min-h-svh flex-col items-center bg-background px-4 pt-10">
      <div role="status" className="my-auto w-full max-w-md rounded-2xl border border-border bg-card p-6 text-center shadow-sm">
        <h1 className="text-xl font-semibold">{tx('Kayıt kapalı')}</h1>
        <p className="mt-3 text-[14px] leading-relaxed text-muted-foreground">
          {tx('Bu kurulumda herkese açık şirket kaydı kapalı. Şirket hesabı açtırmak için platform yöneticinizle iletişime geçin.')}
        </p>
        <div className="mt-6 flex flex-wrap justify-center gap-2">
          <Button asChild>
            <Link to="/giris">{tx('Giriş ekranına git')}</Link>
          </Button>
          <Button asChild variant="outline">
            <Link to="/iletisim">{tx('İletişim bilgileri')}</Link>
          </Button>
        </div>
      </div>
      <SiteFooter className="mt-12" />
    </main>
  )
}

function RegisterCompanyWizardForm() {
  const reduced = useReducedMotion()
  const [step, setStep] = useState<StepIndex>(0)
  // Geçişin yönü: ileri +1, geri -1. Slayt yönü buradan geliyor.
  const direction = useRef(1)
  const goToStep = (next: StepIndex) => {
    direction.current = next >= step ? 1 : -1
    setStep(next)
  }
  const [errors, setErrors] = useState<Errors>({})
  const [slugTouched, setSlugTouched] = useState(false)
  const [values, setValues] = useState<FormState>({
    companyName: '',
    slug: '',
    taxNumber: '',
    emailDomain: '',
    adminFullName: '',
    adminEmail: '',
  })

  const register = useRegisterTenant()
  const navigate = useNavigate()

  // Parola linki ekranda gösterilecekse kayıt ekranında kalınır (link başka sayfaya taşınmaz);
  // değilse (e-postayla gönderildi / yöneticiye başvurulacak) teşekkür sayfasına geçilir.
  useEffect(() => {
    if (!register.isSuccess || register.data.passwordLink?.link) return
    const status = register.data.passwordLink?.emailStatus
    const state: ThanksState = { company: register.data.companyName, emailSent: status === 'queued' || status === 'keycloak' }
    navigate('/tesekkurler?neden=kayit', { replace: true, state })
  }, [register.isSuccess, register.data, navigate])

  /* --------------------------- Slug müsaitlik kontrolü --------------------------- */
  const [debouncedSlug, setDebouncedSlug] = useState('')
  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSlug(values.slug), 400)
    return () => window.clearTimeout(id)
  }, [values.slug])

  const slugQueryable = SLUG_RE.test(debouncedSlug) && debouncedSlug.length >= 3
  const slugCheck = useSlugAvailability(debouncedSlug, slugQueryable)
  const slugSettled = slugQueryable && debouncedSlug === values.slug && slugCheck.isSuccess
  const slugTaken = slugSettled && slugCheck.data?.available === false

  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setValues((prev) => {
      // Kısa ad kullanıcı elle değiştirene kadar şirket adından türetilir.
      if (key === 'companyName' && !slugTouched) {
        return { ...prev, companyName: value as string, slug: slugify(value as string) }
      }
      return { ...prev, [key]: value }
    })
    setErrors((prev) => ({ ...prev, [key]: undefined }))
  }

  const goNext = () => {
    const found = validate(step, values, Boolean(slugTaken))
    setErrors(found)
    if (Object.keys(found).length === 0) goToStep(Math.min(2, step + 1) as StepIndex)
  }

  const submit = () => {
    // Son bir kez iki veri adımını da doğrula — özetten geri dönülmüş olabilir.
    const found = { ...validate(0, values, Boolean(slugTaken)), ...validate(1, values, false) }
    if (Object.keys(found).length > 0) {
      setErrors(found)
      goToStep(found.companyName || found.slug || found.taxNumber || found.emailDomain ? 0 : 1)
      return
    }
    register.mutate({
      companyName: values.companyName.trim(),
      adminEmail: values.adminEmail.trim(),
      adminFullName: values.adminFullName.trim() || undefined,
      slug: values.slug,
      emailDomain: values.emailDomain.trim().toLowerCase() || undefined,
      taxNumber: values.taxNumber.trim() || undefined,
    })
  }

  const submitError = useMemo(() => {
    const error = register.error
    if (!error) return null
    if (error instanceof ApiError) {
      if (error.status === 409) {
        return {
          title: tx('Bu bilgilerle zaten bir kayıt var'),
          detail: error.message,
        }
      }
      if (error.status === 404 || error.status === 502 || error.status === 503) {
        return {
          title: tx('Kayıt servisine ulaşılamıyor'),
          detail:
            tx('Sunucu şu anda yanıt vermiyor. Birkaç dakika sonra tekrar deneyin; sorun sürerse bize ulaşın.'),
        }
      }
      return { title: tx('Kayıt tamamlanamadı'), detail: error.message }
    }
    return {
      title: tx('Kayıt tamamlanamadı'),
      detail: tx('Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.'),
    }
  }, [register.error])

  /* ---------------------------------- Başarı ---------------------------------- */
  if (register.isSuccess) {
    const result = register.data
    const link = result.passwordLink
    return (
      <main className="relative flex min-h-dvh flex-col items-center overflow-hidden bg-background px-5 pt-16">
      <AmbientBackground />
      <Lamp className="opacity-70" />
        <div className={cn('surface relative my-auto w-full rounded-2xl p-8 text-center', link?.link ? 'max-w-lg' : 'max-w-md')}>
          <BorderBeam size={200} duration={8} />
          <motion.span
            initial={reduced ? false : { scale: 0.5, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            transition={{ type: 'spring', stiffness: 260, damping: 16 }}
            className="relative mx-auto mb-5 flex size-12 items-center justify-center rounded-full bg-[hsl(var(--success))]/12"
          >
            {!reduced && (
              <span className="hr-ring absolute inset-0 rounded-full border-2 border-[hsl(var(--success))]" />
            )}
            <CircleCheck className="size-6 text-[hsl(var(--success))]" strokeWidth={1.75} />
          </motion.span>
          <h1 className="text-2xl font-semibold tracking-tight">{tx('Kaydınız alındı')}</h1>
          <p className="mt-3 text-[14px] leading-relaxed text-muted-foreground">
            <strong className="font-medium text-foreground">{result.companyName}</strong>{' '}
            {link?.link
              ? tx('için çalışma alanı hazır. Parolanızı aşağıdaki süreli linkle belirleyin.')
              : link?.emailStatus === 'queued' || link?.emailStatus === 'keycloak'
                ? tx('için çalışma alanı hazırlanıyor. E-postanıza parola belirleme bağlantısı gönderildi.')
                : tx('için çalışma alanı hazırlanıyor. Parola belirleme linki için platform yöneticisine başvurun.')}
          </p>

          {link?.link && (
            <div className="mt-6 text-left">
              <PasswordLinkCard view={link} />
            </div>
          )}

          <dl className="mt-6 rounded-xl border border-border bg-muted/40 p-4 text-left text-[13px]">
            <div className="flex items-baseline justify-between gap-4 py-1">
              <dt className="text-muted-foreground">{tx('Kısa ad')}</dt>
              <dd className="font-mono font-medium">{result.slug}</dd>
            </div>
            <div className="flex items-baseline justify-between gap-4 py-1">
              <dt className="text-muted-foreground">{tx('Yönetici e-postası')}</dt>
              <dd className="truncate font-medium">{values.adminEmail}</dd>
            </div>
          </dl>

          {!link?.link && (
            <p className="mt-6 text-[12px] leading-relaxed text-muted-foreground">
              {tx('Bağlantı gelmediyse istenmeyen posta klasörünü kontrol edin. Kurulum birkaç dakika sürebilir.')}
            </p>
          )}

          <Button className="mt-7 w-full cursor-pointer" asChild>
            <Link to="/giris">{tx('Giriş ekranına git')}</Link>
          </Button>
        </div>
        <SiteFooter className="mt-12" />
      </main>
    )
  }

  /* --------------------------------- Sihirbaz --------------------------------- */
  return (
    <main className="relative min-h-dvh overflow-hidden bg-background px-5 py-10 sm:py-16">
      <AmbientBackground />
      <Lamp className="opacity-70" />
      <div className="relative mx-auto w-full max-w-3xl">
        <Link
          to="/"
          className="mb-8 inline-flex items-center gap-2 text-[17px] font-semibold tracking-tight"
        >
          <img
            src="/icon-emerald.svg"
            alt=""
            aria-hidden="true"
            className="size-7 shrink-0"
          />
          {tx('HR360')}
        </Link>

        <h1 className="text-[30px] leading-tight font-semibold tracking-[-0.04em] sm:text-[42px]">
          <TextReveal text={tx('Şirketinizi')} /> <GradientText>{tx('kaydedin')}</GradientText>
        </h1>
        <p className="mt-2 max-w-xl text-[14px] leading-relaxed text-muted-foreground">
          {tx('Birkaç dakika sürer. Kaydı tamamladığınızda şirketiniz için ayrı bir çalışma alanı oluşturulur ve yönetici hesabınıza parola belirleme bağlantısı gönderilir.')}
        </p>

        {/* Adım göstergesi */}
        <ol className="mt-8 mb-7 flex items-center gap-2" aria-label={tx('Kayıt adımları')}>
          {STEPS.map((label, index) => (
            <li key={label} className="flex flex-1 items-center gap-2">
              <span
                aria-current={index === step ? 'step' : undefined}
                className={cn(
                  'tabular flex size-6 shrink-0 items-center justify-center rounded-full text-[11px] font-semibold',
                  index < step
                    ? 'bg-primary text-primary-foreground shadow-[0_0_14px_-2px_hsl(var(--primary)/0.8)]'
                    : index === step
                      ? 'bg-primary/15 text-primary ring-2 ring-primary/40'
                      : 'bg-muted text-muted-foreground ring-1 ring-border',
                )}
              >
                {index < step ? <Check className="size-3.5" strokeWidth={3} /> : index + 1}
              </span>
              <span
                className={cn(
                  'hidden text-xs font-medium sm:inline',
                  index === step ? 'text-foreground' : 'text-muted-foreground',
                )}
              >
                {label}
              </span>
              {index < STEPS.length - 1 && (
                <span aria-hidden="true" className="relative h-px min-w-4 flex-1 bg-border">
                  <motion.span
                    className="absolute inset-0 origin-left bg-primary"
                    initial={false}
                    animate={{ scaleX: index < step ? 1 : 0 }}
                    transition={{ duration: reduced ? 0 : 0.4, ease: EASE }}
                  />
                </span>
              )}
            </li>
          ))}
        </ol>

        <div className="surface relative rounded-2xl p-5 sm:p-8">
          <BorderBeam size={240} duration={14} />
          {/*
            Adımlar arası geçiş. İleri giderken içerik sağdan, geri dönerken
            soldan geliyor — kullanıcı hangi yöne hareket ettiğini görüyor.
            `mode="wait"` iki adımın üst üste binmesini engelliyor.
          */}
          <AnimatePresence mode="wait" initial={false} custom={direction.current}>
            <motion.div
              key={step}
              custom={direction.current}
              initial={reduced ? false : { opacity: 0, x: direction.current * 28 }}
              animate={{ opacity: 1, x: 0 }}
              exit={reduced ? undefined : { opacity: 0, x: direction.current * -28 }}
              transition={{ duration: 0.32, ease: EASE }}
            >
          {/* ------------------------------ 1. Şirket ------------------------------ */}
          {step === 0 && (
            <div className="flex flex-col gap-5">
              <Field id="companyName" label={tx('Şirket adı')} error={errors.companyName}>
                <Input
                  id="companyName"
                  value={values.companyName}
                  onChange={(e) => set('companyName', e.target.value)}
                  placeholder={tx('Acme Holding A.Ş.')}
                  autoComplete="organization"
                  aria-invalid={Boolean(errors.companyName)}
                />
              </Field>

              <Field
                id="slug"
                label={tx('Kısa ad')}
                error={errors.slug}
                hint={tx('Çalışma alanınızın adresinde ve kayıtlarınızda kullanılır. Sonradan değiştirilemez.')}
              >
                <div className="relative">
                  <Input
                    id="slug"
                    value={values.slug}
                    onChange={(e) => {
                      setSlugTouched(true)
                      set('slug', e.target.value.toLowerCase())
                    }}
                    placeholder="acme-holding"
                    className="pr-10 font-mono"
                    aria-invalid={Boolean(errors.slug) || Boolean(slugTaken)}
                    aria-describedby="slug-durum"
                  />
                  <span className="absolute top-1/2 right-3 -translate-y-1/2">
                    {slugQueryable && slugCheck.isFetching ? (
                      <LoaderCircle className="size-4 animate-spin text-muted-foreground" />
                    ) : slugSettled && slugCheck.data?.available ? (
                      <Check className="size-4 text-[hsl(var(--success))]" strokeWidth={2.5} />
                    ) : slugTaken ? (
                      <X className="size-4 text-destructive" strokeWidth={2.5} />
                    ) : null}
                  </span>
                </div>
                <p id="slug-durum" aria-live="polite" className="sr-only">
                  {slugCheck.isFetching
                    ? tx('Kısa ad kontrol ediliyor')
                    : slugTaken
                      ? tx('Bu kısa ad kullanımda')
                      : slugSettled
                        ? tx('Bu kısa ad müsait')
                        : ''}
                </p>
                {slugSettled && slugCheck.data?.available && !errors.slug && (
                  <p className="text-[12px] text-[hsl(var(--success))]">{tx('Bu kısa ad müsait.')}</p>
                )}
                {slugCheck.isError && (
                  <p className="text-[12px] text-muted-foreground">
                    {tx('Müsaitlik kontrolü yapılamadı; kaydı yine de deneyebilirsiniz.')}
                  </p>
                )}
              </Field>

              <div className="grid gap-5 sm:grid-cols-2">
                <Field id="taxNumber" label={tx('Vergi numarası')} optional error={errors.taxNumber}>
                  <Input
                    id="taxNumber"
                    value={values.taxNumber}
                    inputMode="numeric"
                    onChange={(e) => set('taxNumber', e.target.value.replace(/\D/g, ''))}
                    placeholder="1234567890"
                    className="tabular"
                    aria-invalid={Boolean(errors.taxNumber)}
                  />
                </Field>

                <Field
                  id="emailDomain"
                  label={tx('E-posta alan adı')}
                  optional
                  error={errors.emailDomain}
                  hint={tx('Bu alan adındaki çalışanlar otomatik eşleştirilir.')}
                >
                  <Input
                    id="emailDomain"
                    value={values.emailDomain}
                    onChange={(e) => set('emailDomain', e.target.value)}
                    placeholder="acme.com.tr"
                    aria-invalid={Boolean(errors.emailDomain)}
                  />
                </Field>
              </div>
            </div>
          )}

          {/* ----------------------------- 2. Yönetici ----------------------------- */}
          {step === 1 && (
            <div className="flex flex-col gap-5">
              <p className="text-[13px] leading-relaxed text-muted-foreground">
                {tx('Bu kişi şirketinizin ilk yöneticisi olur: çalışanları ekler, rolleri dağıtır ve onay zincirlerini kurar.')}
              </p>

              <Field id="adminFullName" label={tx('Ad soyad')} error={errors.adminFullName}>
                <Input
                  id="adminFullName"
                  value={values.adminFullName}
                  onChange={(e) => set('adminFullName', e.target.value)}
                  placeholder={tx('Ayşe Yılmaz')}
                  autoComplete="name"
                  aria-invalid={Boolean(errors.adminFullName)}
                />
              </Field>

              <Field
                id="adminEmail"
                label="E-posta"
                error={errors.adminEmail}
                hint={tx('Parola belirleme bağlantısı bu adrese gönderilir.')}
              >
                <Input
                  id="adminEmail"
                  type="email"
                  value={values.adminEmail}
                  onChange={(e) => set('adminEmail', e.target.value)}
                  placeholder="ayse.yilmaz@acme.com.tr"
                  autoComplete="email"
                  aria-invalid={Boolean(errors.adminEmail)}
                />
              </Field>
            </div>
          )}

          {/* -------------------------------- 3. Özet ------------------------------- */}
          {step === 2 && (
            <div className="flex flex-col gap-5">
              <p className="text-[13px] leading-relaxed text-muted-foreground">
                {tx('Bilgileri gözden geçirin. Bir şeyi değiştirmek için ilgili adıma dönebilirsiniz.')}
              </p>

              <dl className="divide-y divide-border rounded-lg border border-border">
                {[
                  { label: tx('Şirket adı'), value: values.companyName, step: 0 as StepIndex },
                  { label: tx('Kısa ad'), value: values.slug, step: 0 as StepIndex, mono: true },
                  { label: tx('Vergi numarası'), value: values.taxNumber || '—', step: 0 as StepIndex },
                  {
                    label: tx('E-posta alan adı'),
                    value: values.emailDomain || '—',
                    step: 0 as StepIndex,
                  },
                  { label: tx('Yönetici'), value: values.adminFullName, step: 1 as StepIndex },
                  { label: tx('Yönetici e-postası'), value: values.adminEmail, step: 1 as StepIndex },
                ].map((row) => (
                  <div
                    key={row.label}
                    className="flex flex-wrap items-baseline justify-between gap-2 px-4 py-2.5"
                  >
                    <dt className="text-[13px] text-muted-foreground">{row.label}</dt>
                    <dd className="flex items-baseline gap-3">
                      <span className={cn('text-[13px] font-medium', row.mono && 'font-mono')}>
                        {row.value}
                      </span>
                      <button
                        type="button"
                        onClick={() => goToStep(row.step)}
                        className="cursor-pointer text-[12px] text-primary underline-offset-2 hover:underline"
                      >
                        {tx('Düzenle')}
                      </button>
                    </dd>
                  </div>
                ))}
              </dl>

              {submitError && (
                <div role="alert">
                  <Callout tone="danger" title={submitError.title}>
                    <p className="break-words">{submitError.detail}</p>
                  </Callout>
                </div>
              )}
            </div>
          )}

            </motion.div>
          </AnimatePresence>

          {/* ------------------------------- Gezinme ------------------------------- */}
          <div className="mt-7 flex items-center justify-between gap-3 border-t border-border pt-5">
            {step > 0 ? (
              <Button
                type="button"
                variant="outline"
                className="cursor-pointer"
                disabled={register.isPending}
                onClick={() => goToStep(Math.max(0, step - 1) as StepIndex)}
              >
                <ArrowLeft className="size-4" />
                {tx('Geri')}
              </Button>
            ) : (
              <Button type="button" variant="ghost" className="cursor-pointer" asChild>
                <Link to="/">
                  <ArrowLeft className="size-4" />
                  {tx('Vazgeç')}
                </Link>
              </Button>
            )}

            {step < 2 ? (
              <Button type="button" className="cursor-pointer" onClick={goNext}>
                {tx('Devam')}
                <ArrowRight className="size-4" />
              </Button>
            ) : (
              <Button
                type="button"
                className="cursor-pointer"
                disabled={register.isPending}
                onClick={submit}
              >
                {register.isPending && <LoaderCircle className="size-4 animate-spin" />}
                {register.isPending ? tx('Gönderiliyor') : tx('Kaydı tamamla')}
              </Button>
            )}
          </div>
        </div>

        <p className="mt-6 text-center text-[13px] text-muted-foreground">{tx('Şirketiniz zaten kayıtlı mı?', [])}{' '}
          <Link to="/giris" className="font-medium text-primary underline-offset-2 hover:underline">
            {tx('Giriş yapın')}
          </Link>
        </p>
      </div>
      <SiteFooter className="mt-12" />
    </main>
  )
}
