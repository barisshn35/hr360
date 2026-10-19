/**
 * Platform yönetimi > Sistem ayarları: kurulumdan sonra arayüzden değiştirilen uygulama içi ayarlar
 * (e-posta sunucusu, herkese açık şirket kaydı, parola belirleme yöntemi, davet linki süresi,
 * herkese açık sitenin yasal metinleri/SSS/iletişim bilgileri ve arama motoru görünürlüğü).
 * Sunucu düzeyindeki işlemler (HTTPS, Keycloak paneli portu, izleme) burada değil; yedek ve güncelleme Platform > Bakım ekranında.
 */
import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import {
  ArrowDown, ArrowUp, CheckCircle2, CircleAlert, ExternalLink, Eye, FileText, FlaskConical, Globe, LoaderCircle, Mail, PencilLine, Plus,
  Scale, Send, ServerCog, ShieldCheck, Trash2, Undo2, UserPlus, WandSparkles,
} from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Tabs } from '@/components/ui/Tabs'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { RowsSkeleton, ErrorState } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { Stagger, StaggerItem, EASE } from '@/motion/primitives'
import {
  passwordLinkApi, passwordLinkKeys, useSystemSettings, type FaqItem, type PasswordMethod, type SiteInput, type SiteSettings, type SystemSettings,
} from '@/api/password-links'
import { SafeMarkdown } from '@/features/site/SafeMarkdown'
import { defaultFaq, defaultPrivacy, defaultTerms } from '@/features/site/defaults'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { lang, tx } from '@/lib/i18n'
import { useModules } from '@/lib/modules'

function useSave<TIn>(fn: (input: TIn) => Promise<SystemSettings>, okText: string) {
  const qc = useQueryClient()
  const toast = useToast()
  const [savedAt, setSavedAt] = useState(0)
  const m = useMutation({
    mutationFn: fn,
    onSuccess: (data) => {
      qc.setQueryData(passwordLinkKeys.system, data)
      void qc.invalidateQueries({ queryKey: ['tenant', 'public-config'] })
      void qc.invalidateQueries({ queryKey: ['site', 'public'] })
      setSavedAt(Date.now())
      toast.ok(okText)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kaydedilemedi.')),
  })
  return { ...m, savedAt }
}

/** Kaydet düğmesinin yanında beliren "Kaydedildi" tiki (2,5 sn). */
function SavedTick({ at }: { at: number }) {
  const reduced = useReducedMotion()
  const [show, setShow] = useState(false)
  useEffect(() => {
    if (!at) return
    setShow(true)
    const id = setTimeout(() => setShow(false), 2500)
    return () => clearTimeout(id)
  }, [at])
  return (
    <AnimatePresence>
      {show && (
        <motion.span initial={reduced ? { opacity: 0 } : { opacity: 0, scale: 0.6, x: -6 }} animate={{ opacity: 1, scale: 1, x: 0 }}
          exit={{ opacity: 0 }} transition={{ type: 'spring', stiffness: 420, damping: 24 }}
          className="inline-flex items-center gap-1 text-[12.5px] font-medium text-[hsl(var(--success))]" role="status">
          <svg viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <motion.path d="M3 8.5l3 3L13 4.5" initial={{ pathLength: reduced ? 1 : 0 }} animate={{ pathLength: 1 }} transition={{ duration: 0.35, ease: EASE }} />
          </svg>
          {tx('Kaydedildi')}
        </motion.span>
      )}
    </AnimatePresence>
  )
}

function Field({ id, label, hint, children }: { id: string; label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label}</Label>
      {children}
      {hint && <p className="text-[11.5px] text-muted-foreground">{hint}</p>}
    </div>
  )
}

/* ------------------------------ E-posta ------------------------------ */

