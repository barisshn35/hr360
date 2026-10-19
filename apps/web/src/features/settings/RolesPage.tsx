import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Info, KeyRound, Mail, Plus, ShieldAlert, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { DataTable, type Column } from '@/components/ui/DataTable'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tooltip, TooltipTrigger, TooltipContent, TooltipProvider } from '@/components/ui/tooltip'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { useCompanies, useDepartmentList } from '@/api/queries'
import { tenantApi } from '@/api/tenant'
import { permissionsFor, roleLabels, type Permission, type Role } from '@/auth/roles'
import { useModules } from '@/lib/modules'
import { tx } from '@/lib/i18n'
import { passwordLinkApi, passwordLinkKeys } from '@/api/password-links'
import { PasswordLinksPanel } from '@/features/access/PasswordLinksPanel'
import { usePasswordLinkDialog } from '@/features/access/PasswordLinkCard'

/**
 * Bu sayfadan atanabilecek roller - platform-admin platform seviyesinde,
 * buradan asla atanmaz/kaldırılmaz (bkz. eski TeamRolesPanel).
 */
const ASSIGNABLE_ROLES: Role[] = ['employee', 'manager', 'accounting', 'hr-admin', 'tenant-admin']

/**
 * Her rolün ne işe yaradığını tek cümleyle anlatır - tooltip içeriği.
 * roles.ts'teki Permission kodları (örn. "leave:manageBalance") burada
 * kod olarak değil, çalışanın anlayacağı dilde özetleniyor.
 */
const ROLE_SUMMARY: Partial<Record<Role, string>> = {
  employee: tx('Kendi izin/masraf talebini açar, kendi puantajını ve eğitimlerini görür.'),
  manager: tx('Onay kutusunda karar verir, ekip kurar, performans ve onboarding sürecini yönetir.'),
  accounting: tx('Masraf beyanlarını görür ve onaylananları ödendi işaretler.'),
  'hr-admin': tx('Çalışan kayıtlarını, ücret bantlarını, izin bakiyelerini ve ilan yayınını yönetir.'),
  'tenant-admin': tx('Şirket ayarlarının sahibi; İK yönetiminin tüm yetkilerini de devralır.'),
}

/**
 * roles.ts'teki Permission union'ındaki her koda kısa bir Türkçe açıklama -
 * "Ek izin" seçicisinde kod yerine bunlar gösterilir. Backend tarafında bu
 * iznin gerçekten hangi işlemleri açtığı, sadece EK_IZIN_BACKEND_DESTEGI'nde
 * listelenen servislerde uygulanmış durumda (bkz. aşağı).
 */
const PERMISSION_LABELS: Record<Permission, string> = {
  'organization:view': tx('Departman / organizasyon yapısını görüntüler'),
  'organization:manage': tx('Departman / organizasyon yapısını düzenler'),
  'employee:viewAll': tx('Tüm çalışanların listesini görüntüler'),
  'employee:manage': tx('Çalışan kayıtlarını düzenler'),
  'employee:create': tx('Yeni çalışan kaydı oluşturur'),
  'workflow:view': tx('Onay taleplerinin durumunu görüntüler'),
  'workflow:create': tx('Kendi adına onay talebi oluşturur'),
  'workflow:decide': tx('Onay kutusundaki taleplere karar verir'),
  'leave:view': tx('İzin taleplerini ve bakiyelerini görüntüler'),
  'leave:create': tx('Kendi adına izin talebi oluşturur'),
  'leave:manageBalance': tx('Çalışanların yıllık izin bakiyesini tanımlar'),
  'recruitment:view': tx('Açık iş ilanlarını görüntüler'),
  'recruitment:candidates': tx('Adayları görüntüler, başvuru sürecini yönetir'),
  'recruitment:publish': tx('İş ilanı oluşturur ve yayına alır'),
  'onboarding:view': tx('Onboarding planlarını görüntüler'),
  'onboarding:manage': tx('Onboarding planı oluşturur, düzenler'),
  'asset:manage': tx('Zimmet / demirbaş kayıtlarını yönetir'),
  'timeshift:view': tx('Puantaj ve vardiya bilgilerini görüntüler'),
  'timeshift:clock': tx('Kendi mesai giriş / çıkışını kaydeder'),
  'timeshift:manage': tx('Vardiya deseni ve puantaj kayıtlarını yönetir'),
  'performance:view': tx('Performans değerlendirmelerini görüntüler'),
  'performance:manage': tx('Performans değerlendirme sürecini yönetir'),
  'team:manage': tx('Ekip kurar, lider atar, üye ekler / çıkarır'),
  'learning:view': tx('Eğitim kataloğunu görüntüler'),
  'learning:enroll': tx('Bir eğitime kayıt olur'),
  'learning:manage': tx('Eğitim kataloğuna eğitim ekler, düzenler'),
  'compensation:view': tx('Ücret bantlarını ve maaş geçmişini görüntüler'),
  'expense:view': tx('Masraf taleplerini görüntüler'),
  'expense:create': tx('Kendi adına masraf talebi oluşturur'),
  'expense:manage': tx('Masraf taleplerini onaylar / yönetir'),
  'expense:markPaid': tx('Onaylanan masraf talebini ödendi işaretler'),
  'document:manage': tx('Şirket dokümanlarını yönetir'),
  'case:view': tx('İK vakalarını görüntüler'),
  'case:create': tx('Yeni İK vakası açar'),
  'case:manage': tx('İK vakalarını yönetir, sonuçlandırır'),
  'notification:view': tx('Kendine gelen bildirimleri görüntüler'),
  'notification:manage': tx('Bildirim şablonlarını oluşturur / düzenler'),
  'osh:physician': tx('İşyeri hekimi: muayene sağlık notlarını görür ve yazar'),
  'osh:specialist': tx('İSG uzmanı: iş kazası ve İSG eğitim kayıtlarını yönetir'),
  'platform:manage': tx('Platformdaki tüm kiracıları yönetir'),
  'tenant:manage': tx('Kendi şirketinin (kiracının) ayarlarını yönetir'),
}

