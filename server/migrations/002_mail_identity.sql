-- Pinwheel mail directory. This stores only self-signed public identity bundles
-- (RSA + Dilithium public keys and the device's direct endpoint). Mail messages
-- never enter this database: they are posted straight to the recipient device
-- and stored only in that device's encrypted SQLite vault.

CREATE TABLE IF NOT EXISTS mail_identities (
  id VARCHAR(64) PRIMARY KEY,
  short_id CHAR(8) NOT NULL UNIQUE,
  handle VARCHAR(32) NOT NULL,
  endpoint VARCHAR(255) NOT NULL,
  bundle_json LONGTEXT NOT NULL,
  token_hash CHAR(64) NOT NULL,
  created_at BIGINT NOT NULL,
  updated_at BIGINT NOT NULL,
  INDEX idx_mail_short (short_id)
) ENGINE=InnoDB;
