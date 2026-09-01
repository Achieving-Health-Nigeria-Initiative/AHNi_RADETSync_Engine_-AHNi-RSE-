\timing on
\set DATIM_CODE 'fakjSY6P1ED'

-- ============================================================
-- AHNi-RSE SQL Test Script
-- Facility: fakjSY6P1ED
-- DATIM: fakjSY6P1ED
-- Run with: psql -d lamisplus -f test_all_5_scripts.sql
-- ============================================================

\echo ''
\echo '=== TEST 0: Confirm DATIM code resolves to a facility_id ==='
SELECT boui.code, bou.name, boui.organisation_unit_id AS facility_id
FROM base_organisation_unit_identifier boui
JOIN base_organisation_unit bou ON bou.id = boui.organisation_unit_id
WHERE boui.code = :'DATIM_CODE'
  AND boui.name = 'DATIM_ID';

-- ============================================================
\echo ''
\echo '=== TEST 1: RADET — patient count for this facility ==='
SELECT COUNT(*) AS radet_patient_count
FROM patient_person p
INNER JOIN base_organisation_unit_identifier boui
        ON boui.organisation_unit_id = p.facility_id
       AND boui.name = 'DATIM_ID'
INNER JOIN hiv_enrollment h ON h.person_uuid = p.uuid
WHERE h.archived = 0
  AND p.archived = 0
  AND boui.code = :'DATIM_CODE';

-- ============================================================
\echo ''
\echo '=== TEST 2: HTS — client count for this facility ==='
SELECT COUNT(*) AS hts_client_count
FROM hts_client hc
LEFT JOIN base_organisation_unit_identifier boui
       ON boui.organisation_unit_id = hc.facility_id
      AND boui.name = 'DATIM_ID'
WHERE hc.archived = 0
  AND boui.code = :'DATIM_CODE'
  AND hc.date_visit >= '1980-01-01'
  AND hc.date_visit <= current_date;

-- ============================================================
\echo ''
\echo '=== TEST 3: INDEX — elicitation records for this facility ==='
SELECT COUNT(*) AS index_elicitation_count
FROM hts_client hc
INNER JOIN hts_index_elicitation hie
        ON hie.hts_client_uuid = hc.uuid
       AND hie.archived = 0
LEFT JOIN base_organisation_unit_identifier boui
       ON boui.organisation_unit_id = hc.facility_id
      AND boui.name = 'DATIM_ID'
WHERE hc.archived = 0
  AND boui.code = :'DATIM_CODE'
  AND hc.date_visit BETWEEN '1980-01-01' AND CURRENT_DATE;

-- ============================================================
\echo ''
\echo '=== TEST 4: PMTCTHTS — female patients in PMTCT for this facility ==='
SELECT COUNT(*) AS pmtcthts_count
FROM patient_person p
LEFT JOIN base_organisation_unit_identifier boui
       ON boui.organisation_unit_id = p.facility_id
      AND boui.name = 'DATIM_ID'
WHERE p.archived = 0
  AND boui.code = :'DATIM_CODE'
  AND p.sex ILIKE '%Female%'
  AND p.uuid IN (
      SELECT person_uuid FROM pmtct_anc
      UNION
      SELECT person_uuid FROM pmtct_enrollment
  );

-- ============================================================
\echo ''
\echo '=== TEST 5: MATERNAL — PMTCT enrollment records for this facility ==='
SELECT COUNT(*) AS maternal_count
FROM pmtct_enrollment pe
LEFT JOIN patient_person pp ON pp.uuid = pe.person_uuid
LEFT JOIN base_organisation_unit_identifier boui
       ON boui.organisation_unit_id = pp.facility_id
      AND boui.name = 'DATIM_ID'
WHERE pe.pmtct_enrollment_date BETWEEN '1980-01-01' AND CURRENT_DATE
  AND boui.code = :'DATIM_CODE';

-- ============================================================
\echo ''
\echo '=== ALL TESTS DONE ==='
\echo 'Any count > 0 = script logic confirmed working for this facility'
\echo 'Count = 0 on PMTCT/MATERNAL = normal if facility has no PMTCT data'
