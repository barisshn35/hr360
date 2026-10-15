-- Performans hedeflerine isteğe bağlı son tarih. İdempotent.
-- Boş (NULL) bırakılabilir; doğrulama (dönem bitişinden sonra olamaz) performance-service'te.
ALTER TABLE performance_goals ADD COLUMN IF NOT EXISTS "DueDate" date NULL;
