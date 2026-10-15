/**
 * HR360 hareket sözlüğü.
 *
 * İki kural:
 *  1. Hareket bilgi taşır — dekorasyon değil. Bir şey belirdiğinde nereden
 *     geldiği, bir sayı değiştiğinde ne kadar değiştiği okunur olmalı.
 *  2. `prefers-reduced-motion` her primitifte karşılanır. Kapatıldığında
 *     içerik kaybolmaz, yalnızca anında yerine oturur.
 */

import {
  animate,
  motion,
  useMotionValue,
  useReducedMotion,
  useScroll,
  useSpring,
  useTransform,
  type MotionValue,
} from 'motion/react'
import {
  Children,
  cloneElement,
  isValidElement,
  useEffect,
  useRef,
  useState,
  type ReactElement,
  type ReactNode,
} from 'react'
import { cn } from '@/lib/utils'

/** Yumuşak "dışarı çık" eğrisi — panelin genelinde tek his için. */
export const EASE = [0.16, 1, 0.3, 1] as const
export const EASE_SOFT = [0.4, 0, 0.2, 1] as const

/* -------------------------------- Görünürlük ------------------------------- */

/**
 * Bir öğenin ekranda olup olmadığını söyler.
 *
 * NEDEN IntersectionObserver DEĞİL: bu ekranların tamamı "görününce beliren"
 * içerik. Gözlemci geri çağırma yapmazsa sayfanın yarısı KALICI OLARAK
 * görünmez kalır — tanıtım sayfasında kabul edilebilir bir hata değil. Böyle
 * ortamlar teorik de değil: bu proje geliştirilirken kullanılan önizleme
 * panelinde ne IntersectionObserver ne de `scroll` olayı tetikleniyordu.
 *
 * Üç katmanlı ölçüm, hepsi `getBoundingClientRect` üzerinden:
 *   1. `scroll` / `resize` dinleyicileri — normal durumda anında yanıt.
 *   2. İlk saniyede kare kare yoklama — yazı tipi ve görsel yerleşirken düzen
 *      kayıyor, ilk ölçüm yanlış olabiliyor.
 *   3. Görünene kadar 250 ms'lik yoklama — olay üretmeyen ortamlar için
 *      emniyet. Öğe göründüğü an duruyor; maliyeti birkaç rect okuması.
 *
 * `amount`, öğenin görünür olması gereken oranı (0–1). Ekrandan uzun öğelerde
 * ölçüt ekran yüksekliğine göre alınır, yoksa asla eşiği geçemezler.
 */
export function useRevealed<T extends HTMLElement>(amount = 0.2, once = true) {
  const ref = useRef<T>(null)
  const [revealed, setRevealed] = useState(false)

  useEffect(() => {
    const el = ref.current
    if (!el) return

    let settled = false
    let raf = 0
    let frames = 0
    let timer = 0

    const visibleEnough = () => {
      const rect = el.getBoundingClientRect()
      const vh = window.innerHeight || document.documentElement.clientHeight
      if (rect.height === 0) return rect.top < vh && rect.bottom > 0
      const visible = Math.min(rect.bottom, vh) - Math.max(rect.top, 0)
      return visible / Math.min(rect.height, vh) >= amount
    }

    const cleanup = () => {
      window.removeEventListener('scroll', check)
      window.removeEventListener('resize', check)
      cancelAnimationFrame(raf)
      window.clearInterval(timer)
    }

    function check() {
      if (settled) return
      if (visibleEnough()) {
        setRevealed(true)
        if (once) {
          settled = true
          cleanup()
        }
      } else if (!once) {
        setRevealed(false)
      }
    }

    const poll = () => {
      check()
      if (!settled && frames++ < 90) raf = requestAnimationFrame(poll)
    }

    window.addEventListener('scroll', check, { passive: true })
    window.addEventListener('resize', check)
    raf = requestAnimationFrame(poll)
    timer = window.setInterval(check, 250)

    return cleanup
  }, [amount, once])

  return [ref, revealed] as const
}

