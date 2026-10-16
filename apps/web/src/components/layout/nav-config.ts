/**
 * Panelin modül ağacı — üst menü, mega menü, dock, mobil menü ve komut
 * paleti hepsi buradan beslenir. `permission`/`requireRoles` alanı olan
 * girdiler, kullanıcının yetkisi yoksa hiçbir yerde görünmez.
 */

import {
  Crown,
  LayoutDashboard, Inbox, CalendarDays, Wallet, LifeBuoy, Clock,
  Building2, Users, UserPlus, ClipboardCheck, Laptop, Target, GraduationCap,
  BadgeDollarSign, FileText, Bell, Shield, UsersRound, UserRound, Crosshair,
  ClipboardList, Gauge, LineChart, MessageSquareText, Lightbulb, CalendarRange,
  Ruler, SlidersHorizontal, CalendarClock, Key, Sparkles, Briefcase, Sprout,
  Settings2, Globe2, HeartHandshake, PartyPopper, MapPin, Handshake, Megaphone,
  MessagesSquare, HeartPulse, GitBranch, LogOut, ScrollText, ShieldCheck, FileStack,
  Workflow, PlugZap, Radar, History, BarChart3, Bot, Calculator, FileSpreadsheet,
  Route, Telescope, Award, Search, Lock, BookOpenText, ScanLine, ReceiptText, Banknote, QrCode, FileCheck2,
  Gift, Plane, TrendingUp, Library, HardHat, Gavel, Scale, Grid3x3, Layers, Network,
} from 'lucide-react'
import type { Permission, Role } from '@/auth/roles'
import { hasStandardRole } from '@/auth/roles'
import { tx } from '@/lib/i18n'

export type NavItemData = {
  id: string
  title: string
  /** Mega menüde başlığın altındaki tek satır. */
  description?: string
  icon: React.ElementType
  path?: string
  badge?: number | string
  permission?: Permission
  /** Şirket düzeyinde modül: şirket seçmemiş platform yöneticisine gösterilmez. */
  tenantOnly?: boolean
  /** permission yerine/yanında: sadece bu sabit rollerden biri varsa göster
   * (Ek İzin - ext-* - sayılmaz). Bkz. roller öğesindeki kullanım notu. */
  requireRoles?: Role[]
  /** İsteğe bağlı modül: kiracıda kapalıysa gizlenir (İK yine görür ki açabilsin). */
  feature?: NavFeature
  children?: NavItemData[]
}

export type NavFeature = 'eom'

export type NavGroupData = {
  id: string
  heading: string
  /** Mega menünün sol tanıtım kutusundaki cümle. */
  tagline: string
  icon: React.ElementType
  items: NavItemData[]
}

export const overviewItem: NavItemData = {
  id: 'overview',
  title: tx('Genel bakış'),
  description: tx('Bekleyen işler ve şirketin nabzı'),
  icon: LayoutDashboard,
  path: '/panel',
}

