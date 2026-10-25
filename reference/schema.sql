-- Pinwheel reference schema — MySQL 8.0+ / InnoDB / utf8mb4_0900_ai_ci
-- Forward-only migrations. Never edit an applied file; add a new numbered migration.
-- INVARIANT: no column stores page, blob, mail, or chat plaintext. Public phonebook
-- metadata (name, title, headline, tags) is intentionally plaintext and searchable.

SET NAMES utf8mb4;
SET time_zone = '+00:00';

CREATE TABLE schema_migrations (
  version       INT UNSIGNED PRIMARY KEY,
  applied_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  checksum      CHAR(64) NOT NULL
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Registry
-- ---------------------------------------------------------------------------

CREATE TABLE domains (
  name            VARCHAR(63)  NOT NULL,             -- lower-case punycode, no trailing dot
  owner_address   VARCHAR(96)  NOT NULL,             -- kb1… bech32m
  owner_kem       VARBINARY(1216) NOT NULL,          -- X25519(32) || ML-KEM-768 pub(1184)
  endpoints       JSON         NOT NULL,
  capabilities    JSON         NOT NULL,
  title           VARCHAR(120) NOT NULL DEFAULT '',
  headline        VARCHAR(280) NOT NULL DEFAULT '',
  tags            JSON         NOT NULL DEFAULT (JSON_ARRAY()),
  content_sha256  BINARY(32)   NOT NULL,             -- name + markup + sealed blobs; arrival check
  content_mismatch_count INT UNSIGNED NOT NULL DEFAULT 0,
  last_mismatch_at BIGINT UNSIGNED NULL,
  created_at      BIGINT UNSIGNED NOT NULL,
  not_after       BIGINT UNSIGNED NOT NULL,
  version         INT UNSIGNED NOT NULL DEFAULT 1,
  current_hash    BINARY(32)   NOT NULL,
  status          ENUM('active','frozen','expired','revoked') NOT NULL DEFAULT 'active',
  sig             VARBINARY(3373) NOT NULL,
  updated_at      TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (name),
  KEY idx_owner (owner_address),
  KEY idx_expiry (not_after),
  KEY idx_status (status),
  KEY idx_headline (headline(120))
) ENGINE=InnoDB;

-- Required tags for the live index. One row per tag; the search joins here.
CREATE TABLE domain_tags (
  name            VARCHAR(63)  NOT NULL,
  tag             VARCHAR(24)  NOT NULL,             -- 2..24 lowercase [a-z0-9-]
  PRIMARY KEY (name, tag),
  KEY idx_tag (tag),
  CONSTRAINT fk_domain_tags_name FOREIGN KEY (name) REFERENCES domains(name) ON DELETE CASCADE,
  CONSTRAINT chk_domain_tag CHECK (tag REGEXP '^[a-z0-9][a-z0-9-]{0,22}[a-z0-9]$')
) ENGINE=InnoDB;

CREATE TABLE domain_history (
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  name          VARCHAR(63) NOT NULL,
  version       INT UNSIGNED NOT NULL,
  record_hash   BINARY(32) NOT NULL,
  prev_hash     BINARY(32) NOT NULL,
  record_cbor   MEDIUMBLOB NOT NULL,                 -- canonical signed record (public data)
  created_at    BIGINT UNSIGNED NOT NULL,
  UNIQUE KEY uq_name_version (name, version),
  KEY idx_name (name)
) ENGINE=InnoDB;

-- RFC 6962-style append-only log. leaves are indexed by position.
CREATE TABLE log_leaves (
  leaf_index    BIGINT UNSIGNED PRIMARY KEY,
  leaf_hash     BINARY(32) NOT NULL,
  payload_hash  BINARY(32) NOT NULL,
  created_at    BIGINT UNSIGNED NOT NULL,
  UNIQUE KEY uq_leaf_hash (leaf_hash)
) ENGINE=InnoDB;

CREATE TABLE log_checkpoints (
  tree_size     BIGINT UNSIGNED PRIMARY KEY,
  root_hash     BINARY(32) NOT NULL,
  timestamp     BIGINT UNSIGNED NOT NULL,
  sig           VARBINARY(3373) NOT NULL
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Devices (public certificates only; private keys never leave the client)
-- ---------------------------------------------------------------------------

CREATE TABLE devices (
  device_id       CHAR(16) NOT NULL,                 -- hex, first 8 bytes of SHA-256(pubkeys)
  identity_addr   VARCHAR(96) NOT NULL,
  sign_pub        VARBINARY(1952) NOT NULL,          -- Ed25519(32) || ML-DSA-65(1920)
  kem_pub         VARBINARY(1216) NOT NULL,
  certificate     MEDIUMBLOB NOT NULL,               -- signed by identity key
  capabilities    JSON NOT NULL,
  created_at      BIGINT UNSIGNED NOT NULL,
  not_after       BIGINT UNSIGNED NOT NULL,
  revoked_at      BIGINT UNSIGNED NULL,
  PRIMARY KEY (device_id),
  KEY idx_identity (identity_addr)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Rendezvous / presence (ephemeral)
-- ---------------------------------------------------------------------------

CREATE TABLE presence (
  peer_id       BINARY(32) PRIMARY KEY,
  identity_addr VARCHAR(96) NOT NULL,
  endpoints     JSON NOT NULL,
  capabilities  JSON NOT NULL,
  proof         VARBINARY(3373) NOT NULL,
  expires_at    BIGINT UNSIGNED NOT NULL,
  updated_at    BIGINT UNSIGNED NOT NULL,
  KEY idx_expiry (expires_at)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Relay (store-and-forward, opaque)
-- ---------------------------------------------------------------------------

CREATE TABLE relay_frames (
  id            BINARY(16) PRIMARY KEY,
  peer_id       BINARY(32) NOT NULL,
  dest_device   CHAR(16) NOT NULL,
  frame         MEDIUMBLOB NOT NULL,                 -- Noise ciphertext, never parsed
  size_bytes    INT UNSIGNED NOT NULL,
  stored_at     BIGINT UNSIGNED NOT NULL,
  expires_at    BIGINT UNSIGNED NOT NULL,
  delivered_at  BIGINT UNSIGNED NULL,
  KEY idx_peer_undelivered (peer_id, delivered_at, expires_at),
  KEY idx_expiry (expires_at)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Mail
-- ---------------------------------------------------------------------------

CREATE TABLE mailboxes (
  mailbox_hash  BINARY(16) PRIMARY KEY,              -- BLAKE3(address || epoch)[:16]
  epoch         INT UNSIGNED NOT NULL,
  created_at    BIGINT UNSIGNED NOT NULL,
  last_seen_at  BIGINT UNSIGNED NULL,
  quota_bytes   INT UNSIGNED NOT NULL DEFAULT 52428800,
  used_bytes    INT UNSIGNED NOT NULL DEFAULT 0,
  KEY idx_epoch (epoch)
) ENGINE=InnoDB;

CREATE TABLE envelopes (
  id            BINARY(16) PRIMARY KEY,
  mailbox_hash  BINARY(16) NOT NULL,
  size_class    INT UNSIGNED NOT NULL,               -- 4096/16384/65536/262144
  ct            MEDIUMBLOB NOT NULL,                 -- HPKE ciphertext, opaque
  deposited_at  BIGINT UNSIGNED NOT NULL,
  expires_at    BIGINT UNSIGNED NOT NULL,
  acked_at      BIGINT UNSIGNED NULL,
  KEY idx_mailbox_live (mailbox_hash, acked_at, expires_at),
  KEY idx_expiry (expires_at),
  CONSTRAINT fk_envelope_mailbox FOREIGN KEY (mailbox_hash)
    REFERENCES mailboxes (mailbox_hash) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE mailbox_tokens (
  token_hash    BINARY(32) PRIMARY KEY,              -- SHA-256(token)
  mailbox_hash  BINARY(16) NOT NULL,
  expires_at    BIGINT UNSIGNED NOT NULL,
  KEY idx_expiry (expires_at),
  KEY idx_mailbox (mailbox_hash)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Blobs (content-addressed ciphertext)
-- ---------------------------------------------------------------------------

CREATE TABLE blobs (
  id            CHAR(64) PRIMARY KEY,                -- BLAKE3-256 hex of ciphertext
  size_bytes    BIGINT UNSIGNED NOT NULL,
  storage_path  VARCHAR(255) NOT NULL,
  owner_device  CHAR(16) NULL,
  created_at    BIGINT UNSIGNED NOT NULL,
  expires_at    BIGINT UNSIGNED NOT NULL,
  refcount      INT UNSIGNED NOT NULL DEFAULT 1,
  KEY idx_expiry (expires_at)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Anti-replay and rate limiting
-- ---------------------------------------------------------------------------

CREATE TABLE request_nonces (
  nonce       BINARY(16) PRIMARY KEY,
  device_id   CHAR(16) NOT NULL,
  seen_at     BIGINT UNSIGNED NOT NULL,
  KEY idx_seen (seen_at)
) ENGINE=InnoDB;

CREATE TABLE rate_counters (
  bucket      VARCHAR(64) NOT NULL,                  -- e.g. "reg:device:<id>:2026-10-24"
  used        INT UNSIGNED NOT NULL DEFAULT 0,
  window_end  BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (bucket),
  KEY idx_window (window_end)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Updates
-- ---------------------------------------------------------------------------

CREATE TABLE update_channels (
  channel       VARCHAR(16) PRIMARY KEY,             -- stable | beta
  min_version   VARCHAR(32) NULL,                    -- force upgrade below this
  kill_switch   TINYINT(1) NOT NULL DEFAULT 0
) ENGINE=InnoDB;

CREATE TABLE updates (
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  channel       VARCHAR(16) NOT NULL,
  version       VARCHAR(32) NOT NULL,
  min_os        VARCHAR(32) NOT NULL,
  url           VARCHAR(512) NOT NULL,
  sha256        CHAR(64) NOT NULL,
  size_bytes    BIGINT UNSIGNED NOT NULL,
  rollout       TINYINT UNSIGNED NOT NULL DEFAULT 100,
  mandatory     TINYINT(1) NOT NULL DEFAULT 0,
  notes         TEXT NOT NULL,
  manifest_cbor MEDIUMBLOB NOT NULL,                 -- signed manifest (canonical)
  sig           VARBINARY(3373) NOT NULL,
  published_at  BIGINT UNSIGNED NOT NULL,
  UNIQUE KEY uq_channel_version (channel, version),
  CONSTRAINT fk_update_channel FOREIGN KEY (channel)
    REFERENCES update_channels (channel)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Nickles testnet (no monetary value; see docs/09)
-- ---------------------------------------------------------------------------

CREATE TABLE nickles_blocks (
  height        INT UNSIGNED PRIMARY KEY,
  hash          BINARY(32) NOT NULL,
  prev_hash     BINARY(32) NOT NULL,
  merkle_root   BINARY(32) NOT NULL,
  difficulty    INT UNSIGNED NOT NULL,
  nonce         BIGINT UNSIGNED NOT NULL,
  mined_at      BIGINT UNSIGNED NOT NULL,
  UNIQUE KEY uq_hash (hash)
) ENGINE=InnoDB;

CREATE TABLE nickles_txs (
  txid          BINARY(32) PRIMARY KEY,
  block_height  INT UNSIGNED NULL,
  from_addr     VARCHAR(96) NOT NULL,
  to_addr       VARCHAR(96) NOT NULL,
  amount        BIGINT UNSIGNED NOT NULL,
  fee           BIGINT UNSIGNED NOT NULL,
  tx_nonce      BIGINT UNSIGNED NOT NULL,
  memo          VARBINARY(256) NULL,
  tx_ts         BIGINT UNSIGNED NOT NULL,
  sig           VARBINARY(3373) NOT NULL,
  status        ENUM('pending','confirmed','rejected') NOT NULL DEFAULT 'pending',
  KEY idx_block (block_height),
  KEY idx_from (from_addr),
  KEY idx_to (to_addr),
  UNIQUE KEY uq_from_nonce (from_addr, tx_nonce)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Audit / ops (no PII, no payloads)
-- ---------------------------------------------------------------------------

CREATE TABLE audit_events (
  id          BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  kind        VARCHAR(48) NOT NULL,                  -- e.g. sig_fail, equivocation, kill_switch
  subject     CHAR(64) NULL,                         -- hashed, never a raw address
  detail      JSON NULL,                             -- must pass a redaction allowlist
  created_at  BIGINT UNSIGNED NOT NULL,
  KEY idx_kind_time (kind, created_at)
) ENGINE=InnoDB;

-- ---------------------------------------------------------------------------
-- Maintenance events (scheduled by cron; each is idempotent and batched)
-- ---------------------------------------------------------------------------
-- DELETE FROM relay_frames   WHERE expires_at < UNIX_TIMESTAMP() LIMIT 5000;
-- DELETE FROM envelopes      WHERE expires_at < UNIX_TIMESTAMP() LIMIT 5000;
-- DELETE FROM presence       WHERE expires_at < UNIX_TIMESTAMP() LIMIT 5000;
-- DELETE FROM mailbox_tokens WHERE expires_at < UNIX_TIMESTAMP() LIMIT 5000;
-- DELETE FROM request_nonces WHERE seen_at    < UNIX_TIMESTAMP() - 86400 LIMIT 5000;
-- DELETE FROM rate_counters  WHERE window_end < UNIX_TIMESTAMP() LIMIT 5000;
-- UPDATE mailboxes SET used_bytes = (SELECT COALESCE(SUM(size_class),0)
--   FROM envelopes WHERE mailbox_hash = mailboxes.mailbox_hash AND acked_at IS NULL);
