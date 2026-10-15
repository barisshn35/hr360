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
