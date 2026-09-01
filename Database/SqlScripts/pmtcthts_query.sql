/*
 * ─────────────────────────────────────────────────────────────────────────────
 * PMTCT HTS Report — Optimized
 * Source : LAMIS / NDR PostgreSQL Database
 * Based  : New pmtctHts CTE query (2025 version)
 *
 * Columns : 16 (as specified)
 * Cross-facility safe: archived filter removed from ALL pmtct_* tables
 *   (pmtct_anc, pmtct_enrollment, pmtct_hts, pmtct_delivery)
 *   Kept only on core tables: patient_person, hts_client,
 *   hts_risk_stratification, laboratory_result
 *
 * datimCode sourced from boui.code (added to pmtctHts CTE)
 * ─────────────────────────────────────────────────────────────────────────────
 */

WITH pmtctHts AS (
    SELECT DISTINCT ON (p.uuid)
        p.uuid                                                      AS personUuid,
        p.id,
        boui.code                                                   AS datimId,
        p.hospital_number                                           AS hospitalNumber,
        p.date_of_birth                                             AS motherDob,
        EXTRACT(YEAR FROM AGE(CAST(NOW() AS DATE), date_of_birth))  AS motherAge,
        p.marital_status->>'display'                                AS maritalStatus,
        p.date_of_registration                                      AS dateOfRegistration,
        facility_state.name                                         AS state,
        facility_lga.name                                           AS lgaName,
        facility.name                                               AS facilityName
    FROM patient_person p
    INNER JOIN (
        SELECT * FROM (
            SELECT p.id,
                CONCAT(
                    CAST(address_object->>'city' AS VARCHAR), ' ',
                    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        CAST(address_object->>'line' AS TEXT),
                        '\\', ''), ']', ''), '[', ''), 'null', ''), '\\', '')
                ) AS address,
                CASE WHEN address_object->>'stateId'  ~ '^\d+(\.\d+)?$'
                     THEN address_object->>'stateId'  ELSE NULL END AS stateId,
                CASE WHEN address_object->>'district' ~ '^\d+(\.\d+)?$'
                     THEN address_object->>'district' ELSE NULL END AS lgaId
            FROM patient_person p,
                 jsonb_array_elements(p.address->'address') WITH ORDINALITY l(address_object)
        ) AS result
    ) r ON r.id = p.id
    LEFT JOIN base_organisation_unit facility
           ON facility.id = p.facility_id
    LEFT JOIN base_organisation_unit facility_lga
           ON facility_lga.id = facility.parent_organisation_unit_id
    LEFT JOIN base_organisation_unit facility_state
           ON facility_state.id = facility_lga.parent_organisation_unit_id
    LEFT JOIN base_organisation_unit res_state
           ON res_state.id = CAST(r.stateid AS BIGINT)
    LEFT JOIN base_organisation_unit res_lga
           ON res_lga.id = CAST(r.lgaid AS BIGINT)
    LEFT JOIN base_organisation_unit_identifier boui
           ON boui.organisation_unit_id = p.facility_id
          AND boui.name = 'DATIM_ID'
    WHERE p.archived = 0
      -- all facilities: datim_code filter removed
      AND p.sex ILIKE '%Female%'
      AND p.uuid IN (
          SELECT person_uuid FROM pmtct_anc
          UNION
          SELECT person_uuid FROM pmtct_enrollment
      )
),

ancClient AS (
    SELECT
        person_uuid                                                 AS person_uuid_anc,
        (CASE WHEN community_setting = 'PMTCT (ANC1 Only)' THEN 'PMTCT (ANC1 Only)'
              ELSE (SELECT display FROM base_application_codeset
                    WHERE code = community_setting) END)            AS ancSettingAnc,
        previously_known_hiv_status                                 AS previouslyKnownHivStatus,
        first_anc_date                                              AS firstAncDate,
        gaweeks                                                     AS gaweeksAnc,
        gravida                                                     AS gravidaAnc,
        parity                                                      AS parityAnc,
        facility_enrolled_in                                        AS facilityEnrolledIn,
        tested_syphilis                                             AS testedSyphilisAnc,
        test_result_syphilis                                        AS testResultSyphilisAnc,
        CASE
            WHEN treated_syphilis = 'Yes'            THEN 'Treated'
            WHEN referred_syphilis_treatment = 'Yes' THEN 'Referred for Treatment'
            ELSE 'No treatment'
        END                                                         AS syphilisTreatmentStatus,
        partner_information->>'age'                                 AS age,
        partner_information->>'syphillisStatus'                     AS syphillisStatus,
        partner_information->>'acceptHivTest'                       AS acceptHivTest,
        partner_information->>'referredTo'                          AS referredTo,
        pmtct_hts_info->>'hivRestested'                             AS hivRestested,
        pmtct_hts_info->>'acceptedHIVTesting'                       AS acceptedHIVTesting,
        pmtct_hts_info->>'dateTestedHivPositive'                    AS dateTestedHivPositive,
        pmtct_hts_info->>'receivedHivRetestedResult'                AS receivedHivRetestedResult,
        pmtct_hts_info->>'previouslyKnownHIVPositive'               AS previouslyKnownHIVPositive,
        anc_no                                                      AS ancNo,
        static_hiv_status                                           AS staticHivStatus,
        ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY first_anc_date DESC) AS rnk1
    FROM pmtct_anc
    -- no archived filter: column does not exist on all 74+ facilities
),