function SmtpPanel({ s }: { s: SystemSettings }) {
  const reduced = useReducedMotion()
  const confirm = useConfirm()
  const [form, setForm] = useState({
    host: s.smtp.mode === 'custom' ? s.smtp.host ?? '' : '',
    port: String(s.smtp.mode === 'custom' ? s.smtp.port ?? 587 : 587),
    user: s.smtp.mode === 'custom' ? s.smtp.user ?? '' : '',
    password: '',
    fromAddress: s.smtp.fromAddress,
    fromName: s.smtp.fromName,
  })
  const [testTo, setTestTo] = useState('')
  const save = useSave(() => passwordLinkApi.saveSmtp({
    host: form.host.trim(), port: Number(form.port) || 587, user: form.user.trim() || undefined,
    password: form.password ? form.password : undefined, fromAddress: form.fromAddress.trim(), fromName: form.fromName.trim() || undefined,
  }), tx('E-posta sunucusu kaydedildi.'))
  const mode = useSave((m: 'mailpit' | 'install') => passwordLinkApi.setSmtpMode(m), tx('E-posta ayarı değiştirildi.'))
  const test = useMutation({ mutationFn: () => passwordLinkApi.testSmtp(testTo.trim()) })

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm((f) => ({ ...f, [k]: e.target.value }))
  const badge = s.smtp.mode === 'custom'
    ? <StatusBadge tone="success">{tx('Arayüzden ayarlandı')}</StatusBadge>
    : s.smtp.mode === 'env'
      ? <StatusBadge tone="success">{tx('Kurulumda ayarlandı')}</StatusBadge>
      : s.smtp.mode === 'self'
        ? <StatusBadge tone="success">{tx('Sunucunun kendi posta servisi')}</StatusBadge>
        : <StatusBadge tone="warning">{tx('Test kutusu (gönderim yok)')}</StatusBadge>

  function onSubmit(e: FormEvent) {
    e.preventDefault()
    save.mutate(undefined)
  }

  return (
    <Panel>
      <PanelHead title={tx('E-posta sunucusu (SMTP)')} note={tx('Davet linkleri, bildirimler ve parola sıfırlama e-postaları')} action={badge} />
      <PanelBody className="space-y-5">
        <AnimatePresence mode="wait" initial={false}>
          <motion.div key={s.smtp.mode} initial={reduced ? false : { opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -6 }}
            transition={{ duration: 0.3, ease: EASE }}
            className={cn('flex gap-3 rounded-lg border p-3 text-[12.5px] leading-relaxed',
              s.smtp.configured ? 'border-[hsl(var(--success))]/30 bg-[hsl(var(--success))]/[0.06]' : 'border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/[0.06]')}>
            {s.smtp.configured ? <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-[hsl(var(--success))]" /> : <CircleAlert className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" />}
            <span>
              {s.smtp.mode === 'self'
                ? tx('E-postalar sunucunun kendi posta servisiyle doğrudan alıcılara gönderiliyor (gönderen: {0}). Teslim için alan adında SPF, DKIM ve DMARC kayıtları ile IP adresinin PTR kaydı gerekir.', [s.smtp.fromAddress])
                : s.smtp.configured
                ? tx('E-postalar {0} üzerinden gönderiliyor (gönderen: {1}).', [s.smtp.host ?? '', s.smtp.fromAddress])
                : tx('Gerçek e-posta gönderilmiyor: e-postalar paketteki test kutusunda (Mailpit) kalır. Parola linkleri yöneticiye gösterilir (kopyala / QR).')}
              {s.smtp.tenantsWithOwnSmtp > 0 && ` ${tx('{0} şirket kendi e-posta sunucusunu kullanıyor.', [s.smtp.tenantsWithOwnSmtp])}`}
            </span>
          </motion.div>
        </AnimatePresence>

        <form onSubmit={onSubmit} className="space-y-4" data-testid="smtp-form">
          <div className="grid gap-4 sm:grid-cols-[1fr_120px]">
            <Field id="smtp-host" label={tx('Sunucu')} hint={tx('Örn. smtp.office365.com, smtp.gmail.com')}>
              <Input id="smtp-host" value={form.host} onChange={set('host')} required maxLength={253} autoComplete="off" />
            </Field>
            <Field id="smtp-port" label={tx('Port')}>
              <Input id="smtp-port" inputMode="numeric" value={form.port} onChange={set('port')} required />
            </Field>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field id="smtp-user" label={tx('Kullanıcı adı')} hint={tx('Boş: kimlik doğrulama yok')}>
              <Input id="smtp-user" value={form.user} onChange={set('user')} maxLength={320} autoComplete="off" />
            </Field>
            <Field id="smtp-pass" label={tx('Parola')} hint={s.smtp.mode === 'custom' && s.smtp.hasPassword ? tx('Kayıtlı; değiştirmek için yazın') : undefined}>
              <Input id="smtp-pass" type="password" value={form.password} onChange={set('password')} maxLength={1000} autoComplete="new-password" />
            </Field>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field id="smtp-from" label={tx('Gönderen adresi')}>
              <Input id="smtp-from" type="email" value={form.fromAddress} onChange={set('fromAddress')} required maxLength={320} />
            </Field>
            <Field id="smtp-name" label={tx('Gönderen adı')}>
              <Input id="smtp-name" value={form.fromName} onChange={set('fromName')} maxLength={120} />
            </Field>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button type="submit" disabled={save.isPending}>
              {save.isPending ? <LoaderCircle className="size-4 animate-spin" /> : <ServerCog className="size-4" />}
              {tx('Kaydet ve kullan')}
            </Button>
            <SavedTick at={save.savedAt || mode.savedAt} />
            <span className="flex-1" />
            {s.smtp.mode !== 'mailpit' && (
              <Button type="button" variant="ghost" size="sm" disabled={mode.isPending}
                onClick={async () => {
                  if (await confirm({ title: tx('Test kutusuna dönülsün mü?'), note: tx('Gerçek e-posta gönderimi durur; e-postalar paketteki test kutusunda (Mailpit) kalır.'), action: tx('Test kutusuna dön') }))
                    mode.mutate('mailpit')
                }}>
                <Undo2 className="size-4" />{tx('Paketteki test kutusuna (Mailpit) dön')}
              </Button>
            )}
            {s.smtp.mode !== 'env' && s.smtp.mode !== 'self' && s.smtp.installHost && (
              <Button type="button" variant="ghost" size="sm" disabled={mode.isPending} onClick={() => mode.mutate('install')}>
                <Undo2 className="size-4" />
                {s.smtp.installSelf ? tx('Sunucunun kendi posta servisine dön') : tx('Kurulum ayarına dön ({0})', [s.smtp.installHost])}
              </Button>
            )}
          </div>
        </form>

        <div className="rounded-lg border border-dashed border-border p-3.5">
          <p className="mb-2 flex items-center gap-2 text-[13px] font-medium"><FlaskConical className="size-4 text-primary" />{tx('Deneme e-postası')}</p>
          <form className="flex flex-wrap gap-2" onSubmit={(e) => { e.preventDefault(); if (testTo.trim()) test.mutate() }}>
            <Input aria-label={tx('Alıcı adresi')} type="email" placeholder={tx('alici@sirket.com')} value={testTo}
              onChange={(e) => setTestTo(e.target.value)} className="min-w-56 flex-1" maxLength={320} />
            <Button type="submit" variant="outline" disabled={test.isPending || !testTo.trim()}>
              {test.isPending ? <LoaderCircle className="size-4 animate-spin" /> : <Send className="size-4" />}{tx('Gönder')}
            </Button>
          </form>
          <AnimatePresence>
            {(test.data || test.error) && (
              <motion.p key={test.data?.message ?? 'err'} role="status"
                initial={reduced ? false : { opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }}
                className={cn('mt-2 text-[12.5px]', test.data?.ok ? 'text-[hsl(var(--success))]' : 'text-destructive')}>
                {test.data?.message ?? (test.error instanceof Error ? test.error.message : tx('Gönderilemedi.'))}
              </motion.p>
            )}
          </AnimatePresence>
        </div>
      </PanelBody>
    </Panel>
  )
}

