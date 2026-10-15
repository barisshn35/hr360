/**
 * Herkese açık sayfaların varsayılan içerikleri. Platform yöneticisi Sistem ayarları › Yasal metinler
 * ve iletişim'de kendi metnini girmezse bunlar gösterilir. Metinler kuruluma göre (işletmeci adı ve
 * iletişim bilgileri) doldurulur. Uzun metinler tx() yerine dile göre seçilir: tek parça Markdown.
 *
 * Çerez tablosu teknik bir bilgidir (yazılımın kendisi ne kullanıyorsa o) ve gizlilik sayfasında
 * her zaman ayrı bölüm olarak gösterilir; işletmeci politikasını değiştirse de doğru kalır.
 */
import type { FaqItem, SiteSettings } from '@/api/password-links'
import type { Lang } from '@/lib/i18n'

import { SOURCE_URL } from './constants'

export { COOKIE_ANCHOR, SOURCE_URL } from './constants'

/** plain: ad teknik bir tanımlayıcı değil, açıklama (eş aralıklı yazı tipi kullanılmaz). */
export type CookieRow = { name: string; kind: string; purpose: string; duration: string; plain?: boolean }

export function cookieRows(lang: Lang): CookieRow[] {
  if (lang === 'en') {
    return [
      { name: 'AUTH_SESSION_ID, KC_RESTART, KC_AUTH_SESSION_HASH', kind: 'Cookie (identity server)', purpose: 'Keeps the sign-in steps together and protects them against forgery', duration: 'Until sign-in completes / browser closes' },
      { name: 'KEYCLOAK_IDENTITY, KEYCLOAK_SESSION', kind: 'Cookie (identity server)', purpose: 'Single sign-on session; keeps you signed in across tabs', duration: 'Session (ends when you sign out or it times out)' },
      { name: 'KEYCLOAK_LOCALE', kind: 'Cookie (identity server)', purpose: 'Remembers the language chosen on the sign-in screen', duration: 'Only if you change the language' },
      { name: 'hr360.theme, hr360.lang', kind: 'Local storage', purpose: 'Theme (light/dark) and interface language', duration: 'Until you clear it' },
      { name: 'hr360.cookieNotice', kind: 'Local storage', purpose: 'Remembers that you closed this cookie notice', duration: 'Until you clear it' },
      { plain: true, name: 'Drafts, offline queue (hr360-offline)', kind: 'Local storage / IndexedDB', purpose: 'Keeps unsent forms so nothing is lost when the connection drops', duration: 'Deleted once sent' },
      { plain: true, name: 'Application cache (service worker)', kind: 'Browser cache', purpose: 'Application files only, so the app opens quickly and offline; no personal data', duration: 'Replaced with each new version' },
    ]
  }
  return [
    { name: 'AUTH_SESSION_ID, KC_RESTART, KC_AUTH_SESSION_HASH', kind: 'Çerez (kimlik sunucusu)', purpose: 'Giriş adımlarını birbirine bağlar ve sahteciliğe karşı korur', duration: 'Giriş tamamlanana / tarayıcı kapanana kadar' },
    { name: 'KEYCLOAK_IDENTITY, KEYCLOAK_SESSION', kind: 'Çerez (kimlik sunucusu)', purpose: 'Tek oturum açma; sekmeler arasında oturumunuzu korur', duration: 'Oturum süresince (çıkış ya da zaman aşımıyla biter)' },
    { name: 'KEYCLOAK_LOCALE', kind: 'Çerez (kimlik sunucusu)', purpose: 'Giriş ekranında seçilen dili hatırlar', duration: 'Yalnızca dili değiştirirseniz' },
    { name: 'hr360.theme, hr360.lang', kind: 'Yerel depolama', purpose: 'Tema (açık/koyu) ve arayüz dili', duration: 'Siz silene kadar' },
    { name: 'hr360.cookieNotice', kind: 'Yerel depolama', purpose: 'Bu çerez bilgilendirmesini kapattığınızı hatırlar', duration: 'Siz silene kadar' },
    { plain: true, name: 'Taslaklar, çevrimdışı kuyruk (hr360-offline)', kind: 'Yerel depolama / IndexedDB', purpose: 'Bağlantı koptuğunda gönderilmemiş formlar kaybolmasın', duration: 'Gönderilince silinir' },
    { plain: true, name: 'Uygulama önbelleği (servis çalışanı)', kind: 'Tarayıcı önbelleği', purpose: 'Yalnızca uygulama dosyaları; hızlı ve çevrimdışı açılış için, kişisel veri içermez', duration: 'Her yeni sürümde yenilenir' },
  ]
}

