# Veritabanı rolleri — elle gözden geçirme listesi

OTOMATİK ÜRETİLDİ: `scripts/db-roles.sh generate`. Eşleme kod taramasıyla çıkarılır; aşağıdakiler
taramanın kesin karar veremediği ya da bilinçli bir tercih gerektiren noktalardır.

## Genel

- Yeni migration (yeni tablo) sonrasında `scripts/db-roles.sh generate && scripts/db-roles.sh grants`
  çalıştırılmalı. `hr360admin` için `ALTER DEFAULT PRIVILEGES` bilerek açılmadı (en az yetki).
- `audit_log`: servis rolleri yalnızca SELECT + INSERT (zincir tetikleyicisi önceki satırı okur).
  UPDATE zaten tetikleyiciyle (`audit_log_immutable`) engelli. Saklama süresi silmesi
  (governance `Retention`, kategori AuditLog) `hr360_retention` rolü ister.
- `messaging_outbox`, `messaging_processed_events` tüm servislerce paylaşılır (tam yetki):
  bir servis rolü başka servisin outbox satırını değiştirebilir. Ayırmak için tablo bölünmeli.
- Fonksiyonlar (`hr360_fold`, `audit_row_hash`, tetikleyici fonksiyonları) PUBLIC EXECUTE ile çalışır.
- Görünümler (`analytics_department_headcount`, `analytics_hires_monthly`, `analytics_leave_monthly`, `analytics_overtime_monthly`) sahibinin (hr360admin) yetkisiyle çalışır; RLS'yi atlar.
- Yalnızca tablo adının bir metin sabitinde geçmesi SELECT verir (yorumlar hariç). Bu, sütun adı/ileti
  gibi yanlış pozitiflerle fazladan SELECT verebilir; yazma yetkileri yalnızca SQL kalıplarından gelir.
- EF ile eşlenmiş tablolarda (ToTable) tam yetki verilir: SaveChanges'in yazıp yazmadığı statik
  olarak ayırt edilemez.
- employee-service ve organization-service açılışta `EnsureCreated()` çağırır: rol tablolarını
  gördüğü sürece (information_schema yetkili tabloları gösterir) işlem yapmaz; rolün hiç tablosu
  görünmezse CREATE TABLE dener ve şema CREATE yetkisi olmadığı için açılış hata verir.
- Python ML servisi, Keycloak, MLflow ve postgres-exporter `hr360admin` ile kalır (kapsam dışı).

## Servis özeti

| Servis | Rol | Toplam tablo | Kendi | Başka servisten salt okuma | Başka servise yazma |
|---|---|---|---|---|---|
| organization-service | `hr360_organization` | 11 | 6 | 4 | 0 |
| employee-service | `hr360_employee` | 7 | 3 | 2 | 0 |
| workflow-service | `hr360_workflow` | 14 | 5 | 7 | 0 |
| leave-service | `hr360_leave` | 15 | 5 | 6 | 1 |
| recruitment-service | `hr360_recruitment` | 24 | 13 | 4 | 5 |
| onboarding-service | `hr360_onboarding` | 15 | 8 | 5 | 1 |
| timeshift-service | `hr360_timeshift` | 24 | 16 | 5 | 1 |
| performance-service | `hr360_performance` | 24 | 18 | 5 | 0 |
| learning-service | `hr360_learning` | 27 | 19 | 6 | 1 |
| engagement-service | `hr360_engagement` | 49 | 27 | 20 | 1 |
| governance-service | `hr360_governance` | 179 | 72 | 56 | 50 |
| compensation-service | `hr360_compensation` | 34 | 19 | 13 | 1 |
| expense-service | `hr360_expense` | 21 | 10 | 5 | 3 |
| notification-service | `hr360_notification` | 11 | 7 | 2 | 0 |
| tenant-service | `hr360_tenant` | 15 | 11 | 2 | 1 |

## leave-service (`hr360_leave`)

- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/leave-service/Controllers/LeaveRequestsController.cs:695

## recruitment-service (`hr360_recruitment`)

