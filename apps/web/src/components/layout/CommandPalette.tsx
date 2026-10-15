import { useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { BookOpenText, Building2, CalendarPlus, CornerDownLeft, Inbox, LayoutDashboard, Megaphone, ReceiptText, Send, User, UserSearch } from 'lucide-react'
import {
  OmniCommandPalette,
  type OmniItem,
  type OmniSource,
} from '@/components/ui/omni-command-palette'
import { employeeApi } from '@/api/employees'
import { organizationApi } from '@/api/organization'
import { workflowApi } from '@/api/workflows'
import { mlInsightsApi } from '@/api/mlInsights'
import { semanticSourceLabel } from '@/lib/mlInsights'
import { qk, useMyEmployeeId } from '@/api/queries'
import { workflowTypeLabels } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { overviewItem, type NavItemData } from './nav-config'
import { useNavGroups } from './use-nav'
import { tx } from '@/lib/i18n'
import { normalizeSearch } from '@/lib/format'
import { directoryApi, directoryKey } from '@/api/directory'
import { OPEN_WHATS_NEW_EVENT } from '@/features/whats-new/WhatsNewPanel'

/**
 * ⌘K paleti — dört kaynağı tek arama kutusunda birleştirir:
 * çalışanlar, şirketler, onay talepleri ve modüllere gidiş komutları.
 *
 * Kaynaklar TanStack Query önbelleğinden okunuyor (fetchQuery); palet her
 * açılışta ağa çıkmıyor, listeler zaten sayfalarda çekilmiş oluyor.
 */
export function CommandPalette({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (v: boolean) => void
}) {
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { can: canRole, roles, tenantSlug } = useAuth()
  const navTree = useNavGroups()
  // Kişisel eylemler (izin/masraf/talep oluşturma) şirket bağlamı ve çalışan kaydı ister: kiracı seçmemiş
  // platform yöneticisine ya da çalışan kaydı olmayan hesaba gösterilmez. Kiracı verisi aramaları da
  // şirket bağlamı yoksa yapılmaz.
  const hasTenant = Boolean(tenantSlug)
  const { employeeId: myEmployeeId } = useMyEmployeeId(hasTenant)
  const hasEmployee = hasTenant && Boolean(myEmployeeId)
  const can = useMemo(() => (p: Parameters<typeof canRole>[0]) => hasTenant && canRole(p), [canRole, hasTenant])

  const sources = useMemo<OmniSource[]>(() => {
    const list: OmniSource[] = []

    /* --- Statik: modüllere gidiş. İzin filtresi sidebar ile birebir aynı. --- */
    const flatten = (items: NavItemData[]): NavItemData[] =>
      items.flatMap((i) => [i, ...(i.children ? flatten(i.children) : [])])

    const goItems: OmniItem[] = [
      overviewItem,
      ...navTree.flatMap((g) =>
        g.items.flatMap((i) => (i.children ? flatten([i]) : [i])),
      ),
    ]
      .filter((i) => Boolean(i.path))
      .map((i) => ({
        id: `go:${i.id}`,
        label: i.title,
        groupId: 'go',
        subtitle: i.path,
        icon: <i.icon className="size-4" strokeWidth={1.5} />,
        keywords: [i.id, i.path ?? ''],
        onAction: () => navigate(i.path!),
      }))

    /* --- Dalga 12: eylemler (yetkiye göre). Sayfalar ?yeni=1 ile yeni kayıt penceresini açar. --- */
    const actions: OmniItem[] = [
      hasEmployee && can('leave:create') && {
        id: 'act:leave', label: tx('İzin talebi oluştur'), subtitle: tx('Yeni izin talebi penceresi'),
        icon: <CalendarPlus className="size-4" strokeWidth={1.5} />, keywords: ['izin', 'tatil', 'leave'],
        onAction: () => navigate('/panel/izin?yeni=1'),
      },
      hasEmployee && can('expense:create') && {
        id: 'act:expense', label: tx('Masraf ekle'), subtitle: tx('Yeni masraf talebi (taslak)'),
        icon: <ReceiptText className="size-4" strokeWidth={1.5} />, keywords: ['masraf', 'fiş', 'harcama', 'expense'],
        onAction: () => navigate('/panel/masraf?yeni=1'),
      },
      hasEmployee && can('workflow:create') && {
        id: 'act:workflow', label: tx('Onay talebi aç'), subtitle: tx('Pozisyon, satın alma ve diğer talepler'),
        icon: <Send className="size-4" strokeWidth={1.5} />, keywords: ['talep', 'onay', 'request'],
        onAction: () => navigate('/panel/onaylar?yeni=1'),
      },
      {
        id: 'act:dashboard', label: tx('Ana paneli düzenle'), subtitle: tx('Kartları göster, gizle, sırala'),
        icon: <LayoutDashboard className="size-4" strokeWidth={1.5} />, keywords: ['panel', 'widget', 'kart', 'dashboard'],
        onAction: () => navigate('/panel?duzenle=1'),
      },
      {
        id: 'act:whatsnew', label: tx('Yenilikler'), subtitle: tx('Son sürümlerde neler değişti'),
        icon: <Megaphone className="size-4" strokeWidth={1.5} />, keywords: ['yeni', 'sürüm', 'changelog'],
        onAction: () => window.dispatchEvent(new CustomEvent(OPEN_WHATS_NEW_EVENT)),
      },
    ].filter(Boolean).map((a) => ({ ...(a as OmniItem), groupId: 'actions' }))
    list.push({
      id: 'actions',
      label: tx('Eylemler'),
      fetch: (q) => {
        const needle = normalizeSearch(q)
        return needle
          ? actions.filter((a) => normalizeSearch(`${a.label} ${(a.keywords ?? []).join(' ')}`).includes(needle))
          : actions
      },
      emptyHint: tx('Eşleşen eylem yok.'),
    })

    list.push({
      id: 'go',
      label: tx('Git'),
      fetch: (q) => {
        // Türkçe harf katlama: "izin" → "İzin", "ise alim" → "İşe alım" (dil seçiminden bağımsız).
        const needle = normalizeSearch(q)
        return needle
          ? goItems.filter((i) => normalizeSearch(i.label).includes(needle))
          : goItems
      },
      emptyHint: tx('Eşleşen modül yok.'),
    })

    /* ------------------------------- Çalışanlar ------------------------------- */
    if (can('employee:viewAll')) {
      list.push({
        id: 'employees',
        label: tx('Çalışanlar'),
        minQuery: 2,
        emptyHint: tx('Eşleşen çalışan yok.'),
        fetch: async (q) => {
          const employees = await qc.fetchQuery({
            queryKey: qk.employees,
            queryFn: ({ signal }) => employeeApi.list(signal),
            staleTime: 60_000,
          })
          const needle = normalizeSearch(q)
          return employees
            .filter((e) =>
              normalizeSearch(`${e.firstName} ${e.lastName} ${e.email}`).includes(needle),
            )
            .slice(0, 8)
            .map<OmniItem>((e) => ({
              id: `emp:${e.id}`,
              label: `${e.firstName} ${e.lastName}`,
              groupId: 'employees',
              subtitle: e.email,
              icon: <User className="size-4" strokeWidth={1.5} />,
              onAction: () => navigate(`/panel/calisanlar/${e.id}`),
            }))
        },
      })
    }

    /* ---- Kişi ara (çalışan listesi yetkisi olmayanlar): dizinden ad, yetenek dizininde açılır ---- */
    if (hasTenant && !can('employee:viewAll') && goItems.some((i) => i.id === 'go:directory')) {
      list.push({
        id: 'people',
        label: tx('Kişiler'),
        minQuery: 2,
        emptyHint: tx('Eşleşen kişi yok.'),
        fetch: async (q) => {
          const people = await qc.fetchQuery({
            queryKey: directoryKey,
            queryFn: ({ signal }) => directoryApi.list(signal),
            staleTime: 10 * 60_000,
          })
          const needle = normalizeSearch(q)
          return people
            .filter((p) => normalizeSearch(p.fullName).includes(needle))
            .slice(0, 8)
            .map<OmniItem>((p) => ({
              id: `person:${p.id}`,
              label: p.fullName,
              groupId: 'people',
              subtitle: tx('Yetenek dizininde göster'),
              icon: <UserSearch className="size-4" strokeWidth={1.5} />,
              onAction: () => navigate(`/panel/yetenek-dizini?ara=${encodeURIComponent(p.fullName)}`),
            }))
        },
      })
    }

    /* -------------------------------- Şirketler ------------------------------- */
    if (can('organization:view')) {
      list.push({
        id: 'companies',
        label: tx('Şirketler'),
        minQuery: 2,
        emptyHint: tx('Eşleşen şirket yok.'),
        fetch: async (q) => {
          const companies = await qc.fetchQuery({
            queryKey: qk.companies,
            queryFn: ({ signal }) => organizationApi.listCompanies(signal),
            staleTime: 60_000,
          })
          const needle = normalizeSearch(q)
          return companies
            .filter((c) => normalizeSearch(c.name).includes(needle))
            .slice(0, 6)
            .map<OmniItem>((c) => ({
              id: `co:${c.id}`,
              label: c.name,
              groupId: 'companies',
              subtitle: tx('{0} departman', [c.departments?.length ?? 0]),
              icon: <Building2 className="size-4" strokeWidth={1.5} />,
              onAction: () => navigate(`/panel/organizasyon/${c.id}`),
            }))
        },
      })
    }

    /* ----------------------------- Onay talepleri ----------------------------- */
    if (can('workflow:view')) {
      list.push({
        id: 'workflows',
        label: tx('Onay talepleri'),
        minQuery: 2,
        emptyHint: tx('Eşleşen talep yok.'),
        fetch: async (q) => {
          const workflows = await qc.fetchQuery({
            queryKey: qk.workflows({}),
            queryFn: ({ signal }) => workflowApi.list({}, signal),
            staleTime: 30_000,
          })
          const needle = normalizeSearch(q)
          return workflows
            .filter((w) => normalizeSearch(`${w.subject ?? ''} ${workflowTypeLabels[w.type]}`).includes(needle))
            .slice(0, 6)
            .map<OmniItem>((w) => ({
              id: `wf:${w.id}`,
              label: w.subject || workflowTypeLabels[w.type],
              groupId: 'workflows',
              subtitle: workflowTypeLabels[w.type],
              icon: <Inbox className="size-4" strokeWidth={1.5} />,
              onAction: () => navigate(`/panel/onaylar/${w.id}`),
            }))
        },
      })
    }

    /* ------------------- Politika ve belgeler (anlamsal arama, ML dalgası 2) ------------------- */
    // Herkese açık: sonuçlar sunucuda kullanıcının görebileceği belgelerle süzülür (şirket bağlamı gerekir).
    if (hasTenant) list.push({
      id: 'semantic',
      label: tx('Politika ve belgeler'),
      minQuery: 3,
      emptyHint: tx('Anlamca yakın belge yok.'),
      fetch: async (q) => {
        const r = await qc.fetchQuery({
          queryKey: ['semantic', q.trim()],
          queryFn: ({ signal }) => mlInsightsApi.semanticSearch(q.trim(), signal),
          staleTime: 60_000,
        }).catch(() => ({ hits: [] }))
        return r.hits.slice(0, 5).map<OmniItem>((h) => ({
          id: `sem:${h.source}:${h.id}`,
          label: h.title,
          groupId: 'semantic',
          subtitle: semanticSourceLabel(h.source),
          icon: <BookOpenText className="size-4" strokeWidth={1.5} />,
          onAction: () => navigate(h.source === 'announcement' ? '/panel/duyurular'
            : `/panel/belgeler-kutuphanesi?ara=${encodeURIComponent(q.trim())}&anlamsal=1`),
        }))
      },
    })

    return list
  }, [can, roles, navigate, qc, navTree, hasEmployee, hasTenant])

  return (
    <OmniCommandPalette
      open={open}
      onOpenChange={onOpenChange}
      sources={sources}
      storageKey="hr360.omni.recents"
      placeholder={tx('Eylem, kişi, talep, politika ara veya bir sayfaya git…')}
      renderFooter={(active) => (
        <div className="flex items-center justify-between px-3 py-2 text-[11px] text-muted-foreground">
          <span>{tx('↑ ↓ gezin · Esc kapat')}</span>
          <span className="flex items-center gap-1">
            <CornerDownLeft className="size-3" />
            {active ? tx('aç') : tx('seç')}
          </span>
        </div>
      )}
    />
  )
}
