/**
 * Herkese açık sayfalar: SSS, gizlilik politikası, kullanım koşulları, iletişim.
 * İçerik Platform › Sistem ayarları › Yasal metinler ve iletişim'den gelir; boşsa varsayılan şablon.
 */
import { useEffect, useMemo, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { ArrowRight, Building2, Cookie, Hash, Landmark, Mail, MapPin, MessageCircleQuestion, Phone, Search, Stamp } from 'lucide-react'
import { Accordion } from '@/components/ui/accordion'
import { CopyButton } from '@/components/ui/copy-button'
import { Input } from '@/components/ui/input'
import { EASE } from '@/motion/primitives'
import { lang, tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { PublicLayout } from './SiteChrome'
import { SafeMarkdown, inline, slugify as slug } from './SafeMarkdown'
import { COOKIE_ANCHOR, SOURCE_URL, cookieRows, defaultFaq, defaultPrivacy, defaultTerms } from './defaults'
import { usePublicSite } from './usePublicSite'

/** Sayfa açıldığında adresteki çapaya (#cerezler) kaydır; içerik geç yüklenebildiği için bir kare bekler. */
function useHashScroll(ready: boolean) {
  const { hash } = useLocation()
  useEffect(() => {
    if (!ready || !hash) return
    const id = decodeURIComponent(hash.slice(1))
    const t = window.setTimeout(() => document.getElementById(id)?.scrollIntoView({ block: 'start' }), 60)
    return () => window.clearTimeout(t)
  }, [hash, ready])
}

/** Markdown metnin ilk "# Başlık" satırı sayfa başlığında zaten var; içerikte tekrar gösterilmez. */
function stripTitle(md: string): string {
  return md.replace(/^\s*#\s+[^\n]*\n+/, '')
}

function LegalSkeleton() {
  return (
    <div className="space-y-3" aria-busy="true" aria-label={tx('Yükleniyor')}>
      {[92, 80, 86, 60, 74].map((w, i) => (
        <div key={i} className="h-3.5 animate-pulse rounded bg-muted" style={{ width: `${w}%` }} />
      ))}
    </div>
  )
}

/* ----------------------------------- SSS ----------------------------------- */

export function FaqPage() {
  const q = usePublicSite()
  const reduced = useReducedMotion()
  const [query, setQuery] = useState('')
  const items = q.data?.faq?.length ? q.data.faq : defaultFaq(lang)
  const filtered = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase(lang === 'en' ? 'en' : 'tr')
    if (!needle) return items
    return items.filter((f) => `${f.q} ${f.a}`.toLocaleLowerCase(lang === 'en' ? 'en' : 'tr').includes(needle))
  }, [items, query])

  return (
    <PublicLayout
      testId="page-faq"
      eyebrow={tx('Yardım')}
      title={tx('Sık sorulan sorular')}
      intro={tx('Giriş, parola, şirket kaydı ve kişisel veriler hakkında en çok sorulanlar.')}
    >
      <div className="relative mb-5">
        <Search className="pointer-events-none absolute top-1/2 left-3.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
        <Input
          id="faq-search"
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={tx('Sorularda ara')}
          aria-label={tx('Sorularda ara')}
          className="h-11 rounded-xl pl-10"
        />
      </div>
      {q.isPending ? (
        <LegalSkeleton />
      ) : (
        <AnimatePresence mode="wait" initial={false}>
          {filtered.length ? (
            <motion.div
              key={`list-${filtered.length}`}
              initial={reduced ? false : { opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              exit={reduced ? undefined : { opacity: 0, y: -6 }}
              transition={{ duration: 0.25, ease: EASE }}
            >
              <Accordion
                type="multiple"
                defaultOpen={query ? filtered.map((_, i) => `faq-${i}`) : ['faq-0']}
                items={filtered.map((f, i) => ({
                  id: `faq-${i}`,
                  title: f.q,
                  content: <SafeMarkdown source={f.a} className="text-[14.5px] leading-relaxed" />,
                }))}
              />
            </motion.div>
          ) : (
            <motion.p
              key="empty"
              initial={reduced ? false : { opacity: 0 }}
              animate={{ opacity: 1 }}
              className="rounded-2xl border border-dashed border-border px-5 py-10 text-center text-[14px] text-muted-foreground"
            >
              {tx('“{0}” ile eşleşen soru yok.', [query])}
            </motion.p>
          )}
        </AnimatePresence>
      )}

      <div className="mt-10 flex flex-col items-start gap-4 rounded-2xl border border-border bg-card/60 p-6 backdrop-blur sm:flex-row sm:items-center">
        <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-primary/12 text-primary">
          <MessageCircleQuestion className="size-5" aria-hidden="true" />
        </span>
        <div className="flex-1">
          <p className="font-medium">{tx('Sorunuzun cevabı burada yok mu?')}</p>
          <p className="mt-0.5 text-[13.5px] text-muted-foreground">{tx('Şirketinizin İK yöneticisine ya da bu kurulumun işletmecisine ulaşın.')}</p>
        </div>
        <Link to="/iletisim" className="group inline-flex items-center gap-1.5 text-[14px] font-medium text-primary">
          {tx('İletişim bilgileri')}
          <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" aria-hidden="true" />
        </Link>
      </div>
    </PublicLayout>
  )
}

/* ---------------------------- Gizlilik / koşullar ---------------------------- */

function CookieSection() {
  const rows = cookieRows(lang)
  return (
    <section id={COOKIE_ANCHOR} aria-labelledby="cookies-title" className="mt-12 scroll-mt-24">
      <div className="flex items-center gap-3">
        <span className="flex size-9 items-center justify-center rounded-xl bg-primary/12 text-primary">
          <Cookie className="size-4.5" aria-hidden="true" />
        </span>
        <h2 id="cookies-title" className="text-[19px] font-semibold tracking-tight">{tx('Çerezler ve benzeri teknolojiler')}</h2>
      </div>
      <p className="mt-3 text-[15px] leading-[1.75] text-muted-foreground">
        {tx('Bu uygulama yalnızca giriş, güvenlik ve tercihlerinizi hatırlamak için zorunlu çerezler ve tarayıcı depolaması kullanır. Reklam, analiz ya da izleme çerezi kullanılmaz; bu nedenle onayınız istenmez, yalnızca bilgilendirme yapılır. Tarayıcı ayarlarından çerezleri silebilirsiniz; ancak bu durumda yeniden giriş yapmanız gerekir.')}
      </p>
      <div className="mt-5 overflow-x-auto rounded-xl border border-border">
        <table className="w-full min-w-[620px] text-left text-[13.5px]">
          <thead className="bg-muted/50">
            <tr>
              <th className="px-3.5 py-2.5 font-medium">{tx('Ad')}</th>
              <th className="px-3.5 py-2.5 font-medium">{tx('Tür')}</th>
              <th className="px-3.5 py-2.5 font-medium">{tx('Amaç')}</th>
              <th className="px-3.5 py-2.5 font-medium">{tx('Süre')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.name} className="border-t border-border align-top text-muted-foreground">
                <td className={cn('px-3.5 py-2.5 text-foreground', !r.plain && 'font-mono text-[12.5px]')}>{r.name}</td>
                <td className="px-3.5 py-2.5">{r.kind}</td>
                <td className="px-3.5 py-2.5">{r.purpose}</td>
                <td className="px-3.5 py-2.5">{r.duration}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

/** Uzun metin için içindekiler: ## başlıklarından üretilir, geniş ekranda yanda sabit durur. */
function Toc({ md, extra }: { md: string; extra?: { id: string; label: string } }) {
  const heads = useMemo(() => [...md.matchAll(/^##\s+(.+)$/gm)].map((m) => m[1].trim()), [md])
  if (heads.length < 3) return null
  return (
    <nav aria-label={tx('İçindekiler')} className="sticky top-24 hidden max-h-[calc(100dvh-8rem)] overflow-y-auto text-[13px] lg:block">
      <p className="mb-3 text-[11px] font-medium tracking-[0.14em] text-muted-foreground uppercase">{tx('İçindekiler')}</p>
      <ul className="space-y-2 border-l border-border">
        {heads.map((h) => (
          <li key={h}>
            <a href={`#${slug(h)}`} className="-ml-px block border-l border-transparent pl-3 text-muted-foreground transition-colors hover:border-primary hover:text-foreground">
              {inline(h, `toc-${h}`)}
            </a>
          </li>
        ))}
        {extra && (
          <li>
            <a href={`#${extra.id}`} className="-ml-px block border-l border-transparent pl-3 text-muted-foreground transition-colors hover:border-primary hover:text-foreground">
              {extra.label}
            </a>
          </li>
        )}
      </ul>
    </nav>
  )
}


function LegalPage({ kind }: { kind: 'privacy' | 'terms' }) {
  const q = usePublicSite()
  const custom = kind === 'privacy' ? q.data?.privacyMd : q.data?.termsMd
  const md = stripTitle(custom?.trim() ? custom : kind === 'privacy' ? defaultPrivacy(q.data ?? undefined, lang) : defaultTerms(q.data ?? undefined, lang))
  useHashScroll(!q.isPending)
  const cookie = kind === 'privacy' ? { id: COOKIE_ANCHOR, label: tx('Çerezler ve benzeri teknolojiler') } : undefined

  return (
    <PublicLayout
      wide
      testId={kind === 'privacy' ? 'page-privacy' : 'page-terms'}
      eyebrow={tx('Yasal')}
      title={kind === 'privacy' ? tx('Gizlilik politikası') : tx('Kullanım koşulları')}
      intro={
        kind === 'privacy'
          ? tx('Kişisel verilerinizin hangi amaçla, hangi hukuki sebeple işlendiği ve haklarınızı nasıl kullanacağınız.')
          : tx('Bu kurulumu kullanırken geçerli olan kurallar.')
      }
    >
      <div className="grid gap-10 lg:grid-cols-[minmax(0,1fr)_220px]">
        <article className="min-w-0 max-w-[70ch]">
          {q.isPending ? <LegalSkeleton /> : <SafeMarkdown source={md} />}
          {kind === 'privacy' && <CookieSection />}
        </article>
        {!q.isPending && <Toc md={md} extra={cookie} />}
      </div>
    </PublicLayout>
  )
}

export function PrivacyPage() {
  return <LegalPage kind="privacy" />
}

export function TermsPage() {
  return <LegalPage kind="terms" />
}

/* --------------------------------- İletişim --------------------------------- */

function ContactCard({
  icon: Icon,
  label,
  value,
  href,
  copy = true,
  delay,
}: {
  icon: typeof Mail
  label: string
  value: string
  href?: string
  copy?: boolean
  delay: number
}) {
  const reduced = useReducedMotion()
  return (
    <motion.div
      initial={reduced ? false : { opacity: 0, y: 14, scale: 0.98 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: 0.5, delay, ease: EASE }}
      whileHover={reduced ? undefined : { y: -3 }}
      className="group relative flex flex-col gap-3 rounded-2xl border border-border bg-card/70 p-5 backdrop-blur transition-shadow hover:shadow-[0_12px_40px_-18px_hsl(var(--primary)/0.55)]"
    >
      <div className="flex items-center gap-3">
        <span className="flex size-10 items-center justify-center rounded-xl bg-primary/12 text-primary transition-transform group-hover:scale-105">
          <Icon className="size-4.5" aria-hidden="true" />
        </span>
        <p className="text-[12px] font-medium tracking-[0.12em] text-muted-foreground uppercase">{label}</p>
      </div>
      <p className="text-[15.5px] font-medium break-words select-all">
        {href ? (
          <a href={href} className="hover:text-primary hover:underline underline-offset-4">{value}</a>
        ) : (
          value
        )}
      </p>
      {copy && <CopyButton value={value} className="self-start" />}
    </motion.div>
  )
}

export function ContactPage() {
  const q = usePublicSite()
  const s = q.data
  const cards: Array<{ icon: typeof Mail; label: string; value: string; href?: string; copy?: boolean }> = []
  if (s?.email) cards.push({ icon: Mail, label: tx('E-posta'), value: s.email, href: `mailto:${s.email}` })
  if (s?.phone) cards.push({ icon: Phone, label: tx('Telefon'), value: s.phone, href: `tel:${s.phone.replace(/[^\d+]/g, '')}` })
  if (s?.kep) cards.push({ icon: Stamp, label: tx('KEP adresi'), value: s.kep })
  if (s?.address) cards.push({ icon: MapPin, label: tx('Adres'), value: s.address })
  if (s?.verbisNo) cards.push({ icon: Landmark, label: tx('VERBİS kayıt no'), value: s.verbisNo })

  return (
    <PublicLayout
      testId="page-contact"
      eyebrow={tx('İletişim')}
      title={s?.operatorName ? s.operatorName : tx('Bize ulaşın')}
      intro={tx('Bu HR360 kurulumunu işleten kuruluşun iletişim bilgileri. Şirketinizle ilgili İK işlemleri için önce şirketinizin İK yöneticisine başvurun.')}
    >
      {q.isPending ? (
        <LegalSkeleton />
      ) : cards.length ? (
        <div className={cn('grid gap-4', cards.length > 1 && 'sm:grid-cols-2')}>
          {cards.map((c, i) => (
            <ContactCard key={c.label} {...c} delay={0.05 * i} />
          ))}
        </div>
      ) : (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-dashed border-border px-6 py-12 text-center">
          <Building2 className="size-8 text-muted-foreground" aria-hidden="true" />
          <p className="font-medium">{tx('İletişim bilgileri henüz eklenmemiş')}</p>
          <p className="max-w-md text-[14px] text-muted-foreground">
            {tx('Hesabınızla ilgili sorular için şirketinizin İK yöneticisine başvurun.')}
          </p>
        </div>
      )}

      <div className="mt-10 grid gap-4 sm:grid-cols-2">
        <Link
          to="/gizlilik"
          className="group flex items-start gap-3 rounded-2xl border border-border bg-card/60 p-5 backdrop-blur transition-colors hover:border-primary/40"
        >
          <Hash className="mt-0.5 size-4.5 text-primary" aria-hidden="true" />
          <span>
            <span className="block font-medium">{tx('Kişisel veri başvuruları')}</span>
            <span className="mt-0.5 block text-[13.5px] text-muted-foreground">{tx('KVKK md. 11 kapsamındaki haklarınız ve başvuru yolu.')}</span>
          </span>
        </Link>
        <Link
          to="/sss"
          className="group flex items-start gap-3 rounded-2xl border border-border bg-card/60 p-5 backdrop-blur transition-colors hover:border-primary/40"
        >
          <MessageCircleQuestion className="mt-0.5 size-4.5 text-primary" aria-hidden="true" />
          <span>
            <span className="block font-medium">{tx('Sık sorulan sorular')}</span>
            <span className="mt-0.5 block text-[13.5px] text-muted-foreground">{tx('Giriş, parola ve şirket kaydıyla ilgili hızlı cevaplar.')}</span>
          </span>
        </Link>
      </div>
      <p className="mt-8 text-[12.5px] text-muted-foreground">
        {tx('HR360 açık kaynaklıdır; yazılımın kaynak kodu:')}{' '}
        <a href={SOURCE_URL} target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">{SOURCE_URL.replace('https://', '')}</a>
      </p>
    </PublicLayout>
  )
}