- Başka servisin tablosuna yazma: `governance_destruction_logs` (INSERT) — apps/services/recruitment-service/Services/RecruitmentSql.cs:159
- Başka servisin tablosuna yazma: `governance_signature_otps` (DELETE) — tetikleyici: recruitment_offers DELETE
- Başka servisin tablosuna yazma: `governance_signatures` (DELETE) — tetikleyici: recruitment_offers DELETE
- Başka servisin tablosuna yazma: `governance_storage_deletions` (INSERT) — apps/services/recruitment-service/Services/RecruitmentSql.cs:144
- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/recruitment-service/Services/RecruitmentSql.cs:81; apps/services/recruitment-service/Services/RecruitmentSql.cs:98
- Not: recruitment_offers üzerindeki DELETE tetikleyicisi governance_signature_otps için DELETE, SELECT istiyor
- Not: recruitment_offers üzerindeki DELETE tetikleyicisi governance_signatures için DELETE, SELECT istiyor

## onboarding-service (`hr360_onboarding`)

- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/onboarding-service/Services/OnboardingJobsWorker.cs:58; apps/services/onboarding-service/Services/Ops.cs:84

## timeshift-service (`hr360_timeshift`)

- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/timeshift-service/Services/TsOps.cs:44
- apps/services/timeshift-service/Controllers/ShiftSwapsController.cs:328: satır kilidi (FOR UPDATE/SHARE) — metindeki tüm tablolara UPDATE verildi: timeshift_assignments

## learning-service (`hr360_learning`)

- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/learning-service/Services/CertificateReminderWorker.cs:130; apps/services/learning-service/Services/DueReminderWorker.cs:170; apps/services/learning-service/Services/LearningDirectory.cs:129

## engagement-service (`hr360_engagement`)

- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/engagement-service/Infrastructure/Platform.cs:156; apps/services/engagement-service/Infrastructure/Platform.cs:190
- apps/services/engagement-service/Security/KeyRotationJob.cs:112: tablo adı çalışma anında birleştiriliyor (`UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old`)
- apps/services/engagement-service/Security/KeyRotationJob.cs:97: tablo adı çalışma anında birleştiriliyor (`SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" `)

## governance-service (`hr360_governance`)

