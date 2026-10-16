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