htsClient AS (
    SELECT * FROM (
        SELECT
            hc.person_uuid                                          AS person_uuid_hts_client,
            COALESCE(NULLIF(TRIM(hiv_test_result2), ''), hiv_test_result) AS hivTestResult,
            hc.risk_stratification_code                             AS risk_stratification_code_hts_client,
            (CASE
                WHEN hts_risk.entry_point = 'HTS_ENTRY_POINT_FACILITY'
                 AND hts_risk.testing_setting = 'FACILITY_HTS_TEST_SETTING_ANC'
                                                                    THEN 'PMTCT (ANC1 Only)'
                WHEN hts_risk.entry_point = 'HTS_ENTRY_POINT_FACILITY'
                 AND hts_risk.testing_setting IN (
                     'FACILITY_HTS_TEST_SETTING_RETESTING',
                     'FACILITY_HTS_TEST_SETTING_L&D')               THEN 'PMTCT (Post ANC1: Pregnancy/L&D)'
                WHEN hts_risk.entry_point = 'HTS_ENTRY_POINT_FACILITY'
                 AND hts_risk.testing_setting = 'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING'
                                                                    THEN 'PMTCT (Post ANC1: Breastfeeding)'
                WHEN hts_risk.entry_point = 'HTS_ENTRY_POINT_COMMUNITY'
                 AND hts_risk.testing_setting IN (
                     'COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING',
                     'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES',
                     'COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX',
                     'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW')       THEN 'PMTCT (ANC1 Only)'
            END)                                                    AS pepfarModalities,
            (SELECT display FROM base_application_codeset
             WHERE code = hts_risk.entry_point)                     AS entryPoint,
            (SELECT display FROM base_application_codeset
             WHERE code = hts_risk.testing_setting)                 AS testingSetting,
            COALESCE(he.date_of_registration, he.date_started)      AS hivEnrollmentDate,
            he.date_of_registration                                 AS dateOfRegistrationOnHiv,
            he.date_confirmed_hiv                                   AS dateConfirmHiv,
            he.date_started                                         AS dateStarted,
            he.unique_id                                            AS hivUniqueId,
            pmtctenroll.pmtct_enrollment_date                       AS pmtctEnrollmentDate,
            pmtctdov.date_of_viral_load                             AS dateOfViralLoad,
            labResult.result_reported                               AS resultReported,
            labResult.date_result_reported                          AS dateResultReported,
            hts_retest.visitDateIntial,
            hts_retest.hivResultInital,
            retestingOpt.reVisitDate,
            retestingOpt.reHivResult,
            (CASE WHEN AGE(hts_retest.visitDateIntial, retestingOpt.reVisitDate) <= INTERVAL '2 years'
                  THEN retestingOpt.reVisitDate ELSE NULL END)      AS maternalRetestingDate,
            (CASE WHEN AGE(hts_retest.visitDateIntial, retestingOpt.reVisitDate) <= INTERVAL '2 years'
                  THEN retestingOpt.reHivResult ELSE NULL END)      AS maternalRetestingResult,
            ROW_NUMBER() OVER (PARTITION BY hc.person_uuid
                               ORDER BY date_visit DESC, hc.date_created DESC) AS rnk,
            date_visit,
            hc.facility_id
        FROM hts_client hc
        LEFT JOIN hts_risk_stratification hts_risk
               ON hc.risk_stratification_code = hts_risk.code
              AND hts_risk.archived = 0
        LEFT JOIN hiv_enrollment he
               ON hc.person_uuid = he.person_uuid
        LEFT JOIN pmtct_enrollment pmtctenroll
               ON hc.person_uuid = pmtctenroll.person_uuid
        LEFT JOIN pmtct_mother_visitation pmtctdov
               ON hc.person_uuid = pmtctdov.person_uuid
        LEFT JOIN laboratory_order labOrder
               ON hc.person_uuid = labOrder.patient_uuid
        LEFT JOIN laboratory_test labTest
               ON labOrder.id = labTest.lab_order_id
              AND labTest.lab_test_id = 16
        LEFT JOIN laboratory_result labResult
               ON labResult.test_id = labTest.id
              AND labResult.archived = 0
        LEFT JOIN (
            SELECT hct.person_uuid,
                   hct.date_visit AS visitDateIntial,
                   (CASE WHEN (hct.hiv_test_result2 IS NULL OR hct.hiv_test_result2 = '')
                         THEN hct.hiv_test_result
                         ELSE hct.hiv_test_result2 END)             AS hivResultInital,
                   risk.testing_setting,
                   hct.risk_stratification_code,
                   ROW_NUMBER() OVER (PARTITION BY hct.person_uuid ORDER BY date_visit DESC) AS rowNums
            FROM hts_client hct
            LEFT JOIN hts_risk_stratification risk
                   ON hct.risk_stratification_code = risk.code
                  AND risk.archived = 0
            WHERE hct.pregnant IN (73,74,75)
              AND risk.testing_setting IN (
                  'FACILITY_HTS_TEST_SETTING_ANC',
                  'COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX',
                  'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW',
                  'COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING',
                  'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES',
                  'FACILITY_HTS_TEST_SETTING_L&D',
                  'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING',
                  'COMMUNITY_PMTCT_SPOKE_HEALTH_FACILITY',
                  'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY')
              AND hct.date_visit BETWEEN '1980-01-01' AND CURRENT_DATE
        ) AS hts_retest ON hc.person_uuid = hts_retest.person_uuid
        LEFT JOIN (
            SELECT hct.person_uuid,
                   hct.date_visit AS reVisitDate,
                   (CASE WHEN (hct.hiv_test_result2 IS NULL OR hct.hiv_test_result2 = '')
                         THEN hct.hiv_test_result
                         ELSE hct.hiv_test_result2 END)             AS reHivResult,
                   risk.testing_setting,
                   hct.risk_stratification_code,
                   ROW_NUMBER() OVER (PARTITION BY hct.person_uuid ORDER BY date_visit DESC) AS rowNums
            FROM hts_client hct
            LEFT JOIN hts_risk_stratification risk
                   ON hct.risk_stratification_code = risk.code
                  AND risk.archived = 0
            WHERE hct.pregnant IN (73,74,75)
              AND risk.testing_setting IN ('FACILITY_HTS_TEST_SETTING_RETESTING')
        ) AS retestingOpt ON hc.person_uuid = retestingOpt.person_uuid
        WHERE hc.archived = 0
          AND hts_risk.testing_setting IN (
              'FACILITY_HTS_TEST_SETTING_ANC',
              'COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX',
              'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW',
              'COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING',
              'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES',
              'FACILITY_HTS_TEST_SETTING_L&D',
              'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING',
              'FACILITY_HTS_TEST_SETTING_RETESTING',
              'COMMUNITY_PMTCT_SPOKE_HEALTH_FACILITY',
              'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY')
        GROUP BY hc.person_uuid, hc.date_visit, hc.hiv_test_result, hc.hiv_test_result2,
                 hc.risk_stratification_code, hc.hepatitis_testing, hc.date_created, hc.recency,
                 hts_risk.testing_setting, hts_risk.entry_point, hc.facility_id,
                 he.date_started, he.date_of_registration, he.date_confirmed_hiv,
                 pmtctenroll.pmtct_enrollment_date, pmtctdov.date_of_viral_load,
                 labResult.result_reported, labResult.date_result_reported, he.unique_id,
                 hts_retest.visitDateIntial, hts_retest.hivResultInital,
                 retestingOpt.reVisitDate, retestingOpt.reHivResult
    ) rr
    WHERE rnk = 1
      AND date_visit BETWEEN '1980-01-01' AND CURRENT_DATE
),