function operator(site: SiteSettings | undefined, lang: Lang): string {
  return site?.operatorName?.trim() || (lang === 'en' ? 'the organisation operating this service' : 'bu hizmeti işleten kuruluş')
}

/** Cümle başında kullanım için ilk harf büyük (Türkçe kurallarıyla). */
function cap(text: string, lang: Lang): string {
  return text.charAt(0).toLocaleUpperCase(lang === 'en' ? 'en' : 'tr') + text.slice(1)
}

function contactLines(site: SiteSettings | undefined, lang: Lang): string {
  const en = lang === 'en'
  const lines: string[] = []
  if (site?.operatorName) lines.push(`- **${en ? 'Data controller' : 'Veri sorumlusu'}:** ${site.operatorName}`)
  if (site?.address) lines.push(`- **${en ? 'Address' : 'Adres'}:** ${site.address}`)
  if (site?.email) lines.push(`- **${en ? 'Email' : 'E-posta'}:** [${site.email}](mailto:${site.email})`)
  if (site?.kep) lines.push(`- **KEP:** ${site.kep}`)
  if (site?.phone) lines.push(`- **${en ? 'Phone' : 'Telefon'}:** ${site.phone}`)
  if (site?.verbisNo) lines.push(`- **${en ? 'VERBIS registry no.' : 'VERBİS kayıt no'}:** ${site.verbisNo}`)
  if (lines.length === 0) {
    lines.push(en
      ? '- Contact details are listed on the [Contact](/iletisim) page.'
      : '- İletişim bilgileri [İletişim](/iletisim) sayfasındadır.')
  }
  return lines.join('\n')
}

