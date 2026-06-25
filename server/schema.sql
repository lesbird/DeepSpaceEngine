-- DeepSpaceEngine — discovery reporting schema.
-- One row per discovered object; the UNIQUE(object_id) constraint is the whole
-- "first finder wins" mechanism (a duplicate report can't insert, and the API returns
-- the existing row instead).
--
-- Apply with:  mysql -u <user> -p <database> < schema.sql

-- Ids are '{galaxyId}-{starId}[-PP[-MM]]'. The galaxy id prefixes the star id because a star id is
-- only unique within its galaxy; galaxy + star ids are each up to 20 digits, so the fields are sized
-- for the longest moon id ("g-s-PP-MM" ≈ 47 chars) with headroom.

CREATE TABLE IF NOT EXISTS discoveries (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  object_id     VARCHAR(64)  CHARACTER SET ascii NOT NULL,   -- '12345-56789' | '…-00' | '…-00-03'
  kind          ENUM('star','planet','moon') NOT NULL,
  star_id       VARCHAR(48)  CHARACTER SET ascii NOT NULL,   -- system root '{galaxyId}-{starId}' (grouping / by-system views)
  designation   VARCHAR(96)  NOT NULL DEFAULT '',
  discoverer    VARCHAR(64)  NOT NULL,
  discovered_at DATETIME     NOT NULL,                        -- stored in UTC
  meta          JSON         NULL,                            -- class/type/temp for the web list (display only)
  UNIQUE KEY uniq_object (object_id),
  KEY idx_discoverer (discoverer),
  KEY idx_star (star_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Star ids became galaxy-prefixed after the initial schema. To upgrade an existing database, widen
-- the id columns in place (old unprefixed rows, if any, remain valid but won't match new ids):
--   ALTER TABLE discoveries MODIFY object_id VARCHAR(64) CHARACTER SET ascii NOT NULL,
--                           MODIFY star_id   VARCHAR(48) CHARACTER SET ascii NOT NULL;