export const navGroups: NavGroupData[] = [
  {
    id: 'daily',
    heading: tx('Günlük iş'),
    tagline: tx('Talepler, onaylar ve zaman çizelgesi tek akışta.'),
    icon: Sparkles,
    items: [
      { id: 'todos', title: tx('Yapılacaklarım'), description: tx('Sizi bekleyen her şey tek listede'), icon: ClipboardList, path: '/panel/yapilacaklarim', tenantOnly: true },
      { id: 'approvals', title: tx('Onay kutusu'), description: tx('Kararınızı bekleyen talepler'), icon: Inbox, path: '/panel/onaylar', permission: 'workflow:view' },
      { id: 'leave', title: tx('İzin'), description: tx('Talepler ve yıllık bakiyeler'), icon: CalendarDays, path: '/panel/izin', permission: 'leave:view' },
      { id: 'expense', title: tx('Masraf'), description: tx('Fiş, harcama ve geri ödeme'), icon: Wallet, path: '/panel/masraf', permission: 'expense:view' },
      { id: 'cases', title: tx('İK vakaları'), description: tx('Soru, talep ve şikâyet defteri'), icon: LifeBuoy, path: '/panel/ik-vakalari', permission: 'case:view' },
      { id: 'timeshift', title: tx('Puantaj'), description: tx('Giriş/çıkış ve fazla mesai'), icon: Clock, path: '/panel/puantaj', permission: 'timeshift:view' },
      { id: 'time-clock', title: tx('Giriş-çıkış'), description: tx('QR, kart ya da PIN ile; biyometri yok'), icon: ScanLine, path: '/panel/giris-cikis', permission: 'timeshift:clock' },
      { id: 'shift-swap', title: tx('Vardiya takası'), description: tx('Takas, devir ve vardiya tercihleri'), icon: CalendarClock, path: '/panel/vardiya-takasi', permission: 'timeshift:view' },
      { id: 'my-payslips', title: tx('Bordrolarım'), description: tx('Bordro pusulalarınız'), icon: ReceiptText, path: '/panel/bordrolarim' },
      { id: 'travel', title: tx('Seyahat'), description: tx('Seyahat talebi ve harcırah'), icon: Plane, path: '/panel/seyahat', permission: 'expense:view' },
      { id: 'benefits', title: tx('Yan haklar'), description: tx('Esnek yan hak seçimi'), icon: Gift, path: '/panel/yan-haklar' },
      { id: 'my-interviews', title: tx('Mülakatlarım'), description: tx('Görüşmeci olduğunuz mülakatlar'), icon: ClipboardCheck, path: '/panel/mulakatlarim' },
      { id: 'refer', title: tx('Aday öner'), description: tx('Açık pozisyonlara aday önerin'), icon: UserPlus, path: '/panel/aday-oner' },
      { id: 'doc-request', title: tx('Belge talebi'), description: tx('Çalışma belgesi, maaş yazısı'), icon: FileCheck2, path: '/panel/belge-talebi' },
      { id: 'my-signatures', title: tx('İmzalarım'), description: tx('İmzanızı bekleyen belgeler'), icon: FileCheck2, path: '/panel/imzalarim' },
      { id: 'announcements', title: tx('Duyurular'), description: tx('Şirket duyuruları, okundu onayı'), icon: Megaphone, path: '/panel/duyurular' },
      { id: 'policy-library', title: tx('Doküman kütüphanesi'), description: tx('Politikalar, el kitapları, arama'), icon: Library, path: '/panel/belgeler-kutuphanesi' },
      { id: 'osh', title: tx('İş sağlığı ve güvenliği'), description: tx('Kaza kaydı, muayene, İSG eğitimi'), icon: HardHat, path: '/panel/isg' },
      { id: 'ethics', title: tx('Etik hattı'), description: tx('Anonim bildirim ve etik kurulu'), icon: Scale, path: '/panel/etik' },
      { id: 'my-defences', title: tx('Savunmalarım'), description: tx('Sizden istenen yazılı savunmalar'), icon: ScrollText, path: '/panel/disiplin/savunmalarim' },
      { id: 'shift-engine', title: tx('Vardiya planı'), description: tx('Döngüsel desenler, 7/24 kapsama'), icon: CalendarClock, path: '/panel/vardiya-motoru', permission: 'timeshift:view' },
      { id: 'workplace', title: tx('Ofis ve masa'), description: tx('Kim nerede, masa/oda rezervasyonu'), icon: MapPin, path: '/panel/ofis', tenantOnly: true },
      { id: 'payroll-sim', title: tx('Bordro simülasyonu'), description: tx('Brütten nete, 2026 parametreleri'), icon: Calculator, path: '/panel/bordro-simulasyonu', tenantOnly: true },
    ],
  },
  {
    id: 'people',
    heading: tx('Kişiler'),
    tagline: tx('Organizasyon, ekipler ve işe alımdan ilk güne.'),
    icon: Briefcase,
    items: [
      { id: 'organization', title: tx('Organizasyon'), description: tx('Şirket ve departman hiyerarşisi'), icon: Building2, path: '/panel/organizasyon', permission: 'organization:view' },
      { id: 'employees', title: tx('Çalışanlar'), description: tx('Kayıtlar ve çalışma durumu'), icon: Users, path: '/panel/calisanlar', permission: 'employee:viewAll' },
      { id: 'teams', title: tx('Ekipler'), description: tx('Diyagram ve ekip yönetimi'), icon: UsersRound, path: '/panel/organizasyon/ekipler', permission: 'organization:view' },
      {
        id: 'recruitment', title: tx('İşe alım'), description: tx('İlanlar, adaylar, mülakatlar'), icon: UserPlus, permission: 'recruitment:view',
        children: [
          { id: 'postings', title: tx('İlanlar'), description: tx('Açık pozisyonlar'), icon: FileText, path: '/panel/ise-alim' },
          { id: 'candidates', title: tx('Adaylar'), description: tx('Aday havuzu'), icon: Users, path: '/panel/ise-alim/adaylar' },
          { id: 'referrals', title: tx('Çalışan önerileri'), description: tx('Öneri programı ve ödüller'), icon: Gift, path: '/panel/ise-alim/oneriler', requireRoles: ['hr-admin', 'tenant-admin', 'platform-admin'] },
          { id: 'recruitment-analytics', title: tx('İşe alım analizi'), description: tx('Huni, süreler, kaynaklar'), icon: BarChart3, path: '/panel/ise-alim/analiz', requireRoles: ['hr-admin', 'tenant-admin', 'platform-admin'] },
          { id: 'hire-saga', title: tx('Teklif → işe giriş'), description: tx('İşe alım sagası izleme'), icon: Route, path: '/panel/ise-alim/saga', tenantOnly: true, requireRoles: ['hr-admin', 'tenant-admin', 'platform-admin'] },
        ],
      },
      {
        id: 'onboarding', title: tx('Onboarding'), description: tx('İşe giriş planları ve zimmet'), icon: ClipboardCheck, permission: 'onboarding:view',
        children: [
          { id: 'plans', title: tx('Planlar'), description: tx('İşe giriş görevleri'), icon: ClipboardCheck, path: '/panel/onboarding' },
          { id: 'assets', title: tx('Zimmet'), description: tx('Demirbaş ve atamalar'), icon: Laptop, path: '/panel/zimmet' },
          { id: 'asset-scan', title: tx('Zimmet okut'), description: tx('QR etiketten demirbaş ve zimmet'), icon: QrCode, path: '/panel/zimmet/tara' },
        ],
      },
      { id: 'offboarding', title: tx('İşten ayrılış'), description: tx('Kontrol listesi, çıkış görüşmesi, hak ediş'), icon: LogOut, path: '/panel/offboarding', permission: 'onboarding:manage', tenantOnly: true },
      { id: 'succession', title: tx('Ardıl planlama'), description: tx('Kritik roller ve yedekler'), icon: GitBranch, path: '/panel/ardil-planlama', permission: 'performance:manage', tenantOnly: true },
      { id: 'org-scenarios', title: tx('Org senaryoları'), description: tx('"Ya şöyle olsaydı?" planlama'), icon: Workflow, path: '/panel/org-senaryolari', permission: 'performance:manage', tenantOnly: true },
    ],
  },
  {
    id: 'growth',
    heading: tx('Gelişim'),
    tagline: tx('Hedefler, değerlendirmeler ve öğrenme yolculuğu.'),
    icon: Sprout,
    items: [
      {
        id: 'performance', title: tx('Performans'), description: tx('Hedef, değerlendirme, geri bildirim'), icon: Target, permission: 'performance:view',
        children: [
          { id: 'perf-me', title: tx('Benim performansım'), description: tx('Kişisel karne'), icon: UserRound, path: '/panel/performans/benim' },
          { id: 'perf-goals', title: tx('Hedefler'), description: tx('Dönem hedefleri'), icon: Crosshair, path: '/panel/performans/hedefler' },
          { id: 'perf-okr', title: tx('OKR hizalama'), description: tx('Şirket → departman → kişi'), icon: Network, path: '/panel/performans/okr' },
          { id: 'perf-reviews', title: tx('Değerlendirmeler'), description: tx('Öz ve yönetici değerlendirmesi'), icon: ClipboardList, path: '/panel/performans/degerlendirme' },
          { id: 'perf-score', title: tx('Puan dökümü'), description: tx('Ağırlıklı puan hesabı'), icon: Gauge, path: '/panel/performans/puan' },
          { id: 'perf-feedback', title: tx('Geri bildirim'), description: tx('Sürekli geri bildirim'), icon: MessageSquareText, path: '/panel/performans/geri-bildirim' },
          { id: 'perf-360', title: tx('360 geri bildirim'), description: tx('Anonim, yetkinlik bazlı'), icon: Users, path: '/panel/performans/360' },
          { id: 'perf-analytics', title: tx('Analiz'), description: tx('Ekip gidişatı'), icon: LineChart, path: '/panel/performans/analiz', permission: 'performance:manage' },
          { id: 'perf-recs', title: tx('Aksiyon önerileri'), description: tx('Model önerileri'), icon: Lightbulb, path: '/panel/performans/oneriler', permission: 'performance:manage' },
          { id: 'perf-ninebox', title: tx('9-kutu'), description: tx('Performans × potansiyel, kalibrasyon'), icon: Grid3x3, path: '/panel/performans/dokuz-kutu', permission: 'performance:manage' },
          { id: 'perf-calibration', title: tx('Kalibrasyon oturumu'), description: tx('Sürükle-bırak 9-kutu, İK onayı'), icon: Scale, path: '/panel/performans/kalibrasyon', permission: 'performance:manage' },
        ],
      },
      {
        id: 'performance-setup', title: tx('Performans kurulumu'), description: tx('Dönem, metrik, puanlama'), icon: SlidersHorizontal, permission: 'performance:manage',
        children: [
          { id: 'perf-cycles', title: tx('Dönemler'), description: tx('Değerlendirme dönemleri'), icon: CalendarRange, path: '/panel/performans/donemler' },
          { id: 'perf-metrics', title: tx('Metrikler'), description: tx('Ölçülen göstergeler'), icon: Ruler, path: '/panel/performans/metrikler' },
          { id: 'perf-settings', title: tx('Puanlama ayarı'), description: tx('Ağırlık ve ölçek'), icon: SlidersHorizontal, path: '/panel/performans/ayarlar' },
        ],
      },
      { id: 'learning', title: tx('Eğitim'), description: tx('Katalog ve sertifikalar'), icon: GraduationCap, path: '/panel/egitim', permission: 'learning:view' },
      { id: 'competencies', title: tx('Yetkinlikler'), description: tx('Yetkinlik açığı ve eğitim önerisi'), icon: Layers, path: '/panel/yetkinlikler', permission: 'learning:view' },
      { id: 'career-paths', title: tx('Kariyer yolları'), description: tx('Rol basamakları ve sonraki adım'), icon: Sprout, path: '/panel/kariyer-yollari', permission: 'learning:view' },
      { id: 'due-training', title: tx('Zorunlu eğitim takibi'), description: tx('Son tarih, sertifika ve İSG eğitimi'), icon: CalendarClock, path: '/panel/egitim-takibi', permission: 'learning:view' },
      { id: 'mentorship', title: tx('Mentorluk'), description: tx('Beceri eşleştirmeli mentor bulma'), icon: Handshake, path: '/panel/mentorluk', tenantOnly: true },
      { id: 'mobility', title: tx('İç ilanlar'), description: tx('Şirket içi açık pozisyonlar'), icon: Megaphone, path: '/panel/ic-ilanlar', tenantOnly: true },
      { id: 'one-on-ones', title: tx('1:1 görüşmeler'), description: tx('Ortak gündem, aksiyonlar, notlar'), icon: MessagesSquare, path: '/panel/birebir', tenantOnly: true },
      { id: 'team-health', title: tx('Ekip sağlığı'), description: tx('Tükenmişlik sinyalleri'), icon: HeartPulse, path: '/panel/ekip-sagligi', permission: 'performance:manage', tenantOnly: true },
    ],
  },
  {
    id: 'community',
    heading: tx('Topluluk'),
    tagline: tx('Takdir, kutlama ve nabız — şirket kültürü burada görünür.'),
    icon: HeartHandshake,
    items: [
      { id: 'kudos', title: tx('Takdir duvarı'), description: tx('Rozetli teşekkürler'), icon: Award, path: '/panel/takdir', tenantOnly: true },
      { id: 'ideas', title: tx('Fikir kutusu'), description: tx('Öneri paylaş, destekle, yanıt al'), icon: Lightbulb, path: '/panel/fikir-kutusu', tenantOnly: true },
      { id: 'eom', title: tx('Ayın çalışanı'), description: tx('Departman oylaması ve kazananlar'), icon: Crown, path: '/panel/ayin-calisani', tenantOnly: true, feature: 'eom' },
      { id: 'celebrations', title: tx('Kutlamalar'), description: tx('Doğum günü ve iş yıl dönümleri'), icon: PartyPopper, path: '/panel/kutlamalar', tenantOnly: true },
      { id: 'surveys', title: tx('Anketler ve eNPS'), description: tx('Anonim nabız anketleri'), icon: MessagesSquare, path: '/panel/anketler', tenantOnly: true },
      { id: 'directory', title: tx('Yetenek dizini'), description: tx('Kim ne biliyor?'), icon: Search, path: '/panel/yetenek-dizini', tenantOnly: true },
    ],
  },
  {
    id: 'insights',
    heading: tx('İçgörü'),
    tagline: tx('Rakamlar, zaman yolculuğu ve yapay zekâ destekli araçlar.'),
    icon: Telescope,
    items: [
      { id: 'analytics', title: tx('Analitik'), description: tx('Kadro, devir, izin, mesai eğilimi'), icon: BarChart3, path: '/panel/analitik', permission: 'performance:manage', tenantOnly: true },
      { id: 'model-card', title: tx('Model kartı'), description: tx('Devir riski modeli: önem, kayma, adalet'), icon: BarChart3, path: '/panel/model-karti', permission: 'performance:manage', tenantOnly: true, requireRoles: ['hr-admin', 'tenant-admin', 'platform-admin'] },
      { id: 'nl-report', title: tx('Rapor asistanı'), description: tx('Türkçe sorun, tablo ve grafik gelsin'), icon: Sparkles, path: '/panel/rapor-asistani', permission: 'performance:manage', tenantOnly: true },
      { id: 'time-machine', title: tx('Zaman makinesi'), description: tx('Organizasyon geçmişte nasıldı?'), icon: History, path: '/panel/zaman-makinesi', permission: 'performance:manage', tenantOnly: true },
      // Olay yükleri kiracı genelinde kişisel veri taşır: yalnızca İK (sunucu: RequireHrAdmin).
      { id: 'event-radar', title: tx('Canlı olay radarı'), description: tx('Kafka olayları gerçek zamanlı'), icon: Radar, path: '/panel/olay-radari', permission: 'employee:manage', tenantOnly: true },
      { id: 'ai-tools', title: tx('Yapay zekâ araçları'), description: tx('CV, ilan, eşleşme, tahmin'), icon: Bot, path: '/panel/ai-araclari', permission: 'recruitment:view', tenantOnly: true },
    ],
  },
  {
    id: 'admin',
    heading: tx('Yönetim'),
    tagline: tx('Roller, ücret yapısı, dokümanlar ve bildirimler.'),
    icon: Settings2,
    items: [
      {
        id: 'roles', title: tx('Roller'), description: tx('Yetkiler ve ek izinler'), icon: Key, path: '/panel/roller',
        permission: 'employee:manage',
        // Bkz. App.tsx'teki "roller" route notu: bu sayfanın veri uçları
        // tenant-service'in KENDİ RequireHrAdmin'ini kullanıyor (bilerek
        // "ext-*" ile genişletilmedi) - employee:manage Ek İznini alan
        // biri menüde bunu görüp tıklasa, sayfa "veriler alınamadı"
        // hatası verirdi. requireRoles, menüyü de backend'in gerçekte
        // kabul ettiği sabit rollerle eşleştiriyor.
        requireRoles: ['hr-admin', 'tenant-admin', 'platform-admin'],
      },
      { id: 'compensation', title: tx('Ücret'), description: tx('Bantlar, geçmiş, zam simülasyonu'), icon: BadgeDollarSign, path: '/panel/ucret', permission: 'compensation:view' },
      { id: 'payroll', title: tx('Bordro'), description: tx('Aylık dönem, pusula, kapatma'), icon: Banknote, path: '/panel/bordro', permission: 'compensation:view' },
      { id: 'raise-cycles', title: tx('Zam dönemi'), description: tx('Öneri, bütçe ve bant kontrolü'), icon: TrendingUp, path: '/panel/zam-donemi', requireRoles: ['manager', 'hr-admin', 'tenant-admin', 'platform-admin'] },
      { id: 'wf-designer', title: tx('Onay akışları'), description: tx('Çok adımlı zincir, koşullar, vekâlet'), icon: GitBranch, path: '/panel/onay-akislari', permission: 'employee:manage' },
      { id: 'time-clock-admin', title: tx('Giriş-çıkış yönetimi'), description: tx('Noktalar, kiosk QR, kartlar'), icon: QrCode, path: '/panel/giris-cikis/yonetim', permission: 'employee:manage' },
      { id: 'documents', title: tx('Dokümanlar'), description: tx('Çalışan dosyaları'), icon: FileText, path: '/panel/dokumanlar', permission: 'document:manage' },
      { id: 'notifications', title: tx('Bildirimler'), description: tx('Gelen kutusu ve şablonlar'), icon: Bell, path: '/panel/bildirimler', permission: 'notification:view' },
      { id: 'doc-templates', title: tx('Belge şablonları'), description: tx('Toplu belge ve PDF üretimi'), icon: FileStack, path: '/panel/belge-sablonlari', permission: 'document:manage', tenantOnly: true },
      { id: 'privacy', title: tx('KVKK'), description: tx('Rıza, başvuru, saklama süresi'), icon: ShieldCheck, path: '/panel/kvkk', permission: 'employee:manage', tenantOnly: true },
      { id: 'custom-fields', title: tx('Özel alanlar'), description: tx('Profil ek bilgi alanları (KVKK üst verisiyle)'), icon: SlidersHorizontal, path: '/panel/ayarlar/ozel-alanlar', permission: 'employee:manage' },
      { id: 'disciplinary', title: tx('Disiplin'), description: tx('Savunma istemi, tutanak, karar'), icon: Gavel, path: '/panel/disiplin', requireRoles: ['manager', 'hr-admin', 'tenant-admin', 'platform-admin'] },
      { id: 'audit', title: tx('Denetim kaydı'), description: tx('Kim neyi ne zaman değiştirdi'), icon: ScrollText, path: '/panel/denetim', permission: 'employee:manage', tenantOnly: true },
      { id: 'rules', title: tx('Kural motoru'), description: tx('"Olursa → yap" otomasyonları'), icon: Workflow, path: '/panel/kural-motoru', permission: 'employee:manage', tenantOnly: true },
      { id: 'integrations', title: tx('Entegrasyonlar'), description: tx('Webhook, API, Slack/Teams'), icon: PlugZap, path: '/panel/entegrasyonlar', permission: 'employee:manage', tenantOnly: true },
      { id: 'security', title: tx('Güvenlik'), description: tx('SSO ve iki adımlı doğrulama'), icon: Lock, path: '/panel/guvenlik', permission: 'tenant:manage', requireRoles: ['tenant-admin', 'platform-admin'], tenantOnly: true },
      { id: 'data-protection', title: tx('Veri koruma'), description: tx('Erişim gözden geçirme, toplu görüntüleme, iz kodu'), icon: ShieldCheck, path: '/panel/veri-koruma', permission: 'employee:manage', requireRoles: ['hr-admin', 'tenant-admin'] },
      { id: 'access-review', title: tx('Erişim gözden geçirme'), description: tx('Ekibimin rol ve izinleri'), icon: ClipboardCheck, path: '/panel/erisim-gozden-gecirme', requireRoles: ['manager', 'hr-admin', 'tenant-admin'] },
      { id: 'import-export', title: tx('İçe/dışa aktarım'), description: tx('Excel ile toplu veri'), icon: FileSpreadsheet, path: '/panel/ice-disa-aktarim', permission: 'employee:manage', tenantOnly: true },
      { id: 'api-docs', title: tx('API belgeleri'), description: tx('Tüm servislerin uçları, deneme'), icon: BookOpenText, path: '/panel/api-belgeleri', permission: 'employee:manage' },
    ],
  },
  {
    id: 'platform',
    heading: tx('Platform'),
    tagline: tx('Kiracılar, platform erişimi ve sistem ayarları.'),
    icon: Globe2,
    items: [
      { id: 'tenants', title: tx('Kiracılar'), description: tx('Şirketler ve yöneticileri'), icon: Shield, path: '/panel/platform/kiracilar', permission: 'platform:manage' },
      { id: 'system-settings', title: tx('Sistem ayarları'), description: tx('E-posta, kayıt ve parola linkleri'), icon: Settings2, path: '/panel/platform/sistem', permission: 'platform:manage' },
    ],
  },
]