export function defaultPrivacy(site: SiteSettings | undefined, lang: Lang): string {
  const op = operator(site, lang)
  if (lang === 'en') {
    return `# Privacy policy

This policy explains how personal data is processed on this HR360 installation, in line with the Turkish Personal Data Protection Law No. 6698 ("KVKK").

## Data controller

${contactLines(site, lang)}

## Who is responsible for what

- **Platform accounts** (company administrators who register, platform administrators, visitors of public pages): ${op} is the data controller.
- **Employee and candidate data** entered by a company: the company itself is the data controller; ${op} processes this data on the company's behalf as a data processor and only within its instructions.

## Personal data we process

- **Identity and contact:** name, surname, work email, phone number.
- **Account and security:** username, role, sign-in and session records, IP address, audit log entries.
- **Company registration:** company name, short name, administrator details.
- **Communication:** messages you send to us and our replies.

## Purposes

- Setting up and running accounts, verifying identity and keeping sessions secure
- Providing the service, support and maintenance
- Information security, abuse prevention and audit trails
- Meeting legal obligations and responding to requests from authorised public bodies

## Legal grounds

Data is processed under KVKK Article 5/2: performance of a contract (c), compliance with a legal obligation (ç), establishment, exercise or protection of a right (e) and legitimate interest (f). Where explicit consent is required, it is asked for separately and can be withdrawn at any time.

## Transfers

Data may be shared with infrastructure providers (hosting, backup, email delivery) only as far as necessary, and with authorised public bodies when legally required. Transfers abroad are made only under the conditions of KVKK Article 9.

## Retention

Data is kept for as long as the purpose requires and for the periods set by law; it is then deleted, destroyed or anonymised. Security records are kept for the legally required period.

## Security

Access is role-based and every request is checked on the server. Passwords are stored only by the identity server, never by HR360. Sensitive fields are encrypted, connections are protected with TLS and every change is written to an audit log.

## Your rights

Under KVKK Article 11 you may:

- learn whether your personal data is processed and request information about it,
- learn the purpose of processing and whether it is used accordingly,
- know the third parties in Turkey or abroad to whom it is transferred,
- request correction of incomplete or inaccurate data,
- request deletion or destruction under the conditions of Article 7,
- request that corrections and deletions are notified to third parties,
- object to a result against you that arises solely from automated analysis,
- claim compensation for damage caused by unlawful processing.

## How to apply

Send your request in writing or by registered electronic mail (KEP) to the contact details above, in line with the Communiqué on the Procedures and Principles of Application to the Data Controller. We reply within 30 days at the latest. If you are an employee of a company using HR360, you can also use **My profile › Privacy** inside the application or contact your company directly.

## Changes

This policy may be updated; the current version is always published on this page.`
  }
  return `# Gizlilik politikası

Bu metin, bu HR360 kurulumunda kişisel verilerin 6698 sayılı Kişisel Verilerin Korunması Kanunu'na ("KVKK") uygun olarak nasıl işlendiğini açıklar.

## Veri sorumlusu

${contactLines(site, lang)}

## Kim neden sorumlu

- **Platform hesapları** (kayıt olan şirket yöneticileri, platform yöneticileri, herkese açık sayfaların ziyaretçileri): veri sorumlusu ${op}.
- **Çalışan ve aday verileri** (bir şirketin sisteme girdiği veriler): veri sorumlusu ilgili şirketin kendisidir; ${op} bu verileri şirket adına, yalnızca şirketin talimatları doğrultusunda **veri işleyen** olarak işler.

## İşlenen kişisel veriler

- **Kimlik ve iletişim:** ad, soyad, iş e-postası, telefon numarası.
- **Hesap ve güvenlik:** kullanıcı adı, rol, giriş ve oturum kayıtları, IP adresi, denetim kayıtları.
- **Şirket kaydı:** şirket adı, kısa ad, yönetici bilgileri.
- **Yazışmalar:** bize gönderdiğiniz iletiler ve yanıtlarımız.

## İşleme amaçları

- Hesapların oluşturulması ve yönetilmesi, kimlik doğrulama, oturum güvenliği
- Hizmetin sunulması, destek ve bakım
- Bilgi güvenliği, kötüye kullanımın önlenmesi ve denetim izi
- Hukuki yükümlülüklerin yerine getirilmesi ve yetkili kurumların taleplerinin karşılanması

## Hukuki sebepler

Veriler KVKK md. 5/2 kapsamında; sözleşmenin kurulması ve ifası (c), hukuki yükümlülüğün yerine getirilmesi (ç), bir hakkın tesisi, kullanılması veya korunması (e) ve meşru menfaat (f) sebeplerine dayanılarak işlenir. Açık rıza gereken durumlarda rıza ayrıca alınır ve her zaman geri alınabilir.

## Aktarım

Veriler yalnızca gerektiği ölçüde altyapı hizmet sağlayıcılarıyla (barındırma, yedekleme, e-posta gönderimi) ve kanunen zorunlu olduğunda yetkili kamu kurum ve kuruluşlarıyla paylaşılır. Yurt dışına aktarım yalnızca KVKK md. 9'daki şartlarla yapılır.

## Saklama süresi

Veriler, işleme amacının gerektirdiği süre ve mevzuatta öngörülen süreler boyunca saklanır; süre sonunda silinir, yok edilir ya da anonim hale getirilir. Güvenlik kayıtları mevzuatın öngördüğü süre boyunca tutulur.

## Güvenlik

Erişim role göre sınırlıdır ve her istek sunucuda denetlenir. Parolalar yalnızca kimlik sunucusunda saklanır, HR360 parola tutmaz. Hassas alanlar şifrelenir, bağlantılar TLS ile korunur, her değişiklik denetim kaydına yazılır.

## Haklarınız

KVKK md. 11 uyarınca;

- kişisel verilerinizin işlenip işlenmediğini öğrenme ve işlenmişse bilgi talep etme,
- işlenme amacını ve amacına uygun kullanılıp kullanılmadığını öğrenme,
- yurt içinde veya yurt dışında aktarıldığı üçüncü kişileri bilme,
- eksik veya yanlış işlenmişse düzeltilmesini isteme,
- md. 7'deki şartlar çerçevesinde silinmesini veya yok edilmesini isteme,
- düzeltme ve silme işlemlerinin aktarıldığı üçüncü kişilere bildirilmesini isteme,
- münhasıran otomatik sistemlerle analiz sonucu aleyhinize bir sonuç çıkmasına itiraz etme,
- kanuna aykırı işleme nedeniyle zarara uğramanız hâlinde zararın giderilmesini talep etme

haklarına sahipsiniz.

## Başvuru

Taleplerinizi, Veri Sorumlusuna Başvuru Usul ve Esasları Hakkında Tebliğ'e uygun olarak yukarıdaki iletişim bilgilerine yazılı olarak ya da kayıtlı elektronik posta (KEP) ile iletebilirsiniz. Başvurunuz en geç 30 gün içinde yanıtlanır. HR360 kullanan bir şirketin çalışanıysanız uygulama içinde **Profilim › Gizlilik** ekranını da kullanabilir ya da doğrudan şirketinize başvurabilirsiniz.

## Değişiklikler

Bu politika güncellenebilir; geçerli sürüm her zaman bu sayfada yayımlanır.`
}