/**
 * "Ek izin" mekanizması backend'de RequireTenantAdmin ile korunuyor ve
 * herhangi bir Permission adını Keycloak'ta bir role çevirip atayabiliyor -
 * ama o rolü GERÇEKTEN tanıyıp yetkilendirmede kullanan servis yoksa,
 * atama sessizce hiçbir şey açmaz. Bu liste, ilgili servisin Program.cs'inde
 * policy'si "ext-<izin>" rolünü de kabul edecek şekilde genişletilmiş tüm
 * izinleri kapsıyor. employee rolünün izinleri (organization:view,
 * workflow:view/create, leave:view/create, onboarding:view, timeshift:view/
 * clock, performance:view, learning:view/enroll, expense:view/create,
 * case:view/create, notification:view) burada YOK - backend'de zaten
 * AUTH_ONLY (herkese açık), bu yüzden "ek izin" mekanizması onlar için
 * anlamsız.
 */
const PERMISSIONS_WITH_BACKEND_SUPPORT = new Set<Permission>([
  'compensation:view',
  'employee:create',
  'employee:manage',
  'recruitment:candidates',
  'recruitment:publish',
  'asset:manage',
  'onboarding:manage',
  'leave:manageBalance',
  'learning:manage',
  'notification:manage',
  'document:manage',
  'expense:manage',
  'expense:markPaid',
  'case:manage',
  'organization:manage',
  'team:manage',
  'performance:manage',
  'osh:physician',
  'osh:specialist',
  'workflow:decide',
  'timeshift:manage',
  'tenant:manage',
  'platform:manage',
])

interface RoleRow {
  /** Satır anahtarı: çalışan kimliği; çalışan kaydı olmayan hesapta "kc:<keycloak kimliği>". */
  rowId: string
  employeeId: string | null
  hasEmployeeRecord?: boolean
  keycloakUserId: string | null
  firstName: string
  lastName: string
  email: string
  hasLoginAccess: boolean
  roles: string[]
  extraPermissions: string[]
  /** organization-service'te bir departmanın başı olarak atanmış mı -
   * öyleyse ama "manager" rolü yoksa, bu kişi kendisine atanmış onay
   * adımlarını hiç karara bağlayamaz (RequireManagerOrAbove'dan geçemez). */
  isDepartmentHead: boolean
}

