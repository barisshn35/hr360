import { tx } from '@/lib/i18n'
/**
 * Rol ve izin matrisi.
 *
 * ÖNEMLİ: Bu yalnızca ARAYÜZ katmanıdır — butonu gizler, menüyü filtreler.
 * Gerçek yetkilendirme backend'de ASP.NET Core policy'leriyle yapılır
 * (RequireHrAdmin / RequireManagerOrAbove / RequirePlatformAdmin).
 * Buradaki matris backend'dekiyle BİREBİR aynı olmalı; ayrışırsa kullanıcı
 * tıklayabildiği ama 403 alan butonlar görür.
 */

export const ROLES = [
  'employee',
  'manager',
  'accounting',
  'hr-admin',
  'tenant-admin',
  'platform-admin',
] as const;

export type Role = (typeof ROLES)[number];

export type Permission =
  // Organizasyon & çalışan
  | 'organization:view' | 'organization:manage'
  | 'employee:viewAll' | 'employee:manage' | 'employee:create'
  // Onay akışı
  | 'workflow:view' | 'workflow:create' | 'workflow:decide'
  // İzin
  | 'leave:view' | 'leave:create' | 'leave:manageBalance'
  // İşe alım
  | 'recruitment:view' | 'recruitment:candidates' | 'recruitment:publish'
  // Onboarding & zimmet
  | 'onboarding:view' | 'onboarding:manage' | 'asset:manage'
  // Puantaj
  | 'timeshift:view' | 'timeshift:clock' | 'timeshift:manage'
  // Performans & ekipler
  | 'performance:view' | 'performance:manage'
  | 'team:manage'
  // Eğitim
  | 'learning:view' | 'learning:enroll' | 'learning:manage'
  // Ücret (hassas)
  | 'compensation:view'
  // Masraf & doküman & vaka
  | 'expense:view' | 'expense:create' | 'expense:manage' | 'expense:markPaid'
  | 'document:manage'
  | 'case:view' | 'case:create' | 'case:manage'
  // Bildirim
  | 'notification:view' | 'notification:manage'
  // İş sağlığı ve güvenliği
  | 'osh:physician' | 'osh:specialist'
  // Platform (çok kiracılılık)
  | 'platform:manage'
  | 'tenant:manage';

/** Her rolün doğrudan sahip olduğu izinler. Roller birleşimli çalışır. */
const MATRIX: Record<Role, Permission[]> = {
  employee: [
    'organization:view',
    'workflow:view', 'workflow:create',
    'leave:view', 'leave:create',
    'onboarding:view',
    'timeshift:view', 'timeshift:clock',
    'performance:view',
    'learning:view', 'learning:enroll',
    'expense:view', 'expense:create',
    'case:view', 'case:create',
    'notification:view',
  ],

  manager: [
    'employee:viewAll', 'employee:create',
    'workflow:decide',
    'leave:view',
    'recruitment:view', 'recruitment:candidates',
    'onboarding:manage',
    'timeshift:manage',
    'performance:manage',
    'team:manage',         // ekip kurma, lider atama, üye ekleme/çıkarma
    'expense:manage',
    'case:manage',
    'notification:manage',
  ],

  accounting: [
    'expense:view', 'expense:markPaid',
  ],

  'hr-admin': [
    'organization:manage',
    'employee:manage',
    'leave:manageBalance',
    'recruitment:publish',
    'asset:manage',
    'learning:manage',
    'compensation:view',   // ücret yalnızca İK yönetiminde
    'document:manage',
    'expense:markPaid',
  ],

  'tenant-admin': ['tenant:manage'],
  'platform-admin': ['platform:manage', 'tenant:manage', 'compensation:view'],
};

/** Rol devralma: soldaki rol, sağdakilerin izinlerini de alır. */
const INHERITS: Partial<Record<Role, Role[]>> = {
  manager: ['employee'],
  accounting: ['employee'],
  'hr-admin': ['manager', 'employee'],
  'tenant-admin': ['hr-admin', 'manager', 'employee'],
  'platform-admin': ['hr-admin', 'manager', 'employee'],
};

function expand(role: Role, seen = new Set<Role>()): Permission[] {
  if (seen.has(role)) return [];
  seen.add(role);
  const inherited = (INHERITS[role] ?? []).flatMap((r) => expand(r, seen));
  return [...(MATRIX[role] ?? []), ...inherited];
}

/**
 * Verilen rollerin toplam izin kümesi. Standart 6 rol dışında,
 * "ext-<izin>" formatındaki roller de tanınır - bunlar Roller sayfasındaki
 * "Ek izinler" ile tek tek atanmış, kullanıcının rolünden bağımsız izinlerdir
 * (bkz. tenant-service/TeamMembersController.AssignExtraPermission). Bu
 * rollerin geçerliliği zaten backend'de (ExtraAssignablePermissions)
 * kontrol edildiği için burada ayrıca doğrulamaya gerek yok - JWT'de
 * görülen her "ext-*" rolü, atanma anında geçerli bir Permission'a karşılık
 * gelmiş demektir.
 */
