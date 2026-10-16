/**
 * Giriş sayfası — tek sütun, sahne ışıklı.
 *
 * 21st.dev kaynakları:
 *  - "Lamp" (aceternity, id 900): üstten vuran zümrüt ışık konisi
 *  - "Background Beams With Collision" (aceternity, id 1497): zemine çarpıp
 *    kıvılcım saçan hüzmeler
 *  - "Border Beam" (Magic UI, id 1268): giriş kartının kenarında dolaşan ışık
 *  - "Text Generate Effect" / "Animated Shiny Text": başlık ve rozet
 *  - "Orbiting Circles" (Magic UI, id 1411): marka işaretinin yörüngesi
 *  - "Logo Marquee" (ddoemonn, id 23537) + "Blurred Marquee" (grootstudio, id 20147):
 *    bağlanabildiği uygulamaların logo şeridi
 *
 * Kimlik doğrulama yalnızca Keycloak üzerinden (Authorization Code + PKCE).
 * Parola belirleme/sıfırlama HR360'ın süreli linkiyle yapılır; parola yalnızca
 * Keycloak'a iletilir, HR360 saklamaz.
 */

import { Link, Navigate, useSearchParams } from 'react-router-dom'
import { motion, useReducedMotion } from 'motion/react'
import {
  ArrowRight,
  BadgeDollarSign,
  CalendarClock,
  CalendarDays,
  ClipboardCheck,
  GraduationCap,
  KeyRound,
  LockKeyhole,
  ShieldCheck,
  Target,
  TriangleAlert,
  UserPlus,
  Wallet,
  Building2,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { AppShellSkeleton } from '@/components/ui/AppShellSkeleton'
import { OfflineScreen } from '@/components/errors/OfflineScreen'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { Lamp } from '@/components/fx/lamp'
import { Beams } from '@/components/fx/beams'
import { BorderBeam } from '@/components/fx/border-beam'
import { OrbitingCircles } from '@/components/fx/orbiting-circles'
import { TextReveal } from '@/components/fx/text-reveal'
import { GradientText } from '@/components/fx/shiny-text'
import { useAuth } from '@/auth/useAuth'
import { usePublicConfig } from '@/api/queries-tenant'
import { EASE } from '@/motion/primitives'
import { tx } from '@/lib/i18n'
import { IntegrationsMarquee } from './IntegrationsMarquee'
import { SiteFooter } from '@/features/site/SiteChrome'
import { CustomDomainBrandBadge, loginWithTenantHint, useCustomDomainBranding } from './CustomDomainBranding'

const MODULES = [
  { icon: CalendarDays, label: tx('İzin') },
  { icon: Wallet, label: tx('Masraf') },
  { icon: Target, label: tx('Performans') },
  { icon: CalendarClock, label: tx('Vardiya') },
  { icon: UserPlus, label: tx('İşe alım') },
  { icon: ClipboardCheck, label: tx('Onboarding') },
  { icon: BadgeDollarSign, label: tx('Ücret') },
  { icon: GraduationCap, label: tx('Eğitim') },
]

const TRUST = [
  { icon: LockKeyhole, text: tx('Parola yalnızca kimlik sunucusunda saklanır') },
  { icon: ShieldCheck, text: tx('Her istek sunucuda denetlenir') },
  { icon: Building2, text: tx('Her şirketin verisi ayrı') },
]

export function SignInPage() {
  const reduced = useReducedMotion()
  const { status, login, error } = useAuth()
  const [params] = useSearchParams()
  const next = params.get('devam') || '/panel'
  const brand = useCustomDomainBranding()
  // Herkese açık kayıt kapalıysa (PUBLIC_SIGNUP_ENABLED=false) "Şirketinizi kaydedin" gizlenir.
  const signupEnabled = usePublicConfig().data?.signupEnabled !== false

  if (status === 'loading') return <AppShellSkeleton label={tx('Oturum doğrulanıyor')} />
  if (status === 'authenticated') return <Navigate to={next} replace />
  if (status === 'error' && typeof navigator !== 'undefined' && !navigator.onLine) return <OfflineScreen />

  const up = (delay: number) =>
    reduced
      ? {}
      : { initial: { opacity: 0, y: 18 }, animate: { opacity: 1, y: 0 }, transition: { duration: 0.7, delay, ease: EASE } }

  return (
    <main className="relative flex min-h-dvh flex-col items-center overflow-hidden bg-background px-5 text-foreground">
      <AmbientBackground />
      <Lamp />
      <Beams className="top-[45%] opacity-60" />

      <div className="relative z-10 flex w-full max-w-[460px] flex-1 flex-col items-center pt-[14vh] pb-10">
        {/* Marka işareti ve yörüngesi */}
        <motion.div {...up(0)} className="relative flex size-36 items-center justify-center">
          <span className="relative z-10 flex size-16 items-center justify-center rounded-[22px] bg-gradient-to-br from-primary to-[hsl(var(--brand-deep))] shadow-[0_0_60px_-8px_hsl(var(--primary))]">
            <img src="/icon-white.svg" alt="" aria-hidden="true" className="size-9" />
          </span>
          <OrbitingCircles radius={56} duration={12} className="size-2.5 bg-primary shadow-[0_0_12px_hsl(var(--primary))]" />
          <OrbitingCircles radius={56} duration={12} angle={180} path={false} className="size-1.5 bg-[hsl(var(--brand-glow))]" />
        </motion.div>

        <motion.div
          {...up(0.1)}
          className="mt-2 inline-flex items-center gap-2 rounded-full border border-border bg-card/60 px-3.5 py-1 text-[12px] font-medium text-muted-foreground backdrop-blur"
        >
          <span className="size-1.5 animate-pulse rounded-full bg-primary" />
          {tx('HR360 Enterprise')}
        </motion.div>

        <h1 className="mt-5 text-center text-[38px] leading-[1.05] font-semibold tracking-[-0.045em] sm:text-[52px]">
          <TextReveal text={tx('İnsan kaynakları,')} delay={0.15} />
          <br />
          <GradientText>{tx('tek ışıkta.')}</GradientText>
        </h1>
        <motion.p {...up(0.35)} className="mt-4 max-w-sm text-center text-[14.5px] leading-relaxed text-muted-foreground">
          {tx('Kimliğiniz kurumsal kimlik sunucusunda doğrulanır; gördüğünüz ekran rolünüze göre şekillenir.')}
        </motion.p>

        <motion.div {...up(0.45)} className="mt-9 w-full">
          <Card className="gap-0 overflow-hidden p-6 sm:p-7">
            <BorderBeam size={200} duration={9} />
            {brand && <CustomDomainBrandBadge brand={brand} />}

            {error && (
              <div
                role="alert"
                className="mb-5 flex items-start gap-2.5 rounded-xl border border-destructive/30 bg-destructive/[0.08] p-3.5 text-[13px] leading-relaxed"
              >
                <TriangleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-destructive" />
                <span>{tx('Kimlik sağlayıcıya ulaşılamadı. {0}', [error])}</span>
              </div>
            )}

            <Button size="lg" className="group h-12 w-full text-[15px]" onClick={() => (brand ? loginWithTenantHint(brand, next) : login(next))}>
              <KeyRound className="size-4.5 transition-transform group-hover:-rotate-12" strokeWidth={1.75} />
              {tx('Kurumsal hesabımla giriş yap')}
            </Button>

            {signupEnabled && (
              <>
                <div className="my-5 flex items-center gap-3 text-[12px] text-muted-foreground">
                  <div className="h-px flex-1 bg-gradient-to-r from-transparent to-border" />
                  {tx('şirketiniz kayıtlı değil mi?')}
                  <div className="h-px flex-1 bg-gradient-to-l from-transparent to-border" />
                </div>

                <Button variant="outline" size="lg" className="group h-12 w-full" asChild>
                  <Link to="/kayit">
                    {tx('Şirketinizi kaydedin')}
                    <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" />
                  </Link>
                </Button>
              </>
            )}

            <p className="mt-5 text-center text-[11.5px] leading-relaxed text-muted-foreground">
              {tx('Parolanızı unuttuysanız oturum açma ekranındaki “Parolamı unuttum” bağlantısını kullanın. Giriş yapamıyorsanız İK yöneticiniz hesabınızı askıya almış olabilir.')}
            </p>
          </Card>
        </motion.div>

        <motion.ul {...up(0.6)} className="mt-6 flex flex-wrap justify-center gap-x-5 gap-y-2 text-[12px] text-muted-foreground">
          {TRUST.map(({ icon: Icon, text }) => (
            <li key={text} className="flex items-center gap-1.5">
              <Icon aria-hidden="true" className="size-3.5 text-primary" strokeWidth={1.75} />
              {text}
            </li>
          ))}
        </motion.ul>
      </div>

      {/* Bağlanabildiği uygulamalar: modül şeridinin üstünde, ters yönde akar */}
      <motion.div {...up(0.65)} className="relative z-10 w-full max-w-4xl pb-4">
        <IntegrationsMarquee />
      </motion.div>

      {/* Modül kayan şeridi */}
      <motion.div {...up(0.7)} className="hr-marquee-mask relative z-10 w-full max-w-4xl overflow-hidden pb-8" aria-hidden="true">
        <div className="hr-marquee flex w-max gap-3">
          {[...MODULES, ...MODULES].map((m, i) => (
            <span
              key={i}
              className="flex items-center gap-2 rounded-full border border-border bg-card/60 px-3.5 py-1.5 text-[12.5px] text-muted-foreground backdrop-blur"
            >
              <m.icon className="size-3.5 text-primary" strokeWidth={1.75} />
              {m.label}
            </span>
          ))}
        </div>
      </motion.div>

      <SiteFooter className="mt-auto" />
    </main>
  )
}
