/**
 * Giriş sayfasındaki "Bağlanabildiği uygulamalar" şeridi.
 *
 * Logolar public/integrations altında yerel dosya (CSP: yalnızca aynı köken);
 * markalar sahiplerine aittir (NOTICE). Varsayılan gri, üzerine gelince renkli.
 * Webhook ve LDAP marka değil, protokol: ikonla gösterilir.
 */
import { Network, Webhook } from 'lucide-react'
import { LogoMarquee, type LogoMarqueeItem } from '@/components/fx/logo-marquee'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'

interface Integration extends LogoMarqueeItem {
  src?: string
  icon?: React.ElementType
  /** Tek renkli (siyah) logo: koyu temada ters çevrilir. */
  mono?: boolean
}

const INTEGRATIONS: Integration[] = [
  { id: 'slack', label: 'Slack', src: '/integrations/slack.svg' },
  { id: 'teams', label: 'Microsoft Teams', src: '/integrations/teams.svg' },
  { id: 'zoom', label: 'Zoom', src: '/integrations/zoom.svg' },
  { id: 'google', label: 'Google Workspace', src: '/integrations/google.svg' },
  { id: 'outlook', label: 'Microsoft 365', src: '/integrations/outlook.svg' },
  { id: 'entra', label: 'Microsoft Entra ID', src: '/integrations/microsoft.svg' },
  { id: 'ldap', label: 'Active Directory / LDAP', icon: Network },
  { id: 'okta', label: 'Okta', src: '/integrations/okta.svg' },
  { id: 'webhook', label: 'Webhook', icon: Webhook },
  { id: 'zapier', label: 'Zapier', src: '/integrations/zapier.svg' },
  { id: 'n8n', label: 'n8n', src: '/integrations/n8n.svg' },
  { id: 'openai', label: 'OpenAI', src: '/integrations/openai.svg', mono: true },
  { id: 'anthropic', label: 'Anthropic', src: '/integrations/anthropic.svg', mono: true },
  { id: 'ollama', label: 'Ollama', src: '/integrations/ollama.svg', mono: true },
]

function IntegrationChip({ item }: { item: Integration }) {
  const Icon = item.icon
  return (
    <span
      title={item.label}
      className="group/logo flex items-center gap-2 rounded-full border border-border/70 bg-card/50 px-3.5 py-1.5 text-[12.5px] whitespace-nowrap text-muted-foreground backdrop-blur transition-colors duration-200 hover:border-primary/40 hover:text-foreground"
    >
      {item.src ? (
        <img
          src={item.src}
          alt=""
          aria-hidden="true"
          draggable={false}
          className={cn(
            'size-4 object-contain opacity-70 grayscale transition-[filter,opacity] duration-300 group-hover/logo:opacity-100 group-hover/logo:grayscale-0',
            item.mono && 'dark:invert',
          )}
        />
      ) : (
        Icon && (
          <Icon
            aria-hidden="true"
            className="size-4 text-muted-foreground/80 transition-colors duration-300 group-hover/logo:text-primary"
            strokeWidth={1.75}
          />
        )
      )}
      {item.label}
    </span>
  )
}

export function IntegrationsMarquee({ className }: { className?: string }) {
  return (
    <div className={cn('w-full', className)} data-testid="integrations-marquee">
      <p className="mb-2.5 text-center text-[11px] font-semibold tracking-[0.08em] text-muted-foreground/80 uppercase">
        {tx('Bağlanabildiği uygulamalar')}
      </p>
      <LogoMarquee
        items={INTEGRATIONS}
        label={tx('Bağlanabildiği uygulamalar')}
        direction="right"
        speed={30}
        gap={12}
        renderItem={(item) => <IntegrationChip item={item} />}
      />
    </div>
  )
}
