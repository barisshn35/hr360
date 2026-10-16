/**
 * Teşekkür sayfası: herkese açık şirket kaydı (e-postayla devam eden durum) ve parola belirleme
 * tamamlandığında yönlendirilir. Bağlam ?neden=kayit|parola ile; şirket adı gibi ayrıntılar yalnızca
 * gezinme durumuyla (URL'de değil) taşınır. Parola linki ASLA buraya taşınmaz: link gösterilen kayıt
 * ekranı kendi yerinde kalır.
 */
import { useEffect, useMemo } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import { motion, useReducedMotion } from 'motion/react'
import { ArrowRight, Check, KeyRound, LogIn, MailCheck, Settings2, UserPlus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { Lamp } from '@/components/fx/lamp'
import { EASE } from '@/motion/primitives'
import { tx } from '@/lib/i18n'
import { SiteFooter } from './SiteChrome'

export type ThanksState = { company?: string; emailSent?: boolean }

const COLORS = ['hsl(var(--primary))', 'hsl(var(--brand-glow))', 'hsl(152 70% 60%)', 'hsl(45 95% 60%)', 'hsl(190 85% 60%)']

/** Kutlama: merkezden saçılan, yerçekimiyle düşen konfeti parçaları (tek seferlik). */
function Burst() {
  const pieces = useMemo(
    () =>
      Array.from({ length: 28 }, (_, i) => {
        const angle = (i / 28) * Math.PI * 2 + (i % 3) * 0.2
        const dist = 90 + ((i * 37) % 70)
        return {
          x: Math.cos(angle) * dist,
          y: Math.sin(angle) * dist - 30,
          r: ((i * 53) % 360) - 180,
          c: COLORS[i % COLORS.length],
          w: i % 4 === 0 ? 10 : 6,
          d: 0.02 * (i % 7),
        }
      }),
    [],
  )
  return (
    <div aria-hidden="true" className="pointer-events-none absolute inset-0 flex items-center justify-center">
      {pieces.map((p, i) => (
        <motion.span
          key={i}
          className="absolute rounded-[2px]"
          style={{ width: p.w, height: p.w * 0.45, background: p.c }}
          initial={{ x: 0, y: 0, opacity: 0, rotate: 0, scale: 0.4 }}
          animate={{ x: p.x, y: [0, p.y, p.y + 140], opacity: [0, 1, 1, 0], rotate: p.r * 3, scale: 1 }}
          transition={{ duration: 1.9, delay: 0.25 + p.d, ease: [0.2, 0.7, 0.3, 1], times: [0, 0.35, 1] }}
        />
      ))}
    </div>
  )
}

function SuccessMark() {
  const reduced = useReducedMotion()
  return (
    <div className="relative flex size-40 items-center justify-center">
      {!reduced && <Burst />}
      {!reduced &&
        [0, 1].map((i) => (
          <motion.span
            key={i}
            aria-hidden="true"
            className="absolute size-20 rounded-full border border-[hsl(var(--success))]/50"
            initial={{ scale: 0.6, opacity: 0.8 }}
            animate={{ scale: 2.1, opacity: 0 }}
            transition={{ duration: 1.8, delay: 0.3 + i * 0.6, repeat: Infinity, repeatDelay: 1.2, ease: 'easeOut' }}
          />
        ))}
      <motion.span
        initial={reduced ? false : { scale: 0.3, opacity: 0, rotate: -30 }}
        animate={{ scale: 1, opacity: 1, rotate: 0 }}
        transition={{ type: 'spring', stiffness: 260, damping: 15 }}
        className="relative flex size-20 items-center justify-center rounded-full bg-[hsl(var(--success))]/15 shadow-[0_0_60px_-10px_hsl(var(--success))]"
      >
        <svg viewBox="0 0 24 24" className="size-10 text-[hsl(var(--success))]" fill="none" aria-hidden="true">
          <motion.path
            d="M5 12.5l4.5 4.5L19 7.5"
            stroke="currentColor"
            strokeWidth={2.4}
            strokeLinecap="round"
            strokeLinejoin="round"
            initial={reduced ? false : { pathLength: 0 }}
            animate={{ pathLength: 1 }}
            transition={{ duration: 0.5, delay: 0.25, ease: EASE }}
          />
        </svg>
      </motion.span>
    </div>
  )
}

export function ThanksPage() {
  const reduced = useReducedMotion()
  const [params] = useSearchParams()
  const reason = params.get('neden') === 'parola' ? 'parola' : params.get('neden') === 'kayit' ? 'kayit' : 'genel'
  const state = (useLocation().state ?? {}) as ThanksState
  const up = (delay: number) =>
    reduced ? {} : { initial: { opacity: 0, y: 14 }, animate: { opacity: 1, y: 0 }, transition: { duration: 0.6, delay, ease: EASE } }

  const content = {
    kayit: {
      title: tx('Teşekkürler, kaydınız alındı'),
      detail: state.company
        ? tx('{0} için çalışma alanı hazırlanıyor.', [state.company])
        : tx('Şirketinizin çalışma alanı hazırlanıyor.'),
      steps: [
        state.emailSent === false
          ? { icon: KeyRound, text: tx('Parola belirleme linki için platform yöneticisine başvurun.') }
          : { icon: MailCheck, text: tx('E-postanıza gelen süreli bağlantıyla parolanızı belirleyin. Gelmediyse istenmeyen posta klasörüne bakın.') },
        { icon: LogIn, text: tx('Kurumsal hesabınızla giriş yapın.') },
        { icon: Settings2, text: tx('Şirket ayarlarını tamamlayın, çalışanlarınızı ekleyin ya da içe aktarın.') },
      ],
    },
    parola: {
      title: tx('Teşekkürler, parolanız belirlendi'),
      detail: tx('Hesabınız kullanıma hazır. Artık yeni parolanızla giriş yapabilirsiniz.'),
      steps: [
        { icon: LogIn, text: tx('Kurumsal hesabınızla giriş yapın.') },
        { icon: UserPlus, text: tx('Profilinizi gözden geçirin; eksik bilgileri tamamlayın.') },
      ],
    },
    genel: {
      title: tx('Teşekkürler'),
      detail: tx('İşleminiz tamamlandı.'),
      steps: [{ icon: LogIn, text: tx('Kurumsal hesabınızla giriş yapın.') }],
    },
  }[reason]

  useEffect(() => {
    document.title = `${content.title} · HR360`
  }, [content.title])

  return (
    <div data-testid="page-thanks" data-reason={reason} className="relative flex min-h-dvh flex-col overflow-x-clip bg-background text-foreground">
      <AmbientBackground />
      <Lamp className="opacity-70" />
      <main className="relative z-10 mx-auto flex w-full max-w-lg flex-1 flex-col items-center px-5 pt-[10vh] pb-12 text-center">
        <SuccessMark />
        <motion.h1 {...up(0.15)} className="mt-2 text-[30px] leading-tight font-semibold tracking-[-0.035em] text-balance sm:text-[36px]">
          {content.title}
        </motion.h1>
        <motion.p {...up(0.25)} className="mt-3 text-[15px] leading-relaxed text-muted-foreground">
          {content.detail}
        </motion.p>

        <motion.ol {...up(0.35)} className="mt-8 w-full space-y-2.5 text-left" aria-label={tx('Sonraki adımlar')}>
          {content.steps.map((s, i) => (
            <motion.li
              key={i}
              initial={reduced ? false : { opacity: 0, x: -12 }}
              animate={{ opacity: 1, x: 0 }}
              transition={{ duration: 0.45, delay: 0.45 + i * 0.1, ease: EASE }}
              className="flex items-start gap-3 rounded-xl border border-border bg-card/70 px-4 py-3.5 backdrop-blur"
            >
              <span className="relative mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-lg bg-primary/12 text-primary">
                <s.icon className="size-4" aria-hidden="true" />
              </span>
              <span className="text-[14px] leading-relaxed">
                <span className="mr-1.5 font-medium text-muted-foreground tabular-nums">{i + 1}.</span>
                {s.text}
              </span>
            </motion.li>
          ))}
        </motion.ol>

        <motion.div {...up(0.6)} className="mt-8 flex w-full flex-col gap-2.5 sm:flex-row">
          <Button size="lg" className="group h-12 flex-1" asChild>
            <Link to="/giris">
              <Check className="size-4" aria-hidden="true" />
              {tx('Giriş ekranına git')}
              <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" aria-hidden="true" />
            </Link>
          </Button>
          <Button size="lg" variant="outline" className="h-12" asChild>
            <Link to="/sss">{tx('Sık sorulan sorular')}</Link>
          </Button>
        </motion.div>
      </main>
      <SiteFooter />
    </div>
  )
}
