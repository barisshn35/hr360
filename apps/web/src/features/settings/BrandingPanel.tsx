import { useEffect, useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Palette, Upload } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { TextField } from '@/components/ui/Field'
import { useToast } from '@/components/ui/Toast'
import { tenantApi, type Tenant } from '@/api/tenant'
import { qkt } from '@/api/queries-tenant'
import { tx } from '@/lib/i18n'

/** Şirket adı düzenleme. */
export function CompanyNamePanel({ tenant }: { tenant: Tenant }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [name, setName] = useState(tenant.name)

  useEffect(() => setName(tenant.name), [tenant.name])

  const mutation = useMutation({
    mutationFn: (n: string) => tenantApi.renameCompany(n),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: qkt.myTenant })
      toast.ok(tx('Şirket adı güncellendi.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Güncellenemedi.')),
  })

  return (
    <Panel>
      <PanelHead title={tx('Şirket adı')} />
      <PanelBody className="space-y-3">
        <TextField
          id="company-name"
          label=""
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={200}
        />
        <Button
          size="sm"
          className="cursor-pointer"
          disabled={mutation.isPending || !name.trim() || name === tenant.name}
          onClick={() => mutation.mutate(name.trim())}
        >
          {tx('Kaydet')}
        </Button>
      </PanelBody>
    </Panel>
  )
}

