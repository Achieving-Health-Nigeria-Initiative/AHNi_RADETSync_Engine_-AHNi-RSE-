-- =============================================================================
-- AHNi-RSE Migration 001: Create RSE support tables
-- Run on: DB-01 (primary) — 10.10.50.11 or via pgbouncer 10.10.40.10:6432
-- Database: lamisplus
-- Run as: postgres
-- =============================================================================

-- -----------------------------------------------------------------------
-- TABLE 1: rse_facility_registry
-- Authoritative list of all 74 active facilities.
-- The RSE reads this table at the start of every cycle.
-- facility_name_blob = facility name with spaces replaced by underscores
--   (used to generate xlsx filenames — must match what's in blob storage)
-- -----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS rse_facility_registry (
    id                  SERIAL          PRIMARY KEY,
    datim_code          VARCHAR(20)     UNIQUE NOT NULL,
    facility_name       VARCHAR(255)    NOT NULL,
    facility_name_blob  VARCHAR(255)    NOT NULL,   -- underscored, for filename generation
    state               VARCHAR(100),
    lga                 VARCHAR(100),
    is_active           BOOLEAN         NOT NULL DEFAULT TRUE,
    expected_min_rows   INT             NOT NULL DEFAULT 10,
    created_at          TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_facility_datim  ON rse_facility_registry (datim_code);
CREATE INDEX IF NOT EXISTS idx_facility_active ON rse_facility_registry (is_active);

COMMENT ON TABLE  rse_facility_registry                    IS 'Authoritative facility list for AHNi-RSE — 74 active facilities';
COMMENT ON COLUMN rse_facility_registry.datim_code         IS 'PEPFAR DATIM code — unique per facility';
COMMENT ON COLUMN rse_facility_registry.facility_name_blob IS 'Facility name as used in xlsx filenames: spaces → underscores, specials removed';
COMMENT ON COLUMN rse_facility_registry.expected_min_rows  IS 'Minimum RADET row count — run fails if actual count is below this';


-- -----------------------------------------------------------------------
-- TABLE 2: rse_run_audit
-- One row per facility per cycle run.
-- Written by the RSE after every facility job (success, fail, or skip).
-- Always written to the PRIMARY — never to the replica.
-- -----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS rse_run_audit (
    id              BIGSERIAL       PRIMARY KEY,
    run_id          UUID            NOT NULL DEFAULT gen_random_uuid(),
    cycle_id        UUID            NOT NULL,           -- same for all 74 facilities in one cron run
    datim_code      VARCHAR(20)     NOT NULL,
    facility_name   VARCHAR(255),
    report_date     DATE            NOT NULL,
    status          VARCHAR(20)     NOT NULL,           -- PENDING / RUNNING / SUCCESS / FAILED / SKIPPED
    radet_rows      INT,
    hts_rows        INT,
    index_rows      INT,
    pmtcthts_rows   INT,
    maternal_rows   INT,
    file_hash       VARCHAR(64),                        -- SHA-256 of uploaded xlsx
    blob_path       TEXT,                               -- autoreport/RADET/{date}/{filename}.xlsx
    db_source       VARCHAR(10),                        -- PRIMARY or REPLICA
    replica_lag_ms  INT,
    duration_ms     INT,
    error_message   TEXT,
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_audit_datim   ON rse_run_audit (datim_code);
CREATE INDEX IF NOT EXISTS idx_audit_date    ON rse_run_audit (report_date);
CREATE INDEX IF NOT EXISTS idx_audit_status  ON rse_run_audit (status);
CREATE INDEX IF NOT EXISTS idx_audit_cycle   ON rse_run_audit (cycle_id);

COMMENT ON TABLE  rse_run_audit              IS 'Per-facility per-cycle audit log for AHNi-RSE';
COMMENT ON COLUMN rse_run_audit.cycle_id     IS 'Groups all 74 facility rows that belong to one cron run';
COMMENT ON COLUMN rse_run_audit.file_hash    IS 'SHA-256 hex of the uploaded xlsx — used for integrity verification';
COMMENT ON COLUMN rse_run_audit.db_source    IS 'Which DB was used: REPLICA (DB-DR) or PRIMARY (DB-01)';


-- -----------------------------------------------------------------------
-- Verification queries — run these after migration to confirm
-- -----------------------------------------------------------------------
-- SELECT table_name, pg_size_pretty(pg_total_relation_size(quote_ident(table_name)))
-- FROM information_schema.tables
-- WHERE table_schema = 'public'
--   AND table_name IN ('rse_facility_registry', 'rse_run_audit');

-- SELECT column_name, data_type, is_nullable
-- FROM information_schema.columns
-- WHERE table_name = 'rse_facility_registry'
-- ORDER BY ordinal_position;

-- SELECT column_name, data_type, is_nullable
-- FROM information_schema.columns
-- WHERE table_name = 'rse_run_audit'
-- ORDER BY ordinal_position;