export function RolesPage() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const { roles: myRoles } = useAuth()
  // Tek şirket kipinde kiracı yönetimi yok: platform:manage ek izni sunulmaz (bakım yonetici hesabındadır).
  const single = useModules().tenancy === 'single'
  const canGrantExtraPermissions = myRoles.includes('tenant-admin') || myRoles.includes('platform-admin')
  // Parola linkleri (liste, sıfırlama linki): İK yöneticisi ve üstü (backend RequireHrAdmin).
  const canManageLinks = canGrantExtraPermissions || myRoles.includes('hr-admin')
  const linkDialog = usePasswordLinkDialog()
  const [addingPermFor, setAddingPermFor] = useState<string | null>(null)

  const companies = useCompanies()
  const companyId = companies.data?.[0]?.id
  const departments = useDepartmentList(companyId)
  const membersQuery = useQuery({
    queryKey: ['team-members'],
    queryFn: ({ signal }) => tenantApi.members(signal),
  })

  const headEmployeeIds = useMemo(
    () => new Set((departments.data ?? []).map((d) => d.headEmployeeId).filter(Boolean)),
    [departments.data],
  )

  const rows: RoleRow[] = useMemo(
    () =>
      (membersQuery.data ?? []).map((m) => ({
        ...m,
        rowId: m.employeeId ?? `kc:${m.keycloakUserId ?? m.email}`,
        isDepartmentHead: m.employeeId ? headEmployeeIds.has(m.employeeId) : false,
      })),
    [membersQuery.data, headEmployeeIds],
  )

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['team-members'] })

  const inviteMutation = useMutation({
    mutationFn: ({ employeeId }: { employeeId: string; who: string }) => tenantApi.inviteMember(employeeId),
    onSuccess: (r, { who }) => {
      invalidate()
      void queryClient.invalidateQueries({ queryKey: passwordLinkKeys.list })
      if (r.passwordLink) linkDialog.show(r.passwordLink, who)
      else toast.ok(tx('Giriş erişimi açıldı.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Davet gönderilemedi.')),
  })

  const resetLinkMutation = useMutation({
    mutationFn: ({ userId }: { userId: string; who: string }) => passwordLinkApi.resetFor(userId),
    onSuccess: (v, { who }) => {
      void queryClient.invalidateQueries({ queryKey: passwordLinkKeys.list })
      linkDialog.show(v, who)
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Link üretilemedi.')),
  })

  const assignMutation = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      tenantApi.assignRole(userId, role),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Rol atandı.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Rol atanamadı.')),
  })

  const removeMutation = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      tenantApi.removeRole(userId, role),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('Rol kaldırıldı.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('Rol kaldırılamadı.')),
  })

  const assignPermMutation = useMutation({
    mutationFn: ({ userId, permission }: { userId: string; permission: string }) =>
      tenantApi.assignExtraPermission(userId, permission),
    onSuccess: () => {
      invalidate()
      setAddingPermFor(null)
      toast.ok(tx('İzin atandı.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İzin atanamadı.')),
  })

  const removePermMutation = useMutation({
    mutationFn: ({ userId, permission }: { userId: string; permission: string }) =>
      tenantApi.removeExtraPermission(userId, permission),
    onSuccess: () => {
      invalidate()
      toast.ok(tx('İzin kaldırıldı.'))
    },
    onError: (e: unknown) => toast.stop(e instanceof Error ? e.message : tx('İzin kaldırılamadı.')),
  })

  const pending = assignMutation.isPending || removeMutation.isPending

  const confirm = useConfirm()

  // Rol vermek/almak yetkiyi hemen değiştirir: yanlış tıklama şirket yöneticiliği verebilirdi.
  async function toggleRole(row: RoleRow, role: Role, checked: boolean) {
    if (!row.keycloakUserId) return
    const who = `${row.firstName} ${row.lastName}`
    const ok = await confirm(checked
      ? { title: tx('Rol verilsin mi?'), note: tx('{0} kişisine "{1}" rolü verilecek; yetkiler bir sonraki girişte geçerli olur.', [who, roleLabels[role]]), action: tx('Rolü ver'), destructive: role === 'tenant-admin' }
      : { title: tx('Rol kaldırılsın mı?'), note: tx('{0} kişisinden "{1}" rolü kaldırılacak.', [who, roleLabels[role]]), action: tx('Rolü kaldır') })
    if (!ok) return
    if (checked) assignMutation.mutate({ userId: row.keycloakUserId, role })
    else removeMutation.mutate({ userId: row.keycloakUserId, role })
  }

  const employeeColumn: Column<RoleRow> = {
    id: 'employee',
    header: tx('Çalışan'),
    searchText: (r) => `${r.firstName} ${r.lastName} ${r.email}`,
    sortValue: (r) => `${r.firstName} ${r.lastName}`,
    cell: (r) => (
      <div className="min-w-0">
        <div className="flex items-center gap-1.5">
          <p className="truncate text-[13px] font-medium">
            {r.firstName} {r.lastName}
          </p>
          {r.isDepartmentHead && !r.roles.includes('manager') && !r.roles.includes('hr-admin') && (
            <TooltipProvider>
              <Tooltip>
                <TooltipTrigger asChild>
                  <ShieldAlert className="size-3.5 shrink-0 text-[hsl(var(--warning))]" />
                </TooltipTrigger>
                <TooltipContent className="max-w-64 text-[12px] leading-relaxed">
                  {tx('Bir departmanın başı ama Yönetici rolü yok - kendisine atanan onayları karara bağlayamaz.')}
                </TooltipContent>
              </Tooltip>
            </TooltipProvider>
          )}
        </div>
        <p className="truncate text-[12px] text-muted-foreground">{r.email}{r.employeeId ? '' : ` · ${tx('Çalışan kaydı yok')}`}</p>
      </div>
    ),
  }

  const roleColumns: Column<RoleRow>[] = ASSIGNABLE_ROLES.map((role) => ({
    id: `role-${role}`,
    header: roleLabels[role],
    align: 'left',
    sortValue: (r) => (r.roles.includes(role) ? 1 : 0),
    cell: (r) =>
      r.hasLoginAccess ? (
        <Checkbox
          checked={r.roles.includes(role)}
          // Şirket yöneticisi rolünü yalnızca şirket/platform yöneticisi verip alabilir.
          disabled={pending || !r.keycloakUserId || (role === 'tenant-admin' && !canGrantExtraPermissions)}
          onCheckedChange={(checked) => toggleRole(r, role, checked === true)}
          aria-label={tx('{0} {1} için {2} rolü', [r.firstName, r.lastName, roleLabels[role]])}
        />
      ) : (
        <span className="text-[12px] text-muted-foreground">—</span>
      ),
  }))

  const inviteColumn: Column<RoleRow> = {
    id: 'invite',
    header: '',
    cell: (r) =>
      r.hasLoginAccess ? (
        canManageLinks && r.keycloakUserId ? (
          <Button
            size="sm"
            variant="ghost"
            className="cursor-pointer"
            disabled={resetLinkMutation.isPending}
            onClick={async () => {
              if (await confirm({
                title: tx('Parola linki üretilsin mi?'),
                note: tx('{0} {1} için tek kullanımlık, süreli bir parola linki üretilir ve size bir kez gösterilir. Açık linkler iptal olur.', [r.firstName, r.lastName]),
                action: tx('Link üret'), destructive: false,
              })) resetLinkMutation.mutate({ userId: r.keycloakUserId!, who: `${r.firstName} ${r.lastName}` })
            }}
          >
            <KeyRound className="size-3.5" />
            {tx('Parola linki')}
          </Button>
        ) : null
      ) : !r.employeeId ? null : (
        <Button
          size="sm"
          variant="outline"
          className="cursor-pointer"
          disabled={inviteMutation.isPending}
          onClick={async () => {
            if (await confirm({
              title: tx('Giriş erişimi verilsin mi?'),
              note: tx('{0} {1} için hesap açılır, Çalışan rolü verilir ve süreli bir parola linki üretilir (e-posta açıksa {2} adresine de gönderilir).', [r.firstName, r.lastName, r.email]),
              action: tx('Erişim ver'), destructive: false,
            })) inviteMutation.mutate({ employeeId: r.employeeId!, who: `${r.firstName} ${r.lastName}` })
          }}
        >
          <Mail className="size-3.5" />
          {tx('Giriş erişimi ver')}
        </Button>
      ),
  }

  /**
   * Standart 5 rolün dışında, kullanıcının rolünden bağımsız tek tek izin
   * atama - yalnızca Şirket/Platform Yöneticisi görür ve kullanır
   * (backend RequireTenantAdmin ile zaten aynı kısıtı uyguluyor; burada
   * sütunu hiç göstermemek, yetkisi olmayan birinin boş bir "+" ile
   * karşılaşıp 403 almasını da önlüyor).
   */
  const permLabel = (perm: string) =>
    single && perm === 'platform:manage'
      ? tx('Bakım ve sistem ayarları (tek şirket kipi)')
      : (PERMISSION_LABELS[perm as Permission] ?? perm)
  const extraPermColumn: Column<RoleRow> = {
    id: 'extra-permissions',
    header: tx('Ek izinler'),
    cell: (r) => {
      if (!r.hasLoginAccess) return <span className="text-[12px] text-muted-foreground">—</span>
      const available = Array.from(PERMISSIONS_WITH_BACKEND_SUPPORT).filter(
        (p) => !r.extraPermissions.includes(p) && !(single && p === 'platform:manage'),
      )
      return (
        <div className="flex flex-wrap items-center gap-1.5">
          {r.extraPermissions.map((perm) => (
            <StatusBadge key={perm} tone="warning" className="group gap-1">
              {permLabel(perm)}
              <button
                type="button"
                aria-label={tx('{0} iznini kaldır', [permLabel(perm)])}
                className="cursor-pointer opacity-60 hover:opacity-100"
                disabled={removePermMutation.isPending}
                onClick={() =>
                  r.keycloakUserId &&
                  removePermMutation.mutate({ userId: r.keycloakUserId, permission: perm })
                }
              >
                <X className="size-3" />
              </button>
            </StatusBadge>
          ))}

          {addingPermFor === r.rowId ? (
            available.length === 0 ? (
              <span className="text-[12px] text-muted-foreground">{tx('Eklenecek izin yok')}</span>
            ) : (
              <SelectField
                id={`add-perm-${r.rowId}`}
                label=""
                value=""
                onChange={(permission) =>
                  r.keycloakUserId &&
                  assignPermMutation.mutate({ userId: r.keycloakUserId, permission })
                }
                options={available.map((p) => ({ value: p, label: PERMISSION_LABELS[p] }))}
                placeholder={tx('İzin seç')}
                className="w-56"
              />
            )
          ) : (
            <button
              type="button"
              className="cursor-pointer text-muted-foreground opacity-60 hover:opacity-100"
              aria-label={tx('Ek izin ekle')}
              onClick={() => setAddingPermFor(r.rowId)}
            >
              <Plus className="size-4" />
            </button>
          )}
        </div>
      )
    },
  }

  return (
    <div>
      <PageHeader
        title={tx('Roller')}
        description={tx('Çalışanların sistem yetkilerini tek tablodan verin ya da kaldırın. Bir hücreyi işaretlemek o rolü ekler.')}
      />

      <TooltipProvider>
        <div className="mb-4 flex flex-wrap gap-x-5 gap-y-2">
          {ASSIGNABLE_ROLES.map((role) => (
            <Tooltip key={role}>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  className="flex cursor-help items-center gap-1 text-[12px] text-muted-foreground underline decoration-dotted underline-offset-4 hover:text-foreground"
                >
                  <Info className="size-3" />
                  {roleLabels[role]}
                </button>
              </TooltipTrigger>
              <TooltipContent className="max-w-72 text-[12px] leading-relaxed">
                <p className="mb-1.5 font-medium">{ROLE_SUMMARY[role]}</p>
                <p className="text-muted-foreground">
                  {Array.from(permissionsFor([role])).sort().join(' · ')}
                </p>
              </TooltipContent>
            </Tooltip>
          ))}
        </div>
      </TooltipProvider>

      <DataTable<RoleRow>
        rows={rows}
        rowKey={(r) => r.rowId}
        isLoading={membersQuery.isPending}
        error={membersQuery.error}
        emptyTitle={tx('Henüz çalışan yok')}
        emptyDetail={tx('Önce Çalışanlar bölümünden ekleyin.')}
        searchPlaceholder={tx('Ad, soyad veya e-posta')}
        columns={[
          employeeColumn,
          ...roleColumns,
          ...(canGrantExtraPermissions ? [extraPermColumn] : []),
          inviteColumn,
        ]}
      />

      {canGrantExtraPermissions && (
        <p className="mt-3 text-[12px] leading-relaxed text-muted-foreground">
          <Info className="mr-1 inline size-3" />
          {tx('Ek izinler, bir çalışana rolünün dışında tek bir yetki verir - yalnızca Şirket ve Platform Yöneticisi bu şekilde izin atayabilir.')}
        </p>
      )}

      <p className="mt-4 flex items-start gap-1.5 text-[12px] leading-relaxed text-muted-foreground">
        <ShieldAlert className="mt-0.5 size-3.5 shrink-0" />
        {tx('Bir tenant\'ta en az bir Şirket Yöneticisi kalmalı - son kişinin rolü kaldırılamaz.')}
      </p>

      {canManageLinks && (
        <div className="mt-6 space-y-5">
          <PasswordLinksPanel />
        </div>
      )}
    </div>
  )
}