export function defaultTerms(site: SiteSettings | undefined, lang: Lang): string {
  const op = operator(site, lang)
  if (lang === 'en') {
    return `# Terms of use

These terms govern the use of this HR360 installation operated by ${op}. By creating an account or signing in you accept these terms.

## The service

HR360 is a human resources platform covering leave, payroll, performance, recruitment, shifts, onboarding and related processes. The service is provided on infrastructure run by ${op}.

## Accounts and security

- You are responsible for keeping your credentials confidential and for all actions taken with your account.
- Passwords are set with time-limited, single-use links; never share these links.
- Tell us or your company administrator without delay if you suspect unauthorised use.

## Acceptable use

You agree not to:

- access data or accounts you are not authorised for, or try to bypass security controls,
- disrupt the service, overload it or use automated tools against it without permission,
- upload unlawful content, malware or data you have no right to process.

## Company data

Each company is the data controller of the employee and candidate data it enters and is responsible for having a valid legal ground for processing it. ${cap(op, lang)} processes this data only to provide the service. See the [Privacy policy](/gizlilik) for details.

## Availability and changes

We aim to keep the service available but do not guarantee uninterrupted operation. Planned maintenance is announced where possible. Features may be added, changed or removed.

## Open source

HR360 is open source software licensed under the GNU Affero General Public License v3.0 (AGPL-3.0). The source code of the software is available at [${SOURCE_URL}](${SOURCE_URL}).

## Limitation of liability

To the extent permitted by law, ${op} is not liable for indirect damages, loss of profit or data loss caused by use of the service. Mandatory statutory liability is not affected.

## Termination

You may stop using the service at any time. Accounts that breach these terms may be suspended or closed.

## Changes to these terms

These terms may be updated; the current version is always published on this page.

## Governing law

These terms are governed by the laws of the Republic of Türkiye. The courts and enforcement offices at the operator's registered seat have jurisdiction.

## Contact

${contactLines(site, lang)}`
  }
  return `# Kullanım koşulları

Bu koşullar, ${op} tarafından işletilen bu HR360 kurulumunun kullanımını düzenler. Hesap oluşturarak ya da giriş yaparak bu koşulları kabul etmiş olursunuz.

## Hizmet

HR360; izin, bordro, performans, işe alım, vardiya, işe başlangıç ve ilgili süreçleri kapsayan bir insan kaynakları platformudur. Hizmet, ${op} tarafından işletilen altyapı üzerinde sunulur.

## Hesap ve güvenlik

- Giriş bilgilerinizin gizliliğinden ve hesabınızla yapılan işlemlerden siz sorumlusunuz.
- Parolalar süreli, tek kullanımlık linklerle belirlenir; bu linkleri kimseyle paylaşmayın.
- Yetkisiz bir kullanımdan şüphelenirseniz bize ya da şirket yöneticinize hemen bildirin.

## Kabul edilebilir kullanım

Şunları yapmamayı kabul edersiniz:

- yetkiniz olmayan verilere veya hesaplara erişmek, güvenlik denetimlerini aşmaya çalışmak,
- hizmeti aksatmak, aşırı yüklemek ya da izinsiz otomatik araçlarla kullanmak,
- hukuka aykırı içerik, zararlı yazılım ya da işleme yetkiniz olmayan veri yüklemek.

## Şirket verileri

Her şirket, sisteme girdiği çalışan ve aday verilerinin veri sorumlusudur ve bu verileri işlemek için geçerli bir hukuki sebebe sahip olmaktan sorumludur. ${cap(op, lang)} bu verileri yalnızca hizmeti sunmak için işler. Ayrıntılar için [Gizlilik politikası](/gizlilik) sayfasına bakın.

## Erişilebilirlik ve değişiklikler

Hizmetin kesintisiz çalışması için çaba gösterilir ancak kesintisizlik garanti edilmez. Planlı bakımlar mümkün olduğunca önceden duyurulur. Özellikler eklenebilir, değiştirilebilir veya kaldırılabilir.

## Açık kaynak

HR360, GNU Affero Genel Kamu Lisansı v3.0 (AGPL-3.0) ile lisanslanmış açık kaynak bir yazılımdır. Yazılımın kaynak koduna [${SOURCE_URL}](${SOURCE_URL}) adresinden ulaşabilirsiniz.

## Sorumluluğun sınırlandırılması

Kanunun izin verdiği ölçüde ${op}, hizmetin kullanımından doğan dolaylı zararlardan, kâr kaybından veya veri kaybından sorumlu tutulamaz. Kanundan doğan emredici sorumluluklar saklıdır.

## Fesih

Hizmeti dilediğiniz zaman kullanmayı bırakabilirsiniz. Bu koşulları ihlal eden hesaplar askıya alınabilir veya kapatılabilir.

## Koşullardaki değişiklikler

Bu koşullar güncellenebilir; geçerli sürüm her zaman bu sayfada yayımlanır.

## Uygulanacak hukuk

Bu koşullara Türkiye Cumhuriyeti hukuku uygulanır. Uyuşmazlıklarda işletmecinin merkezinin bulunduğu yer mahkemeleri ve icra daireleri yetkilidir.

## İletişim

${contactLines(site, lang)}`
}