firstHts AS (
    SELECT person_uuid,
           hiv_test_result                                          AS firstVisitHtsResult,
           date_visit                                               AS firstVisitHts,
           ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY date_visit ASC) AS rnkk
    FROM hts_client
    WHERE archived = 0
      AND hiv_test_result != ''
      AND date_visit BETWEEN '1980-01-01' AND CURRENT_DATE
),

firtPmtctHts AS (
    SELECT person_uuid,
           date_of_hiv_test                                         AS dateOfHivTest,
           final_result                                             AS finalResult,
           ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY date_of_hiv_test ASC) AS rnkk
    FROM pmtct_hts
    -- no archived filter: column does not exist on all 74+ facilities
    WHERE date_of_hiv_test BETWEEN '1980-01-01' AND CURRENT_DATE
),

htsPmtct AS (
    SELECT
        person_uuid                                                 AS personUuid100,
        (CASE
            WHEN test_entry_point = 'ENROLLMENT_SETTING_COMMUNITY'
             AND test_setting IN (
                 'COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING',
                 'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES',
                 'COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX',
                 'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW')           THEN 'PMTCT (ANC1 Only)'
            WHEN test_entry_point = 'ENROLLMENT_SETTING_FACILITY'
             AND test_setting IN (
                 'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY',
                 'FACILITY_HTS_TEST_SETTING_ANC')                    THEN 'PMTCT (ANC1 Only)'
            WHEN test_entry_point = 'ENROLLMENT_SETTING_FACILITY'
             AND test_setting = 'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING'
                                                                    THEN 'PMTCT (Post ANC1: Breastfeeding)'
            WHEN test_entry_point = 'ENROLLMENT_SETTING_FACILITY'
             AND test_setting IN (
                 'FACILITY_HTS_TEST_SETTING_RETESTING',
                 'FACILITY_HTS_TEST_SETTING_L&D')                   THEN 'PMTCT (Post ANC1: Pregnancy/L&D)'
        END)                                                        AS pepfarModalities,
        (SELECT display FROM base_application_codeset
         WHERE code = test_entry_point)                             AS testEntryPoint,
        initial_hiv_test, date_of_hiv_test,
        (SELECT display FROM base_application_codeset
         WHERE code = test_setting)                                 AS testSetting,
        final_result, hepatitis_b, hepatitis_c, syphilis, testing_type,
        ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY date_of_hiv_test DESC) AS rnkk1
    FROM pmtct_hts
    -- no archived filter: column does not exist on all 74+ facilities
    WHERE date_of_hiv_test BETWEEN '1980-01-01' AND CURRENT_DATE
)

