-- =============================================================================
-- AHNi-RSE Migration 002: Populate rse_facility_registry from the 74 DATIM codes
-- Run on: DB-01 (primary) — as postgres
-- Names are taken directly from base_organisation_unit — no manual reassignment
-- facility_name_blob = name with spaces → underscores, specials removed
--   (must match the xlsx filenames already in blob storage)
-- =============================================================================

INSERT INTO rse_facility_registry
    (datim_code, facility_name, facility_name_blob, state, lga, is_active, expected_min_rows)
SELECT
    boui.code                                                           AS datim_code,
    bou.name                                                            AS facility_name,
    -- Build blob-safe filename segment:
    --   1. Remove apostrophes, dots, commas, hyphens
    --   2. Replace one-or-more spaces with a single underscore
    REGEXP_REPLACE(
        REGEXP_REPLACE(
            TRANSLATE(bou.name, '''.,', ''),   -- remove ' . ,
            '\s+', '_', 'g'                    -- spaces → underscore
        ),
        '-', '', 'g'                           -- remove hyphens
    )                                                                   AS facility_name_blob,
    facility_state.name                                                 AS state,
    facility_lga.name                                                   AS lga,
    TRUE                                                                AS is_active,
    10                                                                  AS expected_min_rows
FROM base_organisation_unit_identifier boui
JOIN base_organisation_unit bou
     ON bou.id = boui.organisation_unit_id
LEFT JOIN base_organisation_unit facility_lga
     ON facility_lga.id = bou.parent_organisation_unit_id
LEFT JOIN base_organisation_unit facility_state
     ON facility_state.id = facility_lga.parent_organisation_unit_id
WHERE boui.name = 'DATIM_ID'
  AND boui.code IN (
    'W387n0fbylW','Pjak5oARJBf','RoxOslFYbmA','KrHj7Ycvctj','J2AZkcaFst0',
    'WjDvZSLT4i5','R2bNqBqJUIw','apo2hT9i94X','x7TBWFDy1GZ','wAZ9LXoASUu',
    'cVnKorgLC9B','l01zG9ePfqt','zxSXLppge0P','hozfIkuo4eE','S5HvxHdK2Vt',
    'DqWvsU2R8rw','Nx613mKHzsk','XyFDmlevgCu','KXoSesiSLeE','RRPYgMWYw4h',
    'qY7ZkVcos1O','GbtTSJLBKbI','SSB1jXm3jdi','gfEDfny3Hp0','RxLmSTf6Kpl',
    'DPA6BxE9pzQ','RWQP4pGxyYS','gF8UahvIXw9','VZCEov8JZIe','o1xyfiaFzDG',
    'oN0Woo9C7qb','gxpWWY8YvrW','bUhfkjY3yVP','fakjSY6P1ED','eJDSKTK7sO8',
    'bxBIxJ7LJ4Q','w4gxim2NiVj','FQ0Jc3pO5Op','OQYeCajbh5G','VUQpWeYseot',
    'JcYmXdOSndf','nX6ReqpEjKZ','LN72HGCti4v','z6APuLTiHAX','sBzJ4tnvCMD',
    'JPBcTpp6XUu','DVlcYRf680L','KTsdxBrj1K8','kwnTWvSacEg','wSzsuzrAmU1',
    'wr9dSNAmnWp','FLdJ1UojpI5','YFOBz4PMuNV','DgTi7GSbUAE','Aocjg00w77E',
    'iWJPpf18IyU','GHUokYisiZe','Uyo9Hpc3vql','dlzwTad81iz','sP8QEwb0SXo',
    'KGNkYub5Oxq','rmGHrOrVW9r','qaVq74gMDhs','zZXDNWVezMV','FWEjuDChsDH',
    'C8zbhPQNbPk','IvofjQjgSeE','wC0drfSNmV4','RFVNndb3OKN','JqquBCRr5Ie',
    'q1D4GdFPtqJ','B0V6x5le9Tj','wRBooaCoKz6','M0b885TVP1Q'
  )
ON CONFLICT (datim_code) DO UPDATE SET
    facility_name      = EXCLUDED.facility_name,
    facility_name_blob = EXCLUDED.facility_name_blob,
    state              = EXCLUDED.state,
    lga                = EXCLUDED.lga,
    updated_at         = NOW();

-- =============================================================================
-- Verification: check what was inserted and flag any missing codes
-- =============================================================================

-- How many rows inserted?
SELECT COUNT(*) AS total_inserted FROM rse_facility_registry;

-- Full list — confirm names and blob names look correct
SELECT
    datim_code,
    facility_name,
    facility_name_blob,
    state,
    lga
FROM rse_facility_registry
ORDER BY facility_name;

-- Which of the 74 DATIM codes were NOT found in the database?
SELECT unnest(ARRAY[
    'W387n0fbylW','Pjak5oARJBf','RoxOslFYbmA','KrHj7Ycvctj','J2AZkcaFst0',
    'WjDvZSLT4i5','R2bNqBqJUIw','apo2hT9i94X','x7TBWFDy1GZ','wAZ9LXoASUu',
    'cVnKorgLC9B','l01zG9ePfqt','zxSXLppge0P','hozfIkuo4eE','S5HvxHdK2Vt',
    'DqWvsU2R8rw','Nx613mKHzsk','XyFDmlevgCu','KXoSesiSLeE','RRPYgMWYw4h',
    'qY7ZkVcos1O','GbtTSJLBKbI','SSB1jXm3jdi','gfEDfny3Hp0','RxLmSTf6Kpl',
    'DPA6BxE9pzQ','RWQP4pGxyYS','gF8UahvIXw9','VZCEov8JZIe','o1xyfiaFzDG',
    'oN0Woo9C7qb','gxpWWY8YvrW','bUhfkjY3yVP','fakjSY6P1ED','eJDSKTK7sO8',
    'bxBIxJ7LJ4Q','w4gxim2NiVj','FQ0Jc3pO5Op','OQYeCajbh5G','VUQpWeYseot',
    'JcYmXdOSndf','nX6ReqpEjKZ','LN72HGCti4v','z6APuLTiHAX','sBzJ4tnvCMD',
    'JPBcTpp6XUu','DVlcYRf680L','KTsdxBrj1K8','kwnTWvSacEg','wSzsuzrAmU1',
    'wr9dSNAmnWp','FLdJ1UojpI5','YFOBz4PMuNV','DgTi7GSbUAE','Aocjg00w77E',
    'iWJPpf18IyU','GHUokYisiZe','Uyo9Hpc3vql','dlzwTad81iz','sP8QEwb0SXo',
    'KGNkYub5Oxq','rmGHrOrVW9r','qaVq74gMDhs','zZXDNWVezMV','FWEjuDChsDH',
    'C8zbhPQNbPk','IvofjQjgSeE','wC0drfSNmV4','RFVNndb3OKN','JqquBCRr5Ie',
    'q1D4GdFPtqJ','B0V6x5le9Tj','wRBooaCoKz6','M0b885TVP1Q'
]) AS missing_code
WHERE unnest NOT IN (SELECT datim_code FROM rse_facility_registry);