/* ------------------------------- Görünüm girişi ------------------------------ */

type Direction = 'up' | 'down' | 'left' | 'right' | 'none'

const OFFSET: Record<Direction, { x: number; y: number }> = {
  up: { x: 0, y: 22 },
  down: { x: 0, y: -22 },
  left: { x: 22, y: 0 },
  right: { x: -22, y: 0 },
  none: { x: 0, y: 0 },
}

/**
 * Ekrana girince beliren blok. Sayfa boyunca en çok kullanılan primitif.
 * `once` varsayılan: kullanıcı yukarı kaydırdığında içerik yeniden oynamaz.
 */
export function Reveal({
  children,
  delay = 0,
  duration = 0.62,
  from = 'up',
  amount = 0.2,
  once = true,
  className,
  as: Tag = 'div',
}: {
  children: ReactNode
  delay?: number
  duration?: number
  from?: Direction
  amount?: number
  once?: boolean
  className?: string
  as?: 'div' | 'section' | 'li' | 'span' | 'header' | 'article'
}) {
  const reduced = useReducedMotion()
  const [ref, revealed] = useRevealed<HTMLDivElement>(amount, once)
  const offset = OFFSET[from]
  const Component = motion[Tag] as typeof motion.div

  return (
    <Component
      ref={ref}
      className={className}
      initial={reduced ? false : { opacity: 0, ...offset }}
      animate={revealed || reduced ? { opacity: 1, x: 0, y: 0 } : undefined}
      transition={{ duration, delay, ease: EASE }}
    >
      {children}
    </Component>
  )
}

/* --------------------------------- Sıralı giriş ------------------------------ */

interface StaggerItemProps {
  children: ReactNode
  className?: string
  from?: Direction
  as?: 'div' | 'li' | 'article' | 'span'
  /** Stagger tarafından enjekte edilir; çağıran taraf vermez. */
  _index?: number
  _delay?: number
  _step?: number
  _amount?: number
  _once?: boolean
}

/**
 * Çocuklarını sırayla açan kapsayıcı.
 *
 * NEDEN cloneElement: İlk sürüm motion'ın varyant orkestrasyonuna dayanıyordu
 * (kapsayıcıda `whileInView="show"` + `staggerChildren`, çocuklarda isimli
 * varyantlar). Kapsayıcı yalnızca orkestratör olduğu için varyantı boştu ve
 * etiket alt öğelere geçmiyordu — ızgaraların tamamı `opacity: 0` hâlinde
 * donup kalıyordu. Artık kapsayıcı hiç animasyon yapmıyor; her çocuğa yalnızca
 * SIRASI enjekte ediliyor, gecikmeyi çocuk kendi hesaplıyor ve kendi
 * `whileInView`'ıyla açılıyor. Sihir yok, sürpriz de yok.
 */
export function Stagger({
  children,
  className,
  delay = 0,
  step = 0.07,
  amount = 0.2,
  once = true,
  as: Tag = 'div',
}: {
  children: ReactNode
  className?: string
  delay?: number
  step?: number
  amount?: number
  once?: boolean
  as?: 'div' | 'ul' | 'ol' | 'section'
}) {
  const Component = Tag

  let index = 0
  const items = Children.map(children, (child) => {
    if (!isValidElement(child)) return child
    const element = child as ReactElement<StaggerItemProps>
    if (element.type !== StaggerItem) return child
    return cloneElement(element, {
      _index: index++,
      _delay: delay,
      _step: step,
      _amount: amount,
      _once: once,
    })
  })

  return <Component className={className}>{items}</Component>
}