/** İzni olmayan girdileri (ve boş kalan grupları) ağaçtan çıkarır. */
export function filterByPermission(
  groups: NavGroupData[],
  can: (p: Permission) => boolean,
  roles: string[],
  hasTenant = true,
  features: Partial<Record<NavFeature, boolean>> = {},
): NavGroupData[] {
  const allowed = (item: NavItemData) =>
    (!item.feature || features[item.feature] === true) &&
    (!item.permission || can(item.permission)) &&
    (!item.requireRoles || hasStandardRole(roles, item.requireRoles)) &&
    (!item.tenantOnly || hasTenant)
  return groups
    .map((group) => ({
      ...group,
      items: group.items
        .filter(allowed)
        .map((item) => (item.children ? { ...item, children: item.children.filter(allowed) } : item))
        .filter((item) => !item.children || item.children.length > 0),
    }))
    .filter((group) => group.items.length > 0)
}

/** Ağaçtaki yola gidilebilir tüm girdiler (alt girdiler dâhil). */
export function flattenItems(groups: NavGroupData[]): Array<NavItemData & { group: NavGroupData; parent?: NavItemData }> {
  return groups.flatMap((group) =>
    group.items.flatMap((item) =>
      item.children
        ? item.children.map((child) => ({ ...child, group, parent: item }))
        : [{ ...item, group }],
    ),
  )
}

/** Adres çubuğundaki yolu menüdeki konuma çevirir (bölüm, üst modül, sayfa). */
export function locate(pathname: string): { group?: NavGroupData; parent?: NavItemData; item?: NavItemData } {
  if (pathname === '/panel' || pathname === '/panel/') return { item: overviewItem }
  let best: { group?: NavGroupData; parent?: NavItemData; item?: NavItemData } = {}
  let bestLen = 0
  for (const entry of flattenItems(navGroups)) {
    if (entry.path && pathname.startsWith(entry.path) && entry.path.length > bestLen) {
      bestLen = entry.path.length
      best = { group: entry.group, parent: entry.parent, item: entry }
    }
  }
  return best
}

export function isActivePath(pathname: string, path?: string) {
  if (!path) return false
  if (path === '/panel') return pathname === '/panel' || pathname === '/panel/'
  return pathname === path || pathname.startsWith(path + '/')
}
