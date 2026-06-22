-- DeepSpaceEngine — discovery reporting schema.
-- One row per discovered object; the UNIQUE(object_id) constraint is the whole
-- "first finder wins" mechanism (a duplicate report can't insert, and the API returns
-- the existing row instead).
--
-- Apply with:  mysql -u <user> -p <database> < schema.sql

CREATE TABLE IF NOT EXISTS discoveries (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  object_id     VARCHAR(32)  CHARACTER SET ascii NOT NULL,   -- '12407198355' | '…-02' | '…-02-03'
  kind          ENUM('star','planet','moon') NOT NULL,
  star_id       VARCHAR(20)  CHARACTER SET ascii NOT NULL,   -- decimal star id (grouping / by-system views)
  designation   VARCHAR(96)  NOT NULL DEFAULT '',
  discoverer    VARCHAR(64)  NOT NULL,
  discovered_at DATETIME     NOT NULL,                        -- stored in UTC
  meta          JSON         NULL,                            -- class/type/temp for the web list (display only)
  UNIQUE KEY uniq_object (object_id),
  KEY idx_discoverer (discoverer),
  KEY idx_star (star_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
