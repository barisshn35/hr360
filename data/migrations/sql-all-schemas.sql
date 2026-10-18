-- HR360 - tum servislerin birlesik semasi (hr360_operational veritabani).
-- Kullanim: elle calistirilmaz. docker-compose.yml bu dosyayi Postgres'in
-- docker-entrypoint-initdb.d klasorune baglar; veritabani ILK KEZ olusurken
-- otomatik uygulanir. Mevcut kurulumlarin semasi scripts/sql/*.sql gocleriyle
-- guncellenir (install.sh guncelleme yolu bunlari otomatik calistirir).
--
-- ONEMLI: Dosyanin buyuk kismi EF Core modellerinden uretildi, ancak sondaki
-- "CTO denetimi" bolumu ve bazi indeksler (lower("Email") ile kiraci bazli
-- e-posta tekilligi, kismi/filtreli indeksler) ELLE yazildi. Dosya EF'ten
-- yeniden uretilirse bu satirlar korunmali. Servislerin eski sql/schema.sql
-- dosyalari kaldirildi; tek kaynak bu dosyadir.

-- NOT (hardcore test bulgusu, 2. tur): asagidaki 9 servisin TAMAMININ tablolari
-- EF Core modelleriyle senkron DEGILDI - bazilarinda TenantSlug sutunu hic
-- yoktu (multi-tenancy modellere sonradan eklenmis ama bu dosyaya hic
-- yansitilmamisti - her authenticated istek "column ... TenantSlug does not
-- exist" ile 500 donuyordu), bazilarinda (performance-service: review_scores,
-- scoring_config; timeshift-service: shift_overrides, shift_patterns,
-- shift_pattern_days, shift_teams, shift_team_members) TABLONUN KENDISI HIC
-- YOKTU. Asagidaki tanimlarin tamami, ayni once organization/employee/
-- workflow/tenant icin yapilan duzeltmede oldugu gibi, ilgili servislerin
-- GUNCEL EF Core modellerinden ("dotnet ef migrations script") birebir
-- uretildi - elle yazilmadi.
-- ============================================================

-- ============================================================
-- recruitment-service
-- ============================================================
CREATE TABLE recruitment_candidates (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "FirstName" text NOT NULL,
    "LastName" text NOT NULL,
    "Email" text NOT NULL,
    "Phone" text,
    "ResumeStorageKey" text,
    "Source" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_recruitment_candidates" PRIMARY KEY ("Id")
);

CREATE TABLE recruitment_job_postings (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "DepartmentId" uuid NOT NULL,
    "Description" text,
    "EmploymentType" text NOT NULL,
    "Status" text NOT NULL,
    "Headcount" integer NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "PublishedAt" timestamptz,
    "ClosedAt" timestamptz,
    CONSTRAINT "PK_recruitment_job_postings" PRIMARY KEY ("Id")
);

CREATE TABLE recruitment_applications (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "JobPostingId" uuid NOT NULL,
    "CandidateId" uuid NOT NULL,
    "Status" text NOT NULL,
    "Notes" text,
    "AppliedAt" timestamptz NOT NULL,
    "StatusChangedAt" timestamptz,
    CONSTRAINT "PK_recruitment_applications" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_recruitment_applications_recruitment_candidates_CandidateId" FOREIGN KEY ("CandidateId") REFERENCES recruitment_candidates ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_recruitment_applications_recruitment_job_postings_JobPostin~" FOREIGN KEY ("JobPostingId") REFERENCES recruitment_job_postings ("Id") ON DELETE CASCADE
);

CREATE TABLE recruitment_interviews (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ApplicationId" uuid NOT NULL,
    "Type" text NOT NULL,
    "ScheduledAt" timestamptz NOT NULL,
    "InterviewerEmployeeId" uuid NOT NULL,
    "Result" text NOT NULL,
    "Score" integer,
    "Notes" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_recruitment_interviews" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_recruitment_interviews_recruitment_applications_Application~" FOREIGN KEY ("ApplicationId") REFERENCES recruitment_applications ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_recruitment_applications_CandidateId" ON recruitment_applications ("CandidateId");

CREATE UNIQUE INDEX "IX_recruitment_applications_JobPostingId_CandidateId" ON recruitment_applications ("JobPostingId", "CandidateId");

CREATE INDEX "IX_recruitment_applications_TenantSlug" ON recruitment_applications ("TenantSlug");

CREATE UNIQUE INDEX "IX_recruitment_candidates_TenantSlug_Email" ON recruitment_candidates ("TenantSlug", "Email");

CREATE INDEX "IX_recruitment_candidates_TenantSlug" ON recruitment_candidates ("TenantSlug");

CREATE INDEX "IX_recruitment_interviews_ApplicationId" ON recruitment_interviews ("ApplicationId");

CREATE INDEX "IX_recruitment_interviews_TenantSlug" ON recruitment_interviews ("TenantSlug");

CREATE INDEX "IX_recruitment_job_postings_TenantSlug" ON recruitment_job_postings ("TenantSlug");

-- ============================================================
-- onboarding-service
-- ============================================================
CREATE TABLE onboarding_assets (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "AssetTag" text NOT NULL,
    "Type" text NOT NULL,
    "Model" text,
    "SerialNumber" text,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_onboarding_assets" PRIMARY KEY ("Id")
);

CREATE TABLE onboarding_plans (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "StartDate" date NOT NULL,
    "TemplateName" text,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz,
    CONSTRAINT "PK_onboarding_plans" PRIMARY KEY ("Id")
);

CREATE TABLE onboarding_asset_assignments (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "AssetId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "AssignedOn" date NOT NULL,
    "ReturnedOn" date,
    "ConditionOnReturn" text,
    "Notes" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_onboarding_asset_assignments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_onboarding_asset_assignments_onboarding_assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES onboarding_assets ("Id") ON DELETE CASCADE
);

CREATE TABLE onboarding_tasks (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "PlanId" uuid NOT NULL,
    "Title" text NOT NULL,
    "Category" text NOT NULL,
    "DueDate" date,
    "AssigneeEmployeeId" uuid,
    "Status" text NOT NULL,
    "Order" integer NOT NULL,
    "CompletedAt" timestamptz,
    CONSTRAINT "PK_onboarding_tasks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_onboarding_tasks_onboarding_plans_PlanId" FOREIGN KEY ("PlanId") REFERENCES onboarding_plans ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_onboarding_asset_assignments_AssetId" ON onboarding_asset_assignments ("AssetId");

CREATE INDEX "IX_onboarding_asset_assignments_EmployeeId" ON onboarding_asset_assignments ("EmployeeId");

CREATE INDEX "IX_onboarding_asset_assignments_TenantSlug" ON onboarding_asset_assignments ("TenantSlug");

CREATE UNIQUE INDEX "IX_onboarding_assets_TenantSlug_AssetTag" ON onboarding_assets ("TenantSlug", "AssetTag");

CREATE INDEX "IX_onboarding_assets_TenantSlug" ON onboarding_assets ("TenantSlug");

CREATE INDEX "IX_onboarding_plans_EmployeeId" ON onboarding_plans ("EmployeeId");

CREATE INDEX "IX_onboarding_plans_TenantSlug" ON onboarding_plans ("TenantSlug");

CREATE INDEX "IX_onboarding_tasks_PlanId" ON onboarding_tasks ("PlanId");

CREATE INDEX "IX_onboarding_tasks_TenantSlug" ON onboarding_tasks ("TenantSlug");

-- ============================================================
-- leave-service
-- ============================================================
CREATE TABLE leave_balances (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Year" integer NOT NULL,
    "Type" text NOT NULL,
    "EntitledDays" numeric NOT NULL,
    "UsedDays" numeric NOT NULL,
    "PendingDays" numeric NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_leave_balances" PRIMARY KEY ("Id")
);

CREATE TABLE leave_requests (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Type" text NOT NULL,
    "StartDate" date NOT NULL,
    "EndDate" date NOT NULL,
    "Days" numeric NOT NULL,
    "Reason" text,
    "Status" text NOT NULL,
    "WorkflowRequestId" uuid,
    "CreatedAt" timestamptz NOT NULL,
    "DecidedAt" timestamptz,
    CONSTRAINT "PK_leave_requests" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_leave_balances_EmployeeId_Year_Type" ON leave_balances ("EmployeeId", "Year", "Type");

CREATE INDEX "IX_leave_balances_TenantSlug" ON leave_balances ("TenantSlug");

CREATE INDEX "IX_leave_requests_EmployeeId" ON leave_requests ("EmployeeId");

CREATE INDEX "IX_leave_requests_Status" ON leave_requests ("Status");

CREATE INDEX "IX_leave_requests_TenantSlug" ON leave_requests ("TenantSlug");

-- ============================================================
-- timeshift-service
-- ============================================================
CREATE TABLE timeshift_shift_overrides (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Date" date NOT NULL,
    "Type" text NOT NULL,
    "Note" text,
    "StartTime" time without time zone,
    "EndTime" time without time zone,
    "IsSystemManaged" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_shift_overrides" PRIMARY KEY ("Id")
);

CREATE TABLE timeshift_shift_patterns (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_shift_patterns" PRIMARY KEY ("Id")
);

CREATE TABLE timeshift_shifts (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "StartTime" time without time zone NOT NULL,
    "EndTime" time without time zone NOT NULL,
    "BreakMinutes" integer NOT NULL,
    "DepartmentId" uuid,
    "IsNightShift" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_shifts" PRIMARY KEY ("Id")
);

CREATE TABLE timeshift_time_entries (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Date" date NOT NULL,
    "ClockIn" timestamptz,
    "ClockOut" timestamptz,
    "WorkedMinutes" integer NOT NULL,
    "OvertimeMinutes" integer NOT NULL,
    "Source" text NOT NULL,
    "Note" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_time_entries" PRIMARY KEY ("Id")
);

CREATE TABLE timeshift_shift_pattern_days (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ShiftPatternId" uuid NOT NULL,
    "DayIndex" integer NOT NULL,
    "Type" text NOT NULL,
    "StartTime" time without time zone,
    "EndTime" time without time zone,
    CONSTRAINT "PK_timeshift_shift_pattern_days" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_timeshift_shift_pattern_days_timeshift_shift_patterns_Shift~" FOREIGN KEY ("ShiftPatternId") REFERENCES timeshift_shift_patterns ("Id") ON DELETE CASCADE
);

CREATE TABLE timeshift_shift_teams (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "ShiftPatternId" uuid NOT NULL,
    "AnchorDate" date NOT NULL,
    "DepartmentId" uuid,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_shift_teams" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_timeshift_shift_teams_timeshift_shift_patterns_ShiftPattern~" FOREIGN KEY ("ShiftPatternId") REFERENCES timeshift_shift_patterns ("Id") ON DELETE RESTRICT
);

CREATE TABLE timeshift_assignments (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "ShiftId" uuid NOT NULL,
    "Date" date NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_assignments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_timeshift_assignments_timeshift_shifts_ShiftId" FOREIGN KEY ("ShiftId") REFERENCES timeshift_shifts ("Id") ON DELETE CASCADE
);

CREATE TABLE timeshift_shift_team_members (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ShiftTeamId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Rank" integer NOT NULL,
    "Tag" text,
    "EffectiveFrom" date NOT NULL,
    "EffectiveTo" date,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_timeshift_shift_team_members" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_timeshift_shift_team_members_timeshift_shift_teams_ShiftTea~" FOREIGN KEY ("ShiftTeamId") REFERENCES timeshift_shift_teams ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_timeshift_assignments_EmployeeId_Date" ON timeshift_assignments ("EmployeeId", "Date");

CREATE INDEX "IX_timeshift_assignments_ShiftId" ON timeshift_assignments ("ShiftId");

CREATE INDEX "IX_timeshift_assignments_TenantSlug" ON timeshift_assignments ("TenantSlug");

CREATE UNIQUE INDEX "IX_timeshift_shift_overrides_EmployeeId_Date" ON timeshift_shift_overrides ("EmployeeId", "Date");

CREATE INDEX "IX_timeshift_shift_overrides_TenantSlug" ON timeshift_shift_overrides ("TenantSlug");

CREATE UNIQUE INDEX "IX_timeshift_shift_pattern_days_ShiftPatternId_DayIndex" ON timeshift_shift_pattern_days ("ShiftPatternId", "DayIndex");

CREATE INDEX "IX_timeshift_shift_pattern_days_TenantSlug" ON timeshift_shift_pattern_days ("TenantSlug");

CREATE INDEX "IX_timeshift_shift_patterns_TenantSlug" ON timeshift_shift_patterns ("TenantSlug");

CREATE INDEX "IX_timeshift_shift_team_members_EmployeeId_EffectiveFrom" ON timeshift_shift_team_members ("EmployeeId", "EffectiveFrom");

CREATE INDEX "IX_timeshift_shift_team_members_ShiftTeamId" ON timeshift_shift_team_members ("ShiftTeamId");

CREATE INDEX "IX_timeshift_shift_team_members_TenantSlug" ON timeshift_shift_team_members ("TenantSlug");

CREATE INDEX "IX_timeshift_shift_teams_ShiftPatternId" ON timeshift_shift_teams ("ShiftPatternId");

CREATE INDEX "IX_timeshift_shift_teams_TenantSlug" ON timeshift_shift_teams ("TenantSlug");

CREATE INDEX "IX_timeshift_shifts_TenantSlug" ON timeshift_shifts ("TenantSlug");

CREATE UNIQUE INDEX "IX_timeshift_time_entries_EmployeeId_Date" ON timeshift_time_entries ("EmployeeId", "Date");

CREATE INDEX "IX_timeshift_time_entries_TenantSlug" ON timeshift_time_entries ("TenantSlug");

-- ============================================================
-- performance-service
-- ============================================================
CREATE TABLE performance_cycles (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Year" integer NOT NULL,
    "Period" text NOT NULL,
    "StartDate" date NOT NULL,
    "EndDate" date NOT NULL,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_performance_cycles" PRIMARY KEY ("Id")
);

CREATE TABLE performance_feedback (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "FromEmployeeId" uuid NOT NULL,
    "ToEmployeeId" uuid NOT NULL,
    "CycleId" uuid,
    "MetricId" uuid,
    "Reason" text NOT NULL,
    "ReasonDetail" text,
    "Sentiment" text NOT NULL,
    "Body" text NOT NULL,
    "VisibleToEmployee" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "ReadAt" timestamptz,
    CONSTRAINT "PK_performance_feedback" PRIMARY KEY ("Id")
);

CREATE TABLE performance_metrics (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Code" text NOT NULL,
    "Name" text NOT NULL,
    "Description" text,
    "Category" text NOT NULL,
    "Scale" text NOT NULL,
    "Weight" numeric NOT NULL,
    "DepartmentId" uuid,
    "IsRequired" boolean NOT NULL,
    "IsActive" boolean NOT NULL,
    "SortOrder" integer NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "ArchivedAt" timestamptz,
    CONSTRAINT "PK_performance_metrics" PRIMARY KEY ("Id")
);

CREATE TABLE performance_reviews (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "CycleId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "ReviewerEmployeeId" uuid NOT NULL,
    "Type" text NOT NULL,
    "Strengths" text,
    "Improvements" text,
    "Comments" text,
    "SubmittedAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_performance_reviews" PRIMARY KEY ("Id")
);

CREATE TABLE performance_scoring_config (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Version" integer NOT NULL,
    "IsActive" boolean NOT NULL,
    "EffectiveFrom" timestamptz NOT NULL,
    "GoalWeightPercent" integer NOT NULL,
    "MetricWeightPercent" integer NOT NULL,
    "TechnicalWeight" numeric NOT NULL,
    "BehavioralWeight" numeric NOT NULL,
    "LeadershipWeight" numeric NOT NULL,
    "DeliveryWeight" numeric NOT NULL,
    "CustomWeight" numeric NOT NULL,
    "SelfReviewWeight" numeric NOT NULL,
    "ManagerReviewWeight" numeric NOT NULL,
    "TeamLeadReviewWeight" numeric NOT NULL,
    "PeerReviewWeight" numeric NOT NULL,
    "UpwardReviewWeight" numeric NOT NULL,
    "MinReviewsForValidScore" integer NOT NULL,
    "AllowSelfOnlyScore" boolean NOT NULL,
    "PromotionThreshold" numeric NOT NULL,
    "RecognitionThreshold" numeric NOT NULL,
    "ImprovementThreshold" numeric NOT NULL,
    "CriticalThreshold" numeric NOT NULL,
    "PromotionConsecutivePeriods" integer NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "CreatedByEmployeeId" text,
    CONSTRAINT "PK_performance_scoring_config" PRIMARY KEY ("Id")
);

CREATE TABLE performance_snapshots (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "CycleId" uuid,
    "TeamId" uuid,
    "DepartmentId" uuid,
    "Score" numeric NOT NULL,
    "GoalScore" numeric NOT NULL,
    "MetricScore" numeric NOT NULL,
    "IsProvisional" boolean NOT NULL,
    "ProvisionalReason" text,
    "ReviewCount" integer NOT NULL,
    "ConfigVersion" integer NOT NULL,
    "CategoryBreakdownJson" text,
    "Source" text NOT NULL,
    "CapturedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_performance_snapshots" PRIMARY KEY ("Id")
);

CREATE TABLE performance_goals (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "CycleId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Title" text NOT NULL,
    "Description" text,
    "Weight" integer NOT NULL,
    "TargetValue" numeric,
    "CurrentValue" numeric,
    "Unit" text,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_performance_goals" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_performance_goals_performance_cycles_CycleId" FOREIGN KEY ("CycleId") REFERENCES performance_cycles ("Id") ON DELETE CASCADE
);

CREATE TABLE performance_review_scores (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ReviewId" uuid NOT NULL,
    "MetricId" uuid NOT NULL,
    "Value" numeric NOT NULL,
    "Comment" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_performance_review_scores" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_performance_review_scores_performance_metrics_MetricId" FOREIGN KEY ("MetricId") REFERENCES performance_metrics ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_performance_review_scores_performance_reviews_ReviewId" FOREIGN KEY ("ReviewId") REFERENCES performance_reviews ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_performance_cycles_TenantSlug" ON performance_cycles ("TenantSlug");

CREATE INDEX "IX_performance_feedback_FromEmployeeId" ON performance_feedback ("FromEmployeeId");

CREATE INDEX "IX_performance_feedback_TenantSlug" ON performance_feedback ("TenantSlug");

CREATE INDEX "IX_performance_feedback_ToEmployeeId" ON performance_feedback ("ToEmployeeId");

CREATE INDEX "IX_performance_goals_CycleId" ON performance_goals ("CycleId");

CREATE INDEX "IX_performance_goals_EmployeeId" ON performance_goals ("EmployeeId");

CREATE INDEX "IX_performance_goals_TenantSlug" ON performance_goals ("TenantSlug");

CREATE INDEX "IX_performance_metrics_TenantSlug" ON performance_metrics ("TenantSlug");

CREATE UNIQUE INDEX "IX_performance_metrics_TenantSlug_Code" ON performance_metrics ("TenantSlug", "Code");

CREATE INDEX "IX_performance_review_scores_MetricId" ON performance_review_scores ("MetricId");

CREATE UNIQUE INDEX "IX_performance_review_scores_ReviewId_MetricId" ON performance_review_scores ("ReviewId", "MetricId");

CREATE INDEX "IX_performance_review_scores_TenantSlug" ON performance_review_scores ("TenantSlug");

CREATE INDEX "IX_performance_reviews_CycleId_EmployeeId" ON performance_reviews ("CycleId", "EmployeeId");

CREATE INDEX "IX_performance_reviews_TenantSlug" ON performance_reviews ("TenantSlug");

CREATE INDEX "IX_performance_scoring_config_TenantSlug" ON performance_scoring_config ("TenantSlug");

CREATE UNIQUE INDEX "IX_performance_scoring_config_TenantSlug_Version" ON performance_scoring_config ("TenantSlug", "Version");

CREATE INDEX "IX_performance_snapshots_EmployeeId_CapturedAt" ON performance_snapshots ("EmployeeId", "CapturedAt");

CREATE INDEX "IX_performance_snapshots_TeamId_CapturedAt" ON performance_snapshots ("TeamId", "CapturedAt");

CREATE INDEX "IX_performance_snapshots_TenantSlug" ON performance_snapshots ("TenantSlug");

-- ============================================================
-- learning-service
-- ============================================================
CREATE TABLE learning_certifications (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Name" text NOT NULL,
    "Issuer" text,
    "CredentialId" text,
    "IssuedOn" date NOT NULL,
    "ExpiresOn" date,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_learning_certifications" PRIMARY KEY ("Id")
);

CREATE TABLE learning_courses (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Description" text,
    "Provider" text,
    "DurationHours" numeric NOT NULL,
    "Category" text NOT NULL,
    "IsMandatory" boolean NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_learning_courses" PRIMARY KEY ("Id")
);

CREATE TABLE learning_enrollments (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "CourseId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Status" text NOT NULL,
    "Score" numeric,
    "EnrolledAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz,
    CONSTRAINT "PK_learning_enrollments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_learning_enrollments_learning_courses_CourseId" FOREIGN KEY ("CourseId") REFERENCES learning_courses ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_learning_certifications_EmployeeId" ON learning_certifications ("EmployeeId");

CREATE INDEX "IX_learning_certifications_TenantSlug" ON learning_certifications ("TenantSlug");

CREATE INDEX "IX_learning_courses_TenantSlug" ON learning_courses ("TenantSlug");

CREATE UNIQUE INDEX "IX_learning_enrollments_CourseId_EmployeeId" ON learning_enrollments ("CourseId", "EmployeeId");

CREATE INDEX "IX_learning_enrollments_TenantSlug" ON learning_enrollments ("TenantSlug");

-- ============================================================
-- compensation-service
-- ============================================================
CREATE TABLE compensation_records (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "BaseSalary" numeric NOT NULL,
    "Currency" text NOT NULL,
    "Grade" text,
    "Reason" text NOT NULL,
    "EffectiveFrom" date NOT NULL,
    "EffectiveTo" date,
    "Note" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_compensation_records" PRIMARY KEY ("Id")
);

CREATE TABLE compensation_salary_bands (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Grade" text NOT NULL,
    "Title" text,
    "MinAmount" numeric NOT NULL,
    "MidAmount" numeric NOT NULL,
    "MaxAmount" numeric NOT NULL,
    "Currency" text NOT NULL,
    "Year" integer NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_compensation_salary_bands" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_compensation_records_EmployeeId" ON compensation_records ("EmployeeId");

CREATE INDEX "IX_compensation_records_TenantSlug" ON compensation_records ("TenantSlug");

CREATE UNIQUE INDEX "IX_compensation_salary_bands_TenantSlug_Grade_Year" ON compensation_salary_bands ("TenantSlug", "Grade", "Year");

CREATE INDEX "IX_compensation_salary_bands_TenantSlug" ON compensation_salary_bands ("TenantSlug");

-- ============================================================
-- expense-service
-- ============================================================
CREATE TABLE expense_claims (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Title" text NOT NULL,
    "Currency" text NOT NULL,
    "TotalAmount" numeric NOT NULL,
    "Status" text NOT NULL,
    "WorkflowRequestId" uuid,
    "ApprovedByEmployeeId" uuid,
    "CreatedAt" timestamptz NOT NULL,
    "SubmittedAt" timestamptz,
    "PaidAt" timestamptz,
    CONSTRAINT "PK_expense_claims" PRIMARY KEY ("Id")
);

CREATE TABLE expense_documents (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Type" text NOT NULL,
    "FileName" text NOT NULL,
    "StorageKey" text NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "ContentType" text,
    "UploadedAt" timestamptz NOT NULL,
    "UploadedByEmployeeId" uuid,
    CONSTRAINT "PK_expense_documents" PRIMARY KEY ("Id")
);

CREATE TABLE expense_hr_cases (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Subject" text NOT NULL,
    "Description" text,
    "Category" text NOT NULL,
    "Priority" text NOT NULL,
    "Status" text NOT NULL,
    "AssignedToEmployeeId" uuid,
    "Resolution" text,
    "CreatedAt" timestamptz NOT NULL,
    "ResolvedAt" timestamptz,
    CONSTRAINT "PK_expense_hr_cases" PRIMARY KEY ("Id")
);

CREATE TABLE expense_items (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ClaimId" uuid NOT NULL,
    "Category" text NOT NULL,
    "Amount" numeric NOT NULL,
    "ExpenseDate" date NOT NULL,
    "Description" text,
    "ReceiptStorageKey" text,
    CONSTRAINT "PK_expense_items" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_expense_items_expense_claims_ClaimId" FOREIGN KEY ("ClaimId") REFERENCES expense_claims ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_expense_claims_EmployeeId" ON expense_claims ("EmployeeId");

CREATE INDEX "IX_expense_claims_TenantSlug" ON expense_claims ("TenantSlug");

CREATE INDEX "IX_expense_documents_EmployeeId" ON expense_documents ("EmployeeId");

CREATE INDEX "IX_expense_documents_TenantSlug" ON expense_documents ("TenantSlug");

CREATE INDEX "IX_expense_hr_cases_Status" ON expense_hr_cases ("Status");

CREATE INDEX "IX_expense_hr_cases_TenantSlug" ON expense_hr_cases ("TenantSlug");

CREATE INDEX "IX_expense_items_ClaimId" ON expense_items ("ClaimId");

CREATE INDEX "IX_expense_items_TenantSlug" ON expense_items ("TenantSlug");

-- ============================================================
-- notification-service
-- ============================================================
CREATE TABLE notification_messages (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "RecipientEmployeeId" uuid NOT NULL,
    "RecipientEmail" text,
    "Channel" text NOT NULL,
    "TemplateCode" text,
    "Subject" text,
    "Body" text NOT NULL,
    "Status" text NOT NULL,
    "FailureReason" text,
    "AttemptCount" integer NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "SentAt" timestamptz,
    "ReadAt" timestamptz,
    CONSTRAINT "PK_notification_messages" PRIMARY KEY ("Id")
);

CREATE TABLE notification_templates (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Code" text NOT NULL,
    "Channel" text NOT NULL,
    "Locale" text NOT NULL,
    "SubjectTemplate" text,
    "BodyTemplate" text NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_notification_templates" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_notification_messages_RecipientEmployeeId_Status" ON notification_messages ("RecipientEmployeeId", "Status");

CREATE INDEX "IX_notification_messages_TenantSlug" ON notification_messages ("TenantSlug");

CREATE UNIQUE INDEX "IX_notification_templates_TenantSlug_Code_Channel_Locale" ON notification_templates ("TenantSlug", "Code", "Channel", "Locale");

CREATE INDEX "IX_notification_templates_TenantSlug" ON notification_templates ("TenantSlug");

-- --- paylasilan 'islenmis event' (Kafka tuketici idempotency) tablosu ---
-- expense-service, leave-service, notification-service ve timeshift-service'in
-- HEPSI ayni "messaging_processed_events" tablosunu kullanir (bkz. her birinin
-- Messaging/ProcessedEvent.cs dosyasindaki aciklama, ozellikle expense-service'teki
-- "TUM tuketici servisler arasinda paylasilan ORTAK bir tablodur" notu) - ama bu
-- tablo bu dosyada HICBIR ZAMAN yoktu, yani at-least-once Kafka teslimatinda
-- olay tekilleştirme (deduplication) hicbir zaman calismiyordu.
CREATE TABLE messaging_processed_events (
    "EventId" uuid NOT NULL,
    "Consumer" text NOT NULL,
    "EventType" text NOT NULL,
    "ProcessedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_messaging_processed_events" PRIMARY KEY ("EventId", "Consumer")
);

-- ============================================================
-- organization-service, employee-service, workflow-service,
-- tenant-service ve paylasilan outbox tablosu
--
-- NOT (hardcore test bulgusu): asagidaki 5 blok daha once bu dosyada HIC
-- yoktu. organization-service ve employee-service Database.EnsureCreated()
-- ile "kendi" tablolarini kendileri olusturuyor sanildi, ama EnsureCreated()
-- PAYLASILAN bir veritabaninda "herhangi bir tablo var mi" diye bakar - bu
-- dosyadaki diger servislerin tablolari zaten var oldugu icin EnsureCreated()
-- hicbir sey yapmadan sessizce cikiyor ve bu iki servisin KENDI tablolari
-- HICBIR ZAMAN olusmuyordu. workflow-service'in ise ne bir schema.sql'i ne
-- de EnsureCreated/Migrate cagrisi vardi - tablolari asla olusmuyordu.
-- tenant-service'in kendi sql/schema.sql'i vardi ama bu birlesik dosyaya
-- hic eklenmemisti (ayrica o dosya LogoUrl/SMTP alanlari eklendikten sonra
-- guncellenmemis, eskimisti). Asagidaki tablolar ilgili servislerin GUNCEL
-- EF Core modellerinden ("dotnet ef migrations script") birebir uretildi.
-- ============================================================

-- --- organization-service ---
CREATE TABLE organization_companies (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "TaxNumber" text,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_organization_companies_TenantSlug" ON organization_companies ("TenantSlug");

CREATE TABLE organization_departments (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "CompanyId" uuid NOT NULL REFERENCES organization_companies ("Id") ON DELETE CASCADE,
    "ParentDepartmentId" uuid,
    "HeadEmployeeId" uuid,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_organization_departments_CompanyId" ON organization_departments ("CompanyId");
CREATE INDEX "IX_organization_departments_TenantSlug" ON organization_departments ("TenantSlug");

CREATE TABLE organization_teams (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Description" text,
    "DepartmentId" uuid NOT NULL REFERENCES organization_departments ("Id") ON DELETE CASCADE,
    "LeadEmployeeId" uuid,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_organization_teams_DepartmentId" ON organization_teams ("DepartmentId");
CREATE INDEX "IX_organization_teams_TenantSlug" ON organization_teams ("TenantSlug");

CREATE TABLE organization_team_members (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "TeamId" uuid NOT NULL REFERENCES organization_teams ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    "RoleInTeam" text,
    "JoinedOn" date NOT NULL,
    "LeftOn" date,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_organization_team_members_EmployeeId" ON organization_team_members ("EmployeeId");
CREATE INDEX "IX_organization_team_members_TeamId" ON organization_team_members ("TeamId");
CREATE INDEX "IX_organization_team_members_TenantSlug" ON organization_team_members ("TenantSlug");

-- --- employee-service ---
CREATE TABLE employee_employees (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "FirstName" text NOT NULL,
    "LastName" text NOT NULL,
    "Email" text NOT NULL,
    "Phone" text,
    "KeycloakUserId" text,
    "HireDate" date NOT NULL,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_employee_employees_TenantSlug_Email" ON employee_employees ("TenantSlug", lower("Email"));
CREATE INDEX "IX_employee_employees_TenantSlug" ON employee_employees ("TenantSlug");

CREATE TABLE employee_assignments (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL REFERENCES employee_employees ("Id") ON DELETE CASCADE,
    "DepartmentId" uuid NOT NULL,
    "PositionTitle" text,
    "EffectiveFrom" date NOT NULL,
    "EffectiveTo" date,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_employee_assignments_EmployeeId" ON employee_assignments ("EmployeeId");
CREATE INDEX "IX_employee_assignments_TenantSlug" ON employee_assignments ("TenantSlug");

-- --- workflow-service ---
CREATE TABLE workflow_requests (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Type" text NOT NULL,
    "Status" text NOT NULL,
    "RequesterEmployeeId" uuid NOT NULL,
    "Subject" text,
    "Payload" text,
    "SlaDueAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz
);
CREATE INDEX "IX_workflow_requests_TenantSlug" ON workflow_requests ("TenantSlug");

CREATE TABLE workflow_approval_steps (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "WorkflowRequestId" uuid NOT NULL REFERENCES workflow_requests ("Id") ON DELETE CASCADE,
    "Order" integer NOT NULL,
    "ApproverEmployeeId" uuid NOT NULL,
    "DelegatedToEmployeeId" uuid,
    "Decision" text NOT NULL,
    "Comment" text,
    "DecidedAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_workflow_approval_steps_TenantSlug" ON workflow_approval_steps ("TenantSlug");
CREATE INDEX "IX_workflow_approval_steps_WorkflowRequestId" ON workflow_approval_steps ("WorkflowRequestId");

-- --- tenant-service ---
CREATE TABLE platform_tenants (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Name" text NOT NULL,
    "Slug" text NOT NULL,
    "EmailDomain" text,
    "TaxNumber" text,
    "Status" text NOT NULL,
    "Plan" text NOT NULL,
    "KeycloakOrgId" text,
    "AdminUserId" text,
    "AdminEmail" text NOT NULL,
    "AdminFullName" text,
    "MaxEmployees" integer NOT NULL DEFAULT 25,
    "LogoUrl" text,
    "PrimaryColorHex" text,
    "SmtpHost" text,
    "SmtpPort" integer,
    "SmtpUser" text,
    "SmtpPasswordEncrypted" text,
    "SmtpFromAddress" text,
    "SmtpFromName" text,
    "CreatedAt" timestamptz NOT NULL,
    "ActivatedAt" timestamptz,
    "SuspendedAt" timestamptz,
    "SuspendReason" text,
    "SuspendedUserIdsJson" text
);
CREATE UNIQUE INDEX "IX_platform_tenants_Slug" ON platform_tenants ("Slug");
CREATE INDEX "IX_platform_tenants_AdminEmail" ON platform_tenants ("AdminEmail");

CREATE TABLE platform_tenant_provisioning_log (
    "Id" uuid NOT NULL PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "Step" text NOT NULL,
    "Success" boolean NOT NULL,
    "Detail" text,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX "IX_platform_tenant_provisioning_log_TenantId" ON platform_tenant_provisioning_log ("TenantId");

-- --- paylasilan transactional outbox tablosu ---
-- employee-service, workflow-service, expense-service ve leave-service'in
-- HEPSI ayni "messaging_outbox" tablosunu kullanir (bkz. her birinin
-- Messaging/OutboxMessage.cs dosyasindaki aciklama) - yeni bir olay
-- yayinlama mekanizmasi eklendiginde tablo TEKRAR olusturulmamali, bu
-- tanim tek ve paylasilan olmalidir.
CREATE TABLE messaging_outbox (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Topic" text NOT NULL,
    "EventType" text NOT NULL,
    "PartitionKey" text NOT NULL,
    "Payload" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "PublishedAt" timestamptz,
    "AttemptCount" integer NOT NULL,
    "LastError" text
);
CREATE INDEX "IX_messaging_outbox_PublishedAt_CreatedAt" ON messaging_outbox ("PublishedAt", "CreatedAt");

-- CTO denetimi (2026-09-25): ek butunluk kisitlari
CREATE UNIQUE INDEX "IX_employee_employees_KeycloakUserId" ON employee_employees ("KeycloakUserId") WHERE "KeycloakUserId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_onboarding_asset_assignments_open" ON onboarding_asset_assignments ("AssetId") WHERE "ReturnedOn" IS NULL;

-- Resmi tatil takvimi (leave-service): izin gunu hesabinda dusulur.
CREATE TABLE IF NOT EXISTS leave_public_holidays (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Date" date NOT NULL,
    "Name" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_leave_public_holidays" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_leave_public_holidays_TenantSlug_Date"
    ON leave_public_holidays ("TenantSlug", "Date");

-- ==== 2026-10-02: scripts/sql/2026-10-02_engagement_governance.sql ile ayni (ilk kurulum icin) ====
-- =============================================================================
-- 2026-10-02 — Çalışan deneyimi (engagement-service), yönetişim
-- (governance-service) ve denetim kaydı (tüm servisler) tabloları.
--
-- Bu dosya IDEMPOTENT'tir: hem install.sh güncellemesinde (scripts/sql/*.sql)
-- hem de ilk kurulumda (data/migrations/sql-all-schemas.sql sonuna eklenmiş
-- kopyası) güvenle çalışır.
-- =============================================================================

-- ---------------------------------------------------------------- denetim kaydı
-- Her servisin EF SaveChanges kesicisi (Auditing/AuditInterceptor.cs) değişen
-- her varlık için bir satır yazar: eski/yeni değer, kullanıcı, zaman, istek
-- kimliği. Hassas alanlar (maaş, IBAN, TCKN, parola…) "***" olarak maskelenir.
CREATE TABLE IF NOT EXISTS audit_log (
    "Id" bigserial PRIMARY KEY,
    "TenantSlug" character varying(64),
    "Service" text NOT NULL,
    "EntityType" text NOT NULL,
    "EntityId" text,
    "Action" text NOT NULL,
    "Changes" jsonb,
    "UserId" text,
    "UserName" text,
    "CorrelationId" text,
    "IpAddress" text,
    "OccurredAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_audit_log_Tenant_Occurred" ON audit_log ("TenantSlug", "OccurredAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_audit_log_Entity" ON audit_log ("EntityType", "EntityId");
CREATE INDEX IF NOT EXISTS "IX_audit_log_Correlation" ON audit_log ("CorrelationId");

-- ------------------------------------------------------------- engagement-service
CREATE TABLE IF NOT EXISTS engagement_kudos (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "FromUserId" text NOT NULL,
    "FromEmployeeId" uuid,
    "FromName" text NOT NULL,
    "ToEmployeeId" uuid NOT NULL,
    "ToName" text NOT NULL,
    "Badge" text NOT NULL,
    "Message" text NOT NULL,
    "LikedBy" text[] NOT NULL DEFAULT '{}',
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_engagement_kudos_TenantSlug" ON engagement_kudos ("TenantSlug", "CreatedAt" DESC);

CREATE TABLE IF NOT EXISTS engagement_profiles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "BirthDate" date,
    "ShowBirthday" boolean NOT NULL DEFAULT true,
    "Bio" text,
    "Pronouns" text,
    "Skills" text[] NOT NULL DEFAULT '{}',
    "Interests" text[] NOT NULL DEFAULT '{}',
    "Address" text,
    "EmergencyContactName" text,
    "EmergencyContactPhone" text,
    "Iban" text,
    "NationalId" text,
    "LinkedInUrl" text,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_profiles_Employee" ON engagement_profiles ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS engagement_desks (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Code" text NOT NULL,
    "Name" text NOT NULL,
    "Kind" text NOT NULL,
    "Floor" text,
    "Zone" text,
    "Capacity" integer NOT NULL DEFAULT 1,
    "Features" text[] NOT NULL DEFAULT '{}',
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_desks_Code" ON engagement_desks ("TenantSlug", "Code");

CREATE TABLE IF NOT EXISTS engagement_desk_bookings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "DeskId" uuid NOT NULL REFERENCES engagement_desks("Id") ON DELETE CASCADE,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Date" date NOT NULL,
    "StartMinute" integer NOT NULL,
    "EndMinute" integer NOT NULL,
    "Title" text,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_engagement_desk_bookings_Date" ON engagement_desk_bookings ("TenantSlug", "Date");

CREATE TABLE IF NOT EXISTS engagement_presence (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Date" date NOT NULL,
    "Mode" text NOT NULL,
    "Note" text,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_presence_User_Date" ON engagement_presence ("TenantSlug", "UserId", "Date");

CREATE TABLE IF NOT EXISTS engagement_mentor_profiles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "IsMentor" boolean NOT NULL,
    "IsMentee" boolean NOT NULL,
    "Offers" text[] NOT NULL DEFAULT '{}',
    "Wants" text[] NOT NULL DEFAULT '{}',
    "Capacity" integer NOT NULL DEFAULT 2,
    "Bio" text,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_mentor_profiles_User" ON engagement_mentor_profiles ("TenantSlug", "UserId");

CREATE TABLE IF NOT EXISTS engagement_mentorships (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "MentorUserId" text NOT NULL,
    "MentorName" text NOT NULL,
    "MenteeUserId" text NOT NULL,
    "MenteeName" text NOT NULL,
    "Goal" text,
    "Status" text NOT NULL,
    "MatchScore" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL,
    "StartedAt" timestamptz,
    "EndedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_engagement_mentorships_TenantSlug" ON engagement_mentorships ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_internal_applications (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "JobPostingId" uuid NOT NULL,
    "JobTitle" text NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Motivation" text,
    "Status" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_internal_app" ON engagement_internal_applications ("TenantSlug", "JobPostingId", "UserId");

CREATE TABLE IF NOT EXISTS engagement_one_on_ones (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "ManagerUserId" text NOT NULL,
    "ManagerName" text NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "EmployeeUserId" text,
    "EmployeeName" text NOT NULL,
    "ScheduledAt" timestamptz NOT NULL,
    "Status" text NOT NULL,
    "Agenda" jsonb NOT NULL DEFAULT '[]',
    "SharedNotes" text,
    "PrivateNotes" text,
    "ActionItems" jsonb NOT NULL DEFAULT '[]',
    "Mood" integer,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_engagement_one_on_ones_Tenant" ON engagement_one_on_ones ("TenantSlug", "ScheduledAt");

CREATE TABLE IF NOT EXISTS engagement_succession_plans (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PositionTitle" text NOT NULL,
    "DepartmentName" text,
    "IncumbentEmployeeId" uuid,
    "IncumbentName" text,
    "Criticality" text NOT NULL,
    "VacancyRisk" text NOT NULL,
    "Candidates" jsonb NOT NULL DEFAULT '[]',
    "Notes" text,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS engagement_surveys (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Description" text,
    "Kind" text NOT NULL,
    "Questions" jsonb NOT NULL DEFAULT '[]',
    "IsAnonymous" boolean NOT NULL DEFAULT true,
    "Status" text NOT NULL,
    "ClosesAt" timestamptz,
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS engagement_survey_responses (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "SurveyId" uuid NOT NULL REFERENCES engagement_surveys("Id") ON DELETE CASCADE,
    "RespondentKey" text NOT NULL,
    "DepartmentName" text,
    "Answers" jsonb NOT NULL,
    "SubmittedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_survey_resp" ON engagement_survey_responses ("SurveyId", "RespondentKey");

CREATE TABLE IF NOT EXISTS engagement_offboarding_cases (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "EmployeeName" text NOT NULL,
    "LastWorkingDay" date NOT NULL,
    "Reason" text NOT NULL,
    "Status" text NOT NULL,
    "Checklist" jsonb NOT NULL DEFAULT '[]',
    "ExitInterview" jsonb,
    "RehireEligible" boolean,
    "CreatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz
);

CREATE TABLE IF NOT EXISTS engagement_org_scenarios (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Description" text,
    "Moves" jsonb NOT NULL DEFAULT '[]',
    "Status" text NOT NULL,
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

-- ------------------------------------------------------------- governance-service
CREATE TABLE IF NOT EXISTS governance_events (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64),
    "Topic" text NOT NULL,
    "EventType" text NOT NULL,
    "Payload" jsonb,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_events_Tenant" ON governance_events ("TenantSlug", "OccurredAt" DESC);

CREATE TABLE IF NOT EXISTS governance_consents (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "ConsentType" text NOT NULL,
    "Version" text NOT NULL,
    "Granted" boolean NOT NULL,
    "IpAddress" text,
    "RecordedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_consents_User" ON governance_consents ("TenantSlug", "UserId");

CREATE TABLE IF NOT EXISTS governance_data_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL,
    "Kind" text NOT NULL,
    "Details" text,
    "Status" text NOT NULL,
    "Response" text,
    "DueAt" timestamptz NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz
);

CREATE TABLE IF NOT EXISTS governance_retention_policies (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Category" text NOT NULL,
    "RetentionMonths" integer NOT NULL,
    "Action" text NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "LastRunAt" timestamptz,
    "LastAffected" integer NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_retention" ON governance_retention_policies ("TenantSlug", "Category");

CREATE TABLE IF NOT EXISTS governance_doc_templates (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Category" text NOT NULL,
    "Body" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_rules (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Description" text,
    "Trigger" text NOT NULL,
    "Conditions" jsonb NOT NULL DEFAULT '[]',
    "Actions" jsonb NOT NULL DEFAULT '[]',
    "IsEnabled" boolean NOT NULL,
    "FireCount" integer NOT NULL DEFAULT 0,
    "LastFiredAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_rule_runs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "RuleId" uuid NOT NULL,
    "RuleName" text NOT NULL,
    "EventType" text NOT NULL,
    "Result" text NOT NULL,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_rule_runs" ON governance_rule_runs ("TenantSlug", "OccurredAt" DESC);

CREATE TABLE IF NOT EXISTS governance_webhooks (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Url" text NOT NULL,
    "Secret" text NOT NULL,
    "Events" text[] NOT NULL DEFAULT '{}',
    "IsEnabled" boolean NOT NULL,
    "LastStatus" integer,
    "LastDeliveredAt" timestamptz,
    "FailureCount" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_webhook_deliveries (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "WebhookId" uuid NOT NULL,
    "EventType" text NOT NULL,
    "StatusCode" integer,
    "Error" text,
    "DurationMs" integer NOT NULL,
    "OccurredAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_webhook_deliveries" ON governance_webhook_deliveries ("WebhookId", "OccurredAt" DESC);

CREATE TABLE IF NOT EXISTS governance_api_keys (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Prefix" text NOT NULL,
    "KeyHash" text NOT NULL,
    "Scopes" text[] NOT NULL DEFAULT '{}',
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL,
    "LastUsedAt" timestamptz,
    "RevokedAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_api_keys_Hash" ON governance_api_keys ("KeyHash");

CREATE TABLE IF NOT EXISTS governance_integrations (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Kind" text NOT NULL,
    "Name" text NOT NULL,
    "WebhookUrl" text NOT NULL,
    "Events" text[] NOT NULL DEFAULT '{}',
    "SigningSecret" text,
    "IsEnabled" boolean NOT NULL,
    "LastStatus" integer,
    "CreatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_invoices (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Number" text NOT NULL,
    "Period" text NOT NULL,
    "Plan" text NOT NULL,
    "Seats" integer NOT NULL,
    "UnitPrice" numeric(12,2) NOT NULL,
    "Amount" numeric(12,2) NOT NULL,
    "TaxAmount" numeric(12,2) NOT NULL,
    "Total" numeric(12,2) NOT NULL,
    "Currency" text NOT NULL,
    "Status" text NOT NULL,
    "IssuedAt" timestamptz NOT NULL,
    "DueAt" timestamptz NOT NULL,
    "PaidAt" timestamptz,
    "PaymentRef" text
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_invoices_Period" ON governance_invoices ("TenantSlug", "Period");

CREATE TABLE IF NOT EXISTS governance_calendar_feeds (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "Token" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_calendar_feeds_Token" ON governance_calendar_feeds ("Token");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_calendar_feeds_User" ON governance_calendar_feeds ("TenantSlug", "UserId");

CREATE TABLE IF NOT EXISTS governance_kb_articles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Body" text NOT NULL,
    "Tags" text[] NOT NULL DEFAULT '{}',
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);

-- ------------------------------------------------- analitik (hafif veri ambarı)
-- Operasyonel tabloların üstünde, raporlama için sabitlenmiş görünümler. BI
-- aracı (Metabase/Power BI) bağlanacaksa bu görünümler okunur; operasyonel
-- şemadaki değişiklik raporları kırmaz.
CREATE OR REPLACE VIEW analytics_hires_monthly AS
SELECT "TenantSlug" AS tenant_slug,
       date_trunc('month', "HireDate")::date AS month,
       count(*) AS hires
FROM employee_employees
GROUP BY 1, 2;

CREATE OR REPLACE VIEW analytics_leave_monthly AS
SELECT "TenantSlug" AS tenant_slug,
       date_trunc('month', "StartDate")::date AS month,
       "Type" AS leave_type,
       "Status" AS status,
       count(*) AS requests,
       sum("Days") AS days
FROM leave_requests
GROUP BY 1, 2, 3, 4;

CREATE OR REPLACE VIEW analytics_department_headcount AS
SELECT a."TenantSlug" AS tenant_slug,
       coalesce(d."Name", 'Atanmamış') AS department,
       count(DISTINCT a."EmployeeId") AS headcount
FROM employee_assignments a
JOIN employee_employees e ON e."Id" = a."EmployeeId" AND e."Status" <> 'Terminated'
LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
WHERE a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
GROUP BY 1, 2;

CREATE OR REPLACE VIEW analytics_overtime_monthly AS
SELECT "TenantSlug" AS tenant_slug,
       date_trunc('month', "Date")::date AS month,
       sum("WorkedMinutes") AS worked_minutes,
       sum("OvertimeMinutes") AS overtime_minutes
FROM timeshift_time_entries
GROUP BY 1, 2;

-- ==== 2026-10-03: scripts/sql/2026-10-03_chat_apps.sql ile ayni (ilk kurulum icin) ====
-- Slack uygulamasi / Microsoft Teams botu: kisiye ozel bildirim, onay dugmeleri, komutlar.
-- Idempotent; install.sh guncellemede otomatik uygular.

CREATE TABLE IF NOT EXISTS governance_chat_apps (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Platform" text NOT NULL,
    "Name" text NOT NULL,
    "IsEnabled" boolean NOT NULL DEFAULT true,
    "NotifyApprovals" boolean NOT NULL DEFAULT true,
    "NotifyRequesters" boolean NOT NULL DEFAULT true,
    "SlackTeamId" text,
    "SlackTeamName" text,
    "SlackBotUserId" text,
    "SlackBotTokenEnc" text,
    "SlackSigningSecretEnc" text,
    "TeamsAppId" text,
    "TeamsAppPasswordEnc" text,
    "TeamsAzureTenantId" text,
    "LastError" text,
    "LastActivityAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_governance_chat_apps_tenant ON governance_chat_apps ("TenantSlug");

CREATE TABLE IF NOT EXISTS governance_chat_identities (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL REFERENCES governance_chat_apps ("Id") ON DELETE CASCADE,
    "Platform" text NOT NULL,
    "ExternalUserId" text NOT NULL,
    "EmployeeId" uuid,
    "Email" text,
    "DisplayName" text,
    "ConversationId" text,
    "ServiceUrl" text,
    "LinkedAt" timestamptz NOT NULL DEFAULT now(),
    "LastSeenAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_chat_identities_user ON governance_chat_identities ("AppId", "ExternalUserId");
CREATE INDEX IF NOT EXISTS ix_governance_chat_identities_employee ON governance_chat_identities ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS governance_chat_messages (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL REFERENCES governance_chat_apps ("Id") ON DELETE CASCADE,
    "Platform" text NOT NULL,
    "WorkflowRequestId" uuid NOT NULL,
    "StepId" uuid NOT NULL,
    "RecipientEmployeeId" uuid NOT NULL,
    "ConversationId" text NOT NULL,
    "MessageId" text NOT NULL,
    "ServiceUrl" text,
    "State" text NOT NULL DEFAULT 'Open',
    "Subject" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS ix_governance_chat_messages_wf ON governance_chat_messages ("TenantSlug", "WorkflowRequestId");

-- ==== 2026-10-03: scripts/sql/2026-10-03_calendar_meetings.sql ile ayni (ilk kurulum icin) ====
-- Google / Microsoft 365 takvim baglantilari, Zoom / Teams / Google Meet toplantilari.
-- Idempotent; install.sh guncellemede otomatik uygular.

CREATE TABLE IF NOT EXISTS governance_provider_configs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Provider" text NOT NULL,
    "ClientId" text NOT NULL,
    "ClientSecretEnc" text,
    "MsTenant" text,
    "ZoomAccountId" text,
    "ZoomDefaultHost" text,
    "IsEnabled" boolean NOT NULL DEFAULT true,
    "LastError" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_provider_configs ON governance_provider_configs ("TenantSlug", "Provider");

CREATE TABLE IF NOT EXISTS governance_calendar_connections (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "UserId" text NOT NULL,
    "Provider" text NOT NULL,
    "AccountEmail" text,
    "AccessTokenEnc" text,
    "RefreshTokenEnc" text,
    "ExpiresAt" timestamptz,
    "SyncLeaves" boolean NOT NULL DEFAULT true,
    "Status" text NOT NULL DEFAULT 'Active',
    "LastError" text,
    "LastSyncAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_calendar_connections ON governance_calendar_connections ("TenantSlug", "EmployeeId", "Provider");

CREATE TABLE IF NOT EXISTS governance_oauth_states (
    "State" text PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "UserId" text NOT NULL,
    "Provider" text NOT NULL,
    "CodeVerifier" text NOT NULL,
    "ExpiresAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS governance_calendar_events (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "ConnectionId" uuid NOT NULL REFERENCES governance_calendar_connections ("Id") ON DELETE CASCADE,
    "SourceType" text NOT NULL,
    "SourceId" uuid NOT NULL,
    "ExternalEventId" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_governance_calendar_events_source ON governance_calendar_events ("TenantSlug", "SourceType", "SourceId");

CREATE TABLE IF NOT EXISTS governance_meetings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "SourceType" text NOT NULL,
    "SourceId" uuid,
    "Title" text NOT NULL,
    "Description" text,
    "StartsAt" timestamptz NOT NULL,
    "DurationMinutes" integer NOT NULL,
    "Provider" text NOT NULL,
    "JoinUrl" text,
    "ExternalMeetingId" text,
    "OrganizerEmployeeId" uuid NOT NULL,
    "ParticipantEmployeeIds" uuid[] NOT NULL DEFAULT '{}',
    "ExternalEmails" text[] NOT NULL DEFAULT '{}',
    "Status" text NOT NULL DEFAULT 'Scheduled',
    "Warnings" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_governance_meetings_source ON governance_meetings ("TenantSlug", "SourceType", "SourceId");

-- ==== 2026-10-03: scripts/sql/2026-10-03_ai_llm.sql ile ayni (ilk kurulum icin) ====
-- Yapay zeka (LLM) kiraci ayari ve kullanim kaydi. Idempotent.

CREATE TABLE IF NOT EXISTS governance_ai_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Enabled" boolean NOT NULL DEFAULT false,
    "AllowPersonalData" boolean NOT NULL DEFAULT false,
    "UpdatedBy" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_ai_settings_tenant ON governance_ai_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS governance_ai_usage (
    "Id" bigserial PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text,
    "Task" text NOT NULL,
    "Provider" text NOT NULL,
    "Model" text NOT NULL,
    "InputTokens" integer NOT NULL DEFAULT 0,
    "OutputTokens" integer NOT NULL DEFAULT 0,
    "DurationMs" integer NOT NULL DEFAULT 0,
    "Success" boolean NOT NULL,
    "At" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_governance_ai_usage_tenant_at ON governance_ai_usage ("TenantSlug", "At");

-- 2026-10-03_notification_language.sql
-- Bildirim dili: çalışan tercihi ve her iletinin dili (e-posta çerçevesi de bu dilde).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "Language" text NOT NULL DEFAULT 'tr';

CREATE TABLE IF NOT EXISTS notification_preferences (
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Language" text NOT NULL DEFAULT 'tr',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_notification_preferences" PRIMARY KEY ("TenantSlug", "EmployeeId")
);

-- KVKK temeli: yurt dışı aktarım kayıtları (m.9), imha tutanakları, otomatik analize itiraz (m.11/1-g).
CREATE TABLE IF NOT EXISTS governance_transfer_agreements (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Provider" text NOT NULL,
    "Mechanism" text NOT NULL,
    "SignedAt" date NOT NULL,
    "NotifiedAt" date,
    "Reference" text,
    "Notes" text,
    "UpdatedBy" text NOT NULL DEFAULT '',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_transfer_agreements" ON governance_transfer_agreements ("TenantSlug", "Provider");

CREATE TABLE IF NOT EXISTS governance_destruction_logs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Category" text NOT NULL,
    "Action" text NOT NULL,
    "Affected" integer NOT NULL,
    "RetentionMonths" integer NOT NULL DEFAULT 0,
    "Trigger" text NOT NULL,
    "Actor" text NOT NULL DEFAULT '',
    "Method" text NOT NULL DEFAULT '',
    "RanAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_destruction_logs_tenant_ran" ON governance_destruction_logs ("TenantSlug", "RanAt");

CREATE TABLE IF NOT EXISTS governance_analysis_objections (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "UserId" text NOT NULL,
    "PersonName" text NOT NULL,
    "Analysis" text NOT NULL,
    "Reason" text,
    "Status" text NOT NULL DEFAULT 'Open',
    "Response" text,
    "DecidedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "DecidedAt" timestamptz,
    "DueAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_analysis_objections_emp" ON governance_analysis_objections ("TenantSlug", "EmployeeId", "Analysis");

-- Sohbet botu güvenliği: hesap doğrulama (bağlama kodu), mesajlarda veri en aza indirme,
-- gönderilemeyen mesajlar için yeniden deneme kuyruğu.
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "VerifiedAt" timestamptz;
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "LinkCodeHash" text;
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "LinkCodeExpiresAt" timestamptz;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "RequireVerifiedIdentity" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "MessageDetail" text NOT NULL DEFAULT 'Minimal';

CREATE TABLE IF NOT EXISTS governance_chat_outbox (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "Payload" text NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
    "LastError" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_outbox_due" ON governance_chat_outbox ("NextAttemptAt");

-- Sabah özeti (uygulama ayarı) ve kişi başına günde bir gönderim takibi.
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "DailyDigest" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_chat_identities ADD COLUMN IF NOT EXISTS "LastDigestOn" date;

-- ==== 2026-10-05: scripts/sql/2026-10-05_payroll_time.sql ile ayni (ilk kurulum icin) ====
-- Bordro dönemi (compensation-service), fazla mesai talebi ve giriş-çıkış (timeshift-service).
CREATE TABLE IF NOT EXISTS compensation_payroll_periods (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "Month" integer NOT NULL,
    "Status" text NOT NULL,
    "CalculatedAt" timestamptz,
    "ClosedAt" timestamptz,
    "ClosedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payroll_periods_TenantSlug_Year_Month" ON compensation_payroll_periods ("TenantSlug", "Year", "Month");

CREATE TABLE IF NOT EXISTS compensation_payroll_parameters (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "MinimumWageGross" numeric NOT NULL,
    "SgkEmployerRate" numeric NOT NULL,
    "EmployerIncentivePoints" numeric NOT NULL,
    "StampTaxRate" numeric NOT NULL,
    "SgkCeilingMultiplier" numeric NOT NULL,
    "BracketsJson" text NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payroll_parameters_TenantSlug_Year" ON compensation_payroll_parameters ("TenantSlug", "Year");

CREATE TABLE IF NOT EXISTS compensation_payroll_adjustments (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PeriodId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "Amount" numeric NOT NULL,
    "Description" text NOT NULL,
    "SourceId" uuid,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_compensation_payroll_adjustments_PeriodId_EmployeeId" ON compensation_payroll_adjustments ("PeriodId", "EmployeeId");

CREATE TABLE IF NOT EXISTS compensation_payslips (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PeriodId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Year" integer NOT NULL,
    "Month" integer NOT NULL,
    "Currency" text NOT NULL,
    "MonthlyBaseGross" numeric NOT NULL,
    "PaidDays" integer NOT NULL,
    "UnpaidDays" integer NOT NULL,
    "OvertimeHours" numeric NOT NULL,
    "BaseGross" numeric NOT NULL,
    "OvertimePay" numeric NOT NULL,
    "Additions" numeric NOT NULL,
    "Gross" numeric NOT NULL,
    "SgkBase" numeric NOT NULL,
    "SgkEmployee" numeric NOT NULL,
    "UnemploymentEmployee" numeric NOT NULL,
    "TaxBase" numeric NOT NULL,
    "CumulativeTaxBase" numeric NOT NULL,
    "IncomeTax" numeric NOT NULL,
    "IncomeTaxExemption" numeric NOT NULL,
    "StampTax" numeric NOT NULL,
    "StampTaxExemption" numeric NOT NULL,
    "Deductions" numeric NOT NULL,
    "Net" numeric NOT NULL,
    "SgkEmployer" numeric NOT NULL,
    "UnemploymentEmployer" numeric NOT NULL,
    "EmployerCost" numeric NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payslips_PeriodId_EmployeeId" ON compensation_payslips ("PeriodId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_compensation_payslips_EmployeeId_Year" ON compensation_payslips ("EmployeeId", "Year");

CREATE TABLE IF NOT EXISTS timeshift_overtime_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Date" date NOT NULL,
    "Hours" numeric NOT NULL,
    "Reason" text,
    "Status" text NOT NULL,
    "WorkflowRequestId" uuid,
    "CreatedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "DecidedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_timeshift_overtime_requests_EmployeeId_Date" ON timeshift_overtime_requests ("EmployeeId", "Date");
CREATE INDEX IF NOT EXISTS "IX_timeshift_overtime_requests_WorkflowRequestId" ON timeshift_overtime_requests ("WorkflowRequestId");

CREATE TABLE IF NOT EXISTS timeshift_clock_sites (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "AllowQr" boolean NOT NULL DEFAULT true,
    "AllowTerminal" boolean NOT NULL DEFAULT true,
    "CheckLocation" boolean NOT NULL DEFAULT false,
    "Latitude" double precision,
    "Longitude" double precision,
    "RadiusMeters" integer NOT NULL DEFAULT 200,
    "QrSecret" text NOT NULL,
    "DeviceKeyHash" text,
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS timeshift_clock_credentials (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "BadgeCode" text,
    "CardHash" text,
    "PinHash" text,
    "FailedPinAttempts" integer NOT NULL DEFAULT 0,
    "LockedUntil" timestamptz,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_timeshift_clock_credentials_TenantSlug_EmployeeId" ON timeshift_clock_credentials ("TenantSlug", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_credentials_TenantSlug_BadgeCode" ON timeshift_clock_credentials ("TenantSlug", "BadgeCode");
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_credentials_TenantSlug_CardHash" ON timeshift_clock_credentials ("TenantSlug", "CardHash");

-- KVKK: koordinat sütunu bilinçli olarak yoktur; yalnızca "noktada mı" (OnSite) tutulur.
CREATE TABLE IF NOT EXISTS timeshift_clock_punches (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "SiteId" uuid,
    "Kind" text NOT NULL,
    "Method" text NOT NULL,
    "OnSite" boolean,
    "At" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_punches_EmployeeId_At" ON timeshift_clock_punches ("EmployeeId", "At");

-- ==== 2026-10-05: scripts/sql/2026-10-05_push_offline.sql ile ayni (ilk kurulum icin) ====
-- PWA anlık bildirim (Web Push) abonelikleri ve VAPID anahtarı (notification-service).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "PushedAt" timestamptz;
CREATE INDEX IF NOT EXISTS "IX_notification_messages_push_pending" ON notification_messages ("CreatedAt") WHERE "Channel" = 'InApp' AND "PushedAt" IS NULL;

CREATE TABLE IF NOT EXISTS notification_push_subscriptions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Endpoint" text NOT NULL,
    "P256dh" text NOT NULL,
    "Auth" text NOT NULL,
    "Device" text,
    "FailureCount" integer NOT NULL DEFAULT 0,
    "LastSuccessAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_notification_push_subscriptions_Endpoint" ON notification_push_subscriptions ("Endpoint");
CREATE INDEX IF NOT EXISTS "IX_notification_push_subscriptions_TenantSlug_EmployeeId" ON notification_push_subscriptions ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS notification_vapid_keys (
    "Id" integer PRIMARY KEY,
    "PublicKey" text NOT NULL,
    "PrivateKeyEnc" text NOT NULL
);
-- Eski bildirimler anlık bildirim olarak yeniden gönderilmesin.
UPDATE notification_messages SET "PushedAt" = "CreatedAt" WHERE "PushedAt" IS NULL AND "CreatedAt" < now() - interval '30 minutes';

-- E-postadaki eylem düğmesi (ör. tek kullanımlık karar sayfası).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "ActionUrl" text;
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "ActionLabel" text;

-- ==== 2026-10-05: scripts/sql/2026-10-05_documents_workflow.sql ile ayni (ilk kurulum icin) ====
-- Çalışan belge talebi (governance-service).
ALTER TABLE governance_doc_templates ADD COLUMN IF NOT EXISTS "SelfService" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_doc_templates ADD COLUMN IF NOT EXISTS "RequiresApproval" boolean NOT NULL DEFAULT true;
UPDATE governance_doc_templates SET "SelfService" = true, "RequiresApproval" = false WHERE "Name" = 'Çalışma belgesi' AND "SelfService" = false;

CREATE TABLE IF NOT EXISTS governance_document_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "TemplateId" uuid NOT NULL,
    "TemplateName" text NOT NULL,
    "Purpose" text,
    "Status" text NOT NULL DEFAULT 'Pending',
    "DecisionNote" text,
    "DecidedBy" text,
    "VerificationCode" text,
    "DocumentEnc" text,
    "DocumentHash" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "IssuedAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_governance_document_requests_VerificationCode" ON governance_document_requests ("VerificationCode");
CREATE INDEX IF NOT EXISTS "IX_governance_document_requests_TenantSlug_EmployeeId" ON governance_document_requests ("TenantSlug", "EmployeeId");

-- Onay akışı: görsel akış tanımları, vekâlet, süre aşımında üst yöneticiye iletme, e-postadan tek tıkla karar.
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "SlaHours" integer;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "DelegationId" uuid;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "EscalatedAt" timestamptz;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "ActionTokenHash" text;
ALTER TABLE workflow_approval_steps ADD COLUMN IF NOT EXISTS "ActionTokenExpiresAt" timestamptz;
CREATE INDEX IF NOT EXISTS "IX_workflow_approval_steps_DelegationId" ON workflow_approval_steps ("DelegationId") WHERE "DelegationId" IS NOT NULL;

CREATE TABLE IF NOT EXISTS workflow_definitions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Type" text NOT NULL,
    "Name" text NOT NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "StepsJson" text NOT NULL DEFAULT '[]',
    "HiddenFieldsJson" text NOT NULL DEFAULT '[]',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_workflow_definitions_TenantSlug_Type" ON workflow_definitions ("TenantSlug", "Type");

CREATE TABLE IF NOT EXISTS workflow_delegations (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "FromEmployeeId" uuid NOT NULL,
    "ToEmployeeId" uuid NOT NULL,
    "StartDate" date NOT NULL,
    "EndDate" date NOT NULL,
    "Reason" text,
    "CreatedBy" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "RevokedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_workflow_delegations_TenantSlug_FromEmployeeId" ON workflow_delegations ("TenantSlug", "FromEmployeeId");

-- ==== 2026-10-05: scripts/sql/2026-10-05_leave_hours.sql ile ayni (ilk kurulum icin) ====
-- İzin: saatlik izin ve devreden izin (leave-service).
ALTER TABLE leave_requests ADD COLUMN IF NOT EXISTS "Hours" numeric;
ALTER TABLE leave_balances ADD COLUMN IF NOT EXISTS "CarriedOverDays" numeric NOT NULL DEFAULT 0;
ALTER TABLE leave_balances ADD COLUMN IF NOT EXISTS "CarriedOutDays" numeric NOT NULL DEFAULT 0;
-- Dalga 5a: KVKK operasyonları ve güvenlik
--   K2 aydınlatma metni sürümleri, K3 veri ihlali, K8 başvuru kimlik doğrulama,
--   K9 gizlilik etki değerlendirmesi, G21 değiştirilemez denetim kaydı (hash zinciri),
--   G20 alan düzeyinde yetki, G22 kiracı IP kısıtı.
-- Idempotent: tekrar çalıştırılabilir.

-- ---------------------------------------------------------------- K8 başvuru
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Channel" text NOT NULL DEFAULT 'Panel';
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Contact" text;
-- Mevcut (panelden gelmiş) başvurular oturumla doğrulanmış sayılır.
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "IdentityVerified" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_data_requests ALTER COLUMN "IdentityVerified" SET DEFAULT false;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "VerificationMethod" text;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "VerifiedBy" text;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "VerifiedAt" timestamptz;

-- ---------------------------------------------------------------- K2 aydınlatma metinleri
CREATE TABLE IF NOT EXISTS governance_privacy_notices (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Type" text NOT NULL,
    "Version" text NOT NULL,
    "Title" text NOT NULL,
    "Text" text NOT NULL,
    "ChangeNote" text,
    "PublishedBy" text NOT NULL DEFAULT '',
    "PublishedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_privacy_notices_version" ON governance_privacy_notices ("TenantSlug", "Type", "Version");

-- ---------------------------------------------------------------- K3 veri ihlali
CREATE TABLE IF NOT EXISTS governance_data_breaches (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Description" text NOT NULL,
    "DetectedAt" timestamptz NOT NULL,
    "OccurredAt" timestamptz,
    "DataCategories" text,
    "AffectedCount" integer,
    "AffectedEmployeesJson" text NOT NULL DEFAULT '[]',
    "Severity" text NOT NULL DEFAULT 'Medium',
    "Cause" text,
    "Measures" text,
    "Status" text NOT NULL DEFAULT 'Open',
    "ReportedToBoardAt" timestamptz,
    "BoardReference" text,
    "SubjectsNotifiedAt" timestamptz,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_data_breaches_tenant" ON governance_data_breaches ("TenantSlug", "DetectedAt" DESC);

-- ---------------------------------------------------------------- K9 gizlilik etki değerlendirmesi
CREATE TABLE IF NOT EXISTS governance_privacy_assessments (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Subject" text NOT NULL,
    "Kind" text NOT NULL DEFAULT 'Integration',
    "ProviderKey" text,
    "AnswersJson" text NOT NULL DEFAULT '{}',
    "Risk" text NOT NULL DEFAULT 'Low',
    "Status" text NOT NULL DEFAULT 'Draft',
    "CreatedBy" text NOT NULL DEFAULT '',
    "ApprovedBy" text,
    "ApprovedAt" timestamptz,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- G20 alan düzeyinde yetki
-- Kiracı, profil alanlarının hangi rollere görüneceğini daraltabilir (genişletemez).
CREATE TABLE IF NOT EXISTS governance_field_policies (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Field" text NOT NULL,
    -- self | manager | hr  (alanı görebilecek en düşük düzey)
    "MinLevel" text NOT NULL DEFAULT 'hr',
    "SelfVisible" boolean NOT NULL DEFAULT true,
    "UpdatedBy" text NOT NULL DEFAULT '',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_field_policies" ON governance_field_policies ("TenantSlug", "Field");

-- ---------------------------------------------------------------- G22 IP kısıtı
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "IpAllowlist" text;

-- ---------------------------------------------------------------- G21 hash zinciri
-- Her kiracının denetim kaydı ayrı bir zincirdir: satırın özeti (SHA-256) önceki satırın
-- özetini içerir; geçmişteki bir satırın değiştirilmesi ya da araya satır silinmesi
-- doğrulamada görünür. UPDATE tümden engellenir; DELETE yalnızca saklama süresi dolan
-- en eski kayıtlar içindir (zincirin başı kısalır, ortası kopmaz).
ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS "ChainSeq" bigint;
ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS "PrevHash" text;
ALTER TABLE audit_log ADD COLUMN IF NOT EXISTS "Hash" text;
CREATE INDEX IF NOT EXISTS "IX_audit_log_chain" ON audit_log ((coalesce("TenantSlug", '')), "ChainSeq");

CREATE OR REPLACE FUNCTION audit_row_hash(prev text, seq bigint, tenant text, service text, entity_type text, entity_id text,
    action text, changes jsonb, user_id text, user_name text, correlation text, ip text, occurred timestamptz)
RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT encode(sha256(convert_to(
        coalesce(prev,'') || '|' || seq::text || '|' || coalesce(tenant,'') || '|' || coalesce(service,'') || '|' ||
        coalesce(entity_type,'') || '|' || coalesce(entity_id,'') || '|' || coalesce(action,'') || '|' ||
        coalesce(changes::text,'') || '|' || coalesce(user_id,'') || '|' || coalesce(user_name,'') || '|' ||
        coalesce(correlation,'') || '|' || coalesce(ip,'') || '|' ||
        to_char(occurred AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'), 'UTF8')), 'hex')
$$;

-- Mevcut kayıtlar zincire bir kez dizilir (tetikleyiciler kurulmadan önce).
DO $$
DECLARE t text; r record; prev text; seq bigint;
BEGIN
    IF EXISTS (SELECT 1 FROM audit_log WHERE "Hash" IS NULL) THEN
        FOR t IN SELECT DISTINCT coalesce("TenantSlug", '') FROM audit_log WHERE "Hash" IS NULL LOOP
            SELECT "Hash", "ChainSeq" INTO prev, seq FROM audit_log
             WHERE coalesce("TenantSlug", '') = t AND "Hash" IS NOT NULL ORDER BY "ChainSeq" DESC LIMIT 1;
            prev := coalesce(prev, 'GENESIS'); seq := coalesce(seq, 0);
            FOR r IN SELECT * FROM audit_log WHERE coalesce("TenantSlug", '') = t AND "Hash" IS NULL ORDER BY "Id" LOOP
                seq := seq + 1;
                UPDATE audit_log SET "ChainSeq" = seq, "PrevHash" = prev,
                    "Hash" = audit_row_hash(prev, seq, r."TenantSlug", r."Service", r."EntityType", r."EntityId", r."Action",
                        r."Changes", r."UserId", r."UserName", r."CorrelationId", r."IpAddress", r."OccurredAt")
                 WHERE "Id" = r."Id"
                RETURNING "Hash" INTO prev;
            END LOOP;
        END LOOP;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION audit_log_chain() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE prev text; seq bigint;
BEGIN
    -- Aynı kiracıya eşzamanlı yazımlar sıraya girer; sıra numarası kilit içinde verilir.
    PERFORM pg_advisory_xact_lock(hashtext('hr360-audit:' || coalesce(NEW."TenantSlug", '')));
    SELECT "Hash", "ChainSeq" INTO prev, seq FROM audit_log
     WHERE coalesce("TenantSlug", '') = coalesce(NEW."TenantSlug", '') AND "ChainSeq" IS NOT NULL
     ORDER BY "ChainSeq" DESC LIMIT 1;
    NEW."PrevHash" := coalesce(prev, 'GENESIS');
    NEW."ChainSeq" := coalesce(seq, 0) + 1;
    NEW."OccurredAt" := coalesce(NEW."OccurredAt", now());
    NEW."Hash" := audit_row_hash(NEW."PrevHash", NEW."ChainSeq", NEW."TenantSlug", NEW."Service", NEW."EntityType", NEW."EntityId",
        NEW."Action", NEW."Changes", NEW."UserId", NEW."UserName", NEW."CorrelationId", NEW."IpAddress", NEW."OccurredAt");
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log satırları değiştirilemez (değiştirilemez denetim kaydı)' USING ERRCODE = 'insufficient_privilege';
END $$;

DROP TRIGGER IF EXISTS trg_audit_log_chain ON audit_log;
CREATE TRIGGER trg_audit_log_chain BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION audit_log_chain();
DROP TRIGGER IF EXISTS trg_audit_log_immutable ON audit_log;
CREATE TRIGGER trg_audit_log_immutable BEFORE UPDATE ON audit_log FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();

-- SIEM aktarımında kaldığı yer (kiracıdan bağımsız, tek satır).
CREATE TABLE IF NOT EXISTS governance_siem_cursor (
    "Id" integer PRIMARY KEY DEFAULT 1,
    "LastAuditId" bigint NOT NULL DEFAULT 0,
    "LastSentAt" timestamptz,
    "Sent" bigint NOT NULL DEFAULT 0,
    "LastError" text
);
-- Dalga 5b: bordro ekosistemi ve masraf
--   Y1/Y3/Y4 bordro dosyaları, Y11 avans/borç, Y13 esnek yan haklar, Y21 zam dönemi,
--   G9 döviz kuru/km/limit, Y12 seyahat ve harcırah. Idempotent.

CREATE TABLE IF NOT EXISTS compensation_payroll_exports (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PeriodId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "FileName" text NOT NULL,
    "ContentType" text NOT NULL,
    "Cipher" bytea,
    "RowCount" integer NOT NULL DEFAULT 0,
    "SingleUse" boolean NOT NULL DEFAULT false,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "DownloadedAt" timestamptz,
    "DownloadedBy" text,
    "DownloadCount" integer NOT NULL DEFAULT 0,
    "PurgedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_payroll_exports_period" ON compensation_payroll_exports ("TenantSlug", "PeriodId");

CREATE TABLE IF NOT EXISTS compensation_advances (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Kind" text NOT NULL DEFAULT 'Advance',
    "Amount" numeric(18,2) NOT NULL,
    "Installments" integer NOT NULL DEFAULT 1,
    "StartYear" integer NOT NULL,
    "StartMonth" integer NOT NULL,
    "Reason" text,
    "Status" text NOT NULL DEFAULT 'Pending',
    "RepaidAmount" numeric(18,2) NOT NULL DEFAULT 0,
    "DecidedBy" text,
    "DecisionNote" text,
    "DecidedAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_advances_employee" ON compensation_advances ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS compensation_benefit_plans (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "BudgetPerEmployee" numeric(18,2) NOT NULL,
    "WindowStart" date NOT NULL,
    "WindowEnd" date NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_benefit_plans" ON compensation_benefit_plans ("TenantSlug", "Year");
CREATE TABLE IF NOT EXISTS compensation_benefit_options (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PlanId" uuid NOT NULL,
    "Name" text NOT NULL,
    "Category" text NOT NULL,
    "AnnualCost" numeric(18,2) NOT NULL,
    "Description" text,
    "IsActive" boolean NOT NULL DEFAULT true
);
CREATE TABLE IF NOT EXISTS compensation_benefit_elections (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PlanId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "OptionIdsJson" text NOT NULL DEFAULT '[]',
    "Total" numeric(18,2) NOT NULL DEFAULT 0,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_benefit_elections" ON compensation_benefit_elections ("PlanId", "EmployeeId");

CREATE TABLE IF NOT EXISTS compensation_raise_cycles (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Name" text NOT NULL,
    "Year" integer NOT NULL,
    "BudgetPercent" numeric(9,4) NOT NULL,
    "EffectiveDate" date NOT NULL,
    "Status" text NOT NULL DEFAULT 'Draft',
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "AppliedAt" timestamptz
);
CREATE TABLE IF NOT EXISTS compensation_raise_proposals (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "CycleId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "CurrentSalary" numeric(18,2) NOT NULL,
    "ProposedPercent" numeric(9,4) NOT NULL,
    "ProposedSalary" numeric(18,2) NOT NULL,
    "Note" text,
    "Status" text NOT NULL DEFAULT 'Proposed',
    "ProposedBy" text NOT NULL DEFAULT '',
    "DecidedBy" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_raise_proposals" ON compensation_raise_proposals ("CycleId", "EmployeeId");

-- G9 masraf
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "OriginalCurrency" text;
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "OriginalAmount" numeric(18,2);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "FxRate" numeric(18,6);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "Km" numeric(10,2);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "TravelRequestId" uuid;
CREATE TABLE IF NOT EXISTS expense_fx_rates (
    "Id" uuid PRIMARY KEY,
    "Date" date NOT NULL,
    "Currency" text NOT NULL,
    "Rate" numeric(18,6) NOT NULL,
    "Source" text NOT NULL DEFAULT 'TCMB',
    "TenantSlug" character varying(64),
    "FetchedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_fx_rates" ON expense_fx_rates ("Date", "Currency", "TenantSlug");
CREATE TABLE IF NOT EXISTS expense_policies (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "LimitsJson" text NOT NULL DEFAULT '{}',
    "KmRate" numeric(10,2) NOT NULL DEFAULT 8,
    "PerDiemDomestic" numeric(18,2) NOT NULL DEFAULT 1000,
    "PerDiemAbroad" numeric(18,2) NOT NULL DEFAULT 100,
    "PerDiemAbroadCurrency" text NOT NULL DEFAULT 'EUR',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_expense_policies" ON expense_policies ("TenantSlug");

-- Y12 seyahat
CREATE TABLE IF NOT EXISTS expense_travel_requests (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Destination" text NOT NULL,
    "Abroad" boolean NOT NULL DEFAULT false,
    "StartDate" date NOT NULL,
    "EndDate" date NOT NULL,
    "Purpose" text NOT NULL,
    "Transport" text NOT NULL DEFAULT 'Plane',
    "NeedsAccommodation" boolean NOT NULL DEFAULT false,
    "PerDiemDays" integer NOT NULL DEFAULT 0,
    "PerDiemRate" numeric(18,2) NOT NULL DEFAULT 0,
    "PerDiemCurrency" text NOT NULL DEFAULT 'TRY',
    "PerDiemTotal" numeric(18,2) NOT NULL DEFAULT 0,
    "AdvanceRequested" numeric(18,2),
    "PassportCipher" text,
    "PassportPurgedAt" timestamptz,
    "Status" text NOT NULL DEFAULT 'Submitted',
    "WorkflowRequestId" uuid,
    "DecidedByEmployeeId" uuid,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_travel_employee" ON expense_travel_requests ("TenantSlug", "EmployeeId");

-- ===== 2026-10-08_workplace_compliance
-- Dalga 5c (governance-service): işyeri uyumu
--   Y14 duyurular + okudum onayı, G19 doküman kütüphanesi (sürüm + tam metin arama),
--   Y15 etik/ihbar hattı (anonim), Y6 iş sağlığı ve güvenliği (İSG), Y7 disiplin süreci.
-- Idempotent: tekrar çalıştırılabilir.

-- ---------------------------------------------------------------- Y14 duyurular
CREATE TABLE IF NOT EXISTS governance_announcements (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Body" text NOT NULL,
    -- All | Departments
    "Audience" text NOT NULL DEFAULT 'All',
    "DepartmentIds" uuid[] NOT NULL DEFAULT '{}',
    "PublishAt" timestamptz NOT NULL DEFAULT now(),
    "ExpireAt" timestamptz,
    "RequiresAck" boolean NOT NULL DEFAULT false,
    -- Yayım bildirimi bir kez gönderilir (ileri tarihli duyurularda yayım anında).
    "NotifiedAt" timestamptz,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_announcements_tenant" ON governance_announcements ("TenantSlug", "PublishAt" DESC);

-- ---------------------------------------------------------------- G19 doküman kütüphanesi
CREATE TABLE IF NOT EXISTS governance_library_documents (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Title" text NOT NULL,
    "Category" text NOT NULL DEFAULT 'Policy',
    -- All | Managers | Hr | Departments
    "Audience" text NOT NULL DEFAULT 'All',
    "DepartmentIds" uuid[] NOT NULL DEFAULT '{}',
    "RequiresAck" boolean NOT NULL DEFAULT false,
    "CurrentVersionId" uuid,
    "CurrentVersionNo" integer NOT NULL DEFAULT 0,
    "Archived" boolean NOT NULL DEFAULT false,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_library_documents_tenant" ON governance_library_documents ("TenantSlug", "Title");

CREATE TABLE IF NOT EXISTS governance_library_versions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "DocumentId" uuid NOT NULL REFERENCES governance_library_documents("Id") ON DELETE CASCADE,
    "VersionNo" integer NOT NULL,
    "Title" text NOT NULL,
    "Body" text NOT NULL DEFAULT '',
    "ExternalUrl" text,
    "StorageKey" text,
    "ChangeNote" text,
    "PublishedBy" text NOT NULL DEFAULT '',
    "PublishedAt" timestamptz NOT NULL DEFAULT now(),
    -- Türkçe noktasız ı / noktalı İ farkı aramayı bozmasın: dizinde ve sorguda ı→i katlanır.
    "SearchVector" tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('simple'::regconfig, translate(lower(coalesce("Title", '')), 'ı', 'i')), 'A') ||
        setweight(to_tsvector('simple'::regconfig, translate(lower(coalesce("Body", '')), 'ı', 'i')), 'B')
    ) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_library_versions_no" ON governance_library_versions ("DocumentId", "VersionNo");
CREATE INDEX IF NOT EXISTS "IX_library_versions_tenant" ON governance_library_versions ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_library_versions_search" ON governance_library_versions USING GIN ("SearchVector");

-- ---------------------------------------------------------------- Y14 + G19 okudum / kabul kayıtları
-- Bu bir RIZA kaydı DEĞİLDİR (governance_consents ayrı): yalnızca "okudum/kabul ettim" beyanı.
CREATE TABLE IF NOT EXISTS governance_acknowledgements (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    -- Announcement | LibraryDocument
    "SubjectType" text NOT NULL,
    "SubjectId" uuid NOT NULL,
    -- Doküman sürüm numarası (duyurularda 0). Yeni sürüm yeniden onay ister.
    "Version" integer NOT NULL DEFAULT 0,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "PersonName" text NOT NULL DEFAULT '',
    "AcknowledgedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_acknowledgements" ON governance_acknowledgements ("TenantSlug", "SubjectType", "SubjectId", "Version", "UserId");

-- ---------------------------------------------------------------- Y15 etik hattı
CREATE TABLE IF NOT EXISTS governance_ethics_committee (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" text NOT NULL,
    "EmployeeId" uuid,
    "Name" text NOT NULL DEFAULT '',
    "AddedBy" text NOT NULL DEFAULT '',
    "AddedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ethics_committee_user" ON governance_ethics_committee ("TenantSlug", "UserId");

-- ANONİMLİK: IP, kullanıcı kimliği, tarayıcı bilgisi tutulmaz. Alındığı an gün
-- hassasiyetinde saklanır (saat bilgisi diğer kayıtlarla eşleştirmeye yaramasın).
-- Takip kodunun yalnızca SHA-256 özeti saklanır.
CREATE TABLE IF NOT EXISTS governance_ethics_reports (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Category" text NOT NULL,
    "Description" text NOT NULL,
    -- İhbarcı isterse bıraktığı iletişim bilgisi (şifreli).
    "ContactEnc" text,
    "CodeHash" text NOT NULL,
    -- Received | InReview | Closed
    "Status" text NOT NULL DEFAULT 'Received',
    "Outcome" text,
    "ReceivedOn" date NOT NULL DEFAULT current_date,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "ClosedAt" timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ethics_reports_code" ON governance_ethics_reports ("CodeHash");
CREATE INDEX IF NOT EXISTS "IX_ethics_reports_tenant" ON governance_ethics_reports ("TenantSlug", "Status");

CREATE TABLE IF NOT EXISTS governance_ethics_messages (
    "Id" uuid PRIMARY KEY,
    "Seq" bigserial,
    "TenantSlug" character varying(64) NOT NULL,
    "ReportId" uuid NOT NULL REFERENCES governance_ethics_reports("Id") ON DELETE CASCADE,
    "FromReporter" boolean NOT NULL,
    -- Kurul üyesi yanıtında görünen ad ("Etik Kurulu"); ihbarcıda boş.
    "Author" text,
    "Body" text NOT NULL,
    -- İhbarcı mesajlarında gün hassasiyeti.
    "CreatedOn" date NOT NULL DEFAULT current_date
);
CREATE INDEX IF NOT EXISTS "IX_ethics_messages_report" ON governance_ethics_messages ("ReportId", "Seq");

-- ---------------------------------------------------------------- Y6 İSG
CREATE TABLE IF NOT EXISTS governance_osh_incidents (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    -- Accident | NearMiss
    "Kind" text NOT NULL,
    "OccurredOn" date NOT NULL,
    "OccurredTime" text,
    "Location" text NOT NULL DEFAULT '',
    "Description" text NOT NULL,
    "InjuredEmployeeId" uuid,
    "LostDays" integer NOT NULL DEFAULT 0,
    "RootCause" text,
    "CorrectiveActions" text,
    "SgkNotifiedOn" date,
    "SgkReference" text,
    -- Open | Closed
    "Status" text NOT NULL DEFAULT 'Open',
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_osh_incidents_tenant" ON governance_osh_incidents ("TenantSlug", "OccurredOn" DESC);

-- Sağlık notları ÖZEL NİTELİKLİ veridir (KVKK m.6): şifreli, yalnızca işyeri hekimi okur.
CREATE TABLE IF NOT EXISTS governance_osh_exams (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    -- PreEmployment | Periodic | ReturnToWork | JobChange
    "ExamType" text NOT NULL DEFAULT 'Periodic',
    "ExamDate" date NOT NULL,
    "NextDueDate" date,
    -- Fit | Unfit | Conditional
    "Result" text NOT NULL,
    "NotesEnc" text,
    "RecordedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_osh_exams_employee" ON governance_osh_exams ("TenantSlug", "EmployeeId", "ExamDate" DESC);

CREATE TABLE IF NOT EXISTS governance_osh_trainings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Topic" text NOT NULL,
    "TrainingDate" date NOT NULL,
    "DurationHours" numeric(6,1) NOT NULL DEFAULT 0,
    "ValidityMonths" integer,
    "ExpiresOn" date,
    "Trainer" text,
    "ParticipantIds" uuid[] NOT NULL DEFAULT '{}',
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_osh_trainings_tenant" ON governance_osh_trainings ("TenantSlug", "TrainingDate" DESC);

-- ---------------------------------------------------------------- Y7 disiplin
-- Adli sicil / mahkûmiyet bilgisi TUTULMAZ (arayüz uyarısı + sunucu uyarısı).
CREATE TABLE IF NOT EXISTS governance_disciplinary_cases (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "IncidentDate" date NOT NULL,
    "Category" text NOT NULL,
    "Description" text NOT NULL,
    -- Open | DefenceRequested | DefenceReceived | Decided | Closed
    "Status" text NOT NULL DEFAULT 'Open',
    "DefenceNotice" text,
    "DefenceRequestedAt" timestamptz,
    "DefenceDeadline" date,
    "DefenceText" text,
    "DefenceSubmittedAt" timestamptz,
    "MinutesText" text,
    "Witnesses" text,
    -- Warning | WrittenWarning | NoAction | TerminationRecommendation
    "Decision" text,
    "DecisionNote" text,
    "DecidedBy" text,
    "DecidedAt" timestamptz,
    "ClosedAt" timestamptz,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_disciplinary_cases_employee" ON governance_disciplinary_cases ("TenantSlug", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_disciplinary_cases_status" ON governance_disciplinary_cases ("TenantSlug", "Status");

-- ===== 2026-10-08_recruitment_plus
-- Dalga 5c — İşe alım+: kariyer sayfası ve aday öz-hizmeti (Y16), tekrar aday tespiti ve
-- kanban (G13), mülakat puan kartı ve planlama (Y17), teklif mektubu ve onayı (Y18).
-- İdempotent: tekrar çalıştırılabilir.

-- ------------------------------------------------------------------ adaylar
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "NormalizedEmail" text;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "NormalizedPhone" text;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "Skills" text[] NOT NULL DEFAULT '{}';
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "ResumeText" text;
-- KVKK m.5/1: aday havuzunda saklama yalnızca AÇIK RIZA ile (aydınlatmadan ayrı).
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "TalentPoolConsent" boolean NOT NULL DEFAULT false;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "TalentPoolConsentAt" timestamptz;
ALTER TABLE recruitment_candidates ADD COLUMN IF NOT EXISTS "AnonymizedAt" timestamptz;

-- Mevcut kayıtların karşılaştırma anahtarları (uygulama ile aynı kural: küçük harf, +etiket atılır;
-- telefon yalnızca rakam, son 10 hane).
UPDATE recruitment_candidates
   SET "NormalizedEmail" = lower(regexp_replace(trim("Email"), '\+[^@]*@', '@'))
 WHERE "NormalizedEmail" IS NULL AND "Email" NOT LIKE 'anon-%';
UPDATE recruitment_candidates
   SET "NormalizedPhone" = right(regexp_replace("Phone", '\D', '', 'g'), 10)
 WHERE "NormalizedPhone" IS NULL AND "Phone" IS NOT NULL AND length(regexp_replace("Phone", '\D', '', 'g')) >= 7;

CREATE INDEX IF NOT EXISTS "IX_recruitment_candidates_norm_email" ON recruitment_candidates ("TenantSlug", "NormalizedEmail");
CREATE INDEX IF NOT EXISTS "IX_recruitment_candidates_norm_phone" ON recruitment_candidates ("TenantSlug", "NormalizedPhone");

-- ------------------------------------------------------------------ başvurular
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "Channel" text NOT NULL DEFAULT 'Manual';
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "CoverNote" text;
-- Öz-hizmet bağlantısının yalnızca SHA-256 özeti tutulur; bağlantının kendisi hiçbir yerde saklanmaz.
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "SelfServiceTokenHash" text;
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "OwnsCandidate" boolean NOT NULL DEFAULT false;
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "PrivacyNoticeVersion" text;
ALTER TABLE recruitment_applications ADD COLUMN IF NOT EXISTS "DuplicateReason" text;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_applications_token"
    ON recruitment_applications ("SelfServiceTokenHash") WHERE "SelfServiceTokenHash" IS NOT NULL;

-- ------------------------------------------------------------------ mülakatlar
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "DurationMinutes" integer NOT NULL DEFAULT 60;
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "Location" text;
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "MeetingUrl" text;
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "InterviewerIds" uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE recruitment_interviews ADD COLUMN IF NOT EXISTS "CandidateNotifiedAt" timestamptz;
UPDATE recruitment_interviews SET "InterviewerIds" = ARRAY["InterviewerEmployeeId"]
 WHERE cardinality("InterviewerIds") = 0;
CREATE INDEX IF NOT EXISTS "IX_recruitment_interviews_tenant_at" ON recruitment_interviews ("TenantSlug", "ScheduledAt");

-- ------------------------------------------------------------------ puan kartları
CREATE TABLE IF NOT EXISTS recruitment_scorecard_templates (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "JobPostingId" uuid NOT NULL REFERENCES recruitment_job_postings ("Id") ON DELETE CASCADE,
    "CriteriaJson" text NOT NULL DEFAULT '[]',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_scorecard_templates" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_scorecard_templates_posting" ON recruitment_scorecard_templates ("TenantSlug", "JobPostingId");

CREATE TABLE IF NOT EXISTS recruitment_scorecards (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "InterviewId" uuid NOT NULL REFERENCES recruitment_interviews ("Id") ON DELETE CASCADE,
    "InterviewerEmployeeId" uuid NOT NULL,
    "ScoresJson" text NOT NULL DEFAULT '[]',
    "OverallScore" numeric(4,2),
    "Recommendation" text,
    "Notes" text,
    "SubmittedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_scorecards" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_scorecards_interview_person" ON recruitment_scorecards ("InterviewId", "InterviewerEmployeeId");
CREATE INDEX IF NOT EXISTS "IX_recruitment_scorecards_TenantSlug" ON recruitment_scorecards ("TenantSlug");

-- ------------------------------------------------------------------ teklifler
CREATE TABLE IF NOT EXISTS recruitment_offer_templates (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Body" text NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_offer_templates" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_recruitment_offer_templates_tenant" ON recruitment_offer_templates ("TenantSlug");

CREATE TABLE IF NOT EXISTS recruitment_offers (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "ApplicationId" uuid NOT NULL REFERENCES recruitment_applications ("Id") ON DELETE CASCADE,
    "PositionTitle" text NOT NULL,
    "GrossSalary" numeric(14,2) NOT NULL,
    "Currency" text NOT NULL DEFAULT 'TRY',
    "StartDate" date NOT NULL,
    "Benefits" text,
    "ExpiresAt" date NOT NULL,
    "LetterText" text NOT NULL,
    "Status" text NOT NULL,
    "WorkflowRequestId" uuid,
    "ApproverEmployeeId" uuid,
    "DecidedByEmployeeId" uuid,
    "DecidedByUserId" text,
    "DecisionNote" text,
    "DecidedAt" timestamptz,
    "SentAt" timestamptz,
    "RespondedAt" timestamptz,
    "CreatedByUserId" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_recruitment_offers" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_recruitment_offers_TenantSlug" ON recruitment_offers ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_recruitment_offers_ApplicationId" ON recruitment_offers ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_recruitment_offers_Workflow" ON recruitment_offers ("WorkflowRequestId") WHERE "WorkflowRequestId" IS NOT NULL;

-- ===== 2026-10-08_learning_performance
-- Dalga 5c: yetkinlik matrisi ve eğitim önerisi (Y19), eğitim içeriği / sınav / SCORM 1.2 (Y20),
-- sertifika bitiş hatırlatmaları (G17), performans 9-kutu ve dönem şablonları (G12).
-- Idempotent: tekrar çalıştırılabilir.

/* ============================================================ Y19 yetkinlik matrisi */

