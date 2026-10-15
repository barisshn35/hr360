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