export function defaultFaq(lang: Lang): FaqItem[] {
  if (lang === 'en') {
    return [
      { q: 'How do I sign in?', a: 'Choose **Sign in with my company account** on the sign-in page. Your identity is verified by the identity server and the screens you see depend on your role.' },
      { q: 'I forgot my password. What should I do?', a: 'Use **Forgot password** on the sign-in screen. If email is not enabled on this installation, ask your HR administrator for a new time-limited password link.' },
      { q: 'My password link has expired.', a: 'Links are time-limited and single-use for your security. Ask your HR administrator for a new link; creating a new link cancels the old one.' },
      { q: 'How do I register my company?', a: 'If public registration is open, use **Register your company** on the [registration page](/kayit). Otherwise contact the platform operator using the details on the [Contact](/iletisim) page.' },
      { q: 'Where is my personal data stored?', a: 'On the servers of the organisation operating this installation. Employee data belongs to your company; see the [Privacy policy](/gizlilik) for details.' },
      { q: 'How can I exercise my KVKK rights?', a: 'Inside the application use **My profile › Privacy**, or apply in writing using the contact details on the [Privacy policy](/gizlilik) page.' },
      { q: 'Is there a mobile app?', a: 'HR360 works in any modern browser and can be added to your home screen as an app (PWA). Leave requests, approvals and clock-in work on the phone.' },
      { q: 'Which applications can it connect to?', a: 'Slack, Microsoft Teams, Zoom, Google Workspace, Microsoft 365, Entra ID, Active Directory/LDAP, Okta (SCIM), webhooks, Zapier, n8n and AI providers such as OpenAI, Anthropic or a local Ollama model.' },
      { q: 'Do you use cookies?', a: 'Only cookies that are strictly necessary for signing in and security. There are no advertising or tracking cookies. The full list is in the [cookies section](/gizlilik#cerezler).' },
    ]
  }
  return [
    { q: 'Nasıl giriş yaparım?', a: 'Giriş sayfasında **Kurumsal hesabımla giriş yap** düğmesini kullanın. Kimliğiniz kimlik sunucusunda doğrulanır; gördüğünüz ekranlar rolünüze göre şekillenir.' },
    { q: 'Parolamı unuttum, ne yapmalıyım?', a: 'Giriş ekranındaki **Parolamı unuttum** bağlantısını kullanın. Bu kurulumda e-posta kapalıysa İK yöneticinizden yeni bir süreli parola linki isteyin.' },
    { q: 'Parola linkimin süresi dolmuş.', a: 'Linkler güvenliğiniz için süreli ve tek kullanımlıktır. İK yöneticinizden yeni bir link isteyin; yeni link eskisini otomatik olarak iptal eder.' },
    { q: 'Şirketimi nasıl kaydederim?', a: 'Herkese açık kayıt açıksa [kayıt sayfasından](/kayit) **Şirketinizi kaydedin** adımlarını izleyin. Kapalıysa [İletişim](/iletisim) sayfasındaki bilgilerle platform işletmecisine başvurun.' },
    { q: 'Kişisel verilerim nerede saklanıyor?', a: 'Bu kurulumu işleten kuruluşun sunucularında. Çalışan verilerinin sorumlusu şirketinizdir; ayrıntılar [Gizlilik politikası](/gizlilik) sayfasında.' },
    { q: 'KVKK kapsamındaki haklarımı nasıl kullanırım?', a: 'Uygulama içinde **Profilim › Gizlilik** ekranını kullanabilir ya da [Gizlilik politikası](/gizlilik) sayfasındaki iletişim bilgileriyle yazılı olarak başvurabilirsiniz.' },
    { q: 'Mobil uygulama var mı?', a: 'HR360 her güncel tarayıcıda çalışır ve ana ekrana uygulama olarak eklenebilir (PWA). İzin talebi, onaylar ve giriş-çıkış telefondan yapılabilir.' },
    { q: 'Hangi uygulamalarla bağlantı kurabiliyor?', a: 'Slack, Microsoft Teams, Zoom, Google Workspace, Microsoft 365, Entra ID, Active Directory/LDAP, Okta (SCIM), webhook, Zapier, n8n ve OpenAI, Anthropic ya da yerel Ollama gibi yapay zekâ sağlayıcıları.' },
    { q: 'Çerez kullanıyor musunuz?', a: 'Yalnızca giriş ve güvenlik için zorunlu çerezler kullanılır; reklam veya izleme çerezi yoktur. Tam liste [çerezler bölümünde](/gizlilik#cerezler).' },
  ]
}