CREATE TABLE IF NOT EXISTS learning_competencies (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Name"        character varying(150) NOT NULL,
    "Description" text NULL,
    "Category"    character varying(80) NULL,
    "IsActive"    boolean NOT NULL DEFAULT true,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_competencies_TenantSlug" ON learning_competencies ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_competencies_name" ON learning_competencies ("TenantSlug", lower("Name"));

-- Rol profili: bir pozisyon unvanı YA DA departman için yetkinlik başına beklenen seviye (1-5).
CREATE TABLE IF NOT EXISTS learning_role_profiles (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "CompetencyId"  uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "PositionTitle" character varying(150) NULL,
    "DepartmentId"  uuid NULL,
    "RequiredLevel" integer NOT NULL CHECK ("RequiredLevel" BETWEEN 1 AND 5),
    "CreatedAt"     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_learning_role_profiles_target" CHECK (("PositionTitle" IS NULL) <> ("DepartmentId" IS NULL))
);
CREATE INDEX IF NOT EXISTS "IX_learning_role_profiles_TenantSlug" ON learning_role_profiles ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_role_profiles_target"
    ON learning_role_profiles ("TenantSlug", "CompetencyId", coalesce(lower("PositionTitle"), ''), coalesce("DepartmentId", '00000000-0000-0000-0000-000000000000'::uuid));

-- Değerlendirme geçmişi: öz / yönetici / İK; güncel seviye = en son kayıt.
CREATE TABLE IF NOT EXISTS learning_competency_assessments (
    "Id"                   uuid PRIMARY KEY,
    "TenantSlug"           character varying(64) NOT NULL,
    "EmployeeId"           uuid NOT NULL,
    "CompetencyId"         uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "Level"                integer NOT NULL CHECK ("Level" BETWEEN 1 AND 5),
    "Source"               character varying(16) NOT NULL,
    "AssessedByEmployeeId" uuid NULL,
    "AssessedByName"       character varying(200) NULL,
    "Note"                 character varying(500) NULL,
    "AssessedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_competency_assessments_TenantSlug" ON learning_competency_assessments ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_competency_assessments_emp" ON learning_competency_assessments ("EmployeeId", "CompetencyId", "AssessedAt" DESC);

-- Eğitimin geliştirdiği yetkinlik ve hedef seviye.
CREATE TABLE IF NOT EXISTS learning_course_competencies (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "CourseId"     uuid NOT NULL REFERENCES learning_courses("Id") ON DELETE CASCADE,
    "CompetencyId" uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "TargetLevel"  integer NOT NULL CHECK ("TargetLevel" BETWEEN 1 AND 5)
);
CREATE INDEX IF NOT EXISTS "IX_learning_course_competencies_TenantSlug" ON learning_course_competencies ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_course_competencies" ON learning_course_competencies ("CourseId", "CompetencyId");

/* ============================================================ Y20 içerik, sınav, SCORM */

ALTER TABLE learning_courses ADD COLUMN IF NOT EXISTS "CertificateValidityMonths" integer NULL;

-- Sertifika kaydı: eğitim tamamlanınca otomatik üretilir (doğrulama kodu ile).
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "CourseId" uuid NULL;
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "EnrollmentId" uuid NULL;
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "VerificationCode" character varying(32) NULL;
ALTER TABLE learning_certifications ADD COLUMN IF NOT EXISTS "IsMandatory" boolean NOT NULL DEFAULT false;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_certifications_code" ON learning_certifications ("VerificationCode") WHERE "VerificationCode" IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_certifications_enrollment" ON learning_certifications ("EnrollmentId") WHERE "EnrollmentId" IS NOT NULL;

CREATE TABLE IF NOT EXISTS learning_scorm_packages (
    "Id"                 uuid PRIMARY KEY,
    "TenantSlug"         character varying(64) NOT NULL,
    "Title"              character varying(200) NOT NULL,
    "ManifestIdentifier" character varying(200) NULL,
    "EntryPoint"         character varying(500) NOT NULL,
    "FileCount"          integer NOT NULL,
    "TotalBytes"         bigint NOT NULL,
    "UploadedBy"         character varying(200) NULL,
    "CreatedAt"          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_scorm_packages_TenantSlug" ON learning_scorm_packages ("TenantSlug");

-- Paket dosyaları (MinIO/S3 olmadığı için Postgres bytea; paket + yol anahtarlı).
CREATE TABLE IF NOT EXISTS learning_scorm_files (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "PackageId"   uuid NOT NULL REFERENCES learning_scorm_packages("Id") ON DELETE CASCADE,
    "Path"        character varying(500) NOT NULL,
    "ContentType" character varying(120) NOT NULL,
    "Size"        integer NOT NULL,
    "Content"     bytea NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_learning_scorm_files_TenantSlug" ON learning_scorm_files ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_scorm_files_path" ON learning_scorm_files ("PackageId", "Path");

CREATE TABLE IF NOT EXISTS learning_course_modules (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "CourseId"        uuid NOT NULL REFERENCES learning_courses("Id") ON DELETE CASCADE,
    "Position"        integer NOT NULL DEFAULT 0,
    "Title"           character varying(200) NOT NULL,
    "Kind"            character varying(16) NOT NULL,
    "VideoUrl"        character varying(1000) NULL,
    "TextBody"        text NULL,
    "PassMarkPercent" integer NULL,
    "MaxAttempts"     integer NULL,
    "ScormPackageId"  uuid NULL REFERENCES learning_scorm_packages("Id") ON DELETE SET NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_course_modules_TenantSlug" ON learning_course_modules ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_course_modules_course" ON learning_course_modules ("CourseId", "Position");

-- Sınav soruları. Doğru seçenekler ("CorrectJson") istemciye hiçbir uçta gönderilmez (yalnızca İK cevap anahtarı).
CREATE TABLE IF NOT EXISTS learning_quiz_questions (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "ModuleId"    uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "Position"    integer NOT NULL DEFAULT 0,
    "Text"        character varying(1000) NOT NULL,
    "Kind"        character varying(16) NOT NULL,
    "OptionsJson" jsonb NOT NULL,
    "CorrectJson" jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_learning_quiz_questions_TenantSlug" ON learning_quiz_questions ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_quiz_questions_module" ON learning_quiz_questions ("ModuleId", "Position");

CREATE TABLE IF NOT EXISTS learning_quiz_attempts (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "ModuleId"     uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "EnrollmentId" uuid NOT NULL REFERENCES learning_enrollments("Id") ON DELETE CASCADE,
    "EmployeeId"   uuid NOT NULL,
    "AttemptNo"    integer NOT NULL,
    "AnswersJson"  jsonb NOT NULL,
    "ScorePercent" numeric(5,2) NOT NULL,
    "Passed"       boolean NOT NULL,
    "SubmittedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_quiz_attempts_TenantSlug" ON learning_quiz_attempts ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_quiz_attempts_no" ON learning_quiz_attempts ("EnrollmentId", "ModuleId", "AttemptNo");

CREATE TABLE IF NOT EXISTS learning_module_progress (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "EnrollmentId" uuid NOT NULL REFERENCES learning_enrollments("Id") ON DELETE CASCADE,
    "ModuleId"     uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "EmployeeId"   uuid NOT NULL,
    "Status"       character varying(16) NOT NULL,
    "Score"        numeric(6,2) NULL,
    "CompletedAt"  timestamptz NULL,
    "UpdatedAt"    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_module_progress_TenantSlug" ON learning_module_progress ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_module_progress" ON learning_module_progress ("EnrollmentId", "ModuleId");

-- SCORM 1.2 çalışma zamanı değerleri (cmi.core.lesson_status, score.raw, suspend_data, lesson_location).
CREATE TABLE IF NOT EXISTS learning_scorm_runtime (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "EnrollmentId"   uuid NOT NULL REFERENCES learning_enrollments("Id") ON DELETE CASCADE,
    "ModuleId"       uuid NOT NULL REFERENCES learning_course_modules("Id") ON DELETE CASCADE,
    "PackageId"      uuid NOT NULL,
    "EmployeeId"     uuid NOT NULL,
    "LessonStatus"   character varying(32) NOT NULL DEFAULT 'not attempted',
    "ScoreRaw"       numeric(6,2) NULL,
    "SuspendData"    text NULL,
    "LessonLocation" character varying(255) NULL,
    "SessionCount"   integer NOT NULL DEFAULT 0,
    "UpdatedAt"      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_scorm_runtime_TenantSlug" ON learning_scorm_runtime ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_scorm_runtime" ON learning_scorm_runtime ("EnrollmentId", "ModuleId");

/* ============================================================ G17 sertifika hatırlatmaları */

-- Gönderilmiş hatırlatmalar: aynı sertifika + tür + alıcı için ikinci bildirim gitmez.
CREATE TABLE IF NOT EXISTS learning_cert_reminders (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "CertificationId"     uuid NOT NULL REFERENCES learning_certifications("Id") ON DELETE CASCADE,
    "Kind"                character varying(16) NOT NULL,
    "RecipientEmployeeId" uuid NOT NULL,
    "ExpiresOn"           date NOT NULL,
    "SentAt"              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_cert_reminders_TenantSlug" ON learning_cert_reminders ("TenantSlug");
-- ExpiresOn anahtarda: sertifika yenilenip bitiş tarihi değişirse yeni hatırlatmalar yeniden gider.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_cert_reminders" ON learning_cert_reminders ("CertificationId", "Kind", "RecipientEmployeeId", "ExpiresOn");

/* ============================================================ G12 9-kutu, dönem şablonları */

ALTER TABLE performance_cycles ADD COLUMN IF NOT EXISTS "TemplateId" uuid NULL;
ALTER TABLE performance_cycles ADD COLUMN IF NOT EXISTS "ConfigJson" jsonb NULL;

CREATE TABLE IF NOT EXISTS performance_cycle_templates (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   character varying(64) NOT NULL,
    "Name"         character varying(150) NOT NULL,
    "Description"  character varying(500) NULL,
    "Period"       character varying(16) NOT NULL,
    "DurationDays" integer NOT NULL,
    "ConfigJson"   jsonb NOT NULL,
    "CreatedBy"    character varying(200) NULL,
    "CreatedAt"    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_cycle_templates_TenantSlug" ON performance_cycle_templates ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_cycle_templates_name" ON performance_cycle_templates ("TenantSlug", lower("Name"));

-- Potansiyel (1-3): yöneticinin girdiği değerlendirme; çalışana varsayılan olarak GÖSTERİLMEZ.
CREATE TABLE IF NOT EXISTS performance_potential_ratings (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "CycleId"             uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "EmployeeId"          uuid NOT NULL,
    "Rating"              integer NOT NULL CHECK ("Rating" BETWEEN 1 AND 3),
    "Note"                character varying(500) NULL,
    "RatedByEmployeeId"   uuid NULL,
    "RatedByName"         character varying(200) NULL,
    "PublishedToEmployee" boolean NOT NULL DEFAULT false,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_potential_ratings_TenantSlug" ON performance_potential_ratings ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_potential_ratings" ON performance_potential_ratings ("CycleId", "EmployeeId");

-- Kalibrasyon: İK'nın hücre düzeltmesi (gerekçeli, denetim kaydına yazılır; geçmiş korunur, en son geçerli).
CREATE TABLE IF NOT EXISTS performance_ninebox_overrides (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "CycleId"          uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "EmployeeId"       uuid NOT NULL,
    "PerformanceBand"  integer NOT NULL CHECK ("PerformanceBand" BETWEEN 1 AND 3),
    "PotentialBand"    integer NOT NULL CHECK ("PotentialBand" BETWEEN 1 AND 3),
    "Reason"           character varying(1000) NOT NULL,
    "OverriddenBy"     character varying(200) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_ninebox_overrides_TenantSlug" ON performance_ninebox_overrides ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_ninebox_overrides_emp" ON performance_ninebox_overrides ("CycleId", "EmployeeId", "CreatedAt" DESC);

-- ===== 2026-10-08_ops_plus
-- Dalga 5c (G14, G15, G16, G18, G6, G7): işe alışma şablonları ve "buddy", ilk gün karşılama,
-- zimmet QR/iade hatırlatma/bakım, offboarding hesap kapatma + zimmet kontrolü + imha planı,
-- vardiya tercihleri ve takas, puantaj geç kalma/fazla mesai ayarları.
-- Idempotent: tekrar çalıştırılabilir.

/* ============================================================ G14 onboarding */

CREATE TABLE IF NOT EXISTS onboarding_task_templates (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "Name"          text NOT NULL,
    -- Eşleşme: unvan ve/veya departman (ikisi de boşsa herkese uygulanır).
    "PositionTitle" text NULL,
    "DepartmentId"  uuid NULL,
    "IsActive"      boolean NOT NULL DEFAULT true,
    "CreatedAt"     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_onboarding_task_templates_TenantSlug" ON onboarding_task_templates ("TenantSlug");

CREATE TABLE IF NOT EXISTS onboarding_task_template_items (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "TemplateId"  uuid NOT NULL REFERENCES onboarding_task_templates("Id") ON DELETE CASCADE,
    "Title"       text NOT NULL,
    "Category"    text NOT NULL DEFAULT 'Other',
    -- HR | Manager | IT | Buddy | Employee
    "OwnerRole"   text NOT NULL DEFAULT 'HR',
    -- Başlangıç tarihine göre gün (negatif: başlamadan önce).
    "OffsetDays"  integer NOT NULL DEFAULT 0,
    "Order"       integer NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS "IX_onboarding_task_template_items_TemplateId" ON onboarding_task_template_items ("TemplateId");
CREATE INDEX IF NOT EXISTS "IX_onboarding_task_template_items_TenantSlug" ON onboarding_task_template_items ("TenantSlug");

ALTER TABLE onboarding_tasks ADD COLUMN IF NOT EXISTS "OwnerRole" text NULL;

ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "BuddyEmployeeId" uuid NULL;
ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "Location" text NULL;
ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "AppliedTemplates" text NULL;
ALTER TABLE onboarding_plans ADD COLUMN IF NOT EXISTS "WelcomeSentAt" timestamptz NULL;

-- Kiracı ayarları: ilk gün karşılama şablonu ve zimmet hatırlatmalarının İK sorumlusu.
CREATE TABLE IF NOT EXISTS onboarding_settings (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "WelcomeSubject"      text NULL,
    "WelcomeBody"         text NULL,
    "HrContactEmployeeId" uuid NULL,
    "ReminderDaysBefore"  integer NOT NULL DEFAULT 3,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_onboarding_settings_TenantSlug" ON onboarding_settings ("TenantSlug");

/* ============================================================ G16 zimmet */

-- QR etiketi: kişisel veri içermeyen, tahmin edilemez demirbaş kodu.
ALTER TABLE onboarding_assets ADD COLUMN IF NOT EXISTS "QrCode" text NULL;
UPDATE onboarding_assets SET "QrCode" = upper(substr(md5(random()::text || "Id"::text || clock_timestamp()::text), 1, 16))
 WHERE "QrCode" IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_onboarding_assets_QrCode" ON onboarding_assets ("TenantSlug", "QrCode") WHERE "QrCode" IS NOT NULL;

ALTER TABLE onboarding_asset_assignments ADD COLUMN IF NOT EXISTS "ExpectedReturnOn" date NULL;
ALTER TABLE onboarding_asset_assignments ADD COLUMN IF NOT EXISTS "ReminderBeforeSentAt" timestamptz NULL;
ALTER TABLE onboarding_asset_assignments ADD COLUMN IF NOT EXISTS "ReminderOverdueSentAt" timestamptz NULL;

CREATE TABLE IF NOT EXISTS onboarding_asset_maintenance (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "AssetId"           uuid NOT NULL REFERENCES onboarding_assets("Id") ON DELETE CASCADE,
    "Date"              date NOT NULL,
    -- Periodic | Repair | Inspection | Other
    "Type"              text NOT NULL DEFAULT 'Periodic',
    "Cost"              numeric(12,2) NULL,
    "Vendor"            text NULL,
    "Notes"             text NULL,
    "NextMaintenanceOn" date NULL,
    "CreatedBy"         text NULL,
    "CreatedAt"         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_onboarding_asset_maintenance_AssetId" ON onboarding_asset_maintenance ("AssetId");
CREATE INDEX IF NOT EXISTS "IX_onboarding_asset_maintenance_TenantSlug" ON onboarding_asset_maintenance ("TenantSlug");

/* ============================================================ G15 offboarding */

ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AssetChecks" jsonb NOT NULL DEFAULT '[]'::jsonb;
-- Disabled | NoAccount | Failed | Skipped
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AccountStatus" text NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AccountDisabledAt" timestamptz NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "AccountNote" text NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "RetentionMonths" integer NULL;
ALTER TABLE engagement_offboarding_cases ADD COLUMN IF NOT EXISTS "PlannedAnonymizationOn" date NULL;

/* ============================================================ G6 vardiya tercihleri ve takas */

CREATE TABLE IF NOT EXISTS timeshift_shift_preferences (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "EmployeeId"          uuid NOT NULL,
    -- ISO gün numaraları: 1 = Pazartesi ... 7 = Pazar
    "PreferredDays"       integer[] NOT NULL DEFAULT '{}',
    "UnavailableDays"     integer[] NOT NULL DEFAULT '{}',
    -- Day | Night
    "PreferredShiftTypes" text[] NOT NULL DEFAULT '{}',
    "AvoidShiftTypes"     text[] NOT NULL DEFAULT '{}',
    "MaxNightsPerWeek"    integer NULL,
    "Note"                text NULL,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_timeshift_shift_preferences_emp" ON timeshift_shift_preferences ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS timeshift_swap_requests (
    "Id"                    uuid PRIMARY KEY,
    "TenantSlug"            character varying(64) NOT NULL,
    "RequesterEmployeeId"   uuid NOT NULL,
    "RequesterAssignmentId" uuid NOT NULL,
    "TargetEmployeeId"      uuid NOT NULL,
    -- NULL: devretme (karşılığında vardiya alınmaz)
    "TargetAssignmentId"    uuid NULL,
    -- PendingPeer | PendingApproval | Approved | Rejected | Declined | Cancelled
    "Status"                text NOT NULL,
    "Note"                  text NULL,
    "PeerRespondedAt"       timestamptz NULL,
    "DecidedBy"             text NULL,
    "DecidedAt"             timestamptz NULL,
    "RejectReason"          text NULL,
    "CreatedAt"             timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_timeshift_swap_requests_TenantSlug" ON timeshift_swap_requests ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_timeshift_swap_requests_people" ON timeshift_swap_requests ("RequesterEmployeeId", "TargetEmployeeId");

/* ============================================================ G7 puantaj ayarları */

CREATE TABLE IF NOT EXISTS timeshift_settings (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "LateGraceMinutes"    integer NOT NULL DEFAULT 5,
    "DefaultStart"        time without time zone NOT NULL DEFAULT '09:00',
    "DefaultEnd"          time without time zone NOT NULL DEFAULT '18:00',
    "DefaultBreakMinutes" integer NOT NULL DEFAULT 60,
    "UpdatedAt"           timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_timeshift_settings_TenantSlug" ON timeshift_settings ("TenantSlug");

-- ===== 2026-10-09_platform_gov
-- Dalga 5d (governance-service): özel alanlar (Y24), kayıtlı/zamanlanmış raporlar (Y25/G4),
-- OTP ile basit elektronik imza (Y28), REST hook abonelikleri (G29). İdempotent.

-- ---------------------------------------------------------------- Y24 özel alanlar
CREATE TABLE IF NOT EXISTS governance_custom_fields (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "Target" text NOT NULL DEFAULT 'Employee',
    "Key" character varying(64) NOT NULL,
    "Label" text NOT NULL,
    "Type" text NOT NULL,
    "Options" jsonb NOT NULL DEFAULT '[]'::jsonb,
    "Required" boolean NOT NULL DEFAULT false,
    "Visibility" text NOT NULL DEFAULT 'hr',
    "SelfEditable" boolean NOT NULL DEFAULT false,
    "IsSpecialCategory" boolean NOT NULL DEFAULT false,
    "LegalBasis" text NOT NULL,
    "Purpose" text NOT NULL,
    "RetentionMonths" integer NOT NULL,
    "AssessmentId" uuid NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "SortOrder" integer NOT NULL DEFAULT 0,
    "CreatedBy" text NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_custom_fields" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_custom_fields_key" ON governance_custom_fields ("TenantSlug", "Target", "Key");

CREATE TABLE IF NOT EXISTS governance_custom_field_values (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "FieldId" uuid NOT NULL REFERENCES governance_custom_fields ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    -- Özel nitelikli alanlarda "enc1:" + AES-256-GCM (TENANT_SECRET_KEY)
    "Value" text NOT NULL,
    "UpdatedBy" text NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_custom_field_values" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_custom_field_values" ON governance_custom_field_values ("FieldId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_governance_custom_field_values_emp" ON governance_custom_field_values ("TenantSlug", "EmployeeId");

-- ---------------------------------------------------------------- Y25 / G4 kayıtlı raporlar
CREATE TABLE IF NOT EXISTS governance_saved_reports (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "OwnerUserId" text NOT NULL,
    "OwnerEmployeeId" uuid NULL,
    "OwnerIsHr" boolean NOT NULL DEFAULT false,
    "Name" text NOT NULL,
    "Question" text NOT NULL,
    "Lang" text NOT NULL DEFAULT 'tr',
    "DepartmentId" uuid NULL,
    "FromDate" date NULL,
    "ToDate" date NULL,
    "Compare" boolean NULL,
    "Metric" text NOT NULL,
    "GroupBy" text NOT NULL,
    "PersonLevel" boolean NOT NULL DEFAULT false,
    "Pinned" boolean NOT NULL DEFAULT false,
    "Schedule" text NOT NULL DEFAULT 'None',
    "NextRunAt" timestamp with time zone NULL,
    "LastRunAt" timestamp with time zone NULL,
    "DeliveryCount" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_saved_reports" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_governance_saved_reports_owner" ON governance_saved_reports ("TenantSlug", "OwnerUserId");
CREATE INDEX IF NOT EXISTS "IX_governance_saved_reports_due" ON governance_saved_reports ("NextRunAt") WHERE "Schedule" <> 'None';
-- Teslim saati (Europe/Istanbul, SS:dd) ve günü (haftalık 1–7 pazartesi = 1; aylık 1–28; boşsa pazartesi / ayın 1'i).
ALTER TABLE governance_saved_reports ADD COLUMN IF NOT EXISTS "ScheduleTime" character varying(5) NOT NULL DEFAULT '07:00';
ALTER TABLE governance_saved_reports ADD COLUMN IF NOT EXISTS "ScheduleDay" integer NULL;

-- ---------------------------------------------------------------- Y28 OTP ile basit elektronik imza
CREATE TABLE IF NOT EXISTS governance_signature_otps (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "DocumentId" uuid NOT NULL REFERENCES governance_document_requests ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    -- HMAC-SHA256(TENANT_SECRET_KEY, Id + kod); kodun kendisi saklanmaz
    "CodeHash" text NOT NULL,
    "Channel" text NOT NULL,
    "Attempts" integer NOT NULL DEFAULT 0,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "ConsumedAt" timestamp with time zone NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_governance_signature_otps" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_governance_signature_otps_doc" ON governance_signature_otps ("DocumentId");

-- İmza kanıtı belgeye bağlıdır: belge saklama süresi sonunda silinince kanıt da silinir (CASCADE).
CREATE TABLE IF NOT EXISTS governance_signatures (
    "Id" uuid NOT NULL,
    "TenantSlug" character varying(64) NOT NULL,
    "DocumentType" text NOT NULL DEFAULT 'DocumentRequest',
    "DocumentId" uuid NOT NULL REFERENCES governance_document_requests ("Id") ON DELETE CASCADE,
    "DocumentVersion" integer NOT NULL DEFAULT 1,
    "DocumentSha256" text NOT NULL,
    "SignerEmployeeId" uuid NOT NULL,
    "SignedAt" timestamp with time zone NOT NULL,
    "Method" text NOT NULL,
    "IpPrefix" text NULL,
    "Disclaimer" text NOT NULL,
    "EvidenceSha256" text NOT NULL,
    CONSTRAINT "PK_governance_signatures" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_signatures_doc" ON governance_signatures ("DocumentId", "DocumentVersion", "SignerEmployeeId");

-- ---------------------------------------------------------------- G29 REST hook abonelikleri (Zapier / n8n)
ALTER TABLE governance_webhooks ADD COLUMN IF NOT EXISTS "Source" text NULL;
ALTER TABLE governance_webhooks ADD COLUMN IF NOT EXISTS "ApiKeyId" uuid NULL;

-- ===== 2026-10-09_notification_prefs
-- Dalga 5d / G11: bildirim tercihleri (kategori x kanal), sessiz saatler, günlük özet.
-- İdempotent; tekrar çalıştırılabilir.

-- Kişi başı genel ayarlar (dil satırı zaten vardı): sessiz saatler ve günlük özet.
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietHoursEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietStart" time without time zone NOT NULL DEFAULT '22:00';
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietEnd" time without time zone NOT NULL DEFAULT '08:00';
-- Bit maskesi: 1 << DayOfWeek (Pazar = 0). 127 = her gün. Gün, pencerenin BAŞLADIĞI gündür.
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "QuietDays" integer NOT NULL DEFAULT 127;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "DigestEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "DigestHour" integer NOT NULL DEFAULT 18;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "DigestLastSentAt" timestamp with time zone NULL;

-- Kategori x kanal tercihleri. Satır yoksa tüm kanallar açık (varsayılan).
CREATE TABLE IF NOT EXISTS notification_category_prefs (
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Category" character varying(32) NOT NULL,
    "InApp" boolean NOT NULL DEFAULT true,
    "Email" boolean NOT NULL DEFAULT true,
    "Push" boolean NOT NULL DEFAULT true,
    "Chat" boolean NOT NULL DEFAULT true,
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_notification_category_prefs" PRIMARY KEY ("TenantSlug", "EmployeeId", "Category")
);
CREATE INDEX IF NOT EXISTS "IX_notification_category_prefs_EmployeeId" ON notification_category_prefs ("EmployeeId");

-- E-posta / anlık bildirim ertelemesi (sessiz saatler): bu andan önce gönderilmez (düşürülmez).
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "DeferredUntil" timestamp with time zone NULL;
CREATE INDEX IF NOT EXISTS "IX_notification_messages_email_pending"
    ON notification_messages ("CreatedAt") WHERE "Channel" = 'Email' AND "Status" = 'Pending';
CREATE INDEX IF NOT EXISTS "IX_notification_messages_digest_queued"
    ON notification_messages ("RecipientEmployeeId") WHERE "Status" = 'DigestQueued';

-- ===== 2026-10-09_identity_sign
-- Dalga 5d: Y26 (SCIM 2.0 + LDAP/AD dizin sağlama), G28 (kiracıya özel alan adı + marka),
-- Y28 (OTP ile basit elektronik imza). Idempotent: tekrar çalıştırılabilir.
--
-- KVKK (veri minimizasyonu): dizinden yalnızca kullanıcı adı, ad, soyad, birincil e-posta, unvan,
-- departman ve etkinlik durumu saklanır (+ IdP eşleştirmesi için teknik dış kimlik). Telefon,
-- adres, fotoğraf, yönetici vb. nitelikler yok sayılır.

/* ============================================================ SCIM erişim jetonları (tenant-service) */

CREATE TABLE IF NOT EXISTS tenant_scim_tokens (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Name"        text NOT NULL,
    -- Jetonun kendisi ASLA saklanmaz: yalnızca SHA-256 özeti (hex) ve görüntüleme için ilk karakterleri.
    "TokenHash"   character varying(64) NOT NULL,
    "TokenPrefix" character varying(24) NOT NULL,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "CreatedBy"   text NULL,
    "LastUsedAt"  timestamptz NULL,
    "RevokedAt"   timestamptz NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_scim_tokens_TokenHash" ON tenant_scim_tokens ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_tenant_scim_tokens_TenantSlug" ON tenant_scim_tokens ("TenantSlug");

/* ============================================================ dizin ayarları (SCIM + LDAP) */

CREATE TABLE IF NOT EXISTS tenant_directory_settings (
    "Id"                        uuid PRIMARY KEY,
    "TenantSlug"                character varying(64) NOT NULL,
    -- Yeni oluşturulan hesaplara parola belirleme e-postası gönderilsin mi (SSO kullanan kiracılar kapatır).
    "SendInvitations"           boolean NOT NULL DEFAULT true,
    "LdapEnabled"               boolean NOT NULL DEFAULT false,
    "LdapAutoSync"              boolean NOT NULL DEFAULT false,
    "LdapUrl"                   text NULL,
    "LdapBindDn"                text NULL,
    -- AES-256-GCM (TENANT_SECRET_KEY, SmtpCredentialProtector) ile şifreli; düz metin asla tutulmaz.
    "LdapBindPasswordEncrypted" text NULL,
    "LdapBaseDn"                text NULL,
    "LdapUserFilter"            text NULL,
    "LdapUsernameAttr"          text NOT NULL DEFAULT 'uid',
    "LdapEmailAttr"             text NOT NULL DEFAULT 'mail',
    "LdapDepartmentAttr"        text NULL,
    "LdapTitleAttr"             text NULL,
    "LdapDisabledAttr"          text NULL,
    "LastSyncAt"                timestamptz NULL,
    "LastSyncTrigger"           text NULL,
    "LastSyncStatus"            text NULL,
    "LastSyncSummary"           text NULL,
    "UpdatedAt"                 timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_directory_settings_TenantSlug" ON tenant_directory_settings ("TenantSlug");
-- Önceki taslaktan kalan sütunlar (telefon izni) kaldırılır; yeni sütunlar eklenir.
ALTER TABLE tenant_directory_settings DROP COLUMN IF EXISTS "ScimAllowPhone";
ALTER TABLE tenant_directory_settings ADD COLUMN IF NOT EXISTS "LdapTitleAttr" text NULL;

/* ============================================================ dizinden sağlanan kullanıcılar */

CREATE TABLE IF NOT EXISTS tenant_directory_users (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    -- scim | ldap
    "Source"         text NOT NULL,
    "ExternalId"     text NULL,
    "UserName"       text NOT NULL,
    "GivenName"      text NULL,
    "FamilyName"     text NULL,
    "Email"          text NOT NULL,
    "Title"          text NULL,
    "Department"     text NULL,
    "Active"         boolean NOT NULL DEFAULT true,
    "KeycloakUserId" text NULL,
    "EmployeeId"     uuid NULL,
    -- Pending | Linked | Failed
    "EmployeeState"  text NOT NULL DEFAULT 'Pending',
    "EmployeeError"  text NULL,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"      timestamptz NOT NULL DEFAULT now(),
    "DeactivatedAt"  timestamptz NULL
);
ALTER TABLE tenant_directory_users DROP COLUMN IF EXISTS "Phone";
ALTER TABLE tenant_directory_users DROP COLUMN IF EXISTS "HireDate";
ALTER TABLE tenant_directory_users DROP COLUMN IF EXISTS "Roles";
ALTER TABLE tenant_directory_users ADD COLUMN IF NOT EXISTS "Title" text NULL;
CREATE INDEX IF NOT EXISTS "IX_tenant_directory_users_TenantSlug" ON tenant_directory_users ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_directory_users_Tenant_UserName"
    ON tenant_directory_users ("TenantSlug", lower("UserName"));
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_directory_users_Tenant_Email"
    ON tenant_directory_users ("TenantSlug", lower("Email"));
CREATE INDEX IF NOT EXISTS "IX_tenant_directory_users_Tenant_ExternalId"
    ON tenant_directory_users ("TenantSlug", "Source", "ExternalId");

/* ============================================================ G28 özel alan adı */

CREATE TABLE IF NOT EXISTS tenant_custom_domains (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    -- Küçük harf, IDN ise punycode (xn--) biçiminde.
    "Domain"            text NOT NULL,
    -- DNS TXT kaydı: _hr360-verify.<alan adı> = bu değer.
    "VerificationToken" text NOT NULL,
    -- Pending | Verified
    "Status"            text NOT NULL DEFAULT 'Pending',
    "CreatedAt"         timestamptz NOT NULL DEFAULT now(),
    "VerifiedAt"        timestamptz NULL,
    "LastCheckedAt"     timestamptz NULL,
    "LastCheckError"    text NULL
);
CREATE INDEX IF NOT EXISTS "IX_tenant_custom_domains_TenantSlug" ON tenant_custom_domains ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_custom_domains_Tenant_Domain"
    ON tenant_custom_domains ("TenantSlug", "Domain");
-- Bir alan adı aynı anda yalnızca TEK kiracıda doğrulanmış olabilir.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_custom_domains_Verified"
    ON tenant_custom_domains ("Domain") WHERE "Status" = 'Verified';

/* ============================================================ Y28 basit e-imza (expense-service) */

-- İmza talebi: İK bir özlük dokümanını çalışana imzaya gönderir. Tek kullanımlık kod (OTP)
-- yalnızca SHA-256 (HMAC, TENANT_SECRET_KEY türevli anahtar) özetiyle tutulur; 10 dk geçerli, 5 deneme.
CREATE TABLE IF NOT EXISTS expense_document_signatures (
    "Id"                     uuid PRIMARY KEY,
    "TenantSlug"             character varying(64) NOT NULL,
    "DocumentId"             uuid NOT NULL,
    "EmployeeId"             uuid NOT NULL,
    "RequestedByEmployeeId"  uuid NULL,
    "RequestedByUserId"      text NULL,
    -- Pending | Signed | Cancelled | Superseded
    "Status"                 text NOT NULL DEFAULT 'Pending',
    -- Talep anındaki belge özeti; imzada yeniden hesaplanır, farklıysa imza reddedilir.
    "DocumentHash"           character varying(64) NOT NULL,
    "Message"                text NULL,
    "CreatedAt"              timestamptz NOT NULL DEFAULT now(),
    "OtpHash"                character varying(64) NULL,
    "OtpChannel"             text NULL,
    "OtpExpiresAt"           timestamptz NULL,
    "OtpAttempts"            integer NOT NULL DEFAULT 0,
    "OtpSentCount"           integer NOT NULL DEFAULT 0,
    "OtpLastSentAt"          timestamptz NULL,
    "SignedAt"               timestamptz NULL,
    "CancelledAt"            timestamptz NULL
);
CREATE INDEX IF NOT EXISTS "IX_expense_document_signatures_TenantSlug" ON expense_document_signatures ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_expense_document_signatures_DocumentId" ON expense_document_signatures ("DocumentId");
CREATE INDEX IF NOT EXISTS "IX_expense_document_signatures_EmployeeId" ON expense_document_signatures ("EmployeeId", "Status");
-- Bir dokümanın aynı anda tek açık talebi olabilir.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_expense_document_signatures_OnePending"
    ON expense_document_signatures ("DocumentId") WHERE "Status" = 'Pending';

-- İmza kanıtı: değiştirilemez (UPDATE tetikleyiciyle engellenir). Doküman silindiğinde
-- (saklama süresi dolduğunda imha) kanıt da birlikte silinir - doküman kadar saklanır.
CREATE TABLE IF NOT EXISTS expense_signature_evidence (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "SignatureId"       uuid NOT NULL,
    "DocumentId"        uuid NOT NULL,
    "SignerEmployeeId"  uuid NOT NULL,
    "SignedAt"          timestamptz NOT NULL,
    "DocumentHash"      character varying(64) NOT NULL,
    -- Son okteti maskelenmiş IP (IPv4 a.b.c.0, IPv6 /48).
    "IpMasked"          text NULL,
    -- Kullanıcı aracısının SHA-256 özeti (düz metin tutulmaz).
    "UserAgentHash"     character varying(64) NULL,
    "OtpChannel"        text NOT NULL,
    -- Yukarıdaki alanların kanonik SHA-256 özeti (bütünlük kontrolü).
    "EvidenceHash"      character varying(64) NOT NULL,
    "Method"            text NOT NULL DEFAULT 'SimpleElectronicSignature-OTP'
);
CREATE INDEX IF NOT EXISTS "IX_expense_signature_evidence_TenantSlug" ON expense_signature_evidence ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_expense_signature_evidence_SignatureId" ON expense_signature_evidence ("SignatureId");
CREATE INDEX IF NOT EXISTS "IX_expense_signature_evidence_DocumentId" ON expense_signature_evidence ("DocumentId");

CREATE OR REPLACE FUNCTION expense_signature_evidence_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'expense_signature_evidence kayıtları değiştirilemez';
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_expense_signature_evidence_immutable ON expense_signature_evidence;
CREATE TRIGGER trg_expense_signature_evidence_immutable
    BEFORE UPDATE ON expense_signature_evidence
    FOR EACH ROW EXECUTE FUNCTION expense_signature_evidence_immutable();

-- İmzalanmış doküman kilidi: imzalanan doküman satırı değiştirilemez (yeniden imza = yeni talep).
ALTER TABLE expense_documents ADD COLUMN IF NOT EXISTS "SignedAt" timestamptz NULL;
CREATE OR REPLACE FUNCTION expense_documents_signed_lock() RETURNS trigger AS $$
BEGIN
    IF OLD."SignedAt" IS NOT NULL THEN
        RAISE EXCEPTION 'İmzalanmış doküman değiştirilemez';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_expense_documents_signed_lock ON expense_documents;
CREATE TRIGGER trg_expense_documents_signed_lock
    BEFORE UPDATE ON expense_documents
    FOR EACH ROW EXECUTE FUNCTION expense_documents_signed_lock();

-- ===== 2026-10-09_paging_indexes
-- Dalga 5d / G24: sunucu tarafı sayfalama için indeksler (employee, leave, organization, performance).
-- İdempotent; canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya
--
-- Tipik sorgular (planlar: tests/perf/bench_lists.py - geri alınan bir işlemde TEST kiracısıyla EXPLAIN ANALYZE):
--   çalışan listesi   : WHERE "TenantSlug"=$1 [AND "Status"=$2] ORDER BY "FirstName","LastName","Id" LIMIT/OFFSET
--   izin talepleri    : WHERE "TenantSlug"=$1 [AND "EmployeeId"=$2] [AND "Status"=$3] ORDER BY "CreatedAt" DESC LIMIT/OFFSET
--   tarih çakışması   : WHERE "TenantSlug"=$1 AND "StartDate" <= $2 AND "EndDate" >= $3
--   izin bakiyeleri   : WHERE "TenantSlug"=$1 AND "Year"=$2 ORDER BY "Type","EmployeeId" LIMIT/OFFSET
--   departman üyeleri : WHERE "TenantSlug"=$1 AND "DepartmentId"=$2 AND "EffectiveTo" IS NULL
--   departman/ekip    : WHERE "TenantSlug"=$1 [AND "CompanyId"=$2] ORDER BY "Name","Id" LIMIT/OFFSET
--   değerlendirmeler  : WHERE "TenantSlug"=$1 [AND "EmployeeId"=$2 | "ReviewerEmployeeId"=$3] ORDER BY "CreatedAt" DESC
--   hedefler          : WHERE "TenantSlug"=$1 AND "EmployeeId"=$2 [AND "CycleId"=$3] ORDER BY "CreatedAt" DESC

-- employee-service
CREATE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_Name"
    ON employee_employees ("TenantSlug", "FirstName", "LastName", "Id");
CREATE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_Status"
    ON employee_employees ("TenantSlug", "Status");
CREATE INDEX IF NOT EXISTS "IX_employee_employees_TenantSlug_HireDate"
    ON employee_employees ("TenantSlug", "HireDate");
CREATE INDEX IF NOT EXISTS "IX_employee_assignments_TenantSlug_Department_Current"
    ON employee_assignments ("TenantSlug", "DepartmentId") WHERE "EffectiveTo" IS NULL;

-- leave-service
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_CreatedAt"
    ON leave_requests ("TenantSlug", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_Status_CreatedAt"
    ON leave_requests ("TenantSlug", "Status", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_Employee_CreatedAt"
    ON leave_requests ("TenantSlug", "EmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_StartDate"
    ON leave_requests ("TenantSlug", "StartDate");
-- Tarih çakışması (StartDate <= bitiş AND EndDate >= başlangıç): güncel aya bakan takvim
-- sorgularında EndDate alt sınırı seçicidir (geçmiş izinler elenir).
CREATE INDEX IF NOT EXISTS "IX_leave_requests_TenantSlug_EndDate_StartDate"
    ON leave_requests ("TenantSlug", "EndDate", "StartDate");
CREATE INDEX IF NOT EXISTS "IX_leave_balances_TenantSlug_Year_Type_Employee"
    ON leave_balances ("TenantSlug", "Year", "Type", "EmployeeId");

-- organization-service
CREATE INDEX IF NOT EXISTS "IX_organization_departments_TenantSlug_Name"
    ON organization_departments ("TenantSlug", "Name", "Id");
CREATE INDEX IF NOT EXISTS "IX_organization_teams_TenantSlug_Name"
    ON organization_teams ("TenantSlug", "Name", "Id");

-- performance-service
CREATE INDEX IF NOT EXISTS "IX_performance_reviews_TenantSlug_CreatedAt"
    ON performance_reviews ("TenantSlug", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_performance_reviews_TenantSlug_Employee_CreatedAt"
    ON performance_reviews ("TenantSlug", "EmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_performance_reviews_TenantSlug_Reviewer"
    ON performance_reviews ("TenantSlug", "ReviewerEmployeeId");
CREATE INDEX IF NOT EXISTS "IX_performance_goals_TenantSlug_Employee_CreatedAt"
    ON performance_goals ("TenantSlug", "EmployeeId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_performance_goals_TenantSlug_CreatedAt"
    ON performance_goals ("TenantSlug", "CreatedAt" DESC);

ANALYZE employee_employees;
ANALYZE employee_assignments;
ANALYZE leave_requests;
ANALYZE leave_balances;
ANALYZE organization_departments;
ANALYZE organization_teams;
ANALYZE performance_reviews;
ANALYZE performance_goals;

-- Sunucu tarafı arama arayüzdeki gibi Türkçe harf katlamalı olsun ("ayse" → "Ayşe").
-- employee-service / leave-service EF sorgularında DbContext.Fold olarak eşlenir.
CREATE OR REPLACE FUNCTION public.hr360_fold(value text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$ SELECT translate(lower(translate(value, 'İI', 'ii')), 'ışğüöçâîû', 'isguocaiu') $$;

-- ===== 2026-10-10_chat_plus
-- Dalga 5e: sohbet botu özellikleri (B6–B20, BG4–BG20).
-- Mattermost / Rocket.Chat sağlayıcıları, komut/özellik yönetimi, bekleyen işlemler (fiş, adım
-- doğrulaması, toplu onay, İK vakası), konuşma bağlamı (30 gün, şifreli), kullanım sayaçları,
-- iş tekrarı önleme, nabız anketi (anonim yanıt + ayrı "yanıtladı" kaydı), çıkış anketi ilerlemesi,
-- yıldönümü kutlaması izni. Tekrar çalıştırılabilir.

-- Sağlayıcıdan bağımsız alanlar (Mattermost / Rocket.Chat) ve yönetim ayarları.
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "ServerUrl" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "BotTokenEnc" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "BotUserId" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "IncomingTokenEnc" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "DisabledFeatures" text[] NOT NULL DEFAULT '{}';
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "ChannelId" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "CelebrationsEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "RespectQuietHours" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "ButtonTtlDays" integer NOT NULL DEFAULT 7;

-- Onay bekleyen sohbet işlemleri: fiş önerisi, izin iptali onayı, toplu onay, adım doğrulaması (BG13),
-- İK vakası önerisi. Yük şifreli (enc1:), kısa ömürlü; tek kullanımlık kodun yalnızca özeti tutulur.
CREATE TABLE IF NOT EXISTS governance_chat_pending (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "PayloadEnc" text NOT NULL,
    "State" text NOT NULL DEFAULT 'Pending',
    "CodeHash" text,
    "OtpHash" text,
    "OtpExpiresAt" timestamptz,
    "Summary" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "ConfirmedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_pending_emp" ON governance_chat_pending ("TenantSlug", "EmployeeId", "State");
CREATE INDEX IF NOT EXISTS "IX_governance_chat_pending_code" ON governance_chat_pending ("CodeHash");

-- BG16 asistan konuşma bağlamı: son N tur, 30 gün, şifreli; "geçmişimi sil" ile silinir.
CREATE TABLE IF NOT EXISTS governance_chat_context (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Role" text NOT NULL,
    "TextEnc" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_context_emp" ON governance_chat_context ("TenantSlug", "EmployeeId", "CreatedAt");

-- BG20 kullanım sayaçları (yalnızca sayı; kişi ya da mesaj yok) ve son hatalar.
CREATE TABLE IF NOT EXISTS governance_chat_usage (
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "Day" date NOT NULL,
    "Feature" text NOT NULL,
    "Outcome" text NOT NULL,
    "Count" integer NOT NULL DEFAULT 0,
    PRIMARY KEY ("TenantSlug", "AppId", "Day", "Feature", "Outcome")
);
CREATE TABLE IF NOT EXISTS governance_chat_errors (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "Feature" text NOT NULL,
    "Message" text NOT NULL,
    "At" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_errors_app" ON governance_chat_errors ("AppId", "At" DESC);

-- Zamanlanmış bot mesajlarının tekrarını önler (karşılama, kontrol listesi, kutlama, hatırlatma...).
CREATE TABLE IF NOT EXISTS governance_chat_job_log (
    "TenantSlug" character varying(64) NOT NULL,
    "Key" text NOT NULL,
    "SentAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("TenantSlug", "Key")
);

-- B12 nabız anketi: soru engagement_surveys'te (Kind = Pulse, anonim); yanıt engagement_survey_responses'a
-- kimliksiz yazılır. Burada yalnızca gönderim planı ve "yanıtladı" bilgisi (tekrarı önlemek için) tutulur.
CREATE TABLE IF NOT EXISTS governance_chat_pulses (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "SurveyId" uuid NOT NULL,
    "Question" text NOT NULL,
    "SendAt" timestamptz NOT NULL,
    "ClosesAt" timestamptz,
    "Status" text NOT NULL DEFAULT 'Scheduled',
    "SentCount" integer NOT NULL DEFAULT 0,
    "CreatedBy" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS governance_chat_pulse_answered (
    "TenantSlug" character varying(64) NOT NULL,
    "PulseId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "AnsweredOn" date NOT NULL,
    PRIMARY KEY ("PulseId", "EmployeeId")
);

-- B20 çıkış anketi ilerlemesi (yanıtlar şifreli; bitince ayrılış kaydına yazılır ve bu satırın yanıtları silinir).
CREATE TABLE IF NOT EXISTS governance_chat_exit_progress (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "CaseId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Step" integer NOT NULL DEFAULT 0,
    "AnswersEnc" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "CompletedAt" timestamptz,
    UNIQUE ("CaseId")
);

-- B11 yıldönümü kutlaması için açık izin (doğum günü için engagement_profiles."ShowBirthday" kullanılır).
CREATE TABLE IF NOT EXISTS governance_chat_optins (
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "ShowAnniversary" boolean NOT NULL DEFAULT false,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("TenantSlug", "EmployeeId")
);

-- ===== 2026-10-11_signature_unify
-- Y28 — tek imza motoru (governance-service). İdempotent.
-- governance_signatures / governance_signature_otps artık belge türünden bağımsız:
--   DocumentType = 'DocumentRequest' (düzenlenmiş belge talepleri, governance)
--   DocumentType = 'HrDocument'      (İK özlük dokümanları, expense_documents; expense-service iç uçlarla çağırır)
-- expense_signature_evidence bu değişiklikten önceki imzalar için SALT OKUNUR kalır.

-- ---------------------------------------------------------------- kodlar
ALTER TABLE governance_signature_otps ADD COLUMN IF NOT EXISTS "DocumentType" text NOT NULL DEFAULT 'DocumentRequest';
CREATE INDEX IF NOT EXISTS "IX_governance_signature_otps_type_doc"
    ON governance_signature_otps ("TenantSlug", "DocumentType", "DocumentId", "CreatedAt");

-- ---------------------------------------------------------------- kanıtlar
ALTER TABLE governance_signatures ADD COLUMN IF NOT EXISTS "Title" text NULL;
CREATE INDEX IF NOT EXISTS "IX_governance_signatures_signer" ON governance_signatures ("TenantSlug", "SignerEmployeeId", "SignedAt");
CREATE INDEX IF NOT EXISTS "IX_governance_signatures_type_doc" ON governance_signatures ("TenantSlug", "DocumentType", "DocumentId");
-- Benzersizlik belge türünü de kapsar (aynı belge sürümünü aynı kişi bir kez imzalar).
DROP INDEX IF EXISTS "UX_governance_signatures_doc";
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_signatures_type_doc"
    ON governance_signatures ("DocumentType", "DocumentId", "DocumentVersion", "SignerEmployeeId");

-- Belge talebine bağlı yabancı anahtarlar kaldırılır (HrDocument kimlikleri expense_documents'tadır).
-- Saklama bağı tetikleyicilerle korunur: belge silinince (saklama sonu imha) kodlar ve kanıt da silinir.
ALTER TABLE governance_signatures DROP CONSTRAINT IF EXISTS "governance_signatures_DocumentId_fkey";
ALTER TABLE governance_signature_otps DROP CONSTRAINT IF EXISTS "governance_signature_otps_DocumentId_fkey";

CREATE OR REPLACE FUNCTION governance_signatures_cascade_document_request() RETURNS trigger AS $$
BEGIN
    DELETE FROM governance_signature_otps WHERE "DocumentType" = 'DocumentRequest' AND "DocumentId" = OLD."Id";
    DELETE FROM governance_signatures WHERE "DocumentType" = 'DocumentRequest' AND "DocumentId" = OLD."Id";
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_cascade_document_request ON governance_document_requests;
CREATE TRIGGER trg_governance_signatures_cascade_document_request
    AFTER DELETE ON governance_document_requests
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_cascade_document_request();

CREATE OR REPLACE FUNCTION governance_signatures_cascade_hr_document() RETURNS trigger AS $$
BEGIN
    DELETE FROM governance_signature_otps WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = OLD."Id";
    DELETE FROM governance_signatures WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = OLD."Id";
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_cascade_hr_document ON expense_documents;
CREATE TRIGGER trg_governance_signatures_cascade_hr_document
    AFTER DELETE ON expense_documents
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_cascade_hr_document();

-- Kanıt satırı değiştirilemez (silme yalnızca saklama sonu imha için serbest).
CREATE OR REPLACE FUNCTION governance_signatures_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'governance_signatures kayıtları değiştirilemez';
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_immutable ON governance_signatures;
CREATE TRIGGER trg_governance_signatures_immutable
    BEFORE UPDATE ON governance_signatures
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_immutable();

-- ---------------------------------------------------------------- expense: talep → governance kanıtı
-- Yeni imzalarda kanıt governance_signatures'tadır (DocumentType 'HrDocument'); talep yalnızca kimliğini tutar.
-- OTP sütunları (OtpHash, OtpExpiresAt, ...) artık yazılmaz; eski satırlar için yerinde bırakılır.
ALTER TABLE expense_document_signatures ADD COLUMN IF NOT EXISTS "EvidenceRef" uuid NULL;

-- ===== 2026-10-12_goal_due_date
-- Performans hedeflerine isteğe bağlı son tarih. İdempotent.
-- Boş (NULL) bırakılabilir; doğrulama (dönem bitişinden sonra olamaz) performance-service'te.
ALTER TABLE performance_goals ADD COLUMN IF NOT EXISTS "DueDate" date NULL;

-- ===== 2026-10-13_workflow_security
-- Onay akışı ve olay güvenliği (dalga 5, grup A). İdempotent: tekrar çalıştırılabilir.

-- ---------------------------------------------------------------- 1) Sızmış e-posta karar jetonları
-- workflow.submitted olayı tek kullanımlık e-posta karar jetonunu (ActionToken) ve onaycı
-- e-postasını taşıyordu; governance bunları governance_events'e ham yazıyor, olay radarı
-- (/api/events/recent, yöneticilere açıktı) ve açık API (events:read) döndürüyordu.
-- governance artık saklamadan önce ayıklıyor; mevcut kayıtlardan da silinir.
UPDATE governance_events
   SET "Payload" = "Payload" - 'ActionToken' - 'actionToken' - 'ApproverEmail' - 'approverEmail'
 WHERE jsonb_typeof("Payload") = 'object'
   AND "Payload" ?| ARRAY['ActionToken', 'actionToken', 'ApproverEmail', 'approverEmail'];

-- Sızmış olabilecek jetonlar geçersiz kılınır: e-postadaki "İncele ve karar ver" bağlantıları
-- artık çalışmaz, onaycılar uygulamadan karar verir. Yeni jeton yalnızca adım yeniden
-- atandığında (sıra o adıma geldiğinde, vekâlet başladığında, süre aşımıyla iletildiğinde)
-- workflow-service tarafından üretilir.
UPDATE workflow_approval_steps
   SET "ActionTokenHash" = NULL, "ActionTokenExpiresAt" = NULL
 WHERE "ActionTokenHash" IS NOT NULL OR "ActionTokenExpiresAt" IS NOT NULL;

-- ---------------------------------------------------------------- 2) İK onaycısı (onay ayarları)
-- Üst onaycısı bulunamayan talepler (departman başının kendi izni vb.) ve süre aşımında
-- iletilecek üst yöneticisi olmayan adımlar bu kişiye gider. Kiracı başına tek satır.
CREATE TABLE IF NOT EXISTS workflow_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "HrApproverEmployeeId" uuid,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_workflow_settings_TenantSlug" ON workflow_settings ("TenantSlug");

-- ---------------------------------------------------------------- 3) Eski izin başlıkları
-- "2 günlük Annual talebi (...)" → "2 günlük yıllık izin talebi (...)". Eşleme leave-service
-- LeaveRequestsController.TypeLabel ile aynı. Yalnızca bu kalıba uyan başlıklar değişir.
UPDATE workflow_requests w
   SET "Subject" = regexp_replace(w."Subject", '^([0-9.,]+ (günlük|saatlik)) ' || m.en || ' talebi', '\1 ' || m.tr || ' talebi')
  FROM (VALUES ('Annual', 'yıllık izin'), ('Sick', 'hastalık izni'), ('Unpaid', 'ücretsiz izin'),
               ('Maternity', 'doğum izni'), ('Paternity', 'babalık izni'), ('Marriage', 'evlilik izni'),
               ('Bereavement', 'vefat izni')) AS m(en, tr)
 WHERE w."Type" = 'LeaveRequest'
   AND w."Subject" ~ ('^[0-9.,]+ (günlük|saatlik) ' || m.en || ' talebi');

-- ===== 2026-10-14_audit_anchors
-- Güvenlik dalgası 1: denetim zincirinin gecelik çapası (governance AuditChainGuard).
-- Kiracı başına her gece zincir başı (son sıra no + özet) yazılır; sonraki kontrolde önceki çapa
-- satırının hâlâ aynı özetle durduğu ve zincirin geriye gitmediği denetlenir.
-- Platform düzeyi kayıtlar (TenantSlug NULL) için "TenantSlug" = ''.
CREATE TABLE IF NOT EXISTS governance_audit_anchors (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "CheckedAt" timestamptz NOT NULL DEFAULT now(),
    "Ok" boolean NOT NULL,
    "Rows" bigint NOT NULL DEFAULT 0,
    "FromSeq" bigint,
    "ToSeq" bigint,
    "HeadHash" text,
    "Problem" text
);
CREATE INDEX IF NOT EXISTS "IX_governance_audit_anchors_Tenant" ON governance_audit_anchors ("TenantSlug", "CheckedAt" DESC);

-- ===== 2026-10-15_department_links
-- Dalga 2 (şema görünümleri): matris organizasyon — departmanlar arası noktalı çizgi bağları.
-- organization-service DepartmentLinksController. "From" departmanı "To" departmanına fonksiyonel
-- ya da proje bağıyla (ikincil) raporlar. Departman silinince bağları da silinir.
CREATE TABLE IF NOT EXISTS organization_department_links (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "FromDepartmentId" uuid NOT NULL REFERENCES organization_departments ("Id") ON DELETE CASCADE,
    "ToDepartmentId" uuid NOT NULL REFERENCES organization_departments ("Id") ON DELETE CASCADE,
    "Kind" varchar(32) NOT NULL DEFAULT 'Functional',
    "Note" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_organization_department_links_NotSelf" CHECK ("FromDepartmentId" <> "ToDepartmentId"),
    CONSTRAINT "CK_organization_department_links_Kind" CHECK ("Kind" IN ('Functional', 'Project'))
);
CREATE INDEX IF NOT EXISTS "IX_organization_department_links_TenantSlug" ON organization_department_links ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_organization_department_links_To" ON organization_department_links ("ToDepartmentId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_organization_department_links_Pair"
    ON organization_department_links ("TenantSlug", "FromDepartmentId", "ToDepartmentId", "Kind");

-- ===== 2026-10-16_identity_security
-- Güvenlik dalgası 2A (tenant-service + tüm servislerin kiracı kapısı):
--   * iki adımlı doğrulama politikası (şirket başına),
--   * şüpheli giriş tespiti (Keycloak LOGIN / LOGIN_ERROR olayları),
--   * platform yöneticisinin süreli kiracı erişim izni (break-glass).
-- Idempotent.

-- İki adımlı doğrulama zorunluluğu: NULL/'off' = yok, 'privileged' = İK, şirket yöneticisi ve
-- yönetici rolleri, 'all' = tüm şirket kullanıcıları. tenant-service düzenli olarak uygular.
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "MfaPolicy" character varying(16);

-- Şüpheli giriş: kullanıcının daha önce giriş yaptığı ağlar. KVKK: ham IP tutulmaz; ağ öneki
-- (IPv4 /24, IPv6 /48) anahtarlı özetle (HMAC-SHA256) saklanır. 180 gün kullanılmayan satır silinir.
CREATE TABLE IF NOT EXISTS tenant_login_networks (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserId" character varying(64) NOT NULL,
    "NetworkHash" character varying(64) NOT NULL,
    "FirstSeenAt" timestamptz NOT NULL,
    "LastSeenAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_tenant_login_networks_User_Network"
    ON tenant_login_networks ("TenantSlug", "UserId", "NetworkHash");
CREATE INDEX IF NOT EXISTS "IX_tenant_login_networks_LastSeenAt" ON tenant_login_networks ("LastSeenAt");

-- Şüpheli giriş uyarıları (yeni ağdan giriş, art arda hatalı parola). Güvenlik ekranında
-- şirket yöneticisine gösterilir; 180 gün saklanır.
CREATE TABLE IF NOT EXISTS tenant_security_alerts (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Kind" character varying(32) NOT NULL,
    "UserId" character varying(64),
    "Username" character varying(256),
    "NetworkHash" character varying(64),
    "Count" integer NOT NULL DEFAULT 1,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_tenant_security_alerts_TenantSlug" ON tenant_security_alerts ("TenantSlug", "CreatedAt" DESC);

-- Platform yöneticisinin süreli kiracı erişim izni: gerekçe zorunlu, en fazla 4 saat. Servislerin
-- kiracı kapısı (TenantStatusGate) platform yöneticisinin kiracı verisi isteklerinde etkin izin arar.
CREATE TABLE IF NOT EXISTS platform_access_grants (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "GrantedToUserId" character varying(64) NOT NULL,
    "GrantedToName" character varying(256),
    "Reason" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "RevokedAt" timestamptz,
    "RevokedByUserId" character varying(64),
    "RevokedByName" character varying(256)
);
CREATE INDEX IF NOT EXISTS "IX_platform_access_grants_Active"
    ON platform_access_grants ("TenantSlug", "GrantedToUserId", "ExpiresAt");
CREATE INDEX IF NOT EXISTS "IX_platform_access_grants_TenantSlug" ON platform_access_grants ("TenantSlug", "CreatedAt" DESC);

-- ===== 2026-10-17_data_protection
-- Güvenlik dalgası 2B — veri koruma. İdempotent: tekrar çalıştırılabilir.
-- Servisler (compensation, governance) bu dosya uygulanmadan dağıtılmamalı: compensation-service'in
-- EF modeli yeni sütunları okur.

-- ---------------------------------------------------------------- 1) Bordro görevler ayrılığı
-- Dönemi hesaplayan ve elle ek ödeme/kesinti giren kişi kayda geçer; aynı kişi dönemi kapatamaz
-- (compensation-service Payroll/SegregationOfDuties.cs). Eski satırlarda boş (kural uygulanmaz).
ALTER TABLE compensation_payroll_periods ADD COLUMN IF NOT EXISTS "CalculatedBy" text;
ALTER TABLE compensation_payroll_periods ADD COLUMN IF NOT EXISTS "CalculatedByName" text;
ALTER TABLE compensation_payroll_adjustments ADD COLUMN IF NOT EXISTS "CreatedBy" text;

-- ---------------------------------------------------------------- 2) Kiracı veri koruma ayarları
-- Kiracı başına tek satır; satır yoksa varsayılanlar (görevler ayrılığı AÇIK, 10 dakikada 50'den fazla
-- farklı kayıt = uyarı, engel kapalı). Görevler ayrılığını yalnızca şirket yöneticisi gerekçeyle kapatır.
CREATE TABLE IF NOT EXISTS governance_security_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "PayrollSod" boolean NOT NULL DEFAULT true,
    "PayrollSodReason" text,
    "MassViewThreshold" integer NOT NULL DEFAULT 50,
    "MassViewWindowMinutes" integer NOT NULL DEFAULT 10,
    "MassViewBlock" boolean NOT NULL DEFAULT false,
    "AlertEmployeeIds" uuid[] NOT NULL DEFAULT '{}',
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedBy" text,
    CONSTRAINT "CK_governance_security_settings_MassView" CHECK ("MassViewThreshold" BETWEEN 10 AND 1000 AND "MassViewWindowMinutes" BETWEEN 1 AND 60)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_security_settings_TenantSlug" ON governance_security_settings ("TenantSlug");

-- ---------------------------------------------------------------- 3) Güvenlik uyarıları (toplu görüntüleme)
-- governance MassViewWorker yazar. "BlockedUntil" yalnızca kiracı "geçici engel" ayarını açtıysa dolar;
-- engagement-service (TCKN/IBAN açma) ve governance (özel nitelikli ek alanlar) bu süre boyunca açmaz.
CREATE TABLE IF NOT EXISTS governance_security_alerts (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "Kind" varchar(32) NOT NULL,
    "UserId" text NOT NULL,
    "UserName" text,
    "DistinctCount" integer NOT NULL,
    "WindowMinutes" integer NOT NULL,
    "Threshold" integer NOT NULL,
    "DetectedAt" timestamptz NOT NULL DEFAULT now(),
    "BlockedUntil" timestamptz,
    "AcknowledgedAt" timestamptz,
    "AcknowledgedBy" text
);
CREATE INDEX IF NOT EXISTS "IX_governance_security_alerts_Tenant" ON governance_security_alerts ("TenantSlug", "DetectedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_governance_security_alerts_Block" ON governance_security_alerts ("TenantSlug", "UserId", "BlockedUntil");

-- ---------------------------------------------------------------- 4) İz kodu araması
-- Filigranlı çıktıların iz kodu audit_log."Changes"->>'traceCode' içinde; kodla kaynak bulma için kısmi indeks.
-- Toplu görüntüleme dedektörü son 60 dakikanın erişim satırlarını okur.
CREATE INDEX IF NOT EXISTS "IX_audit_log_TraceCode" ON audit_log ((("Changes"->>'traceCode'))) WHERE "Changes" ? 'traceCode';
CREATE INDEX IF NOT EXISTS "IX_audit_log_Access_Occurred" ON audit_log ("OccurredAt")
    WHERE "Action" IN ('Revealed', 'SensitiveViewed', 'Exported', 'Viewed');

-- ---------------------------------------------------------------- 5) Erişim gözden geçirme kampanyaları
-- İK başlatır (çeyreklik ya da istendiğinde); her satır bir kişinin o anki rol/izinleri ve gözden
-- geçirenin (departman başı; yoksa İK) kararıdır. Ad tutulmaz (çalışan tablosundan okunur).
CREATE TABLE IF NOT EXISTS governance_access_reviews (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "Title" text NOT NULL,
    "Status" varchar(16) NOT NULL DEFAULT 'Open',
    "DueDate" date,
    "CreatedBy" text NOT NULL,
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ClosedAt" timestamptz,
    "LastReminderAt" timestamptz,
    "QuarterReminderAt" timestamptz,
    CONSTRAINT "CK_governance_access_reviews_Status" CHECK ("Status" IN ('Open', 'Closed'))
);
CREATE INDEX IF NOT EXISTS "IX_governance_access_reviews_Tenant" ON governance_access_reviews ("TenantSlug", "CreatedAt" DESC);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_access_reviews_OneOpen" ON governance_access_reviews ("TenantSlug") WHERE "Status" = 'Open';

CREATE TABLE IF NOT EXISTS governance_access_review_items (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ReviewId" uuid NOT NULL REFERENCES governance_access_reviews ("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    "KeycloakUserId" text NOT NULL,
    "Roles" text[] NOT NULL DEFAULT '{}',
    "Permissions" text[] NOT NULL DEFAULT '{}',
    "ReviewerEmployeeId" uuid,
    "Decision" varchar(16),
    "RemoveRoles" text[] NOT NULL DEFAULT '{}',
    "Note" text,
    "DecidedBy" text,
    "DecidedByName" text,
    "DecidedAt" timestamptz,
    "AppliedAt" timestamptz,
    "AppliedBy" text,
    "ApplyResult" text,
    CONSTRAINT "CK_governance_access_review_items_Decision" CHECK ("Decision" IS NULL OR "Decision" IN ('Keep', 'Remove'))
);
CREATE INDEX IF NOT EXISTS "IX_governance_access_review_items_Review" ON governance_access_review_items ("ReviewId");
CREATE INDEX IF NOT EXISTS "IX_governance_access_review_items_Reviewer" ON governance_access_review_items ("TenantSlug", "ReviewerEmployeeId");

-- ===== 2026-10-18_expense_anomaly
-- Masraf denetimi (ML dalgası 1, madde 41 + 43): e-Fatura/e-Arşiv karekod alanları ve
-- gönderimde üretilen denetim işaretleri (olağan dışı tutar, olası mükerrer fiş).
-- İşaretler yalnızca onaycıya bilgidir; beyan otomatik reddedilmez. İdempotent.
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "SupplierTaxId" varchar(11);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "InvoiceNo" varchar(32);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "Ettn" varchar(36);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "VatAmount" numeric(18,2);
ALTER TABLE expense_items ADD COLUMN IF NOT EXISTS "AnomalyFlags" jsonb;
ALTER TABLE expense_claims ADD COLUMN IF NOT EXISTS "AnomalyCheckedAt" timestamptz;
-- Mükerrer fiş araması (aynı ETTN) için kiracı + ETTN dizini.
CREATE INDEX IF NOT EXISTS "IX_expense_items_TenantSlug_Ettn" ON expense_items ("TenantSlug", "Ettn") WHERE "Ettn" IS NOT NULL;

-- ===== 2026-10-18_ml_model_quality
-- ML dalgası 1 (madde 37 + 39): devir riski modelinin kiracı başına inceleme eşiği ve
-- adillik denetimi raporları. Eşik İK'ca kalibrasyon tablosuna bakılarak seçilir; eşik üstü
-- skor bir KARAR değildir, "insan incelemesi önerilir" işaretidir. Adillik raporu yalnızca toplu
-- grup oranlarını tutar (5'ten küçük gruplar ML servisinde gizlenir; kişi/kimlik yok). İdempotent.
CREATE TABLE IF NOT EXISTS governance_ml_model_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ModelName" varchar(128) NOT NULL,
    "RiskThreshold" numeric(4,3) NOT NULL,
    "UpdatedBy" varchar(128),
    "UpdatedByName" varchar(256),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_governance_ml_model_settings_Threshold" CHECK ("RiskThreshold" >= 0.01 AND "RiskThreshold" <= 0.99)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_ml_model_settings_Tenant_Model"
    ON governance_ml_model_settings ("TenantSlug", "ModelName");

CREATE TABLE IF NOT EXISTS governance_ml_fairness_reports (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ModelName" varchar(128) NOT NULL,
    "ModelVersion" varchar(32),
    "Threshold" numeric(4,3) NOT NULL,
    "Rows" integer NOT NULL,
    "SkippedRows" integer NOT NULL DEFAULT 0,
    "Report" jsonb NOT NULL,
    "CreatedBy" varchar(128),
    "CreatedByName" varchar(256),
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_ml_fairness_reports_Tenant_Created"
    ON governance_ml_fairness_reports ("TenantSlug", "CreatedAt" DESC);

-- ===== 2026-10-19_payroll_anomaly
-- Bordro denetimi (ML dalgası 2, madde 42): dönem hesaplanınca ml-inference'ın ürettiği işaretler
-- (çalışanın kendi geçmişine / eş grubuna göre olağan dışı fazla mesai, ek ödeme, kesinti, brüt;
-- yıllık 270 saat fazla mesai sınırı). Yalnızca bordroyu hazırlayan/onaylayana bilgi; hesaplamayı ve
-- kapatmayı engellemez, çalışanın kendi pusulasında gösterilmez. İdempotent.
ALTER TABLE compensation_payslips ADD COLUMN IF NOT EXISTS "AnomalyFlags" jsonb;
ALTER TABLE compensation_payroll_periods ADD COLUMN IF NOT EXISTS "AnomalyCheckedAt" timestamptz;

-- ===== 2026-10-20_rls
-- Güvenlik dalgası 3 / 19: kiracı yalıtımı için satır düzeyi güvenlik (RLS) — İSTEĞE BAĞLI.
-- İdempotent. Bu dosya RLS'Yİ AÇMAZ: yalnızca politikaları ve aç/kapa fonksiyonlarını oluşturur.
-- Politika olan ama RLS'si kapalı tabloda davranış değişmez.
--
-- Tasarım:
--   * "TenantSlug" sütunu olan her public tablosunda hr360_tenant_isolation politikası.
--   * Oturum değişkeni app.tenant (SET app.tenant = 'demo' ya da set_config('app.tenant', 'demo', true)).
--     Tanımsız/boşsa politika HER satırı gösterir (izin verici): RLS servisler değişkeni ayarlamadan
--     açılsa bile hiçbir şey kırılmaz. Tanımlıysa yalnızca o kiracının satırları okunur/yazılır.
--   * Tablo sahibi (hr360admin) RLS'den muaftır (FORCE kullanılmaz); politika yalnızca servis başına
--     rollerle (deploy/postgres/roles.sql) bağlanıldığında etkilidir.
--   * Açmak:   SELECT hr360_rls_enable();    Kapatmak: SELECT hr360_rls_disable();
--     Durum:   SELECT * FROM hr360_rls_status();
--   * Görünümler (analytics_*) sahibinin yetkisiyle çalışır; RLS'yi atlar.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- "TenantSlug" sütunu olup politikası olmayan tablolara politikayı ekler; eklenen sayısını döner.
CREATE OR REPLACE FUNCTION hr360_rls_apply_policies() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    FOR t IN
        SELECT c.relname
          FROM pg_class c
          JOIN pg_namespace ns ON ns.oid = c.relnamespace
          JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'TenantSlug' AND NOT a.attisdropped
         WHERE ns.nspname = 'public' AND c.relkind IN ('r', 'p')
           AND NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'hr360_tenant_isolation')
         ORDER BY c.relname
    LOOP
        -- Yalnızca eksik politika eklenir (var olanlara dokunulmaz: çalışan sistemde gereksiz tablo kilidi yok).
        EXECUTE format($p$CREATE POLICY hr360_tenant_isolation ON public.%I AS PERMISSIVE FOR ALL
            USING (coalesce(current_setting('app.tenant', true), '') = '' OR "TenantSlug" = current_setting('app.tenant', true))
            WITH CHECK (coalesce(current_setting('app.tenant', true), '') = '' OR "TenantSlug" = current_setting('app.tenant', true))$p$,
            t.relname);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

-- Politikası olan tablolarda RLS'yi açar (önce eksik politikaları tamamlar). Açılan tablo sayısını döner.
CREATE OR REPLACE FUNCTION hr360_rls_enable() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    PERFORM hr360_rls_apply_policies();
    FOR t IN
        SELECT c.relname FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
         WHERE p.polname = 'hr360_tenant_isolation' AND c.relnamespace = 'public'::regnamespace
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t.relname);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

CREATE OR REPLACE FUNCTION hr360_rls_disable() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    FOR t IN
        SELECT c.relname FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
         WHERE p.polname = 'hr360_tenant_isolation' AND c.relnamespace = 'public'::regnamespace AND c.relrowsecurity
    LOOP
        EXECUTE format('ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY', t.relname);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

CREATE OR REPLACE FUNCTION hr360_rls_status() RETURNS TABLE (table_name text, rls_enabled boolean, has_policy boolean)
LANGUAGE sql STABLE AS $$
    SELECT c.relname::text, c.relrowsecurity,
           EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'hr360_tenant_isolation')
      FROM pg_class c
      JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'TenantSlug' AND NOT a.attisdropped
     WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p')
     ORDER BY 1
$$;

-- Yalnızca yönetici çağırabilsin (servis rolleri RLS'yi kapatamasın).
REVOKE ALL ON FUNCTION hr360_rls_apply_policies() FROM PUBLIC;
REVOKE ALL ON FUNCTION hr360_rls_enable() FROM PUBLIC;
REVOKE ALL ON FUNCTION hr360_rls_disable() FROM PUBLIC;

DO $$ BEGIN PERFORM hr360_rls_apply_policies(); END $$;

-- ===== 2026-10-21_payroll_tr
-- Bordro dalgası 8 (madde 58–65): Türkiye bordrosu — yıllık/yarıyıllık bordro parametreleri, SGK APHB
-- alanları (meslek kodu, belge türü, kanun, SGDP), bordro ayarları (eksik gün kodları, banka şablonu,
-- hesap planı), fark bordrosu, kıdem/ihbar hesabı, e-bordro teslim/okundu kaydı, ücret bandı yürürlük
-- tarihi ve dışa aktarım dosya özeti. İdempotent; canlı veriyi değiştirmez (parametre tohumu yoktur).
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- 60) bordro parametreleri
-- Yıl içinde değişiklik (ör. Temmuz'da asgari ücret artışı) için "ValidFromMonth" (1 = yıl başı, 7 = Temmuz).
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "ValidFromMonth" integer NOT NULL DEFAULT 1;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "SgkEmployeeRate" numeric NOT NULL DEFAULT 0.14;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "UnemploymentEmployeeRate" numeric NOT NULL DEFAULT 0.01;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "UnemploymentEmployerRate" numeric NOT NULL DEFAULT 0.02;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "MinimumWageNet" numeric;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "MinimumWageExemption" boolean NOT NULL DEFAULT true;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "AgiMonthly" numeric;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "SeveranceCeilingH1" numeric;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "SeveranceCeilingH2" numeric;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "Verified" boolean NOT NULL DEFAULT true;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "Source" text;
ALTER TABLE compensation_payroll_parameters ADD COLUMN IF NOT EXISTS "UpdatedBy" text;
DROP INDEX IF EXISTS "IX_compensation_payroll_parameters_TenantSlug_Year";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payroll_parameters_TenantSlug_Year_From"
    ON compensation_payroll_parameters ("TenantSlug", "Year", "ValidFromMonth");

-- Tohum YOK (2026-10-27 ürün kararı): yıllık bordro parametrelerini bordro yetkilisi Bordro › Parametreler'de
-- girer; uygulama doğrulanmamış değer varsaymaz. Bu dosyanın önceki sürümüyle eklenmiş satırlar (UpdatedBy = 'tohum')
-- canlıda kalır; arayüz onları "örnek değer — kontrol edip kaydedin" diye işaretler.

-- ---------------------------------------------------------------- 58/62/64) bordro ayarları (şirket başına tek satır)
CREATE TABLE IF NOT EXISTS compensation_payroll_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "SgkJson" text NOT NULL DEFAULT '{}',
    "AccountMapJson" text NOT NULL DEFAULT '{}',
    "CostCentersJson" text NOT NULL DEFAULT '{}',
    "BankTemplateJson" text NOT NULL DEFAULT '{}',
    "UpdatedBy" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payroll_settings_TenantSlug" ON compensation_payroll_settings ("TenantSlug");

-- 58) Çalışanın SGK bildirim bilgileri: meslek kodu (ISCO-08 tabanlı SGK meslek kodu), belge türü/kanun
-- (boşsa şirket varsayılanı), SGDP (emekli çalışan). Özel nitelikli veri değildir; yalnızca bordro yetkilisi görür.
CREATE TABLE IF NOT EXISTS compensation_employee_sgk (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "OccupationCode" character varying(16),
    "DocumentType" character varying(4),
    "LawNo" character varying(8),
    "Sgdp" boolean NOT NULL DEFAULT false,
    "UpdatedBy" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_employee_sgk_emp" ON compensation_employee_sgk ("TenantSlug", "EmployeeId");

-- 62) dışa aktarım: üretilen dosyanın SHA-256 özeti ve biçimi (denetim kaydına da yazılır).
ALTER TABLE compensation_payroll_exports ADD COLUMN IF NOT EXISTS "ContentSha256" text;
ALTER TABLE compensation_payroll_exports ADD COLUMN IF NOT EXISTS "Format" text;

-- ---------------------------------------------------------------- 63) fark bordrosu
CREATE TABLE IF NOT EXISTS compensation_retro_diffs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "SourcePeriodId" uuid NOT NULL,
    "SourceYear" integer NOT NULL,
    "SourceMonth" integer NOT NULL,
    "OldBase" numeric(18,2) NOT NULL,
    "NewBase" numeric(18,2) NOT NULL,
    "OldGross" numeric(18,2) NOT NULL,
    "NewGross" numeric(18,2) NOT NULL,
    "DiffGross" numeric(18,2) NOT NULL,
    "Status" text NOT NULL DEFAULT 'Approved',
    "TargetPeriodId" uuid NOT NULL,
    "AdjustmentId" uuid,
    "CreatedBy" text NOT NULL DEFAULT '',
    "CreatedByName" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_compensation_retro_diffs_emp" ON compensation_retro_diffs ("TenantSlug", "EmployeeId", "SourcePeriodId");
CREATE INDEX IF NOT EXISTS "IX_compensation_retro_diffs_target" ON compensation_retro_diffs ("TenantSlug", "TargetPeriodId");

-- ---------------------------------------------------------------- 61) kıdem ve ihbar
CREATE TABLE IF NOT EXISTS compensation_severance_calcs (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "OffboardingCaseId" uuid,
    "HireDate" date NOT NULL,
    "LastWorkingDay" date NOT NULL,
    "Reason" text NOT NULL,
    "InputJson" text NOT NULL DEFAULT '{}',
    "ResultJson" text NOT NULL DEFAULT '{}',
    "TotalNet" numeric(18,2) NOT NULL DEFAULT 0,
    "Status" text NOT NULL DEFAULT 'Draft',
    "PreparedBy" text NOT NULL DEFAULT '',
    "PreparedByName" text,
    "DecidedBy" text,
    "DecidedByName" text,
    "DecisionNote" text,
    "DecidedAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_compensation_severance_calcs_emp" ON compensation_severance_calcs ("TenantSlug", "EmployeeId");

-- ---------------------------------------------------------------- 59) e-bordro teslim ve okundu kaydı
-- "SealedCopy": teslim edilen pusulanın kanonik JSON'u, AES-256-GCM ile şifreli (TENANT_SECRET_KEYS;
-- anahtar yenilemede KeyRotationJob yeniden yazar). "ContentSha256" çalışanın onayladığı içeriğin özeti.
CREATE TABLE IF NOT EXISTS compensation_payslip_deliveries (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "PayslipId" uuid NOT NULL,
    "PeriodId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "ContentSha256" text NOT NULL,
    "SealedCopy" bytea,
    "PublishedBy" text,
    "PublishedAt" timestamptz NOT NULL DEFAULT now(),
    "EmailQueued" boolean NOT NULL DEFAULT false,
    "FirstOpenedAt" timestamptz,
    "LastOpenedAt" timestamptz,
    "OpenCount" integer NOT NULL DEFAULT 0,
    "AcknowledgedAt" timestamptz,
    "AcknowledgedSha256" text,
    "AckIpPrefix" text
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_compensation_payslip_deliveries_slip" ON compensation_payslip_deliveries ("TenantSlug", "PayslipId");
CREATE INDEX IF NOT EXISTS "IX_compensation_payslip_deliveries_period" ON compensation_payslip_deliveries ("TenantSlug", "PeriodId");

-- ---------------------------------------------------------------- 65) ücret bandı yürürlük tarihi
ALTER TABLE compensation_salary_bands ADD COLUMN IF NOT EXISTS "EffectiveFrom" date;

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-22_time_leave
-- Dalga 9 (madde 44, 66–72): izin, puantaj ve vardiya.
--  66) çalışma süresi kuralları (11 saat dinlenme, haftalık 45 / günlük 11 saat, gece 7,5 saat, ardışık gün) kiracı başına
--  67) konum doğrulamalı giriş-çıkış: yalnızca "noktada mı + uzaklık aralığı"; ham koordinat yalnızca şirket açarsa, süreli
--  68) QR kiosk: (yeni tablo yok; kiosk terminal anahtarıyla çalışır)
--  69) ekip izin çakışması eşiği, 70) saatlik izinde günlük çalışma saati, 71) devir üst sınırı: leave_settings
--  70) kısmi gün izni vardiya takviminde (timeshift_shift_overrides."IsPartial")
--  72) yarım gün resmî tatil (arife, 28 Ekim) ve puantaj dönemi kilidi
-- İdempotent; mevcut veriyi değiştirmez (yeni sütunlar varsayılanla gelir, davranış eskisiyle aynı kalır).
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- 66) çalışma süresi kuralları
-- Varsayılanlar 4857 s. İş Kanunu m.63/m.69 ve Çalışma Süreleri Yönetmeliği değerleridir; sektörel istisnalar
-- (ör. sağlık, güvenlik) için şirket değiştirebilir. Takas onayında kural ihlali engeldir; atamada uyarıdır.
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "MinRestHours" numeric NOT NULL DEFAULT 11;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "WeeklyMaxHours" numeric NOT NULL DEFAULT 45;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "DailyMaxHours" numeric NOT NULL DEFAULT 11;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "NightMaxHours" numeric NOT NULL DEFAULT 7.5;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "MaxConsecutiveDays" integer NOT NULL DEFAULT 6;

-- ---------------------------------------------------------------- 67) konum doğrulama
-- "GeoOutsidePolicy": Block = nokta dışından giriş reddedilir (önceki davranış), Flag = kaydedilir, "noktada değil" işaretlenir.
-- "GeoStoreRaw": ham koordinat saklama (varsayılan kapalı); açıksa "GeoRawRetentionDays" gün sonra silinir.
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "GeoOutsidePolicy" character varying(16) NOT NULL DEFAULT 'Block';
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "GeoStoreRaw" boolean NOT NULL DEFAULT false;
ALTER TABLE timeshift_settings ADD COLUMN IF NOT EXISTS "GeoRawRetentionDays" integer NOT NULL DEFAULT 30;
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "DistanceBucket" character varying(16);
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "RawLatitude" double precision;
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "RawLongitude" double precision;
ALTER TABLE timeshift_clock_punches ADD COLUMN IF NOT EXISTS "RawExpiresAt" timestamptz;
CREATE INDEX IF NOT EXISTS "IX_timeshift_clock_punches_RawExpiresAt" ON timeshift_clock_punches ("RawExpiresAt") WHERE "RawExpiresAt" IS NOT NULL;

-- ---------------------------------------------------------------- 70) kısmi gün izni takvimde
ALTER TABLE timeshift_shift_overrides ADD COLUMN IF NOT EXISTS "IsPartial" boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------- 72) puantaj dönemi kilidi
-- Kapalı dönemde giriş-çıkış, puantaj düzeltmesi ve fazla mesai talebi/kararı yapılamaz; bordro bu dönemin
-- onaylı fazla mesaisini okur. Yeniden açma gerekçeyle ve denetim kaydıyla.
CREATE TABLE IF NOT EXISTS timeshift_timesheet_periods (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year" integer NOT NULL,
    "Month" integer NOT NULL,
    "Status" character varying(16) NOT NULL DEFAULT 'Open',
    "ClosedAt" timestamptz,
    "ClosedBy" text,
    "ReopenedAt" timestamptz,
    "ReopenedBy" text,
    "ReopenReason" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_timeshift_timesheet_periods_month" ON timeshift_timesheet_periods ("TenantSlug", "Year", "Month");

-- ---------------------------------------------------------------- 72) yarım gün resmî tatil
-- Arife günleri ve 28 Ekim öğleden sonra tatildir: izin gün hesabında 0,5 gün sayılır.
ALTER TABLE leave_public_holidays ADD COLUMN IF NOT EXISTS "IsHalfDay" boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------- 69–71) izin ayarları (kiracı başına tek satır)
CREATE TABLE IF NOT EXISTS leave_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    -- Saatlik izinde gün = saat / günlük çalışma saati (boşsa LEAVE_DAY_HOURS, o da yoksa 7,5).
    "DayHours" numeric,
    -- Ekip çakışma uyarısı: aynı departmanda aynı günlerde izinli/izin bekleyen oranı bu yüzdeyi aşarsa.
    "ConflictWarnEnabled" boolean NOT NULL DEFAULT true,
    "ConflictThresholdPercent" integer NOT NULL DEFAULT 30,
    -- Yıllık izin devrinde varsayılan üst sınır (boş = sınırsız; yasal olarak yıllık izin yanmaz).
    "CarryOverMaxDays" numeric,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_leave_settings_TenantSlug" ON leave_settings ("TenantSlug");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-23_kvkk_ml
-- Dalga 10: KVKK (VERBİS envanteri, başvuru süre takibi ve veri paketi, rıza yenileme kampanyası,
-- imha kapsamı ve nesne deposu doğrulaması) + ML (kiracı verisiyle devir modeli eğitimi izni).
-- İdempotent; tüm tablolar "TenantSlug" ile kiracıya bağlı.

-- 53) VERBİS envanteri: ürünün yerleşik kataloğundan tohumlanır, İK/KVKK sorumlusu düzenler.
CREATE TABLE IF NOT EXISTS governance_privacy_inventory (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ActivityKey" varchar(80) NOT NULL,
    "Source" varchar(16) NOT NULL DEFAULT 'Catalog',
    "Module" varchar(120) NOT NULL,
    "Activity" varchar(300) NOT NULL,
    "Subjects" text[] NOT NULL DEFAULT '{}',
    "DataCategories" text[] NOT NULL DEFAULT '{}',
    "Purpose" text NOT NULL DEFAULT '',
    "LegalBasis" text NOT NULL DEFAULT '',
    "Special" boolean NOT NULL DEFAULT false,
    "Retention" text NOT NULL DEFAULT '',
    "RetentionCategory" varchar(64),
    "Recipients" text[] NOT NULL DEFAULT '{}',
    "TransferProviders" text[] NOT NULL DEFAULT '{}',
    "Measures" text NOT NULL DEFAULT '',
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedBy" varchar(128),
    "UpdatedByName" varchar(256),
    CONSTRAINT "CK_governance_privacy_inventory_Source" CHECK ("Source" IN ('Catalog','Custom'))
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_privacy_inventory_key" ON governance_privacy_inventory ("TenantSlug", "ActivityKey");

-- 54) İlgili kişi başvurusu: 20./27. gün hatırlatması ve süre aşımı uyarısı (her biri bir kez).
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Reminder20At" timestamptz;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "Reminder27At" timestamptz;
ALTER TABLE governance_data_requests ADD COLUMN IF NOT EXISTS "OverdueAlertAt" timestamptz;

-- 54) Erişim başvurusu veri paketi (ZIP, AES-256-GCM şifreli; süre sonunda silinir).
CREATE TABLE IF NOT EXISTS governance_data_request_packages (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "RequestId" uuid NOT NULL REFERENCES governance_data_requests("Id") ON DELETE CASCADE,
    "EmployeeId" uuid NOT NULL,
    "FileName" varchar(200) NOT NULL,
    "ContentEnc" text NOT NULL,
    "Sha256" varchar(64) NOT NULL,
    "SizeBytes" integer NOT NULL,
    "Sections" jsonb NOT NULL DEFAULT '{}'::jsonb,
    "CreatedBy" varchar(256) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "DownloadCount" integer NOT NULL DEFAULT 0,
    "LastDownloadedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_governance_data_request_packages_req" ON governance_data_request_packages ("TenantSlug", "RequestId");

-- 55) Rıza/aydınlatma metni yeni sürümünde yeniden onay kampanyası.
CREATE TABLE IF NOT EXISTS governance_consent_campaigns (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ConsentType" varchar(64) NOT NULL,
    "Version" varchar(40) NOT NULL,
    "PreviousVersion" varchar(40),
    "Status" varchar(16) NOT NULL DEFAULT 'Open',
    "StartedBy" varchar(256) NOT NULL,
    "StartedAt" timestamptz NOT NULL DEFAULT now(),
    "ClosedAt" timestamptz,
    "ClosedBy" varchar(256),
    "ReminderCount" integer NOT NULL DEFAULT 0,
    "LastReminderAt" timestamptz,
    CONSTRAINT "CK_governance_consent_campaigns_Status" CHECK ("Status" IN ('Open','Closed'))
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_consent_campaigns_version" ON governance_consent_campaigns ("TenantSlug", "ConsentType", "Version");

-- 56) İmha turunun tablo bazında dökümü (kaç satır, hangi işlem).
ALTER TABLE governance_destruction_logs ADD COLUMN IF NOT EXISTS "Details" jsonb;

-- 57) Silinen/anonimleştirilen kayıtların başvurduğu nesne deposu anahtarları ve silme doğrulaması.
-- Anahtarın kendisi (kişi adı içerebilir) işlendikten sonra NULL yapılır; yalnızca SHA-256 özeti kalır.
CREATE TABLE IF NOT EXISTS governance_storage_deletions (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "Category" varchar(64) NOT NULL,
    "SourceTable" varchar(80) NOT NULL,
    "SourceColumn" varchar(80) NOT NULL,
    "StorageKey" text,
    "KeyHash" varchar(64) NOT NULL,
    "Status" varchar(16) NOT NULL DEFAULT 'Pending',
    "Detail" text,
    "Attempts" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ProcessedAt" timestamptz,
    CONSTRAINT "CK_governance_storage_deletions_Status" CHECK ("Status" IN ('Pending','Deleted','Absent','NotStored','Failed'))
);
CREATE INDEX IF NOT EXISTS "IX_governance_storage_deletions_tenant" ON governance_storage_deletions ("TenantSlug", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_governance_storage_deletions_pending" ON governance_storage_deletions ("Status") WHERE "Status" IN ('Pending','Failed');

-- ML 36) Kiracı verisiyle devir modeli eğitimi: kiracının açık izni (varsayılan kapalı) ve son eğitim özeti.
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "TrainOnTenantData" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "TrainConsentBy" varchar(256);
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "TrainConsentAt" timestamptz;
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "LastTenantTrainingAt" timestamptz;
ALTER TABLE governance_ml_model_settings ADD COLUMN IF NOT EXISTS "LastTenantTraining" jsonb;

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-24_recruitment_w11
-- Dalga 11 (işe alım): çalışan aday önerisi (73), aday durum bağlantısı (74), Google for Jobs
-- alanları (75), aşama geçmişi (78 — huni analizi). İdempotent.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- Kiracı başına işe alım program ayarları: öneri ödülü ve ilan yayın tercihleri.
CREATE TABLE IF NOT EXISTS recruitment_program_settings (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ReferralEnabled" boolean NOT NULL DEFAULT true,
    "ReferralRewardAmount" numeric(14,2) NULL,
    "ReferralRewardCurrency" varchar(3) NOT NULL DEFAULT 'TRY',
    -- Ödül, işe girişten bu kadar gün sonra (deneme süresi; İş K. m.15 en çok 2 ay) hak edilir.
    "ReferralProbationDays" integer NOT NULL DEFAULT 60,
    "ReferralRewardNote" varchar(500) NULL,
    -- Google for Jobs (JobPosting JSON-LD) çıktısında ücret aralığı yalnızca kiracı açarsa yer alır.
    "PublishSalaryInJobPostings" boolean NOT NULL DEFAULT false,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recruitment_program_settings_tenant ON recruitment_program_settings ("TenantSlug");

-- İlanın herkese açık yapılandırılmış veri alanları (75).
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "Location" varchar(120) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "Region" varchar(120) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "Country" varchar(2) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "RemoteAllowed" boolean NOT NULL DEFAULT false;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "ValidThrough" timestamptz NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryMin" numeric(14,2) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryMax" numeric(14,2) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryCurrency" varchar(3) NULL;
ALTER TABLE recruitment_job_postings ADD COLUMN IF NOT EXISTS "SalaryPeriod" varchar(10) NULL;

-- Çalışan aday önerisi (73). Aday kişisel verisi burada TUTULMAZ (aday kaydına bağlanır); aday
-- silinince/anonimleşince bağlantı boşalır, öneri yalnızca öneren + ilan + ödül durumu olarak kalır.
CREATE TABLE IF NOT EXISTS recruitment_referrals (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "JobPostingId" uuid NOT NULL REFERENCES recruitment_job_postings("Id") ON DELETE CASCADE,
    "ReferrerEmployeeId" uuid NOT NULL,
    "CandidateId" uuid NULL REFERENCES recruitment_candidates("Id") ON DELETE SET NULL,
    "ApplicationId" uuid NULL REFERENCES recruitment_applications("Id") ON DELETE SET NULL,
    "Relationship" varchar(40) NULL,
    "Note" varchar(1000) NULL,
    -- Öneren, adayın önerilmeyi kabul ettiğini beyan eder (onay kutusu); aday ayrıca KVKK m.10 e-postası alır.
    "CandidateConsentConfirmed" boolean NOT NULL,
    "ConsentConfirmedAt" timestamptz NOT NULL,
    "NoticeSentAt" timestamptz NULL,
    -- None | Waiting | Eligible | Approved | Paid | Forfeited | NotEligible
    "RewardStatus" varchar(20) NOT NULL DEFAULT 'None',
    "HiredAt" timestamptz NULL,
    "RewardEligibleAt" timestamptz NULL,
    "RewardAmount" numeric(14,2) NULL,
    "RewardCurrency" varchar(3) NULL,
    "RewardDecidedAt" timestamptz NULL,
    "RewardDecidedByUserId" varchar(64) NULL,
    "RewardNote" varchar(500) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_recruitment_referrals_referrer ON recruitment_referrals ("TenantSlug", "ReferrerEmployeeId");
CREATE UNIQUE INDEX IF NOT EXISTS ux_recruitment_referrals_application ON recruitment_referrals ("ApplicationId") WHERE "ApplicationId" IS NOT NULL;

-- Aday durum bağlantısı (74): yalnızca kaba aşama + sonraki adım gösterir (kişisel veri yok).
-- Jetonun yalnızca SHA-256 özeti saklanır; İK ya da aday iptal edebilir; süresi dolar.
CREATE TABLE IF NOT EXISTS recruitment_status_links (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "ApplicationId" uuid NOT NULL REFERENCES recruitment_applications("Id") ON DELETE CASCADE,
    "TokenHash" varchar(64) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "RevokedAt" timestamptz NULL,
    -- Hr | Candidate | Reissued
    "RevokedBy" varchar(20) NULL,
    "LastViewedAt" timestamptz NULL,
    "ViewCount" integer NOT NULL DEFAULT 0,
    "CreatedByUserId" varchar(64) NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recruitment_status_links_hash ON recruitment_status_links ("TokenHash");
CREATE INDEX IF NOT EXISTS ix_recruitment_status_links_app ON recruitment_status_links ("ApplicationId");

-- Aşama geçmişi (78): aşamada geçen süre ve işe alım süresi için. Tetikleyiciyle doldurulur, böylece
-- durumu değiştiren her yol (kanban, teklif yanıtı, olay tüketicisi...) kaydedilir. Kişisel veri yok;
-- başvuru silinince birlikte silinir.
CREATE TABLE IF NOT EXISTS recruitment_application_stage_events (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantSlug" varchar(64) NOT NULL,
    "ApplicationId" uuid NOT NULL REFERENCES recruitment_applications("Id") ON DELETE CASCADE,
    "FromStatus" varchar(20) NULL,
    "ToStatus" varchar(20) NOT NULL,
    "ChangedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_recruitment_stage_events_app ON recruitment_application_stage_events ("ApplicationId", "ChangedAt");
CREATE INDEX IF NOT EXISTS ix_recruitment_stage_events_tenant ON recruitment_application_stage_events ("TenantSlug", "ChangedAt");

-- SECURITY DEFINER: servis rolüne (deploy/postgres/roles.sql) bu tabloya yazma yetkisi gerekmeden çalışır.
CREATE OR REPLACE FUNCTION recruitment_stage_event() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
        VALUES (NEW."TenantSlug", NEW."Id", NULL, NEW."Status", coalesce(NEW."AppliedAt", now()));
    ELSIF NEW."Status" IS DISTINCT FROM OLD."Status" THEN
        INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
        VALUES (NEW."TenantSlug", NEW."Id", OLD."Status", NEW."Status", now());
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_recruitment_stage_event ON recruitment_applications;
CREATE TRIGGER trg_recruitment_stage_event AFTER INSERT OR UPDATE OF "Status" ON recruitment_applications
    FOR EACH ROW EXECUTE FUNCTION recruitment_stage_event();

-- Geçmişi olmayan mevcut başvurular için yaklaşık geçmiş: başvuru anı + son durum değişikliği.
INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
SELECT a."TenantSlug", a."Id", NULL, 'Applied', a."AppliedAt"
  FROM recruitment_applications a
 WHERE NOT EXISTS (SELECT 1 FROM recruitment_application_stage_events e WHERE e."ApplicationId" = a."Id");
INSERT INTO recruitment_application_stage_events ("TenantSlug","ApplicationId","FromStatus","ToStatus","ChangedAt")
SELECT a."TenantSlug", a."Id", 'Applied', a."Status", coalesce(a."StatusChangedAt", a."AppliedAt")
  FROM recruitment_applications a
 WHERE a."Status" <> 'Applied'
   AND NOT EXISTS (SELECT 1 FROM recruitment_application_stage_events e WHERE e."ApplicationId" = a."Id" AND e."ToStatus" <> 'Applied');

-- ===== 2026-10-24_offer_esign
-- Dalga 11 / madde 76: iş teklifi mektubunun aday tarafından basit elektronik imzayla kabulü. İdempotent.
-- Kod, imza ve kanıt governance-service'teki TEK imza motorundadır (DocumentType 'OfferLetter');
-- recruitment-service iç uçlarla (X-Internal-Token) çağırır. İmzalayan çalışan değil ADAYDIR:
-- governance_signatures."SignerEmployeeId" bu satırlarda aday kimliğini taşır, "SignerKind" = 'Candidate'.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- governance: imzalayan türü
-- Mevcut satırlar çalışan imzasıdır (varsayılan). Kanıt satırları değiştirilemez (BEFORE UPDATE tetikleyicisi);
-- sütun ekleme UPDATE sayılmaz.
ALTER TABLE governance_signatures ADD COLUMN IF NOT EXISTS "SignerKind" text NOT NULL DEFAULT 'Employee';

-- ---------------------------------------------------------------- recruitment: imza bağlantısı ve imzalı belge
-- SignTokenHash: aday imza bağlantısı jetonunun SHA-256 özeti (jeton saklanmaz; İK yeniler/iptal eder).
-- LetterSha256: imzalanan mektup metninin özeti (kanıttaki belge özeti).
-- SignedLetterHtml: imzalı belge (mektup + kanıt bloğu; ücret içerir); SignedDocumentSha256 onun özeti.
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignTokenHash" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignTokenCreatedAt" timestamp with time zone NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "LetterSha256" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignedLetterHtml" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignedDocumentSha256" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignatureEvidenceId" uuid NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignedAt" timestamp with time zone NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_recruitment_offers_sign_token"
    ON recruitment_offers ("SignTokenHash") WHERE "SignTokenHash" IS NOT NULL;

-- Saklama bağı: teklif silinince (aday KVKK silme talebi / imha — aday kaydından CASCADE) kodlar ve kanıt da silinir.
-- SECURITY DEFINER: servis başına rollerde recruitment rolüne governance tablolarında yetki verilmez; fonksiyon
-- yalnızca bu iki DELETE'i, silinen teklifin kimliğiyle yapar (search_path sabit).
CREATE OR REPLACE FUNCTION governance_signatures_cascade_offer_letter() RETURNS trigger
    SECURITY DEFINER SET search_path = public AS $$
BEGIN
    DELETE FROM governance_signature_otps WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = OLD."Id";
    DELETE FROM governance_signatures WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = OLD."Id";
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_cascade_offer_letter ON recruitment_offers;
CREATE TRIGGER trg_governance_signatures_cascade_offer_letter
    AFTER DELETE ON recruitment_offers
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_cascade_offer_letter();

-- ===== 2026-10-24_performance_w11
-- Dalga 11 / performans ve gelişim: kalibrasyon oturumu (79), OKR hizalama ağacı (80),
-- anonim 360 derece geri bildirim (81). İdempotent: tekrar çalıştırılabilir.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

/* ============================================================ 79 kalibrasyon oturumu */

-- İK'nın bir dönem için yürüttüğü kalibrasyon toplantısı. Oturum sonuçlanınca (Finalized) değişen
-- hücreler performance_ninebox_overrides'a gerekçeyle yazılır. Otomatik karar yok: her nihai hücre
-- İK onayı (Confirmed) ister.
CREATE TABLE IF NOT EXISTS performance_calibration_sessions (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      varchar(64) NOT NULL,
    "CycleId"         uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "Name"            varchar(200) NOT NULL,
    "Status"          varchar(20) NOT NULL DEFAULT 'Open' CHECK ("Status" IN ('Open','Finalized','Cancelled')),
    "CreatedBy"       varchar(200) NOT NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now(),
    "FinalizedBy"     varchar(200),
    "FinalizedAt"     timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_sessions_TenantSlug" ON performance_calibration_sessions ("TenantSlug");
-- Bir dönemde aynı anda tek açık oturum.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_calibration_sessions_open" ON performance_calibration_sessions ("TenantSlug", "CycleId") WHERE "Status" = 'Open';

CREATE TABLE IF NOT EXISTS performance_calibration_items (
    "Id"                       uuid PRIMARY KEY,
    "TenantSlug"               varchar(64) NOT NULL,
    "SessionId"                uuid NOT NULL REFERENCES performance_calibration_sessions("Id") ON DELETE CASCADE,
    "EmployeeId"               uuid NOT NULL,
    "OriginalPerformanceBand"  integer NOT NULL CHECK ("OriginalPerformanceBand" BETWEEN 1 AND 3),
    "OriginalPotentialBand"    integer NOT NULL CHECK ("OriginalPotentialBand" BETWEEN 1 AND 3),
    "PerformanceBand"          integer NOT NULL CHECK ("PerformanceBand" BETWEEN 1 AND 3),
    "PotentialBand"            integer NOT NULL CHECK ("PotentialBand" BETWEEN 1 AND 3),
    "Score"                    numeric(6,2),
    "DecisionNote"             varchar(1000),
    "Confirmed"                boolean NOT NULL DEFAULT false,
    "ConfirmedBy"              varchar(200),
    "ConfirmedAt"              timestamptz,
    "UpdatedAt"                timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_items_TenantSlug" ON performance_calibration_items ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_calibration_items_emp" ON performance_calibration_items ("SessionId", "EmployeeId");

-- Değişiklik günlüğü (yalnızca ekleme): her taşıma/onay kim, ne zaman, hangi hücreden hangisine.
CREATE TABLE IF NOT EXISTS performance_calibration_changes (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  varchar(64) NOT NULL,
    "SessionId"   uuid NOT NULL REFERENCES performance_calibration_sessions("Id") ON DELETE CASCADE,
    "EmployeeId"  uuid NOT NULL,
    "Action"      varchar(20) NOT NULL CHECK ("Action" IN ('Moved','Confirmed','Unconfirmed','Finalized')),
    "FromCell"    integer,
    "ToCell"      integer,
    "Note"        varchar(1000),
    "ChangedBy"   varchar(200) NOT NULL,
    "ChangedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_changes_TenantSlug" ON performance_calibration_changes ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_calibration_changes_session" ON performance_calibration_changes ("SessionId", "ChangedAt" DESC);

/* ============================================================ 80 OKR hizalama ağacı */

-- Şirket ve departman amaçları (kişisel hedefler performance_goals'ta kalır).
CREATE TABLE IF NOT EXISTS performance_objectives (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    varchar(64) NOT NULL,
    "CycleId"       uuid NOT NULL REFERENCES performance_cycles("Id") ON DELETE CASCADE,
    "Level"         varchar(20) NOT NULL CHECK ("Level" IN ('Company','Department')),
    "DepartmentId"  uuid,
    "ParentId"      uuid REFERENCES performance_objectives("Id") ON DELETE SET NULL,
    "Title"         varchar(200) NOT NULL,
    "Description"   varchar(2000),
    "Weight"        integer NOT NULL DEFAULT 100 CHECK ("Weight" BETWEEN 1 AND 100),
    "CreatedBy"     varchar(200),
    "CreatedAt"     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_performance_objectives_dept" CHECK (("Level" = 'Department') = ("DepartmentId" IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS "IX_performance_objectives_TenantSlug" ON performance_objectives ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_objectives_cycle" ON performance_objectives ("CycleId");

-- Kişisel hedefin bağlı olduğu şirket/departman amacı (isteğe bağlı).
ALTER TABLE performance_goals ADD COLUMN IF NOT EXISTS "ParentObjectiveId" uuid;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_performance_goals_objective') THEN
        ALTER TABLE performance_goals ADD CONSTRAINT "FK_performance_goals_objective"
            FOREIGN KEY ("ParentObjectiveId") REFERENCES performance_objectives("Id") ON DELETE SET NULL;
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS "IX_performance_goals_parent_objective" ON performance_goals ("ParentObjectiveId") WHERE "ParentObjectiveId" IS NOT NULL;

/* ============================================================ 81 anonim 360 geri bildirim */

-- 360 talebi: değerlendirilen kişi, yetkinlikler, kapanış. Sonuçlar yalnızca KAPANMIŞ ve en az
-- "MinResponses" (≥5) yanıt almış talepte gösterilir.
CREATE TABLE IF NOT EXISTS performance_f360_requests (
    "Id"                 uuid PRIMARY KEY,
    "TenantSlug"         varchar(64) NOT NULL,
    "CycleId"            uuid REFERENCES performance_cycles("Id") ON DELETE SET NULL,
    "SubjectEmployeeId"  uuid NOT NULL,
    "Title"              varchar(200) NOT NULL,
    "CompetenciesJson"   jsonb NOT NULL DEFAULT '[]',
    "Status"             varchar(20) NOT NULL DEFAULT 'Open' CHECK ("Status" IN ('Open','Closed')),
    "DueDate"            date,
    "MinResponses"       integer NOT NULL DEFAULT 5 CHECK ("MinResponses" >= 5),
    "ReleasedToSubject"  boolean NOT NULL DEFAULT false,
    "CreatedByEmployeeId" uuid,
    "CreatedBy"          varchar(200) NOT NULL,
    "CreatedAt"          timestamptz NOT NULL DEFAULT now(),
    "ClosedAt"           timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_performance_f360_requests_TenantSlug" ON performance_f360_requests ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_f360_requests_subject" ON performance_f360_requests ("SubjectEmployeeId");

-- Katılım: kim davet edildi, yanıtladı mı. Yanıtla İLİŞKİLENDİRİLMEZ (zaman damgası da tutulmaz).
CREATE TABLE IF NOT EXISTS performance_f360_participants (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          varchar(64) NOT NULL,
    "RequestId"           uuid NOT NULL REFERENCES performance_f360_requests("Id") ON DELETE CASCADE,
    "ReviewerEmployeeId"  uuid NOT NULL,
    "Relationship"        varchar(20) NOT NULL CHECK ("Relationship" IN ('Manager','Peer','DirectReport','Other')),
    "Submitted"           boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_performance_f360_participants_TenantSlug" ON performance_f360_participants ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_performance_f360_participants" ON performance_f360_participants ("RequestId", "ReviewerEmployeeId");
CREATE INDEX IF NOT EXISTS "IX_performance_f360_participants_reviewer" ON performance_f360_participants ("ReviewerEmployeeId");

-- Yanıt: değerlendiren kimliği, ilişki türü ve zaman damgası YOK (anonimlik).
CREATE TABLE IF NOT EXISTS performance_f360_responses (
    "Id"           uuid PRIMARY KEY,
    "TenantSlug"   varchar(64) NOT NULL,
    "RequestId"    uuid NOT NULL REFERENCES performance_f360_requests("Id") ON DELETE CASCADE,
    "RatingsJson"  jsonb NOT NULL DEFAULT '{}',
    "Comment"      varchar(2000)
);
CREATE INDEX IF NOT EXISTS "IX_performance_f360_responses_TenantSlug" ON performance_f360_responses ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_performance_f360_responses_request" ON performance_f360_responses ("RequestId");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-24_learning_w11
-- Dalga 11 (madde 82–84): öğrenme ve gelişim.
--  82) yetkinlik açığı → eğitim önerisi: yeni tablo yok (mevcut learning_course_competencies + ML /skills/recommend)
--  83) kariyer yolları: rol basamakları (pozisyon unvanı) ve basamak başına beklenen yetkinlik seviyeleri
--  84) zorunlu eğitim son tarihi (learning_enrollments."DueOn") ve eğitim/İSG eğitimi hatırlatmaları
--      (30/7/0 gün; çalışana ve yöneticisine) — gönderilen hatırlatma tekrar gitmesin diye kayıt tablosu.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- 84) son tarih + hatırlatma kaydı
ALTER TABLE learning_enrollments ADD COLUMN IF NOT EXISTS "DueOn" date NULL;
CREATE INDEX IF NOT EXISTS "IX_learning_enrollments_DueOn" ON learning_enrollments ("TenantSlug", "DueOn") WHERE "DueOn" IS NOT NULL;

-- SourceType: Training (learning_enrollments) | Osh (governance_osh_trainings, katılımcı başına).
-- DueOn anahtarda: son tarih değişirse (yenileme/erteleme) yeni hatırlatma döngüsü başlar.
CREATE TABLE IF NOT EXISTS learning_due_reminders (
    "Id"                  uuid PRIMARY KEY,
    "TenantSlug"          character varying(64) NOT NULL,
    "SourceType"          character varying(16) NOT NULL,
    "SourceId"            uuid NOT NULL,
    "SubjectEmployeeId"   uuid NOT NULL,
    "Kind"                character varying(16) NOT NULL,
    "RecipientEmployeeId" uuid NOT NULL,
    "DueOn"               date NOT NULL,
    "SentAt"              timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_due_reminders_TenantSlug" ON learning_due_reminders ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_due_reminders" ON learning_due_reminders
    ("TenantSlug", "SourceType", "SourceId", "SubjectEmployeeId", "Kind", "RecipientEmployeeId", "DueOn");

-- ---------------------------------------------------------------- 83) kariyer yolları
CREATE TABLE IF NOT EXISTS learning_career_paths (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Name"        character varying(150) NOT NULL,
    "Description" character varying(1000) NULL,
    "IsActive"    boolean NOT NULL DEFAULT true,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_learning_career_paths_TenantSlug" ON learning_career_paths ("TenantSlug");

CREATE TABLE IF NOT EXISTS learning_career_steps (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "PathId"        uuid NOT NULL REFERENCES learning_career_paths("Id") ON DELETE CASCADE,
    "StepOrder"     integer NOT NULL,
    "PositionTitle" character varying(150) NOT NULL,
    "Description"   character varying(1000) NULL,
    "MinMonths"     integer NULL
);
CREATE INDEX IF NOT EXISTS "IX_learning_career_steps_TenantSlug" ON learning_career_steps ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_learning_career_steps_PathId" ON learning_career_steps ("PathId", "StepOrder");

CREATE TABLE IF NOT EXISTS learning_career_step_requirements (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "StepId"        uuid NOT NULL REFERENCES learning_career_steps("Id") ON DELETE CASCADE,
    "CompetencyId"  uuid NOT NULL REFERENCES learning_competencies("Id") ON DELETE CASCADE,
    "RequiredLevel" integer NOT NULL CHECK ("RequiredLevel" BETWEEN 1 AND 5)
);
CREATE INDEX IF NOT EXISTS "IX_learning_career_step_requirements_TenantSlug" ON learning_career_step_requirements ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_learning_career_step_requirements" ON learning_career_step_requirements ("StepId", "CompetencyId");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-25_account_provisioning
-- Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma (işe giriş) ve askıya alma (ayrılış).
-- Kurulum düzeyinde ACCOUNT_PROVISIONING_ENABLED=true ile açılır; her istek İK onayından sonra gönderilir,
-- her adım audit_log'a yazılır. Sağlayıcı sırları (servis hesabı anahtarı / client secret) şifreli (SecretBox).
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- Kiracı başına sağlayıcı ayarı (Provider: Google | Microsoft).
--   Google   : CredentialsEnc = servis hesabı JSON anahtarı (alan genelinde yetki), AdminSubject = adına işlem yapılan yönetici
--   Microsoft: ClientId + CredentialsEnc (client secret) + MsTenant (dizin kimliği), uygulama izni User.ReadWrite.All
CREATE TABLE IF NOT EXISTS governance_provisioning_configs (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "Provider"       character varying(16) NOT NULL,
    "IsEnabled"      boolean NOT NULL DEFAULT false,
    "Domain"         character varying(253) NOT NULL,
    "AutoCreate"     boolean NOT NULL DEFAULT true,
    "AutoSuspend"    boolean NOT NULL DEFAULT true,
    "ClientId"       character varying(256) NULL,
    "CredentialsEnc" text NULL,
    "AdminSubject"   character varying(320) NULL,
    "OrgUnit"        character varying(256) NULL,
    "MsTenant"       character varying(64) NULL,
    "UsageLocation"  character varying(2) NULL,
    "LastTestAt"     timestamptz NULL,
    "LastError"      character varying(500) NULL,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"      timestamptz NOT NULL DEFAULT now(),
    "UpdatedBy"      character varying(128) NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_provisioning_configs_TenantSlug" ON governance_provisioning_configs ("TenantSlug");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_provisioning_configs" ON governance_provisioning_configs ("TenantSlug", "Provider");

-- İstek: Action Create | Suspend; Status Pending | Processing | Done | Failed | Rejected; Source Auto | Manual.
-- Kişisel veri en aza: ad/soyad tutulmaz (çalışan kaydından okunur), yalnızca iş hesabı adresi.
CREATE TABLE IF NOT EXISTS governance_provisioning_requests (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "EmployeeId"      uuid NOT NULL,
    "Provider"        character varying(16) NOT NULL,
    "Action"          character varying(16) NOT NULL,
    "Status"          character varying(16) NOT NULL DEFAULT 'Pending',
    "Source"          character varying(16) NOT NULL DEFAULT 'Auto',
    "AccountEmail"    character varying(320) NOT NULL,
    "ExternalId"      character varying(128) NULL,
    "Note"            character varying(500) NULL,
    "Error"           character varying(500) NULL,
    "RequestedBy"     character varying(128) NULL,
    "RequestedByName" character varying(200) NULL,
    "DecidedBy"       character varying(128) NULL,
    "DecidedByName"   character varying(200) NULL,
    "DecidedAt"       timestamptz NULL,
    "Attempts"        integer NOT NULL DEFAULT 0,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now(),
    "CompletedAt"     timestamptz NULL
);
CREATE INDEX IF NOT EXISTS "IX_governance_provisioning_requests_TenantSlug" ON governance_provisioning_requests ("TenantSlug", "Status");
-- Aynı çalışan/sağlayıcı/işlem için tek açık ya da tamamlanmış istek (otomatik tarama idempotent kalır).
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_provisioning_requests_open" ON governance_provisioning_requests
    ("TenantSlug", "EmployeeId", "Provider", "Action") WHERE "Status" IN ('Pending', 'Processing', 'Done');

-- ===== 2026-10-25_ui_prefs
-- Dalga 12 (madde 87, 89, 90): kullanıcı başına arayüz tercihleri.
--  87) liste ekranlarında kayıtlı filtre/görünümler   (anahtar: views:<tablo>)
--  89) kişiselleştirilebilir ana panel (widget sırası/gizli)  (anahtar: dashboard)
--  90) "Yenilikler" paneli: en son görülen sürüm notu        (anahtar: whatsnew)
-- Sahibi notification-service (kişi tercihlerinin sahibi). Kişi, Keycloak kimliğiyle ("UserSub")
-- tutulur: çalışan kaydı olmayan hesaplar (ör. İK yöneticisi) de tercih saklayabilir.
-- KVKK: yalnızca kişinin kendi arayüz ayarı; başkasına ait kişisel veri yazılmaz (değer 16 KB ile sınırlı).
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS notification_ui_prefs (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserSub"    character varying(64) NOT NULL,
    "Key"        character varying(100) NOT NULL,
    "Value"      jsonb NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_notification_ui_prefs_Owner_Key" ON notification_ui_prefs ("TenantSlug", "UserSub", "Key");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-25_webhook_retry_api_key_scopes
-- ===================================================================
-- Dalga 12 (madde 93-94): webhook teslimat yeniden denemesi ve kapsamlı API anahtarları
--  * governance_webhook_deliveries: olay kimliği, deneme sırası, sonraki deneme zamanı
--    (üstel geri çekilme), yeniden deneme durumu ve elle yeniden gönderim izi.
--    Yük (payload) teslimat tablosunda TUTULMAZ: yeniden gönderimde governance_events
--    (30 gün saklanır, kişisel alanları ayıklanmış kopya) kullanılır — veri en aza indirme.
--  * governance_api_keys: son kullanma, döndürme (rotate) zinciri, toplam kullanım,
--    son kullanılan yetki.
--  * governance_api_key_usage: anahtar × gün × yetki başına istek/ret sayacı
--    (IP veya istek içeriği tutulmaz). 180 günden eskiler bakım işinde silinir.
-- İdempotent.
-- ===================================================================

ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "EventId" uuid;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "Attempt" integer NOT NULL DEFAULT 1;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "NextRetryAt" timestamptz;
-- null: başarılı/yeniden denenmeyecek · pending: sırada · retrying: işleniyor · retried: yeni deneme yapıldı
-- gave_up: deneme hakkı bitti/uç kapalı · resent: elle yeniden gönderildi
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "RetryState" character varying(16);
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "Manual" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "ParentDeliveryId" uuid;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "TriggeredByName" text;

CREATE INDEX IF NOT EXISTS "IX_governance_webhook_deliveries_Retry"
    ON governance_webhook_deliveries ("NextRetryAt") WHERE "RetryState" = 'pending';
CREATE INDEX IF NOT EXISTS "IX_governance_webhook_deliveries_Failed"
    ON governance_webhook_deliveries ("TenantSlug", "OccurredAt" DESC) WHERE "Error" IS NOT NULL;

ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "ExpiresAt" timestamptz;
ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "RotatedFromId" uuid;
ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "UsageCount" bigint NOT NULL DEFAULT 0;
ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "LastUsedScope" character varying(64);

CREATE TABLE IF NOT EXISTS governance_api_key_usage (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "KeyId" uuid NOT NULL,
    "Day" date NOT NULL,
    "Scope" character varying(64) NOT NULL,
    "Count" integer NOT NULL DEFAULT 0,
    "DeniedCount" integer NOT NULL DEFAULT 0,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_api_key_usage" ON governance_api_key_usage ("KeyId", "Day", "Scope");
CREATE INDEX IF NOT EXISTS "IX_governance_api_key_usage_Tenant" ON governance_api_key_usage ("TenantSlug", "Day");

-- ===== 2026-10-25_indeksler
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

-- ===== 2026-10-26_opening_tax_base
-- Bordro: devreden kümülatif gelir vergisi matrahı (çalışan × yıl).
-- Yıl ortasında HR360'a geçen şirket ya da yıl içinde başka işverenden gelen çalışan için o yılın HR360
-- dışındaki GV matrahı toplamı; bordro hesabında ve ihbar GV'sinde kümülatif matraha eklenir.
-- Sahibi compensation-service. KVKK: ücret verisi; yalnızca bordro yetkilisi görür/yazar, audit_log'a yazılır.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS compensation_opening_tax_bases (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Year"       integer NOT NULL,
    "Amount"     numeric(18,2) NOT NULL DEFAULT 0,
    "Note"       character varying(300),
    "UpdatedBy"  text,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_compensation_opening_tax_bases_Emp_Year"
    ON compensation_opening_tax_bases ("TenantSlug", "EmployeeId", "Year");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-26_leave_holiday_autoload
-- İzin: resmî tatil otomatik yükleme işareti (şirket × yıl × veri sürümü).
-- HolidayAutoLoader, Türkiye takvimini kullanan şirketlerde bu yılın ve gelecek yılın EKSİK tatillerini
-- (yarım gün arifeler ve 28 Ekim dahil) gömülü tablodan bir kez ekler ve buraya yazar. Sürüm artınca
-- (ör. yarım gün desteği) eksikler bir kez daha tamamlanır. Şirketin sonradan sildiği/düzenlediği tatil
-- yeniden eklenmez ve mevcut kayıtlar değiştirilmez. Sahibi leave-service. Kişisel veri içermez.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS leave_holiday_autoloads (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Year"       integer NOT NULL,
    "Version"    integer NOT NULL,
    "Added"      integer NOT NULL DEFAULT 0,
    "AppliedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_leave_holiday_autoloads_Tenant_Year"
    ON leave_holiday_autoloads ("TenantSlug", "Year");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-26_org_scenario_owner
-- Org senaryo taslaklarının sahibi (Keycloak kullanıcı kimliği). Yönetici yalnızca kendi taslaklarını
-- görür/düzenler; İK tümünü. Eski satırlarda sahip bilinmez (NULL): yalnızca İK görür.
ALTER TABLE engagement_org_scenarios ADD COLUMN IF NOT EXISTS "CreatedByUserId" text NULL;
CREATE INDEX IF NOT EXISTS "IX_engagement_org_scenarios_Tenant_Owner"
    ON engagement_org_scenarios ("TenantSlug", "CreatedByUserId");

-- ===== 2026-10-26_access_review_accounts
-- Erişim gözden geçirme: çalışan kaydı olmayan hesaplar (ör. yalnızca yönetim için açılmış şirket
-- yöneticisi) da kampanyaya girer. Bu satırlarda "EmployeeId" boştur; ad Keycloak hesabından
-- ("AccountName": ad soyad ya da kullanıcı adı) alınır ve gözden geçiren İK'dır.
ALTER TABLE governance_access_review_items ALTER COLUMN "EmployeeId" DROP NOT NULL;
ALTER TABLE governance_access_review_items ADD COLUMN IF NOT EXISTS "AccountName" text NULL;

-- ===== 2026-10-15_plansiz
-- Paketsiz ürün: plan/paket sistemi, çalışan kotası ve faturalandırma kaldırıldı. Her kiracı bütün
-- modülleri ve özellikleri sınırsız kullanır. İdempotent.
ALTER TABLE platform_tenants DROP COLUMN IF EXISTS "Plan";
ALTER TABLE platform_tenants DROP COLUMN IF EXISTS "MaxEmployees";
DROP TABLE IF EXISTS governance_invoices;
-- Süreli davet linki (parola belirleme/sıfırlama) ve platform sistem ayarları. İdempotent.
--
-- tenant_password_invites: HR360'ın kendi tek kullanımlık, süreli parola linkleri. Token
-- veritabanında TUTULMAZ, yalnızca SHA-256 özeti ("TokenHash", hex). Yeni link aynı hesabın
-- açık linklerini iptal eder; kullanılınca "UsedAt" dolar.
CREATE TABLE IF NOT EXISTS tenant_password_invites (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NULL,
    "KeycloakUserId" varchar(64) NOT NULL,
    "Email" varchar(320) NOT NULL,
    "FullName" varchar(300) NULL,
    "Purpose" varchar(16) NOT NULL,
    "TokenHash" varchar(64) NULL,
    "Channel" varchar(16) NOT NULL,
    "EmailStatus" varchar(16) NOT NULL DEFAULT 'none',
    "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "UsedAt" timestamptz NULL,
    "RevokedAt" timestamptz NULL,
    "CreatedBy" varchar(64) NULL,
    "CreatedByName" varchar(300) NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_tenant_password_invites_TokenHash"
    ON tenant_password_invites ("TokenHash") WHERE "TokenHash" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_tenant_password_invites_User" ON tenant_password_invites ("KeycloakUserId");
CREATE INDEX IF NOT EXISTS "IX_tenant_password_invites_Tenant" ON tenant_password_invites ("TenantSlug", "CreatedAt" DESC);

-- platform_settings: arayüzden değiştirilen uygulama içi ayarlar (tek satır, "Id" = 1). Boş alan
-- = .env'deki ilk değer geçerli. SMTP parolası tenant anahtarıyla şifreli (anahtar yenilemesinde
-- KeyRotationJob yeniden yazar).
CREATE TABLE IF NOT EXISTS platform_settings (
    "Id" integer PRIMARY KEY,
    "SmtpMode" varchar(16) NULL,
    "SmtpHost" varchar(253) NULL,
    "SmtpPort" integer NULL,
    "SmtpUser" varchar(320) NULL,
    "SmtpPasswordEncrypted" text NULL,
    "SmtpFromAddress" varchar(320) NULL,
    "SmtpFromName" varchar(120) NULL,
    "SignupEnabled" boolean NULL,
    "PasswordMethod" varchar(16) NULL,
    "InviteTtlHours" integer NULL,
    "UpdatedAt" timestamptz NULL,
    "UpdatedBy" varchar(300) NULL
);

-- Şirket düzeyi parola belirleme yöntemi: NULL = platform varsayılanı; 'invite' | 'keycloak'.
ALTER TABLE platform_tenants ADD COLUMN IF NOT EXISTS "PasswordSetupMethod" varchar(16) NULL;

-- Herkese açık site bilgileri (Platform › Sistem ayarları › Yasal metinler ve iletişim): işletmeci
-- ve iletişim bilgileri, gizlilik politikası, kullanım koşulları, SSS, arama motoru görünürlüğü.
-- Boş alan = arayüzdeki varsayılan şablon. SearchIndexing NULL/false = arama motorlarına kapalı
-- (robots.txt "Disallow: /", sayfalarda noindex).
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteOperatorName" varchar(200) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteAddress" varchar(500) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteEmail" varchar(320) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SitePhone" varchar(40) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteKep" varchar(320) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SiteVerbisNo" varchar(64) NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "PrivacyPolicyMd" text NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "TermsMd" text NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "FaqJson" text NULL;
ALTER TABLE platform_settings ADD COLUMN IF NOT EXISTS "SearchIndexing" boolean NULL;

-- ===== 2026-10-28_employee_of_month
-- "Ayın çalışanı" oylaması (isteğe bağlı; kiracı ayarıyla açılır, varsayılan kapalı). Sahibi engagement-service.
--
-- engagement_eom_settings  : kiracı başına tek satır (takvim, kapsam, süreç, görünürlük, katsayılar).
-- engagement_eom_periods   : oylama dönemi. Açıldığı andaki ayarlar "Config" içinde dondurulur: dönem
--                            sürerken ayar değişikliği (özellikle görünürlük) o dönemi etkilemez.
-- engagement_eom_units     : dönem içindeki oylama birimleri (departman grupları); üyeler açılışta sabitlenir.
--                            İki aşamalı kipte 2. aşama birimi, alt birimlerin kazananları arasından seçer.
-- engagement_eom_nominations / engagement_eom_votes : aday gösterme ve oylar.
--
-- KVKK / oy gizliliği: görünürlük "gizli" ise oy satırında kişi kimliği tutulmaz ("VoterEmployeeId" boş);
-- yalnızca dönem tuzuyla üretilmiş özet ("VoterKey", tekrar oy ve oy değiştirme için) saklanır. Dönem
-- kapanınca tuz silinir: sonrasında veritabanından bile kimin kime oy verdiği çıkarılamaz. Oylar denetim
-- kaydına (audit_log) yazılmaz. Kimliğin görünür olduğu kiplerde kural oy verirken kişiye gösterilir.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS engagement_eom_settings (
    "Id"                 uuid PRIMARY KEY,
    "TenantSlug"         character varying(64) NOT NULL,
    "Enabled"            boolean NOT NULL DEFAULT false,
    "Cadence"            character varying(16) NOT NULL DEFAULT 'Monthly',
    "Scope"              character varying(24) NOT NULL DEFAULT 'TopLevel',
    "Process"            character varying(16) NOT NULL DEFAULT 'Direct',
    "Visibility"         character varying(24) NOT NULL DEFAULT 'WinnerOnly',
    "RevealTo"           character varying(16) NOT NULL DEFAULT 'HrAndHead',
    "StartDay"           integer NOT NULL DEFAULT 1,
    "OpenHour"           integer NOT NULL DEFAULT 9,
    "NominationDays"     integer NOT NULL DEFAULT 2,
    "FinalistCount"      integer NOT NULL DEFAULT 5,
    "VotingDays"         integer NOT NULL DEFAULT 3,
    "Stage2Days"         integer NOT NULL DEFAULT 1,
    "ReminderDaysBefore" integer NOT NULL DEFAULT 2,
    "CooldownPeriods"    integer NOT NULL DEFAULT 3,
    "WeightEmployee"     numeric(4,2) NOT NULL DEFAULT 1,
    "WeightManager"      numeric(4,2) NOT NULL DEFAULT 2,
    "WeightHr"           numeric(4,2) NOT NULL DEFAULT 2,
    "AllowComment"       boolean NOT NULL DEFAULT true,
    "UpdatedAt"          timestamptz NOT NULL DEFAULT now(),
    "UpdatedByName"      text
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_settings_TenantSlug" ON engagement_eom_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_periods (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "PeriodKey"         character varying(32) NOT NULL,
    "Status"            character varying(16) NOT NULL,
    "Config"            jsonb NOT NULL,
    "NominationStartsAt" timestamptz,
    "VotingStartsAt"    timestamptz NOT NULL,
    "Stage1EndsAt"      timestamptz NOT NULL,
    "Stage2EndsAt"      timestamptz,
    "Salt"              bytea,
    "UpcomingSentAt"    timestamptz,
    "OpenedSentAt"      timestamptz,
    "VotingSentAt"      timestamptz,
    "LastCallSentAt"    timestamptz,
    "Stage2SentAt"      timestamptz,
    "AnnouncedAt"       timestamptz,
    "CreatedAt"         timestamptz NOT NULL DEFAULT now(),
    "ClosedAt"          timestamptz
);
-- Aynı dönem iki kez açılmaz (işçi yarışına karşı); iptal edilen dönem yeniden başlatılabilir.
-- Elle başlatılan dönem anahtarı "M-yyyyMMddHHmmssfff" (19 karakter); ilk sürüm 16 karakterdi.
ALTER TABLE engagement_eom_periods ALTER COLUMN "PeriodKey" TYPE character varying(32);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_periods_Key" ON engagement_eom_periods ("TenantSlug", "PeriodKey") WHERE "Status" <> 'Cancelled';
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_periods_TenantSlug" ON engagement_eom_periods ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_units (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "PeriodId"      uuid NOT NULL REFERENCES engagement_eom_periods("Id") ON DELETE CASCADE,
    "Stage"         integer NOT NULL,
    "DepartmentId"  uuid NOT NULL,
    "Name"          text NOT NULL,
    "ParentUnitId"  uuid,
    "HeadEmployeeId" uuid,
    "MemberIds"     uuid[] NOT NULL,
    "CandidateIds"  uuid[] NOT NULL,
    "Status"        character varying(16) NOT NULL,
    "WinnerEmployeeId" uuid,
    "WinnerScore"   numeric(10,2),
    "TiedIds"       uuid[],
    "ClosedAt"      timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_units_PeriodId" ON engagement_eom_units ("PeriodId");
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_units_TenantSlug" ON engagement_eom_units ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_nominations (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "UnitId"          uuid NOT NULL REFERENCES engagement_eom_units("Id") ON DELETE CASCADE,
    "NominatorKey"    character varying(64) NOT NULL,
    "NominatorEmployeeId" uuid,
    "NomineeEmployeeId" uuid NOT NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_nominations_Unit_Nominator" ON engagement_eom_nominations ("UnitId", "NominatorKey");
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_nominations_TenantSlug" ON engagement_eom_nominations ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_eom_votes (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "UnitId"          uuid NOT NULL REFERENCES engagement_eom_units("Id") ON DELETE CASCADE,
    "VoterKey"        character varying(64) NOT NULL,
    "VoterEmployeeId" uuid,
    "NomineeEmployeeId" uuid NOT NULL,
    "Weight"          numeric(4,2) NOT NULL,
    "Comment"         character varying(280),
    "CreatedAt"       timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_eom_votes_Unit_Voter" ON engagement_eom_votes ("UnitId", "VoterKey");
CREATE INDEX IF NOT EXISTS "IX_engagement_eom_votes_TenantSlug" ON engagement_eom_votes ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-28_employee_photos
-- Çalışan fotoğrafı (profil, ana sayfa "Ayın çalışanı" kartı, avatarlar).
-- Sahibi employee-service. Fotoğraf kişisel veridir: herkese açık nesne deposuna (logolar gibi) konmaz,
-- veritabanında kiracı yalıtımı altında tutulur ve yalnızca oturum açmış aynı şirket kullanıcılarına
-- servis ucundan verilir. Tarayıcı yüklemeden önce en çok 512 px'e küçültür; sunucu 512 KB'ı reddeder.
-- Çalışan silinince fotoğraf da silinir (ON DELETE CASCADE); KVKK anonimleştirmesi de siler.
-- "PhotoVersion" önbellek anahtarıdır: fotoğraf değişince artar, istemci yeni adresi ister.
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

ALTER TABLE employee_employees ADD COLUMN IF NOT EXISTS "PhotoVersion" integer;

CREATE TABLE IF NOT EXISTS employee_photos (
    "EmployeeId"  uuid PRIMARY KEY REFERENCES employee_employees("Id") ON DELETE CASCADE,
    "TenantSlug"  character varying(64) NOT NULL,
    "ContentType" character varying(32) NOT NULL,
    "Data"        bytea NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_employee_photos_TenantSlug" ON employee_photos ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_document_requirements
-- Eksik belge takibi (yenilik haritası #11). Sahibi expense-service (özlük dokümanları).
--  * expense_documents."ValidUntil": belgenin geçerlilik bitişi (ör. sağlık raporu, kimlik). Boşsa ve
--    gereksinimde süre tanımlıysa yükleme tarihinden hesaplanır.
--  * expense_document_requirements: şirkette (DepartmentId boş) ya da bir departmanda zorunlu belge türleri.
-- İdempotent. Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya
ALTER TABLE expense_documents ADD COLUMN IF NOT EXISTS "ValidUntil" date;

CREATE TABLE IF NOT EXISTS expense_document_requirements (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "Type"           text NOT NULL,
    "DepartmentId"   uuid,
    "ValidityMonths" integer,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_expense_document_requirements_TenantSlug" ON expense_document_requirements ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_events
-- Etkinlikler ve katılım (yenilik haritası #28) ile gönüllülük saatleri (#30). Sahibi engagement-service.
-- Kontenjan dolunca katılım isteği bekleme listesine düşer; biri vazgeçince sıradaki otomatik alınır.
-- Gönüllülük etkinliğinde İK katılımı onaylayınca kişiye etkinliğin saati yazılır. İdempotent.
CREATE TABLE IF NOT EXISTS engagement_events (
    "Id"             uuid PRIMARY KEY,
    "TenantSlug"     character varying(64) NOT NULL,
    "Title"          character varying(120) NOT NULL,
    "Description"    character varying(2000),
    "Kind"           character varying(16) NOT NULL,
    "StartsAt"       timestamptz NOT NULL,
    "EndsAt"         timestamptz NOT NULL,
    "Location"       character varying(200),
    "OnlineUrl"      character varying(500),
    "Capacity"       integer,
    "RsvpDeadline"   timestamptz,
    "VolunteerHours" numeric(5,1),
    "Status"         character varying(16) NOT NULL DEFAULT 'Published',
    "CreatedByUserId" character varying(64) NOT NULL,
    "CreatedByName"  text,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_events_TenantSlug" ON engagement_events ("TenantSlug", "StartsAt");

CREATE TABLE IF NOT EXISTS engagement_event_rsvps (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EventId"     uuid NOT NULL REFERENCES engagement_events("Id") ON DELETE CASCADE,
    "EmployeeId"  uuid NOT NULL,
    "Name"        text NOT NULL,
    "Status"      character varying(16) NOT NULL,
    "Attended"    boolean NOT NULL DEFAULT false,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_event_rsvps_Event_Emp" ON engagement_event_rsvps ("EventId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_engagement_event_rsvps_TenantSlug" ON engagement_event_rsvps ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_ideas
-- Fikir kutusu (yenilik haritası #24). Sahibi engagement-service.
-- Çalışan fikir yazar (isteğe bağlı anonim), diğerleri destekler ve yorumlar, İK durum ve yanıt verir.
-- KVKK / anonimlik: anonim fikirde yazarın kimliği tutulmaz; yalnızca kendi fikrini tanıyabilmesi için
-- kiracı + kullanıcıdan türetilen tek yönlü anahtar ("AuthorKey") saklanır. Fikir satırları denetim
-- kaydına yazılmaz (yazar ve destekleyenler oradan çıkarılabilirdi). İdempotent.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS engagement_ideas (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "AuthorKey"        character varying(64) NOT NULL,
    "AuthorEmployeeId" uuid,
    "AuthorName"       text,
    "Anonymous"        boolean NOT NULL DEFAULT false,
    "Title"            character varying(120) NOT NULL,
    "Body"             character varying(2000) NOT NULL,
    "Category"         character varying(40) NOT NULL,
    "Status"           character varying(16) NOT NULL DEFAULT 'New',
    "Response"         character varying(2000),
    "RespondedByName"  text,
    "RespondedAt"      timestamptz,
    "Voters"           text[] NOT NULL DEFAULT '{}',
    "CreatedAt"        timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_ideas_TenantSlug" ON engagement_ideas ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_idea_comments (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "IdeaId"           uuid NOT NULL REFERENCES engagement_ideas("Id") ON DELETE CASCADE,
    "AuthorUserId"     character varying(64) NOT NULL,
    "AuthorEmployeeId" uuid,
    "AuthorName"       text NOT NULL,
    "Body"             character varying(1000) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_idea_comments_IdeaId" ON engagement_idea_comments ("IdeaId");
CREATE INDEX IF NOT EXISTS "IX_engagement_idea_comments_TenantSlug" ON engagement_idea_comments ("TenantSlug");

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_interview_booking
-- Adayın mülakat saatini kendisi seçmesi (yenilik haritası #10). Sahibi recruitment-service.
-- İşe alım sorumlusu 2-10 uygun saat önerir; aday tek kullanımlık bağlantıyla birini seçer, mülakat o saate
-- kurulur. Bağlantının yalnızca SHA-256 özeti saklanır. İdempotent.
CREATE TABLE IF NOT EXISTS recruitment_interview_proposals (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "ApplicationId"   uuid NOT NULL,
    "TokenHash"       character varying(64) NOT NULL,
    "Type"            text NOT NULL,
    "DurationMinutes" integer NOT NULL,
    "InterviewerIds"  uuid[] NOT NULL,
    "Location"        character varying(300),
    "MeetingUrl"      character varying(500),
    "Slots"           timestamptz[] NOT NULL,
    "Status"          character varying(16) NOT NULL DEFAULT 'Open',
    "ExpiresAt"       timestamptz NOT NULL,
    "ChosenAt"        timestamptz,
    "InterviewId"     uuid,
    "CreatedByName"   text,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
-- Başvuru silinince (KVKK imhası dahil) öneri de silinir.
DELETE FROM recruitment_interview_proposals p WHERE NOT EXISTS (SELECT 1 FROM recruitment_applications a WHERE a."Id" = p."ApplicationId");
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_recruitment_interview_proposals_App') THEN
        ALTER TABLE recruitment_interview_proposals ADD CONSTRAINT "FK_recruitment_interview_proposals_App"
            FOREIGN KEY ("ApplicationId") REFERENCES recruitment_applications("Id") ON DELETE CASCADE;
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_recruitment_interview_proposals_Token" ON recruitment_interview_proposals ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_recruitment_interview_proposals_App" ON recruitment_interview_proposals ("ApplicationId");
CREATE INDEX IF NOT EXISTS "IX_recruitment_interview_proposals_TenantSlug" ON recruitment_interview_proposals ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_newhire_checkins
-- İlk 90 gün takibi (yenilik haritası #8). Sahibi engagement-service.
-- Yeni çalışana işe girişinin 30., 60. ve 90. gününde kısa uyum formu gönderilir; yöneticisi (departman
-- başı) hatırlatılır, yanıt yöneticiye ve İK'ya açıktır (form bunu çalışana söyler; anonim değildir).
-- Kilometre taşı son 7 gün içindeyse kayıt açılır: eski çalışanlara geriye dönük form gitmez. İdempotent.
CREATE TABLE IF NOT EXISTS engagement_newhire_checkins (
    "Id"                uuid PRIMARY KEY,
    "TenantSlug"        character varying(64) NOT NULL,
    "EmployeeId"        uuid NOT NULL,
    "Milestone"         integer NOT NULL,
    "DueOn"             date NOT NULL,
    "ManagerEmployeeId" uuid,
    "SentAt"            timestamptz NOT NULL DEFAULT now(),
    "AnsweredAt"        timestamptz,
    "Belonging"         smallint,
    "Clarity"           smallint,
    "Support"           smallint,
    "Comment"           character varying(1000)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_newhire_checkins_Emp_Milestone" ON engagement_newhire_checkins ("TenantSlug", "EmployeeId", "Milestone");
CREATE INDEX IF NOT EXISTS "IX_engagement_newhire_checkins_TenantSlug" ON engagement_newhire_checkins ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_positions
-- Norm kadro ve pozisyon yönetimi (yenilik haritası #12). Sahibi organization-service.
-- Departman başına onaylı pozisyon (unvan + kadro sayısı, isteğe bağlı aylık bütçe ve kademe). Dolu kadro,
-- departmanda o unvanla atanmış aktif çalışanlardan hesaplanır (ayrı bağ tutulmaz). Bütçe yalnızca İK'ya döner.
CREATE TABLE IF NOT EXISTS organization_positions (
    "Id"            uuid PRIMARY KEY,
    "TenantSlug"    character varying(64) NOT NULL,
    "DepartmentId"  uuid NOT NULL REFERENCES organization_departments("Id") ON DELETE CASCADE,
    "Title"         character varying(120) NOT NULL,
    "Headcount"     integer NOT NULL,
    "Grade"         character varying(40),
    "BudgetMonthly" numeric(14,2),
    "Currency"      character varying(3) NOT NULL DEFAULT 'TRY',
    "Notes"         character varying(500),
    "IsActive"      boolean NOT NULL DEFAULT true,
    "CreatedAt"     timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_organization_positions_TenantSlug" ON organization_positions ("TenantSlug");
CREATE INDEX IF NOT EXISTS "IX_organization_positions_DepartmentId" ON organization_positions ("DepartmentId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_organization_positions_Dept_Title" ON organization_positions ("DepartmentId", lower("Title")) WHERE "IsActive";
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_product_feedback
-- Ürün içi geri bildirim ve ekran kullanım ölçümü (yenilik haritası #33). Sahibi engagement-service.
-- KVKK: kişi kimliği tutulmaz. Geri bildirim isimsizdir (yalnızca ekran, oy, isteğe bağlı yorum ve rol
-- grubu); kullanım ölçümü gün ve ekran şablonu başına sayaçtır (adres içindeki kimlikler istemcide
-- ":id" ile değiştirilir). İdempotent.
CREATE TABLE IF NOT EXISTS engagement_product_feedback (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "Path"       character varying(200) NOT NULL,
    "Rating"     smallint NOT NULL,
    "Comment"    character varying(1000),
    "RoleGroup"  character varying(16) NOT NULL,
    "CreatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_product_feedback_TenantSlug" ON engagement_product_feedback ("TenantSlug", "CreatedAt");

CREATE TABLE IF NOT EXISTS engagement_page_views (
    "TenantSlug" character varying(64) NOT NULL,
    "Day"        date NOT NULL,
    "Path"       character varying(200) NOT NULL,
    "Views"      integer NOT NULL DEFAULT 0,
    PRIMARY KEY ("TenantSlug", "Day", "Path")
);

DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-29_weekly_checkins
-- Haftalık check-in (yenilik haritası #9). Sahibi engagement-service.
-- Çalışan haftada bir kısa not yazar (yaptıkları, engeller, gelecek hafta, ruh hâli 1-5); yöneticisi
-- (departman başı ya da ekip lideri) görür, engeli bir sonraki 1:1 gündemine ekleyebilir. Anonim değildir.
CREATE TABLE IF NOT EXISTS engagement_weekly_checkins (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "WeekStart"  date NOT NULL,
    "Progress"   character varying(1000),
    "Blockers"   character varying(1000),
    "NextWeek"   character varying(1000),
    "Mood"       smallint NOT NULL,
    "AddedToOneOnOne" boolean NOT NULL DEFAULT false,
    "CreatedAt"  timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_weekly_checkins_Emp_Week" ON engagement_weekly_checkins ("TenantSlug", "EmployeeId", "WeekStart");
CREATE INDEX IF NOT EXISTS "IX_engagement_weekly_checkins_TenantSlug" ON engagement_weekly_checkins ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-30_announcement_feed
-- Şirket içi akış (yenilik haritası #31): duyurulara tepki ve yorum. Sahibi governance-service.
-- Kişi başına tek tepki (değiştirilebilir/geri alınabilir). Yorumlar duyuruyu görebilenlere açıktır;
-- İK duyuru bazında yorumları kapatabilir ("AllowComments"). Duyuru silinince tepki/yorum da silinir.
ALTER TABLE governance_announcements ADD COLUMN IF NOT EXISTS "AllowComments" boolean NOT NULL DEFAULT true;

CREATE TABLE IF NOT EXISTS governance_announcement_reactions (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "AnnouncementId"  uuid NOT NULL REFERENCES governance_announcements("Id") ON DELETE CASCADE,
    "UserId"          character varying(64) NOT NULL,
    "EmployeeId"      uuid,
    -- like | love | celebrate | clap | insightful | funny
    "Kind"            character varying(16) NOT NULL,
    "CreatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_announcement_reactions_User" ON governance_announcement_reactions ("AnnouncementId", "UserId");
CREATE INDEX IF NOT EXISTS "IX_governance_announcement_reactions_TenantSlug" ON governance_announcement_reactions ("TenantSlug");

CREATE TABLE IF NOT EXISTS governance_announcement_comments (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "AnnouncementId"   uuid NOT NULL REFERENCES governance_announcements("Id") ON DELETE CASCADE,
    "AuthorUserId"     character varying(64) NOT NULL,
    "AuthorEmployeeId" uuid,
    "AuthorName"       text NOT NULL,
    "Body"             character varying(1000) NOT NULL,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_announcement_comments_Ann" ON governance_announcement_comments ("AnnouncementId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_governance_announcement_comments_TenantSlug" ON governance_announcement_comments ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-30_badges
-- Rozet koleksiyonu ve kilometre taşları (yenilik haritası #29). Sahibi engagement-service.
-- Rozetler mevcut verilerden hesaplanır; burada yalnızca kazanma anı tutulur (bildirim bir kez gitsin,
-- koleksiyonda tarih görünsün). "_init" satırı kişinin ilk eşitlemesinin sessiz yapıldığını gösterir.
CREATE TABLE IF NOT EXISTS engagement_badge_awards (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "BadgeId"     character varying(32) NOT NULL,
    "AwardedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_badge_awards" ON engagement_badge_awards ("TenantSlug", "EmployeeId", "BadgeId");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-30_coffee
-- Kahve eşleştirme (yenilik haritası #27). Sahibi engagement-service. İsteğe bağlı (varsayılan kapalı):
-- isteyen çalışan katılır; seçilen günde (Türkiye saatiyle 09:00 sonrası) katılımcılar rastgele ikişerli
-- eşlenir (tek sayıda ise bir grup üç kişi), son eşleşmeler ve istenirse aynı departman kaçınılır.
-- Dönem başına tek tur (benzersiz dönem anahtarı). İdempotent.
CREATE TABLE IF NOT EXISTS engagement_coffee_settings (
    "Id"              uuid PRIMARY KEY,
    "TenantSlug"      character varying(64) NOT NULL,
    "Enabled"         boolean NOT NULL DEFAULT false,
    "Cadence"         character varying(16) NOT NULL DEFAULT 'Biweekly',
    "DayOfWeek"       integer NOT NULL DEFAULT 1,
    "CrossDepartment" boolean NOT NULL DEFAULT true,
    "UpdatedAt"       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_coffee_settings_TenantSlug" ON engagement_coffee_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_coffee_participants (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "JoinedAt"    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_coffee_participants_Emp" ON engagement_coffee_participants ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS engagement_coffee_rounds (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "PeriodKey"   character varying(16) NOT NULL,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_coffee_rounds_Period" ON engagement_coffee_rounds ("TenantSlug", "PeriodKey");

CREATE TABLE IF NOT EXISTS engagement_coffee_matches (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "RoundId"     uuid NOT NULL REFERENCES engagement_coffee_rounds("Id") ON DELETE CASCADE,
    "MemberIds"   uuid[] NOT NULL,
    "Met"         boolean NOT NULL DEFAULT false,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_coffee_matches_TenantSlug" ON engagement_coffee_matches ("TenantSlug", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_engagement_coffee_matches_Members" ON engagement_coffee_matches USING gin ("MemberIds");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-30_group_cards
-- Grup kartı (yenilik haritası #26): doğum günü, veda, hoş geldin vb. için herkesin imzaladığı kart.
-- Sahibi engagement-service. Kart, teslim anına kadar alıcıdan gizlidir; teslim zamanı gelince
-- (CardWorker) ya da açan kişi erken teslim edince alıcıya bildirim gider. İdempotent.
CREATE TABLE IF NOT EXISTS engagement_cards (
    "Id"                   uuid PRIMARY KEY,
    "TenantSlug"           character varying(64) NOT NULL,
    "RecipientEmployeeId"  uuid NOT NULL,
    "RecipientName"        text NOT NULL,
    -- Birthday | Farewell | Welcome | Anniversary | Congrats | GetWell | Other
    "Occasion"             character varying(16) NOT NULL,
    "Title"                character varying(120) NOT NULL,
    "DeliverAt"            timestamptz NOT NULL,
    "DeliveredAt"          timestamptz,
    "CreatedByEmployeeId"  uuid,
    "CreatedByUserId"      character varying(64) NOT NULL,
    "CreatedByName"        text,
    "CreatedAt"            timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_cards_TenantSlug" ON engagement_cards ("TenantSlug", "DeliveredAt");
CREATE INDEX IF NOT EXISTS "IX_engagement_cards_Due" ON engagement_cards ("DeliverAt") WHERE "DeliveredAt" IS NULL;

CREATE TABLE IF NOT EXISTS engagement_card_signatures (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "CardId"      uuid NOT NULL REFERENCES engagement_cards("Id") ON DELETE CASCADE,
    "EmployeeId"  uuid NOT NULL,
    "Name"        text NOT NULL,
    "Message"     character varying(500) NOT NULL,
    "Color"       character varying(16) NOT NULL DEFAULT 'yellow',
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_card_signatures_Card_Emp" ON engagement_card_signatures ("CardId", "EmployeeId");
CREATE INDEX IF NOT EXISTS "IX_engagement_card_signatures_TenantSlug" ON engagement_card_signatures ("TenantSlug");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-30_platform_ops
-- Arayüzden yedek / geri yükleme / güncelleme (yenilik haritası #34). Sahibi tenant-service.
-- Konteynerler sunucu komutu çalıştırmaz (docker soketi verilmez): platform yöneticisi arayüzden iş
-- kuyruğa yazar; sunucudaki scripts/ops-runner.py (kullanıcı cron'u, dakikada bir) yalnızca izinli
-- komutları çalıştırır, sonucu ve sunucu durum özetini buraya yazar. Kiracıya ait değildir. İdempotent.
CREATE TABLE IF NOT EXISTS platform_ops_jobs (
    "Id"           uuid PRIMARY KEY,
    -- backup | backup-verify | schedule | unschedule | check-updates | update | rollback | restore
    "Kind"         character varying(32) NOT NULL,
    "Args"         jsonb NOT NULL DEFAULT '{}'::jsonb,
    -- Pending | Running | Succeeded | Failed | Cancelled
    "Status"       character varying(16) NOT NULL DEFAULT 'Pending',
    "RequestedBy"  text,
    "RequestedAt"  timestamptz NOT NULL DEFAULT now(),
    "StartedAt"    timestamptz,
    "FinishedAt"   timestamptz,
    "ExitCode"     integer,
    "Output"       text
);
CREATE INDEX IF NOT EXISTS "IX_platform_ops_jobs_Status" ON platform_ops_jobs ("Status", "RequestedAt");

CREATE TABLE IF NOT EXISTS platform_ops_status (
    "Id"         integer PRIMARY KEY,
    "Snapshot"   jsonb NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);

-- ===== 2026-10-30_recognition_points
-- Takdir puanları ve ödül kataloğu (yenilik haritası #25). Sahibi engagement-service. İsteğe bağlı
-- (varsayılan kapalı): her çalışanın aylık verebileceği puan bütçesi vardır; takdirle birlikte puan
-- gönderilir, alınan puan katalogdaki ödüllerle takas edilir. Bakiye hareket defterinden hesaplanır
-- (engagement_points_ledger); takas anında puan düşülür, İK reddederse iade edilir. İdempotent.
ALTER TABLE engagement_kudos ADD COLUMN IF NOT EXISTS "Points" integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS engagement_recognition_settings (
    "Id"               uuid PRIMARY KEY,
    "TenantSlug"       character varying(64) NOT NULL,
    "Enabled"          boolean NOT NULL DEFAULT false,
    "MonthlyAllowance" integer NOT NULL DEFAULT 100,
    "MaxPerKudos"      integer NOT NULL DEFAULT 50,
    "UpdatedAt"        timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_engagement_recognition_settings_TenantSlug" ON engagement_recognition_settings ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_points_ledger (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "Delta"       integer NOT NULL,
    -- Kudos | KudosReversed | Redeem | Refund | Grant
    "Reason"      character varying(16) NOT NULL,
    "RefId"       uuid,
    "Note"        character varying(200),
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_points_ledger_Emp" ON engagement_points_ledger ("TenantSlug", "EmployeeId");

CREATE TABLE IF NOT EXISTS engagement_rewards (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "Title"       character varying(120) NOT NULL,
    "Description" character varying(1000),
    "Cost"        integer NOT NULL,
    "Stock"       integer,
    "Active"      boolean NOT NULL DEFAULT true,
    "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_engagement_rewards_TenantSlug" ON engagement_rewards ("TenantSlug");

CREATE TABLE IF NOT EXISTS engagement_reward_redemptions (
    "Id"          uuid PRIMARY KEY,
    "TenantSlug"  character varying(64) NOT NULL,
    "RewardId"    uuid NOT NULL REFERENCES engagement_rewards("Id") ON DELETE RESTRICT,
    "RewardTitle" character varying(120) NOT NULL,
    "EmployeeId"  uuid NOT NULL,
    "Name"        text NOT NULL,
    "Cost"        integer NOT NULL,
    -- Pending | Fulfilled | Rejected
    "Status"      character varying(16) NOT NULL DEFAULT 'Pending',
    "Note"        character varying(500),
    "CreatedAt"   timestamptz NOT NULL DEFAULT now(),
    "DecidedAt"   timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_engagement_reward_redemptions_TenantSlug" ON engagement_reward_redemptions ("TenantSlug", "Status");
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;

-- ===== 2026-10-30_sample_data
-- Örnek veri (yenilik haritası #35): boş bir şirketi tek tıkla örnek çalışan, departman, takdir,
-- fikir ve etkinlikle doldurma; sonra tek tıkla temizleme. Sahibi tenant-service. Burada yalnızca
-- örnek verinin hangi departmanları açtığı tutulur (temizlikte yalnızca bunlar silinir). İdempotent.
CREATE TABLE IF NOT EXISTS platform_sample_data (
    "TenantSlug"     character varying(64) PRIMARY KEY,
    "DepartmentIds"  uuid[] NOT NULL DEFAULT '{}',
    "CreatedBy"      text,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now()
);

-- ===== 2026-10-22_time_leave (sonradan eklenen sutun)
ALTER TABLE leave_settings ADD COLUMN IF NOT EXISTS "TeamCalendarNames" boolean NOT NULL DEFAULT false;

-- ===== 2026-10-31_rls_shared_rows
-- Satır düzeyi güvenlik: kiracılar arası ORTAK satırlar (TenantSlug NULL). İdempotent.
-- Bazı tablolarda NULL kiracı "herkese ortak" demektir (ör. expense_fx_rates: TCMB kur önbelleği; kiracının
-- elle girdiği kur kendi TenantSlug'ıyla durur). Kiracı oturumunda (app.tenant dolu) bu satırlar okunabilmeli
-- ve önbelleğe yazılabilmeli. İstisna YALNIZCA aşağıdaki listedeki tablolar içindir; diğerlerinde NULL satır
-- (ör. audit_log'daki platform kayıtları) kiracı oturumuna kapalı kalır.

CREATE OR REPLACE FUNCTION hr360_rls_shared_tables() RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
    SELECT ARRAY['expense_fx_rates']::text[]
$$;

CREATE OR REPLACE FUNCTION hr360_rls_expr(tbl text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT $e$coalesce(current_setting('app.tenant', true), '') = '' OR "TenantSlug" = current_setting('app.tenant', true)$e$
        || CASE WHEN tbl = ANY (hr360_rls_shared_tables()) THEN $e$ OR "TenantSlug" IS NULL$e$ ELSE '' END
$$;

-- Eksik politikaları ekler (ifade tabloya göre: ortak tablolarda NULL satır da görünür).
CREATE OR REPLACE FUNCTION hr360_rls_apply_policies() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    FOR t IN
        SELECT c.relname
          FROM pg_class c
          JOIN pg_namespace ns ON ns.oid = c.relnamespace
          JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'TenantSlug' AND NOT a.attisdropped
         WHERE ns.nspname = 'public' AND c.relkind IN ('r', 'p')
           AND NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'hr360_tenant_isolation')
         ORDER BY c.relname
    LOOP
        EXECUTE format('CREATE POLICY hr360_tenant_isolation ON public.%I AS PERMISSIVE FOR ALL USING (%s) WITH CHECK (%s)',
            t.relname, hr360_rls_expr(t.relname), hr360_rls_expr(t.relname));
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

-- Var olan politikaları listeye göre güncelle (yalnızca ortak tablolar; diğerleri aynen kalır).
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY hr360_rls_shared_tables() LOOP
        IF EXISTS (SELECT 1 FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
                    WHERE p.polname = 'hr360_tenant_isolation' AND c.relname = t AND c.relnamespace = 'public'::regnamespace) THEN
            EXECUTE format('ALTER POLICY hr360_tenant_isolation ON public.%I USING (%s) WITH CHECK (%s)', t, hr360_rls_expr(t), hr360_rls_expr(t));
        END IF;
    END LOOP;
END $$;

REVOKE ALL ON FUNCTION hr360_rls_apply_policies() FROM PUBLIC;

-- ===== scripts/sql/2026-11-01_sms.sql
-- SMS kanalı (notification-service): kiracının yurt içi SMS sağlayıcısı (Netgsm / İleti Merkezi; Ayarlar › SMS),
-- bekleyen SMS'in alıcı numarası (gönderim sonuçlanınca silinir) ve kişinin SMS tercihi. İdempotent.
CREATE TABLE IF NOT EXISTS notification_sms_settings (
    "TenantSlug" character varying(64) PRIMARY KEY,
    "Enabled"    boolean NOT NULL DEFAULT false,
    "Provider"   text NOT NULL DEFAULT 'netgsm',
    "Username"   text NOT NULL DEFAULT '',
    -- KeyRing ile şifreli (KeyRotationJob yeniler); yanıtta dönmez.
    "SecretEnc"  text NOT NULL DEFAULT '',
    "Sender"     text NOT NULL DEFAULT '',
    "DailyLimit" integer NOT NULL DEFAULT 200,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_notification_sms_settings_TenantSlug" ON notification_sms_settings ("TenantSlug");
ALTER TABLE notification_messages ADD COLUMN IF NOT EXISTS "RecipientPhone" text NULL;
ALTER TABLE notification_preferences ADD COLUMN IF NOT EXISTS "SmsEnabled" boolean NOT NULL DEFAULT true;
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;
