-- Pinwheel game directory. The registry stores game CARDS (a small signed record: title, tagline, author, the SHA-256 and size of the game
-- file, a version and the publisher's public key) and a list of SOURCES (computers that currently hold a game and will serve it).
-- It never stores a game file: the bytes travel from computer to computer, and every client checks them against the card's SHA-256.
-- Cards are signed by their publisher with ML-DSA-65; clients verify the signature, the registry cannot forge or alter a card.

CREATE TABLE IF NOT EXISTS game_cards (
  publisher_id CHAR(16) NOT NULL,
  game_id VARCHAR(40) NOT NULL,
  title VARCHAR(40) NOT NULL,
  tagline VARCHAR(90) NOT NULL,
  author VARCHAR(24) NOT NULL,
  accent CHAR(7) NOT NULL,
  sha256 CHAR(64) NOT NULL,
  size_bytes INT NOT NULL,
  version INT NOT NULL,
  card_json TEXT NOT NULL,
  token_hash CHAR(64) NOT NULL,
  hidden TINYINT NOT NULL DEFAULT 0,
  created_at BIGINT NOT NULL,
  updated_at BIGINT NOT NULL,
  PRIMARY KEY (publisher_id, game_id),
  INDEX idx_game_updated (updated_at),
  INDEX idx_game_sha (sha256)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS game_sources (
  sha256 CHAR(64) NOT NULL,
  endpoint VARCHAR(255) NOT NULL,
  observed VARCHAR(64) NOT NULL,
  seen_at BIGINT NOT NULL,
  PRIMARY KEY (sha256, endpoint),
  INDEX idx_source_seen (seen_at)
) ENGINE=InnoDB;