/* ------------------------- Kayıt ve parola ------------------------- */

function Toggle({ checked, onChange, label, disabled }: { checked: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  const reduced = useReducedMotion()
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} disabled={disabled} onClick={() => onChange(!checked)}
      className={cn('relative inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full p-0.5 transition-colors disabled:opacity-50', checked ? 'bg-primary' : 'bg-muted-foreground/30')}>
      <motion.span layout={!reduced} transition={{ type: 'spring', stiffness: 500, damping: 32 }}
        className={cn('size-5 rounded-full bg-white shadow-sm', checked ? 'ml-auto' : 'ml-0')} />
    </button>
  )
}

function AccessPanel({ s }: { s: SystemSettings }) {
  const reduced = useReducedMotion()
  // Tek şirket kipinde herkese açık kayıt hiç açılamaz (sunucu da reddeder): anahtar gösterilmez.
  const single = useModules().tenancy === 'single'
  const [ttl, setTtl] = useState(String(s.inviteTtlHours))
  const save = useSave((input: { signupEnabled?: boolean; passwordMethod?: PasswordMethod; inviteTtlHours?: number }) => passwordLinkApi.saveGeneral(input), tx('Ayar kaydedildi.'))
  const methods: Array<{ value: PasswordMethod; title: string; detail: string; disabled?: boolean }> = [
    { value: 'invite', title: tx('Süreli davet linki'), detail: tx('Varsayılan. Link yöneticiye gösterilir (kopyala / QR), e-posta açıksa e-postayla da gider.') },
    {
      value: 'keycloak', title: tx('Keycloak e-postası (eski yöntem)'),
      detail: s.smtp.configured ? tx('Kimlik sunucusu bağlantıyı e-postayla gönderir.') : tx('Önce e-posta sunucusunu ayarlayın.'),
      disabled: !s.smtp.configured,
    },
  ]
  return (
    <Panel>
      <PanelHead title={tx('Hesaplar ve parolalar')} note={tx('Şirketler kendi ayarlarında yöntemi değiştirebilir')} action={<SavedTick at={save.savedAt} />} />
      <PanelBody className="space-y-6">
        {!single && <div className="flex items-start justify-between gap-4">
          <div>
            <p className="flex items-center gap-2 text-[13px] font-medium"><UserPlus className="size-4 text-primary" />{tx('Herkese açık şirket kaydı')}</p>
            <p className="mt-1 text-[12px] text-muted-foreground">{tx('Açıkken giriş sayfasında "Şirketinizi kaydedin" görünür. Kapalıyken şirketleri yalnızca platform yöneticisi açar.')}</p>
          </div>
          <Toggle checked={s.signupEnabled} onChange={(v) => save.mutate({ signupEnabled: v })} label={tx('Herkese açık şirket kaydı')} disabled={save.isPending} />
        </div>}

        <div className="space-y-2.5">
          <p className="flex items-center gap-2 text-[13px] font-medium"><ShieldCheck className="size-4 text-primary" />{tx('Varsayılan parola belirleme yöntemi')}</p>
          <div role="radiogroup" aria-label={tx('Varsayılan parola belirleme yöntemi')} className="grid gap-2.5 sm:grid-cols-2">
            {methods.map((m) => {
              const active = s.passwordMethod === m.value
              return (
                <button key={m.value} type="button" role="radio" aria-checked={active} disabled={m.disabled || save.isPending}
                  onClick={() => !active && save.mutate({ passwordMethod: m.value })}
                  className={cn('relative cursor-pointer rounded-xl border p-3.5 text-left transition-colors disabled:cursor-not-allowed disabled:opacity-60',
                    active ? 'border-primary/50 bg-primary/[0.05]' : 'border-border hover:bg-muted/40')}>
                  {active && <motion.span layoutId="sys-pw-method" className="absolute inset-0 rounded-xl ring-2 ring-primary/40"
                    transition={reduced ? { duration: 0 } : { type: 'spring', stiffness: 380, damping: 30 }} />}
                  <span className="relative block text-[13px] font-medium">{m.title}</span>
                  <span className="relative mt-1 block text-[12px] leading-relaxed text-muted-foreground">{m.detail}</span>
                </button>
              )
            })}
          </div>
        </div>

        <form className="flex flex-wrap items-end gap-3" onSubmit={(e) => { e.preventDefault(); save.mutate({ inviteTtlHours: Number(ttl) }) }}>
          <Field id="ttl" label={tx('Davet linki süresi (saat)')} hint={tx('1-720 saat; varsayılan 72')}>
            <Input id="ttl" inputMode="numeric" value={ttl} onChange={(e) => setTtl(e.target.value)} className="w-32" />
          </Field>
          <Button type="submit" variant="outline" disabled={save.isPending || Number(ttl) === s.inviteTtlHours}>{tx('Kaydet')}</Button>
        </form>
      </PanelBody>
    </Panel>
  )
}

