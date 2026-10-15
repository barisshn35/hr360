-- Yavaş sorgu incelemesi (dalga 12, madde 97): EF sorguları ve ham SQL okunarak bulunan,
-- sık çalışan filtrelerde eksik olan indeksler. Hepsi idempotent (IF NOT EXISTS).
--
-- Neden CONCURRENTLY değil: bu dosya sql-all-schemas.sql içinde (tek dosya, ON_ERROR_STOP) ve
-- docker-entrypoint-initdb'de de çalışır; CREATE INDEX CONCURRENTLY hata durumunda INVALID indeks
-- bırakır ve "IF NOT EXISTS" bunu bir dahaki sefere atlar. Tablolar bugün küçük (en büyüğü
-- audit_log); düz CREATE INDEX kısa süreli yazma kilidi alır (okuma serbest). Büyük bir canlı
-- kurulumda (audit_log milyonlarca satır) aynı ifadeler CONCURRENTLY ile elle, tek tek
-- (transaction dışında) çalıştırılabilir — bkz. docs/runbooks/yavas-sorgu.md.

-- ---- İş akışı: onay kutusu ve görünürlük filtresi
-- WorkflowsController.VisibleTo: Steps.Any(ApproverEmployeeId = me OR DelegatedToEmployeeId = me);
-- vekalet atama/geri alma: ApproverEmployeeId = X AND Decision = 'Pending'.
CREATE INDEX IF NOT EXISTS "IX_workflow_approval_steps_Approver_Decision"
    ON workflow_approval_steps ("ApproverEmployeeId", "Decision");
CREATE INDEX IF NOT EXISTS "IX_workflow_approval_steps_DelegatedTo"
    ON workflow_approval_steps ("DelegatedToEmployeeId") WHERE "DelegatedToEmployeeId" IS NOT NULL;
-- Talep listesi: RequesterEmployeeId = me / Status = x, CreatedAt DESC sıralı.
CREATE INDEX IF NOT EXISTS "IX_workflow_requests_Tenant_Requester_Created"
    ON workflow_requests ("TenantSlug", "RequesterEmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_workflow_requests_Tenant_Status_Created"
    ON workflow_requests ("TenantSlug", "Status", "CreatedAt" DESC);
-- SLA tırmandırma işi (WorkflowJobs, tüm kiracılar): Status = 'Pending' AND SlaDueAt < now.
CREATE INDEX IF NOT EXISTS "IX_workflow_requests_pending_sla"
    ON workflow_requests ("SlaDueAt") WHERE "Status" = 'Pending' AND "SlaDueAt" IS NOT NULL;
-- Vekaletlerim: FromEmployeeId = me OR ToEmployeeId = me (From zaten indeksli).
CREATE INDEX IF NOT EXISTS "IX_workflow_delegations_Tenant_To"
    ON workflow_delegations ("TenantSlug", "ToEmployeeId");

-- ---- İş akışı olay tüketicileri: karar gelince kaynağı WorkflowRequestId ile bulur
CREATE INDEX IF NOT EXISTS "IX_leave_requests_WorkflowRequestId"
    ON leave_requests ("WorkflowRequestId") WHERE "WorkflowRequestId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_expense_claims_WorkflowRequestId"
    ON expense_claims ("WorkflowRequestId") WHERE "WorkflowRequestId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_expense_travel_requests_WorkflowRequestId"
    ON expense_travel_requests ("WorkflowRequestId") WHERE "WorkflowRequestId" IS NOT NULL;

-- Bildirim kutusu (RecipientEmployeeId = me ORDER BY CreatedAt DESC) için ayrı indeks EKLENMEDİ:
-- mevcut ("RecipientEmployeeId","Status") kişi başına birkaç yüz satırı zaten daraltıyor
-- (600 bin satırlık denemede 0,71 ms → 0,66 ms; yazma maliyetine değmez).

-- ---- Denetim kaydı (en hızlı büyüyen tablo)
-- Denetim araması userId filtresi; bordro kapanışındaki IBAN değişikliği kontrolü (UserId = x).
CREATE INDEX IF NOT EXISTS "IX_audit_log_Tenant_User_Occurred"
    ON audit_log ("TenantSlug", "UserId", "OccurredAt" DESC);
-- KVKK veri paketi erişim dökümü ve hassas erişim raporu: TenantSlug + EntityId (EntityType'sız).
-- Mevcut IX_audit_log_Entity EntityType ile başlıyor; PG18 skip scan az sayıda EntityType'ta
-- onu kullanabiliyor ama canlıda ~190 farklı EntityType var.
CREATE INDEX IF NOT EXISTS "IX_audit_log_Tenant_Entity_Occurred"
    ON audit_log ("TenantSlug", "EntityId", "OccurredAt" DESC);

-- ---- Yönetici ekip sorguları (izin/masraf): JOIN organization_departments ... "HeadEmployeeId" = me
CREATE INDEX IF NOT EXISTS "IX_organization_departments_Tenant_Head"
    ON organization_departments ("TenantSlug", "HeadEmployeeId") WHERE "HeadEmployeeId" IS NOT NULL;

-- ---- Kişi bazlı listeler
CREATE INDEX IF NOT EXISTS "IX_learning_enrollments_Tenant_Employee"
    ON learning_enrollments ("TenantSlug", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_expense_hr_cases_Tenant_Employee"
    ON expense_hr_cases ("TenantSlug", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_expense_hr_cases_Tenant_Assigned"
    ON expense_hr_cases ("TenantSlug", "AssignedToEmployeeId") WHERE "AssignedToEmployeeId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_onboarding_tasks_Assignee"
    ON onboarding_tasks ("AssigneeEmployeeId") WHERE "AssigneeEmployeeId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_engagement_kudos_Tenant_To_Created"
    ON engagement_kudos ("TenantSlug", "ToEmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_engagement_one_on_ones_Manager"
    ON engagement_one_on_ones ("TenantSlug", "ManagerUserId");
CREATE INDEX IF NOT EXISTS "IX_engagement_one_on_ones_EmployeeUser"
    ON engagement_one_on_ones ("TenantSlug", "EmployeeUserId");
-- Takas isteklerim: RequesterEmployeeId = me OR TargetEmployeeId = me (mevcut bileşik indeks
-- Requester ile başladığı için Target tarafını karşılamıyor).
CREATE INDEX IF NOT EXISTS "IX_timeshift_swap_requests_Target"
    ON timeshift_swap_requests ("TargetEmployeeId");