-- ── Final SELECT — 16 columns as specified ────────────────────────────────────
SELECT
    pmtctHts.datimId                                                AS "datimCode",
    pmtctHts.personUuid                                             AS "Patient ID",
    pmtctHts.hospitalNumber                                         AS "Mother's Hospital Num",
    pmtctHts.motherAge                                              AS "Age",
    anc.ancSettingAnc                                               AS "ANC Setting",
    COALESCE(htsPmtct.pepfarModalities, hts.pepfarModalities)       AS "Modality",
    COALESCE(htsPmtct.testEntryPoint,   hts.entryPoint)             AS "Point of Entry",
    hts.pmtctEnrollmentDate                                         AS "Date of registration in index pregnancy",
    anc.gaweeksAnc                                                  AS "Gestational Age (Weeks) @ First ANC visit",
    (CASE WHEN firtPmtctHts.dateOfHivTest <= firstHts.firstVisitHts
          THEN COALESCE(firtPmtctHts.dateOfHivTest,    firstHts.firstVisitHts)
          ELSE COALESCE(firstHts.firstVisitHts,         firtPmtctHts.dateOfHivTest)
     END)                                                           AS "Date Tested for HIV",
    (CASE WHEN firtPmtctHts.dateOfHivTest <= firstHts.firstVisitHts
          THEN COALESCE(NULLIF(TRIM(firtPmtctHts.finalResult), ''), firstHts.firstVisitHtsResult)
          ELSE COALESCE(NULLIF(TRIM(firstHts.firstVisitHtsResult), ''), firtPmtctHts.finalResult)
     END)                                                           AS "HIV Test Result",
    anc.testResultSyphilisAnc                                       AS "Syphillis Test Result",
    (CASE WHEN htsPmtct.testing_type = 'RETESTING'
          THEN COALESCE(htsPmtct.date_of_hiv_test, hts.maternalRetestingDate)
          ELSE COALESCE(hts.maternalRetestingDate, NULL)
     END)                                                           AS "Date Of Maternal Retesting",
    anc.previouslyKnownHivStatus                                    AS "Previously Known HIV +Ve Status",
    anc.facilityEnrolledIn                                          AS "Facility Enrolled In",
    anc.syphilisTreatmentStatus                                     AS "Linked to Syphilis Treatment"

FROM pmtctHts
LEFT JOIN pmtct_enrollment pe
       ON pmtctHts.personUuid = pe.person_uuid
LEFT JOIN ancClient anc
       ON pmtctHts.personUuid = anc.person_uuid_anc
      AND anc.rnk1 = 1
LEFT JOIN htsClient hts
       ON hts.person_uuid_hts_client = pmtctHts.personUuid
      AND hts.hivTestResult IS NOT NULL
      AND hts.hivTestResult != ''
      AND hts.rnk = 1
LEFT JOIN hiv_enrollment he
       ON he.person_uuid = pmtctHts.personUuid
LEFT JOIN htsPmtct
       ON htsPmtct.personUuid100 = pmtctHts.personUuid
      AND htsPmtct.rnkk1 = 1
LEFT JOIN firstHts
       ON firstHts.person_uuid = pmtctHts.personUuid
      AND firstHts.rnkk = 1
LEFT JOIN firtPmtctHts
       ON firtPmtctHts.person_uuid = pmtctHts.personUuid
      AND firtPmtctHts.rnkk = 1;