/**
 * Ek izinle verilen bir yetkinin çalışması için gereken görüntüleme izni.
 * Örn. "ilan yayınlama" ek izni alan biri İşe alım sayfasını açabilmeli;
 * aksi halde izin verilir ama sayfa hiç görünmez.
 */
const IMPLIED_BY_EXTRA: Partial<Record<Permission, Permission[]>> = {
  'recruitment:publish': ['recruitment:view'],
  'recruitment:candidates': ['recruitment:view'],
  'employee:create': ['employee:viewAll'],
  'employee:manage': ['employee:viewAll'],
};

export function permissionsFor(roles: string[]): Set<Permission> {
  const valid = roles.filter((r): r is Role => (ROLES as readonly string[]).includes(r));
  const extra = roles
    .filter((r) => r.startsWith('ext-'))
    .map((r) => r.slice('ext-'.length).replace('-', ':') as Permission);
  const implied = extra.flatMap((p) => IMPLIED_BY_EXTRA[p] ?? []);
  return new Set([...valid.flatMap((r) => expand(r)), ...extra, ...implied]);
}

export function hasPermission(roles: string[], permission: Permission): boolean {
  return permissionsFor(roles).has(permission);
}

/**
 * "Ek izin" (ext-*) rollerini SAYMADAN, kullanıcının gerçekten bu sabit
 * rollerden birine sahip olup olmadığını kontrol eder.
 *
 * Neden gerekli: bazı ekranlar (örn. Roller sayfası - rol/izin atama),
 * roles.ts'te bir Permission'a karşılık gelse de, aslında SADECE gerçek
 * hr-admin/tenant-admin/platform-admin'e özel backend uçlarına bağlı -
 * ve o uçların policy'si (bilerek) "ext-*" ile genişletilmemiş, çünkü rol
 * atama gücünün kendisinin Ek İzin ile devredilmesi istenmiyor. Böyle bir
 * ekranı normal can('employee:manage') ile korursak, "employee:manage" ek
 * iznini alan biri sayfaya girer ama içindeki veri uçları 403 döner - sayfa
 * boş/hatalı görünür. Bu fonksiyon, tam olarak backend'in gerçekte kabul
 * ettiği kümeyi (sabit roller) yansıtır.
 */
export function hasStandardRole(userRoles: string[], allowed: Role[]): boolean {
  return userRoles.some((r) => (allowed as readonly string[]).includes(r));
}

/** İK rolleri: backend'deki RequireHrAdmin politikalarının standart rol kısmı. */
export const HR_ROLES: Role[] = ['hr-admin', 'tenant-admin', 'platform-admin'];

/**
 * Kullanıcı İK mı? Bazı işlemler (dönem açma/kapama, rol atama, ödeme vb.)
 * backend'de yöneticiye değil yalnızca İK'ya açık; `extraRole` verilirse o ek
 * izin rolü (ör. "ext-performance-manage") de kabul edilir.
 */
export function isHr(userRoles: string[], extraRole?: string): boolean {
  return hasStandardRole(userRoles, HR_ROLES) || (extraRole ? userRoles.includes(extraRole) : false);
}

/**
 * Tüm çalışan listesini okuyabilir mi? employee-service'teki IsManagerOrAbove
 * kuralının aynısı: yönetici, İK, muhasebe ve listeye ihtiyaç duyan ek izinler
 * (ext-*-manage, ext-employee-viewAll, ext-compensation-view, ext-expense-markPaid).
 */
export function canListEmployees(userRoles: string[]): boolean {
  return (
    hasStandardRole(userRoles, ['manager', 'accounting', ...HR_ROLES]) ||
    userRoles.some(
      (r) =>
        r === 'ext-employee-viewAll' ||
        r === 'ext-employee-create' ||
        r === 'ext-compensation-view' ||
        r === 'ext-expense-markPaid' ||
        (r.startsWith('ext-') && r.endsWith('-manage')),
    )
  );
}

/** Arayüzde gösterilecek Türkçe rol etiketleri. */
export const roleLabels: Record<Role, string> = {
  employee: tx('Çalışan'),
  manager: tx('Yönetici'),
  accounting: tx('Muhasebe'),
  'hr-admin': tx('İK Yöneticisi'),
  'tenant-admin': tx('Şirket Yöneticisi'),
  'platform-admin': tx('Platform Yöneticisi'),
};

/** Kullanıcının en yetkili rolü — arayüzde tek etiket göstermek için. */
export function primaryRole(roles: string[]): Role {
  const order: Role[] = ['platform-admin', 'tenant-admin', 'hr-admin', 'manager', 'accounting', 'employee'];
  return order.find((r) => roles.includes(r)) ?? 'employee';
}
