-- 0003: enroll jetonlarına çok-kullanımlı destek.
--
-- Bir laba tek MSI ile toplu kurulumda (ENROLL_TOKEN MSI property) tüm makineler AYNI jetonu
-- sunar. Tek kullanımlık jetonda yalnızca ilk makine kaydolurdu; bu yüzden jetona kullanım
-- sayacı eklenir: max_uses kadar makine kaydolabilir, use_count dolunca is_used TRUE olur.
-- Varsayılan max_uses=1 (mevcut davranış korunur). Bu dosya idempotenttir.

ALTER TABLE enroll_tokens ADD COLUMN IF NOT EXISTS max_uses  INTEGER NOT NULL DEFAULT 1;
ALTER TABLE enroll_tokens ADD COLUMN IF NOT EXISTS use_count INTEGER NOT NULL DEFAULT 0;