export function StaggerItem({
  children,
  className,
  from = 'up',
  as: Tag = 'div',
  _index = 0,
  _delay = 0,
  _step = 0.07,
  _amount = 0.2,
  _once = true,
}: StaggerItemProps) {
  const reduced = useReducedMotion()
  const [ref, revealed] = useRevealed<HTMLDivElement>(_amount, _once)
  const offset = OFFSET[from]
  const Component = motion[Tag] as typeof motion.div

  return (
    <Component
      ref={ref}
      className={className}
      initial={reduced ? false : { opacity: 0, ...offset }}
      animate={revealed || reduced ? { opacity: 1, x: 0, y: 0 } : undefined}
      transition={{ duration: 0.58, delay: reduced ? 0 : _delay + _index * _step, ease: EASE }}
    >
      {children}
    </Component>
  )
}

/* ------------------------------ Başlık açılışı ------------------------------ */

/**
 * Başlığı kelime kelime açar. Harf harf açmak bu boyutta okumayı zorlaştırıyor;
 * kelime birimi hem okunur hem de yeterince "yazılıyor" hissi veriyor.
 */
export function WordReveal({
  text,
  className,
  wordClassName,
  delay = 0,
  step = 0.055,
}: {
  text: string
  /** Dış kapsayıcı — yerleşim sınıfları buraya. */
  className?: string
  /**
   * HER KELİMEYE ayrı ayrı uygulanır. Gradyan metin (`bg-clip-text`) burada
   * verilmeli: kelimeler animasyon için `transform` aldığından yeni bir boyama
   * bağlamı açılıyor ve üst öğeden gelen metin kırpması boş görünüyor.
   */
  wordClassName?: string
  delay?: number
  step?: number
}) {
  const reduced = useReducedMotion()
  const words = text.split(' ')

  if (reduced) {
    return (
      <span className={className}>
        <span className={wordClassName}>{text}</span>
      </span>
    )
  }

  return (
    <span className={className}>
      {words.map((word, i) => (
        <span
          key={`${word}-${i}`}
          className="inline-block overflow-hidden pb-[0.12em] align-bottom"
        >
          <motion.span
            className={cn('inline-block', wordClassName)}
            initial={{ y: '110%', opacity: 0 }}
            animate={{ y: '0%', opacity: 1 }}
            transition={{ duration: 0.7, delay: delay + i * step, ease: EASE }}
          >
            {word}
          </motion.span>
        </span>
      ))}
    </span>
  )
}

/* ---------------------------------- Sayılar --------------------------------- */

/**
 * Görünür olunca hedefe doğru sayan rakam.
 *
 * Biçimlendirme dışarıdan verilir (para, yüzde, tam sayı hepsi tr-TR).
 * Hareket kapalıysa doğrudan son değer basılır.
 */
