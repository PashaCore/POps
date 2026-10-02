-- 0019: görev bağlamı. Panel her listede "ne · kim · nereden · neden" gösterebilsin diye görevin okunur adı
-- (adımın adı), isteğin geldiği panel sayfası, gerekçe, isteğin geldiği IP ve aynı istekte açılan görevleri
-- gruplayan iş kimliği saklanır. Eski görevlerde bu alanlar boştur.
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS title TEXT;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS source TEXT;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS reason TEXT;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS client_ip TEXT;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS batch_id TEXT;
CREATE INDEX IF NOT EXISTS idx_tasks_batch ON tasks (batch_id) WHERE batch_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_tasks_target_pc ON tasks (target_pc, id DESC);