- Başka servisin tablosuna yazma: `compensation_payslips` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:309
- Başka servisin tablosuna yazma: `employee_employees` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:184
- Başka servisin tablosuna yazma: `employee_photos` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:182
- Başka servisin tablosuna yazma: `engagement_desk_bookings` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:86
- Başka servisin tablosuna yazma: `engagement_eom_nominations` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:168
- Başka servisin tablosuna yazma: `engagement_eom_votes` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:163; apps/services/governance-service/Infrastructure/RetentionPlans.cs:166
- Başka servisin tablosuna yazma: `engagement_event_rsvps` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:179
- Başka servisin tablosuna yazma: `engagement_events` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:180
- Başka servisin tablosuna yazma: `engagement_idea_comments` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:174
- Başka servisin tablosuna yazma: `engagement_ideas` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:171
- Başka servisin tablosuna yazma: `engagement_internal_applications` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:76
- Başka servisin tablosuna yazma: `engagement_kudos` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:65; apps/services/governance-service/Infrastructure/RetentionPlans.cs:67
- Başka servisin tablosuna yazma: `engagement_mentor_profiles` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:69
- Başka servisin tablosuna yazma: `engagement_mentorships` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:70
- Başka servisin tablosuna yazma: `engagement_newhire_checkins` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:177
- Başka servisin tablosuna yazma: `engagement_offboarding_cases` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:87
- Başka servisin tablosuna yazma: `engagement_one_on_ones` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:78
- Başka servisin tablosuna yazma: `engagement_presence` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:85
- Başka servisin tablosuna yazma: `engagement_profiles` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:55
- Başka servisin tablosuna yazma: `engagement_weekly_checkins` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:178
- Başka servisin tablosuna yazma: `expense_hr_cases` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:150
- Başka servisin tablosuna yazma: `expense_travel_requests` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:100
- Başka servisin tablosuna yazma: `learning_certifications` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:129
- Başka servisin tablosuna yazma: `learning_due_reminders` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:119
- Başka servisin tablosuna yazma: `learning_scorm_runtime` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:131
- Başka servisin tablosuna yazma: `leave_requests` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:90
- Başka servisin tablosuna yazma: `notification_category_prefs` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:149
- Başka servisin tablosuna yazma: `notification_messages` (DELETE, INSERT) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:135; apps/services/governance-service/Infrastructure/RetentionPlans.cs:302
- Başka servisin tablosuna yazma: `notification_preferences` (DELETE, INSERT, UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:137
- Başka servisin tablosuna yazma: `notification_push_subscriptions` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:136
- Başka servisin tablosuna yazma: `onboarding_asset_assignments` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:133
- Başka servisin tablosuna yazma: `performance_calibration_changes` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:113
- Başka servisin tablosuna yazma: `performance_calibration_items` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:111
- Başka servisin tablosuna yazma: `performance_f360_participants` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:117
- Başka servisin tablosuna yazma: `performance_f360_requests` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:115
- Başka servisin tablosuna yazma: `performance_feedback` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:104
- Başka servisin tablosuna yazma: `performance_potential_ratings` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:106
- Başka servisin tablosuna yazma: `performance_reviews` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:102
- Başka servisin tablosuna yazma: `recruitment_applications` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:259
- Başka servisin tablosuna yazma: `recruitment_candidates` (DELETE, UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:287
- Başka servisin tablosuna yazma: `recruitment_interviews` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:262
- Başka servisin tablosuna yazma: `recruitment_offers` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:267
- Başka servisin tablosuna yazma: `recruitment_referrals` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:121; apps/services/governance-service/Infrastructure/RetentionPlans.cs:253
- Başka servisin tablosuna yazma: `recruitment_scorecards` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:264
- Başka servisin tablosuna yazma: `recruitment_status_links` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:270
- Başka servisin tablosuna yazma: `timeshift_clock_credentials` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:96
- Başka servisin tablosuna yazma: `timeshift_clock_punches` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:94
- Başka servisin tablosuna yazma: `timeshift_overtime_requests` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:98
- Başka servisin tablosuna yazma: `timeshift_shift_preferences` (DELETE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:97
- Başka servisin tablosuna yazma: `timeshift_time_entries` (UPDATE) — apps/services/governance-service/Infrastructure/RetentionPlans.cs:92
- apps/services/governance-service/Infrastructure/NlReport.cs:459: tablo adı çalışma anında birleştiriliyor (`SELECT coalesce(d.\"Name\", '{unassigned}'), {valueExpr}, {distinctPeople} FROM {from} {De`)
- apps/services/governance-service/Infrastructure/NlReport.cs:460: tablo adı çalışma anında birleştiriliyor (`SELECT date_trunc('month', {dateExpr})::date, {valueExpr}, {distinctPeople} FROM {from} {D`)
- apps/services/governance-service/Infrastructure/NlReport.cs:461: tablo adı çalışma anında birleştiriliyor (`SELECT {typeExpr}, {valueExpr}, {distinctPeople} FROM {from} {DeptJoin} {where} GROUP BY 1`)
- apps/services/governance-service/Infrastructure/NlReport.cs:462: tablo adı çalışma anında birleştiriliyor (`SELECT e.\"FirstName\" || ' ' || e.\"LastName\", {valueExpr}, 1 FROM {from} {DeptJoin} {wh`)
- apps/services/governance-service/Infrastructure/NlReport.cs:463: tablo adı çalışma anında birleştiriliyor (`SELECT '{label}', {valueExpr}, {distinctPeople} FROM {from} {DeptJoin} {where}`)
- apps/services/governance-service/Infrastructure/NlReport.cs:531: tablo adı çalışma anında birleştiriliyor (`SELECT e.\"FirstName\" || ' ' || e.\"LastName\", round(avg(s.\"Score\"), 1), 1 FROM {lates`)
- apps/services/governance-service/Infrastructure/NlReport.cs:533: tablo adı çalışma anında birleştiriliyor (`SELECT coalesce(d.\"Name\", '{unassigned}'), round(avg(s.\"Score\"), 1), count(*) FROM {la`)
- apps/services/governance-service/Infrastructure/NlReport.cs:534: tablo adı çalışma anında birleştiriliyor (`SELECT '{L("Ortalama puan", "Average score")}', round(avg(s.\"Score\"), 1), count(*) FROM `)
- apps/services/governance-service/Infrastructure/RetentionPlans.cs:40: tablo adı çalışma anında birleştiriliyor (`SELECT count(*) FROM {TableOf(head)} WHERE {where}`)
- apps/services/governance-service/Infrastructure/RetentionPlans.cs:43: tablo adı çalışma anında birleştiriliyor (`SELECT count(*) FROM {TableOf(head)} WHERE {where}`)
- apps/services/governance-service/Infrastructure/WebhookRetry.cs:115: satır kilidi (FOR UPDATE/SHARE) — metindeki tüm tablolara UPDATE verildi: governance_webhook_deliveries
- apps/services/governance-service/Security/KeyRotationJob.cs:112: tablo adı çalışma anında birleştiriliyor (`UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old`)
- apps/services/governance-service/Security/KeyRotationJob.cs:97: tablo adı çalışma anında birleştiriliyor (`SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" `)
- Not: audit_log: DELETE VERİLMEDİ (değiştirilemez denetim kaydı; silme hr360_retention'da)
- Not: governance_document_requests üzerindeki DELETE tetikleyicisi governance_signatures için DELETE istiyor

## compensation-service (`hr360_compensation`)

- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/compensation-service/Controllers/PayrollEcosystemController.cs:65; apps/services/compensation-service/Controllers/PayrollTrController.cs:552
- apps/services/compensation-service/Security/KeyRotationJob.cs:112: tablo adı çalışma anında birleştiriliyor (`UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old`)
- apps/services/compensation-service/Security/KeyRotationJob.cs:97: tablo adı çalışma anında birleştiriliyor (`SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" `)

## expense-service (`hr360_expense`)

- Başka servisin tablosuna yazma: `governance_signature_otps` (DELETE) — tetikleyici: expense_documents DELETE
- Başka servisin tablosuna yazma: `governance_signatures` (DELETE) — tetikleyici: expense_documents DELETE
- Başka servisin tablosuna yazma: `notification_messages` (INSERT) — apps/services/expense-service/Controllers/DocumentComplianceController.cs:133; apps/services/expense-service/Controllers/DocumentSignaturesController.cs:88
- apps/services/expense-service/Security/KeyRotationJob.cs:112: tablo adı çalışma anında birleştiriliyor (`UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old`)
- apps/services/expense-service/Security/KeyRotationJob.cs:97: tablo adı çalışma anında birleştiriliyor (`SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" `)
- Not: expense_documents üzerindeki DELETE tetikleyicisi governance_signature_otps için DELETE, SELECT istiyor
- Not: expense_documents üzerindeki DELETE tetikleyicisi governance_signatures için DELETE, SELECT istiyor

## notification-service (`hr360_notification`)

- apps/services/notification-service/Security/KeyRotationJob.cs:112: tablo adı çalışma anında birleştiriliyor (`UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old`)
- apps/services/notification-service/Security/KeyRotationJob.cs:97: tablo adı çalışma anında birleştiriliyor (`SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" `)

## tenant-service (`hr360_tenant`)

- Başka servisin tablosuna yazma: `organization_companies` (DELETE, INSERT) — apps/services/tenant-service/Services/TenantProvisioningService.cs:146
- apps/services/tenant-service/Security/KeyRotationJob.cs:112: tablo adı çalışma anında birleştiriliyor (`UPDATE {c.Table} SET {col} = @new WHERE {id} = @id AND {col} = @old`)
- apps/services/tenant-service/Security/KeyRotationJob.cs:97: tablo adı çalışma anında birleştiriliyor (`SELECT {id}, {col} FROM {c.Table} WHERE {col} IS NOT NULL{filter}{(last is null ? "" : $" `)

## Hiçbir servisin kodunda geçmeyen tablolar

Bu tablolara hiçbir rol yetki almadı (ölü tablo, yalnızca betik/ML kullanımı ya da dinamik ad olabilir):

- `analytics_hires_monthly`
- `governance_invoices`