/** Ana renk + kendi SMTP sunucusu. */
export function BrandingPanel({ tenant }: { tenant: Tenant }) {
  const toast = useToast()
  const queryClient = useQueryClient()

  const [color, setColor] = useState(tenant.primaryColorHex ?? '#0b8f63')
  const [smtpHost, setSmtpHost] = useState('')
  const [smtpPort, setSmtpPort] = useState('587')
  const [smtpUser, setSmtpUser] = useState('')
  const [smtpPassword, setSmtpPassword] = useState('')
  const [smtpFromAddress, setSmtpFromAddress] = useState('')
  const [smtpFromName, setSmtpFromName] = useState('')

  useEffect(() => setColor(tenant.primaryColorHex ?? '#0b8f63'), [tenant.primaryColorHex])

  const invalidate = () => queryClient.invalidateQueries({ queryKey: qkt.myTenant })

  const fileInputRef = useRef<HTMLInputElement>(null)

  const uploadLogoMutation = useMutation({
    mutationFn: (file: File) => tenantApi.uploadLogo(file),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Logo yüklendi.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Yüklenemedi.')),
  })

  const deleteLogoMutation = useMutation({
    mutationFn: () => tenantApi.deleteLogo(),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Logo kaldırıldı.'))
    },
  })

  const colorMutation = useMutation({
    mutationFn: (primaryColorHex: string) => tenantApi.updateBranding({ primaryColorHex }),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Ana renk güncellendi.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Güncellenemedi.')),
  })

  const resetColorMutation = useMutation({
    mutationFn: () => tenantApi.updateBranding({ primaryColorHex: '' }),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Varsayılan renge dönüldü.'))
    },
  })

  const smtpMutation = useMutation({
    mutationFn: () =>
      tenantApi.updateBranding({
        smtpHost, smtpPort: Number(smtpPort) || 587, smtpUser,
        smtpPassword: smtpPassword || undefined,
        smtpFromAddress, smtpFromName,
      }),
    onSuccess: () => {
      invalidate()
      setSmtpPassword('')
      toast.ok(tx('SMTP ayarları kaydedildi.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Kaydedilemedi.')),
  })

  const removeSmtpMutation = useMutation({
    mutationFn: () => tenantApi.updateBranding({ smtpHost: '' }),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Özel SMTP kaldırıldı, varsayılan gönderim kullanılacak.'))
    },
  })

  return (
    <>
      <Panel>
        <PanelHead
          title={tx('Logo')}
          note={tx('PNG, JPEG veya WebP - en fazla 2 MB')}
        />
        <PanelBody className="space-y-3">
          <div className="flex items-center gap-4">
            {tenant.logoUrl ? (
              <img
                src={tenant.logoUrl}
                alt={tx('Şirket logosu')}
                className="h-16 w-16 rounded border border-border object-contain p-1"
              />
            ) : (
              <div className="flex h-16 w-16 items-center justify-center rounded border border-dashed border-border text-[11px] text-muted-foreground">
                {tx('Logo yok')}
              </div>
            )}
            <div className="flex gap-2">
              <Button
                size="sm"
                variant="outline"
                className="cursor-pointer"
                disabled={uploadLogoMutation.isPending}
                onClick={() => fileInputRef.current?.click()}
              >
                <Upload className="size-3.5" />
                {tenant.logoUrl ? tx('Değiştir') : tx('Yükle')}
              </Button>
              {tenant.logoUrl && (
                <Button
                  size="sm"
                  variant="outline"
                  className="cursor-pointer"
                  disabled={deleteLogoMutation.isPending}
                  onClick={() => deleteLogoMutation.mutate()}
                >
                  {tx('Kaldır')}
                </Button>
              )}
              <input
                ref={fileInputRef}
                type="file"
                accept="image/png,image/jpeg,image/webp"
                className="hidden"
                onChange={(e) => {
                  const file = e.target.files?.[0]
                  if (file) uploadLogoMutation.mutate(file)
                  e.target.value = ''
                }}
              />
            </div>
          </div>
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead
          title={tx('Ana renk')}
          note={tx('Arayüzdeki marka rengini şirketinize göre değiştirin')}
        />
        <PanelBody className="space-y-3">
          <div className="flex items-center gap-3">
            <input
              type="color"
              value={color}
              onChange={(e) => setColor(e.target.value)}
              className="h-10 w-16 cursor-pointer rounded border border-border bg-transparent"
              aria-label={tx('Ana renk seçici')}
            />
            <span className="font-mono text-[13px] text-muted-foreground">{color}</span>
          </div>
          <div className="flex gap-2">
            <Button
              size="sm"
              className="cursor-pointer"
              disabled={colorMutation.isPending || color === (tenant.primaryColorHex ?? '#0b8f63')}
              onClick={() => colorMutation.mutate(color)}
            >
              <Palette className="size-3.5" />
              {tx('Kaydet')}
            </Button>
            {tenant.primaryColorHex && (
              <Button
                size="sm"
                variant="outline"
                className="cursor-pointer"
                disabled={resetColorMutation.isPending}
                onClick={() => resetColorMutation.mutate()}
              >
                {tx('Varsayılana dön')}
              </Button>
            )}
          </div>
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead
          title={tx('Kendi SMTP sunucusu')}
          note={
            tenant.hasCustomSmtp
              ? tx('Şu an {0} üzerinden gönderiliyor', [tenant.smtpFromAddress ?? tx('özel sunucu')])
              : tx('Ayarlanmazsa platformun varsayılan gönderim sunucusu kullanılır')
          }
        />
        <PanelBody className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField
              id="smtp-host" label={tx('Sunucu')} placeholder="smtp.sirketiniz.com"
              value={smtpHost} onChange={(e) => setSmtpHost(e.target.value)}
            />
            <TextField
              id="smtp-port" label={tx('Port')} type="number" placeholder="587"
              value={smtpPort} onChange={(e) => setSmtpPort(e.target.value)}
            />
            <TextField
              id="smtp-user" label={tx('Kullanıcı adı')}
              value={smtpUser} onChange={(e) => setSmtpUser(e.target.value)}
            />
            <TextField
              id="smtp-password" label={tx('Şifre')} type="password"
              placeholder={tenant.hasCustomSmtp ? tx('(değiştirmek için girin)') : ''}
              value={smtpPassword} onChange={(e) => setSmtpPassword(e.target.value)}
            />
            <TextField
              id="smtp-from-address" label={tx('Gönderen e-posta')} placeholder="noreply@sirketiniz.com"
              value={smtpFromAddress} onChange={(e) => setSmtpFromAddress(e.target.value)}
            />
            <TextField
              id="smtp-from-name" label={tx('Gönderen adı')} placeholder={tx('Şirketiniz')}
              value={smtpFromName} onChange={(e) => setSmtpFromName(e.target.value)}
            />
          </div>
          <div className="flex gap-2">
            <Button
              size="sm"
              className="cursor-pointer"
              disabled={smtpMutation.isPending || !smtpHost.trim()}
              onClick={() => smtpMutation.mutate()}
            >
              {tx('Kaydet')}
            </Button>
            {tenant.hasCustomSmtp && (
              <Button
                size="sm"
                variant="outline"
                className="cursor-pointer"
                disabled={removeSmtpMutation.isPending}
                onClick={() => removeSmtpMutation.mutate()}
              >
                {tx('Kaldır')}
              </Button>
            )}
          </div>
        </PanelBody>
      </Panel>
    </>
  )
}
