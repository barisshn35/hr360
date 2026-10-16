import { tx } from '@/lib/i18n'
import type { Rule } from '@/api/governance'

export type RuleDraft = Omit<Rule, 'id' | 'fireCount' | 'lastFiredAt' | 'createdAt'>
export interface RuleRecipe { id: string; title: string; detail: string; draft: RuleDraft }

/**
 * Hazır otomasyon tarifleri (yenilik haritası #14): kural motorunun olaylarıyla kurulan, tek tıkla
 * düzenleyicide açılan şablonlar. Webhook/Slack/Teams adresleri boş gelir; kayıttan önce doldurulur.
 */
export const ruleRecipes = (): RuleRecipe[] => [
  { id: 'welcome', title: tx('Yeni çalışana hoş geldin'), detail: tx('İşe alınan kişiye ilk gün uygulama içi karşılama mesajı.'),
    draft: { name: tx('Yeni çalışana hoş geldin'), description: tx('Hazır tarif'), trigger: 'employee.hired', conditions: [], isEnabled: true,
      actions: [{ type: 'notify', target: 'employee', message: tx('Aramıza hoş geldin {{FirstName}}! İlk haftan için işe giriş planına göz atmayı unutma.') }] } },
  { id: 'long-leave', title: tx('Uzun izinleri İK kanalına bildir'), detail: tx('5 gün ve üzeri onaylanan izinler Slack kanalına yazılır.'),
    draft: { name: tx('Uzun izin bildirimi'), description: tx('Hazır tarif'), trigger: 'leave.approved', isEnabled: true,
      conditions: [{ field: 'Days', op: 'gte', value: '5' }], actions: [{ type: 'slack', target: '', message: '{{ozet}}' }] } },
  { id: 'get-well', title: tx('Hastalık izninde geçmiş olsun'), detail: tx('Onaylanan hastalık izninde çalışana nazik bir mesaj.'),
    draft: { name: tx('Geçmiş olsun mesajı'), description: tx('Hazır tarif'), trigger: 'leave.approved', isEnabled: true,
      conditions: [{ field: 'Type', op: 'eq', value: 'Sick' }], actions: [{ type: 'notify', target: 'employee', message: tx('Geçmiş olsun, kendine iyi bak. İşler seni bekler.') }] } },
  { id: 'offboarding-it', title: tx('Ayrılışta BT\'ye haber ver'), detail: tx('Çalışan ayrıldı olarak işaretlenince hesap kapatma için webhook tetiklenir.'),
    draft: { name: tx('Ayrılış: BT hesap kapatma'), description: tx('Hazır tarif'), trigger: 'employee.status-changed', isEnabled: true,
      conditions: [{ field: 'NewStatus', op: 'eq', value: 'Terminated' }], actions: [{ type: 'webhook', target: '', message: '{{ozet}}' }] } },
  { id: 'new-role', title: tx('Yeni görevde başarılar'), detail: tx('Görev değişikliğinde çalışana tebrik mesajı.'),
    draft: { name: tx('Görev değişikliği tebriği'), description: tx('Hazır tarif'), trigger: 'employee.assigned', conditions: [], isEnabled: true,
      actions: [{ type: 'notify', target: 'employee', message: tx('Yeni görevinde ({{PositionTitle}}) başarılar {{FirstName}}!') }] } },
  { id: 'rejected', title: tx('Reddedilen talepte yol göster'), detail: tx('Talep reddedilince sahibine ne yapabileceğini anlatan mesaj.'),
    draft: { name: tx('Ret sonrası bilgilendirme'), description: tx('Hazır tarif'), trigger: 'workflow.rejected', conditions: [], isEnabled: true,
      actions: [{ type: 'notify', target: 'requester', message: tx('"{{Subject}}" talebin reddedildi. Gerekçeyi talebin ayrıntısında görebilir, gerekirse yöneticinle konuşabilirsin.') }] } },
  { id: 'approvals-teams', title: tx('Onaya düşenleri Teams\'e yaz'), detail: tx('Her yeni onay talebi Teams kanalına özetlenir.'),
    draft: { name: tx('Onay talepleri Teams\'e'), description: tx('Hazır tarif'), trigger: 'workflow.submitted', conditions: [], isEnabled: true,
      actions: [{ type: 'teams', target: '', message: '{{ozet}}' }] } },
  { id: 'signed-archive', title: tx('İmzalanan belgeyi arşive gönder'), detail: tx('Basit e-imzayla imzalanan belgeler arşiv sistemine webhook ile bildirilir.'),
    draft: { name: tx('İmzalı belge arşivi'), description: tx('Hazır tarif'), trigger: 'document.signed', conditions: [], isEnabled: true,
      actions: [{ type: 'webhook', target: '', message: '{{ozet}}' }] } },
]
