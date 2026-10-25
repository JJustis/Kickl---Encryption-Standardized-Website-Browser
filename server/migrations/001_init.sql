-- Pinwheel registry schema (MariaDB 10.4+ / MySQL 8).
-- Records are never deleted: closing a name sets status='closed'.

CREATE TABLE IF NOT EXISTS registry_meta (
  meta_key VARCHAR(64) PRIMARY KEY,
  meta_value VARCHAR(255) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS registry_records (
  name VARCHAR(80) PRIMARY KEY,
  owner VARCHAR(120) NOT NULL DEFAULT '',
  doc_file VARCHAR(200) NOT NULL DEFAULT '',
  version INT NOT NULL DEFAULT 1,
  not_after BIGINT NOT NULL DEFAULT 0,
  proof VARCHAR(100) NULL,
  sig VARCHAR(200) NOT NULL DEFAULT '',
  status VARCHAR(16) NOT NULL DEFAULT 'active',
  owner_id VARCHAR(120) NOT NULL DEFAULT '',
  badge_id VARCHAR(120) NOT NULL DEFAULT '',
  title_seq INT NOT NULL DEFAULT 0,
  origin_device_id VARCHAR(120) NOT NULL DEFAULT '',
  direct_public_key TEXT NULL,
  direct_endpoint VARCHAR(200) NULL,
  title VARCHAR(200) NOT NULL DEFAULT '',
  headline VARCHAR(400) NOT NULL DEFAULT '',
  story_author VARCHAR(120) NOT NULL DEFAULT '',
  story_date VARCHAR(40) NOT NULL DEFAULT '',
  story_location VARCHAR(120) NOT NULL DEFAULT '',
  tags JSON NOT NULL,
  updated_utc BIGINT NOT NULL DEFAULT 0,
  content_hash CHAR(64) NOT NULL DEFAULT '',
  content_mismatch_count INT NOT NULL DEFAULT 0,
  last_content_mismatch_utc BIGINT NOT NULL DEFAULT 0,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;

-- The exact signed JSON envelope served to clients. Signing happens offline in
-- tools/Kickl.RegistrySigner; this table never holds private key material.
CREATE TABLE IF NOT EXISTS registry_snapshots (
  version INT PRIMARY KEY,
  generated_at BIGINT NOT NULL,
  root_hash CHAR(64) NOT NULL,
  envelope LONGTEXT NOT NULL,
  signer_fingerprint CHAR(64) NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS registry_deltas (
  from_version INT NOT NULL,
  to_version INT NOT NULL,
  root_hash CHAR(64) NOT NULL,
  envelope LONGTEXT NOT NULL,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (from_version, to_version)
) ENGINE=InnoDB;

INSERT INTO registry_meta (meta_key, meta_value) VALUES ('latest_version', '0')
  ON DUPLICATE KEY UPDATE meta_key = meta_key;