/* ------------------- Herkese açık site: yasal metinler ve iletişim ------------------- */

type SiteForm = {
  operatorName: string
  address: string
  email: string
  phone: string
  kep: string
  verbisNo: string
  privacyMd: string
  termsMd: string
  faq: FaqItem[]
  searchIndexing: boolean
}

function toForm(site: SiteSettings): SiteForm {
  return {
    operatorName: site.operatorName ?? '',
    address: site.address ?? '',
    email: site.email ?? '',
    phone: site.phone ?? '',
    kep: site.kep ?? '',
    verbisNo: site.verbisNo ?? '',
    privacyMd: site.privacyMd ?? '',
    termsMd: site.termsMd ?? '',
    faq: site.faq ?? [],
    searchIndexing: site.searchIndexing,
  }
}

type DocTab = 'privacy' | 'terms' | 'faq'

/** Markdown düzenleyici: yaz / önizle geçişi; boşken varsayılan şablonun kullanılacağı söylenir. */
function MarkdownEditor({
  id,
  value,
  onChange,
  template,
  label,
}: {
  id: string
  value: string
  onChange: (v: string) => void
  template: () => string
  label: string
}) {
  const reduced = useReducedMotion()
  const [preview, setPreview] = useState(false)
  const shown = value.trim() ? value : template()
  return (
    <div className="space-y-2.5">
      <div className="flex flex-wrap items-center gap-2">
        <div role="tablist" aria-label={label} className="inline-flex rounded-lg border border-border p-0.5 text-[12.5px]">
          {[
            { key: false, text: tx('Düzenle'), icon: PencilLine },
            { key: true, text: tx('Önizle'), icon: Eye },
          ].map((t) => (
            <button
              key={String(t.key)}
              type="button"
              role="tab"
              aria-selected={preview === t.key}
              onClick={() => setPreview(t.key)}
              className={cn('relative inline-flex cursor-pointer items-center gap-1.5 rounded-md px-2.5 py-1', preview === t.key ? 'text-foreground' : 'text-muted-foreground')}
            >
              {preview === t.key && (
                <motion.span layoutId={`${id}-mode`} className="absolute inset-0 rounded-md bg-muted" transition={reduced ? { duration: 0 } : { type: 'spring', stiffness: 420, damping: 32 }} />
              )}
              <t.icon className="relative size-3.5" aria-hidden="true" />
              <span className="relative">{t.text}</span>
            </button>
          ))}
        </div>
        <Button type="button" size="sm" variant="ghost" onClick={() => { onChange(template()); setPreview(false) }}>
          <WandSparkles className="size-3.5" aria-hidden="true" />
          {value.trim() ? tx('Şablonla değiştir') : tx('Şablonu düzenlemek için yükle')}
        </Button>
        {value.trim() && (
          <Button type="button" size="sm" variant="ghost" onClick={() => onChange('')}>
            <Undo2 className="size-3.5" aria-hidden="true" />
            {tx('Varsayılana dön')}
          </Button>
        )}
        <span className="ml-auto text-[11.5px] text-muted-foreground tabular-nums">{tx('{0} karakter', [value.length.toLocaleString(lang === 'en' ? 'en-GB' : 'tr-TR')])}</span>
      </div>
      <AnimatePresence mode="wait" initial={false}>
        {preview ? (
          <motion.div
            key="preview"
            initial={reduced ? false : { opacity: 0, y: 6 }}
            animate={{ opacity: 1, y: 0 }}
            exit={reduced ? undefined : { opacity: 0, y: -4 }}
            transition={{ duration: 0.2, ease: EASE }}
            className="max-h-[480px] overflow-y-auto rounded-xl border border-border bg-muted/20 p-5"
          >
            <SafeMarkdown source={shown} className="text-[14px]" />
          </motion.div>
        ) : (
          <motion.div key="edit" initial={reduced ? false : { opacity: 0 }} animate={{ opacity: 1 }} exit={reduced ? undefined : { opacity: 0 }} transition={{ duration: 0.15 }}>
            <Textarea
              id={id}
              aria-label={label}
              value={value}
              onChange={(e) => onChange(e.target.value)}
              placeholder={tx('Boş bırakılırsa varsayılan şablon yayımlanır. Markdown: # başlık, **kalın**, - madde, [bağlantı](https://…)')}
              className="min-h-[260px] font-mono text-[12.5px] leading-relaxed"
              maxLength={100000}
            />
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  )
}

function FaqEditor({ items, onChange }: { items: FaqItem[]; onChange: (next: FaqItem[]) => void }) {
  const reduced = useReducedMotion()
  const set = (i: number, patch: Partial<FaqItem>) => onChange(items.map((f, j) => (j === i ? { ...f, ...patch } : f)))
  const move = (i: number, d: number) => {
    const j = i + d
    if (j < 0 || j >= items.length) return
    const next = items.slice()
    ;[next[i], next[j]] = [next[j], next[i]]
    onChange(next)
  }
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" size="sm" variant="outline" onClick={() => onChange([...items, { q: '', a: '' }])} disabled={items.length >= 50}>
          <Plus className="size-3.5" aria-hidden="true" />
          {tx('Soru ekle')}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={() => onChange(defaultFaq(lang))}>
          <WandSparkles className="size-3.5" aria-hidden="true" />
          {tx('Varsayılan soruları yükle')}
        </Button>
        {items.length > 0 && (
          <Button type="button" size="sm" variant="ghost" onClick={() => onChange([])}>
            <Undo2 className="size-3.5" aria-hidden="true" />
            {tx('Varsayılana dön')}
          </Button>
        )}
        <span className="ml-auto text-[11.5px] text-muted-foreground">{tx('{0} / 50 soru', [String(items.length)])}</span>
      </div>
      {items.length === 0 && (
        <p className="rounded-xl border border-dashed border-border px-4 py-6 text-center text-[12.5px] text-muted-foreground">
          {tx('Özel soru eklenmedi; SSS sayfasında varsayılan sorular gösterilir.')}
        </p>
      )}
      <ul className="space-y-2.5">
        <AnimatePresence initial={false}>
          {items.map((f, i) => (
            <motion.li
              key={i}
              layout={!reduced}
              initial={reduced ? false : { opacity: 0, y: -8, scale: 0.98 }}
              animate={{ opacity: 1, y: 0, scale: 1 }}
              exit={reduced ? { opacity: 0 } : { opacity: 0, x: 24, transition: { duration: 0.18 } }}
              transition={{ type: 'spring', stiffness: 380, damping: 32 }}
              className="space-y-2 rounded-xl border border-border bg-card/50 p-3"
            >
              <div className="flex items-center gap-2">
                <span className="text-[11.5px] font-medium text-muted-foreground tabular-nums">{i + 1}.</span>
                <Input
                  id={`faq-q-${i}`}
                  aria-label={tx('Soru {0}', [String(i + 1)])}
                  value={f.q}
                  onChange={(e) => set(i, { q: e.target.value })}
                  placeholder={tx('Soru')}
                  maxLength={300}
                  className="h-9"
                />
                <Button type="button" size="icon" variant="ghost" className="size-8 shrink-0" onClick={() => move(i, -1)} disabled={i === 0} aria-label={tx('Yukarı taşı')}>
                  <ArrowUp className="size-3.5" />
                </Button>
                <Button type="button" size="icon" variant="ghost" className="size-8 shrink-0" onClick={() => move(i, 1)} disabled={i === items.length - 1} aria-label={tx('Aşağı taşı')}>
                  <ArrowDown className="size-3.5" />
                </Button>
                <Button type="button" size="icon" variant="ghost" className="size-8 shrink-0 text-destructive" onClick={() => onChange(items.filter((_, j) => j !== i))} aria-label={tx('Soruyu sil')}>
                  <Trash2 className="size-3.5" />
                </Button>
              </div>
              <Textarea
                id={`faq-a-${i}`}
                aria-label={tx('Cevap {0}', [String(i + 1)])}
                value={f.a}
                onChange={(e) => set(i, { a: e.target.value })}
                placeholder={tx('Cevap (Markdown kullanılabilir)')}
                maxLength={5000}
                className="min-h-16 text-[13px]"
              />
            </motion.li>
          ))}
        </AnimatePresence>
      </ul>
    </div>
  )
}

function SitePanel({ s }: { s: SystemSettings }) {
  const [form, setForm] = useState<SiteForm>(() => toForm(s.site))
  const [tab, setTab] = useState<DocTab>('privacy')
  const save = useSave((input: SiteInput) => passwordLinkApi.saveSite(input), tx('Site bilgileri kaydedildi.'))
  const saved = toForm(s.site)
  const dirty = JSON.stringify(form) !== JSON.stringify(saved)
  const up = <K extends keyof SiteForm>(k: K, v: SiteForm[K]) => setForm((f) => ({ ...f, [k]: v }))
  // Şablonlar formdaki (henüz kaydedilmemiş) işletmeci bilgileriyle doldurulur.
  const live: SiteSettings = { ...s.site, ...form, operatorName: form.operatorName || null, address: form.address || null, email: form.email || null, phone: form.phone || null, kep: form.kep || null, verbisNo: form.verbisNo || null }

  function onSubmit(e: FormEvent) {
    e.preventDefault()
    save.mutate({
      ...form,
      faq: form.faq.filter((f) => f.q.trim() || f.a.trim()),
    })
  }

  return (
    <Panel>
      <PanelHead
        title={tx('Herkese açık site')}
        note={tx('Yasal metinler, SSS ve iletişim bilgileri; giriş sayfasının alt bilgisinden açılır')}
        action={<SavedTick at={save.savedAt} />}
      />
      <PanelBody>
        <form className="space-y-7" onSubmit={onSubmit} noValidate>
          <div className="flex items-start justify-between gap-4 rounded-xl border border-border p-4">
            <div>
              <p className="flex items-center gap-2 text-[13px] font-medium"><Globe className="size-4 text-primary" />{tx('Arama motorlarında görünür')}</p>
              <p className="mt-1 text-[12px] leading-relaxed text-muted-foreground">
                {form.searchIndexing
                  ? tx('Açık: giriş, SSS, gizlilik, koşullar ve iletişim sayfaları dizinlenebilir (sitemap.xml). Panel, API ve kişisel bağlantılar her zaman engellidir.')
                  : tx('Kapalı (önerilen varsayılan): robots.txt tüm siteyi engeller, sayfalara "noindex" eklenir. İç kullanım için kurulan İK sistemlerinde kapalı kalması önerilir.')}
              </p>
            </div>
            <Toggle checked={form.searchIndexing} onChange={(v) => up('searchIndexing', v)} label={tx('Arama motorlarında görünür')} />
          </div>

          <div className="space-y-3">
            <p className="flex items-center gap-2 text-[13px] font-medium"><Mail className="size-4 text-primary" />{tx('İşletmeci ve iletişim bilgileri')}</p>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field id="site-name" label={tx('İşletmeci (veri sorumlusu) adı')} hint={tx('Gizlilik politikası ve alt bilgide görünür')}>
                <Input id="site-name" value={form.operatorName} onChange={(e) => up('operatorName', e.target.value)} maxLength={200} placeholder={tx('Örn. Acme Bilişim A.Ş.')} />
              </Field>
              <Field id="site-email" label={tx('E-posta')}>
                <Input id="site-email" type="email" value={form.email} onChange={(e) => up('email', e.target.value)} maxLength={320} placeholder="kvkk@sirket.com" />
              </Field>
              <Field id="site-phone" label={tx('Telefon')}>
                <Input id="site-phone" type="tel" value={form.phone} onChange={(e) => up('phone', e.target.value)} maxLength={40} placeholder="+90 212 000 00 00" />
              </Field>
              <Field id="site-kep" label={tx('KEP adresi (isteğe bağlı)')}>
                <Input id="site-kep" value={form.kep} onChange={(e) => up('kep', e.target.value)} maxLength={320} placeholder="sirket@hs01.kep.tr" />
              </Field>
              <Field id="site-verbis" label={tx('VERBİS kayıt no (isteğe bağlı)')}>
                <Input id="site-verbis" value={form.verbisNo} onChange={(e) => up('verbisNo', e.target.value)} maxLength={64} />
              </Field>
              <Field id="site-address" label={tx('Adres')}>
                <Textarea id="site-address" value={form.address} onChange={(e) => up('address', e.target.value)} maxLength={500} className="min-h-10" />
              </Field>
            </div>
          </div>

          <div className="space-y-3">
            <p className="flex items-center gap-2 text-[13px] font-medium"><Scale className="size-4 text-primary" />{tx('Metinler')}</p>
            <div role="note" className="flex items-start gap-2.5 rounded-xl border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/[0.07] px-3.5 py-2.5 text-[12px] leading-relaxed">
              <CircleAlert className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden="true" />
              <span>{tx('Varsayılan şablonlar KVKK\'ya göre hazırlanmış genel örneklerdir; işletmeci olarak kendi durumunuza göre gözden geçirmeniz ve gerekiyorsa hukuk danışmanınıza onaylatmanız gerekir. Bu not ziyaretçilere gösterilmez.')}</span>
            </div>
            <Tabs<DocTab>
              label={tx('Metinler')}
              value={tab}
              onChange={setTab}
              tabs={[
                { key: 'privacy', label: tx('Gizlilik politikası') },
                { key: 'terms', label: tx('Kullanım koşulları') },
                { key: 'faq', label: tx('SSS'), count: form.faq.length || undefined },
              ]}
            />
            {tab === 'privacy' && (
              <MarkdownEditor id="site-privacy" label={tx('Gizlilik politikası')} value={form.privacyMd} onChange={(v) => up('privacyMd', v)} template={() => defaultPrivacy(live, lang)} />
            )}
            {tab === 'terms' && (
              <MarkdownEditor id="site-terms" label={tx('Kullanım koşulları')} value={form.termsMd} onChange={(v) => up('termsMd', v)} template={() => defaultTerms(live, lang)} />
            )}
            {tab === 'faq' && <FaqEditor items={form.faq} onChange={(v) => up('faq', v)} />}
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" disabled={save.isPending || !dirty}>
              {save.isPending ? <LoaderCircle className="size-4 animate-spin" /> : <FileText className="size-4" />}
              {tx('Kaydet')}
            </Button>
            {dirty && (
              <Button type="button" variant="ghost" onClick={() => setForm(saved)}>
                {tx('Değişiklikleri geri al')}
              </Button>
            )}
            <div className="ml-auto flex flex-wrap gap-x-4 gap-y-1 text-[12.5px]">
              {[
                { to: '/sss', label: tx('SSS') },
                { to: '/gizlilik', label: tx('Gizlilik') },
                { to: '/kosullar', label: tx('Koşullar') },
                { to: '/iletisim', label: tx('İletişim') },
              ].map((l) => (
                <a key={l.to} href={l.to} target="_blank" rel="noopener" className="inline-flex items-center gap-1 text-muted-foreground hover:text-foreground">
                  {l.label}
                  <ExternalLink className="size-3" aria-hidden="true" />
                </a>
              ))}
            </div>
          </div>
        </form>
      </PanelBody>
    </Panel>
  )
}

export function SystemSettingsPage() {
  const q = useSystemSettings()
  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Sistem ayarları')}
        description={tx('Kurulumdan sonra değiştirilebilen platform ayarları. Değişiklikler denetim kaydına yazılır.')}
      />
      {q.isPending ? <RowsSkeleton rows={6} columns={2} /> : q.isError ? (
        <ErrorState message={q.error instanceof Error ? q.error.message : undefined} onRetry={() => void q.refetch()} />
      ) : (
        <Stagger className="space-y-5">
          <StaggerItem><SmtpPanel s={q.data} /></StaggerItem>
          <StaggerItem><AccessPanel s={q.data} /></StaggerItem>
          <StaggerItem><SitePanel s={q.data} /></StaggerItem>
          {q.data.updatedAt && (
            <StaggerItem>
              <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground">
                <Mail className="size-3.5" />{tx('Son değişiklik: {0}{1}', [formatDateTime(q.data.updatedAt), q.data.updatedBy ? ` · ${q.data.updatedBy}` : ''])}
              </p>
            </StaggerItem>
          )}
        </Stagger>
      )}
    </div>
  )
}