export function CountUp({
  to,
  from = 0,
  duration = 1.5,
  format = (v: number) => String(Math.round(v)),
  className,
  delay = 0,
}: {
  to: number
  from?: number
  duration?: number
  format?: (value: number) => string
  className?: string
  delay?: number
}) {
  const reduced = useReducedMotion()
  const [ref, revealed] = useRevealed<HTMLSpanElement>(0.4)
  const [text, setText] = useState(() => format(reduced ? to : from))

  useEffect(() => {
    if (reduced) {
      setText(format(to))
      return
    }
    if (!revealed) return
    const controls = animate(from, to, {
      duration,
      delay,
      ease: EASE_SOFT,
      // Tam sayıdan tam sayıya sayarken ara kareler de tam sayı olsun.
      onUpdate: (v) => setText(format(Number.isInteger(to) && Number.isInteger(from) ? Math.round(v) : v)),
    })
    return () => controls.stop()
    // `format` her render'da yeni referans olabilir; bağımlılığa almıyoruz.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revealed, to, from, duration, delay, reduced])

  return (
    <span ref={ref} className={cn('tabular', className)}>
      {text}
    </span>
  )
}

/* -------------------------------- Etkileşim -------------------------------- */

/**
 * İmlece hafifçe yaklaşan düğme sarmalayıcı. Sapma 8px'i geçmez —
 * daha fazlası oyuncak gibi duruyor ve tıklama hedefini kaydırıyor.
 */
export function Magnetic({
  children,
  strength = 0.28,
  className,
}: {
  children: ReactNode
  strength?: number
  className?: string
}) {
  const reduced = useReducedMotion()
  const ref = useRef<HTMLDivElement>(null)
  const x = useSpring(useMotionValue(0), { stiffness: 260, damping: 22, mass: 0.4 })
  const y = useSpring(useMotionValue(0), { stiffness: 260, damping: 22, mass: 0.4 })

  if (reduced) return <div className={cn('inline-flex', className)}>{children}</div>

  return (
    <motion.div
      ref={ref}
      className={cn('inline-flex', className)}
      style={{ x, y }}
      onPointerMove={(e) => {
        const rect = ref.current?.getBoundingClientRect()
        if (!rect) return
        const dx = (e.clientX - (rect.left + rect.width / 2)) * strength
        const dy = (e.clientY - (rect.top + rect.height / 2)) * strength
        x.set(Math.max(-8, Math.min(8, dx)))
        y.set(Math.max(-8, Math.min(8, dy)))
      }}
      onPointerLeave={() => {
        x.set(0)
        y.set(0)
      }}
    >
      {children}
    </motion.div>
  )
}

/**
 * İmleci izleyen ışık. Konum CSS değişkenine yazılır, boyama `.hr-spotlight`
 * içinde yapılır — her fare hareketinde React render'ı tetiklenmez.
 */
export function Spotlight({ children, className }: { children: ReactNode; className?: string }) {
  const ref = useRef<HTMLDivElement>(null)

  return (
    <div
      ref={ref}
      className={cn('hr-spotlight relative', className)}
      onPointerMove={(e) => {
        const el = ref.current
        if (!el) return
        const rect = el.getBoundingClientRect()
        el.style.setProperty('--hr-x', `${e.clientX - rect.left}px`)
        el.style.setProperty('--hr-y', `${e.clientY - rect.top}px`)
      }}
    >
      {children}
    </div>
  )
}

/* --------------------------------- Kaydırma -------------------------------- */

/**
 * Öğe ekrandan geçerken dikeyde kayar. `range` piksel cinsinden toplam yol.
 * Parallax yalnızca büyük görsellerde; metinde okumayı bozuyor.
 */
export function useParallax(range = 60): {
  ref: React.RefObject<HTMLDivElement | null>
  y: MotionValue<number>
} {
  const reduced = useReducedMotion()
  const ref = useRef<HTMLDivElement>(null)
  const { scrollYProgress } = useScroll({
    target: ref,
    offset: ['start end', 'end start'],
  })
  const raw = useTransform(scrollYProgress, [0, 1], reduced ? [0, 0] : [range, -range])
  const y = useSpring(raw, { stiffness: 120, damping: 28, mass: 0.5 })
  return { ref, y }
}

/** Sayfanın en üstünde ilerleme çubuğu. */
export function ScrollProgress({ className }: { className?: string }) {
  const { scrollYProgress } = useScroll()
  const scaleX = useSpring(scrollYProgress, { stiffness: 160, damping: 30, mass: 0.3 })

  return (
    <motion.div
      aria-hidden="true"
      style={{ scaleX }}
      className={cn(
        'fixed inset-x-0 top-0 z-[60] h-0.5 origin-left bg-gradient-to-r from-primary via-primary to-primary/40',
        className,
      )}
    />
  )
}

/* ------------------------------ Sayfa geçişleri ----------------------------- */

/**
 * Panel içi rota değişiminde içerik yerine otururken kısa bir yükselme.
 * Süre kasıtlı olarak kısa: gezinme hissi ağırlaşmasın.
 */
export function PageTransition({ children, routeKey }: { children: ReactNode; routeKey: string }) {
  const reduced = useReducedMotion()

  return (
    <motion.div
      key={routeKey}
      initial={reduced ? false : { opacity: 0, y: 10 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.32, ease: EASE }}
    >
      {children}
    </motion.div>
  )
}
