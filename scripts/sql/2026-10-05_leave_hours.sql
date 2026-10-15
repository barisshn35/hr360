-- İzin: saatlik izin ve devreden izin (leave-service).
ALTER TABLE leave_requests ADD COLUMN IF NOT EXISTS "Hours" numeric;
ALTER TABLE leave_balances ADD COLUMN IF NOT EXISTS "CarriedOverDays" numeric NOT NULL DEFAULT 0;
ALTER TABLE leave_balances ADD COLUMN IF NOT EXISTS "CarriedOutDays" numeric NOT NULL DEFAULT 0;
