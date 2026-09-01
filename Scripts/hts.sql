-- HTS (HIV Testing Services) Report Query
--
-- Parameters:
--   current_date  date  — Reporting period end. Replaces CURRENT_DATE / NOW() throughout.
--
-- The fiscal-year start (Oct 1) in the dateVisit BETWEEN clause is computed
-- inline from current_date — no extra parameter needed.

-- HTS (HIV Testing Services) Report Query
--
-- Parameters:
--   current_date  date  — Reporting period end. Replaces CURRENT_DATE / NOW() throughout.
--
-- The fiscal-year start (Oct 1) in the dateVisit BETWEEN clause is computed
-- inline from current_date — no extra parameter needed.

WITH htsEncounter AS (
    SELECT patientUuid, clientCode, dateOfVisit, finalHivTestResult, previouslyTestedThisYear, basEntry.display entryPoint, baspreviouslyTestedNegative.display previouslyTestedNegative,
           basFacility.display AS testingSetting, basSession.display AS typeOfSession, basPreg.display AS pregnancyStatus, basIndexTest.display AS indexTesting,
           latestHts.indexClientCode, basIndexRel.display AS indexRelationship, basHivTest.display AS typeOfHivTestDone, basRecency.display AS recencyTest,
           basSyph.display AS syphilisTestResult, basKits.display AS hivTestKitsProvided, basCategory.display AS categoryOfClients, basCondom.display AS condomsProvided,
           CAST(latestHts.source AS TEXT) AS source, latestHts.longitude, latestHts.latitude, latestHts.facilityId, completedBy, CAST(CASE WHEN finalHivTestResult ILIKE '%Pos%' THEN patientUuid ELSE NULL END AS TEXT) AS positiveUuid,
           CAST((CASE WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting IN ('FACILITY_HTS_TEST_SETTING_ANC', 'FACILITY_HTS_TEST_SETTING_RETESTING', 'FACILITY_HTS_TEST_SETTING_L&D', 'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING') THEN ''
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_WARD_INPATIENT' THEN 'Inpatient'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_CT' THEN 'CT'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_TB' THEN 'TB'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_FP' THEN 'FP'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_STI' THEN 'STI'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting IN ('FACILITY_HTS_TEST_SETTING_SNS', 'FACILITY_HTS_TEST_SETTING_INDEX', 'FACILITY_HTS_TEST_SETTING_EMERGENCY', 'FACILITY_HTS_TEST_SETTING_BLOOD_BANK', 'FACILITY_HTS_TEST_SETTING_PEDIATRIC', 'FACILITY_HTS_TEST_SETTING_MALNUTRITION','FACILITY_HTS_TEST_SETTING_PREP_TESTING', 'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY', 'FACILITY_HTS_TEST_SETTING_OTHERS_(SPECIFY)')  THEN 'Others'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_STANDALONE_HTS' THEN 'Standalone'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting IN ('COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING', 'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES','COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX', 'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW') THEN 'Pregnant Women (Community)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting IN ('COMMUNITY_HTS_TEST_SETTING_INDEX', 'COMMUNITY_HTS_TEST_SETTING_OTHERS','COMMUNITY_HTS_TEST_SETTING_SNS', 'COMMUNITY_HTS_TEST_SETTING_CT', 'COMMUNITY_HTS_TEST_SETTING_OVC') THEN 'Others (Community)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting IN ('COMMUNITY_HTS_TEST_SETTING_OUTREACH', 'COMMUNITY_HTS_TEST_SETTING_STANDALONE_HTS') THEN 'Outreach (Community)'
               END) AS TEXT) AS gonModalities,
           CAST((CASE WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_ANC' THEN 'PMTCT (ANC1 Only)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting IN ('FACILITY_HTS_TEST_SETTING_RETESTING', 'FACILITY_HTS_TEST_SETTING_L&D' ) THEN 'PMTCT (Post ANC1: Pregnancy/L&D)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING' THEN 'PMTCT (Post ANC1: Breastfeeding)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_WARD_INPATIENT' THEN 'Inpatient'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_CT' THEN 'VCT'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_TB' THEN 'TB_STAT/OtherPITC'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting IN ('FACILITY_HTS_TEST_SETTING_FP', 'FACILITY_HTS_TEST_SETTING_BLOOD_BANK', 'FACILITY_HTS_TEST_SETTING_STANDALONE_HTS','FACILITY_HTS_TEST_SETTING_OTHERS_(SPECIFY)', 'FACILITY_HTS_TEST_SETTING_OTHERS') THEN 'Other PITC'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting ='FACILITY_HTS_TEST_SETTING_STI' THEN 'STI'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting ='FACILITY_HTS_TEST_SETTING_SNS' THEN 'SNS'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting ='FACILITY_HTS_TEST_SETTING_INDEX' THEN 'Index'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting ='FACILITY_HTS_TEST_SETTING_EMERGENCY' THEN 'Emergency'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_PEDIATRIC' THEN 'Pediatric'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_MALNUTRITION' THEN 'Malnutrition'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_PREP_TESTING' THEN 'PrEP_CT HTS'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_FACILITY' AND latestHts.facilitySetting = 'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY' THEN 'PMTCT (ANC1 Only)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting IN ('COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING', 'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES','COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX', 'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW') THEN 'PMTCT (ANC1 Only)'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting = 'COMMUNITY_HTS_TEST_SETTING_INDEX' THEN 'Index'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting IN ('COMMUNITY_HTS_TEST_SETTING_OTHERS', 'COMMUNITY_HTS_TEST_SETTING_OVC', 'COMMUNITY_HTS_TEST_SETTING_STANDALONE_HTS') THEN 'Other Community Platforms'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting = 'COMMUNITY_HTS_TEST_SETTING_SNS' THEN 'SNS'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting = 'COMMUNITY_HTS_TEST_SETTING_CT' THEN 'VCT'
                      WHEN latestHts.entryPoint = 'HTS_ENTRY_POINT_COMMUNITY' AND latestHts.facilitySetting = 'COMMUNITY_HTS_TEST_SETTING_OUTREACH' THEN 'Mobile'
               END) AS TEXT) AS pepfarModalities
    FROM (
             SELECT CAST(patient_uuid AS TEXT) patientUuid, client_code clientCode, CAST(date_of_visit AS DATE) dateOfVisit, setting entryPoint,
                    observation->>'previouslyTestedNegative' previouslyTestedNegative,
                    COALESCE(observation->>'facilitySetting', observation->>'communityEntryPoint') facilitySetting,
                    observation->>'typeOfSession' typeOfSession,

                    CASE
                        WHEN observation->>'pregnancyStatus' = 'Pregnant' THEN 'PREGANACY_STATUS_PREGNANT'
                        WHEN observation->>'pregnancyStatus' = 'Not Pregnant' THEN 'PREGANACY_STATUS_NOT_PREGNANT'
                        WHEN TRIM(SPLIT_PART(observation->>'pregnancyStatus', ' ', 1)) = ''
                            THEN 'PREGANACY_STATUS_NOT_PREGNANT'
                        ELSE SPLIT_PART(observation->>'pregnancyStatus', ' ', 1)
                        END AS pregnancyStatus,

                    observation->>'indexTesting' indexTesting,
                    observation->>'indexClientCode' indexClientCode,
                    observation->>'indexRelationship' indexRelationship,
                    observation->>'typeOfHivTestDone' typeOfHivTestDone,
                    observation->>'finalHivTestResult' finalHivTestResult,
                    observation->>'recencyTest' recencyTest,
                    observation->>'syphilisTestResult' syphilisTestResult,
                    observation->>'previouslyTestedThisYear' previouslyTestedThisYear,
                    observation->>'hivTestKitsProvided' hivTestKitsProvided,
                    observation->>'categoryOfClients' categoryOfClients,
                    observation->>'condomsProvided' condomsProvided,
                    observation->>'completedBy' completedBy,
                    source, longitude, latitude, facility_id facilityId,
                    ROW_NUMBER() OVER (PARTITION BY patient_uuid ORDER BY date_of_visit DESC) rnk
             FROM hts_encounter
             WHERE archived IS FALSE AND date_of_visit BETWEEN '1980-01-01' AND current_date AND facility_id = (select distinct (sm2.organisation_unit_id) from base_application_user_organisation_unit sm2)
         ) latestHts
             LEFT JOIN base_application_codeset basEntry ON basEntry.code = latestHts.entryPoint
             LEFT JOIN base_application_codeset baspreviouslyTestedNegative ON baspreviouslyTestedNegative.code = latestHts.previouslyTestedNegative
             LEFT JOIN base_application_codeset basFacility ON basFacility.code = latestHts.facilitySetting
             LEFT JOIN base_application_codeset basSession ON basSession.code = latestHts.typeOfSession
             LEFT JOIN base_application_codeset basPreg ON basPreg.code = latestHts.pregnancyStatus
             LEFT JOIN base_application_codeset basIndexTest ON basIndexTest.code = latestHts.indexTesting
             LEFT JOIN base_application_codeset basIndexRel ON basIndexRel.code = latestHts.indexRelationship
             LEFT JOIN base_application_codeset basHivTest ON basHivTest.code = latestHts.typeOfHivTestDone
             LEFT JOIN base_application_codeset basRecency ON basRecency.code = latestHts.recencyTest
             LEFT JOIN base_application_codeset basSyph ON basSyph.code = latestHts.syphilisTestResult
             LEFT JOIN base_application_codeset basKits ON basKits.code = latestHts.hivTestKitsProvided
             LEFT JOIN base_application_codeset basCategory ON basCategory.code = latestHts.categoryOfClients
             LEFT JOIN base_application_codeset basCondom ON basCondom.code = latestHts.condomsProvided
    WHERE rnk = 1
),
     previousHts AS (
         SELECT CAST(patient_uuid AS TEXT), dateOfPreviousHts, previousHtsResult FROM (
                                                                                          select patient_uuid, CAST(date_of_visit AS DATE) dateOfPreviousHts, observation->>'finalHivTestResult' previousHtsResult,
                                                                                                 ROW_NUMBER() OVER ( PARTITION BY patient_uuid ORDER BY date_of_visit DESC) AS rnk
                                                                                          FROM hts_encounter
                                                                                          WHERE archived IS FALSE AND date_of_visit BETWEEN '1980-01-01' AND current_date
                                                                                      ) pre where rnk = 2
     ),
     htsCounts AS (
         SELECT CAST(patient_uuid AS TEXT), CAST(COUNT(patient_uuid) AS INTEGER) AS numberOfCounts from hts_encounter where archived IS FALSE AND observation->>'finalHivTestResult' IS NOT NULL
                                                                                                                        AND date_of_visit BETWEEN '1980-01-01' AND current_date
         group by 1
     ),
     bio_data AS (
         SELECT DISTINCT ON (p.uuid) CAST(p.uuid AS TEXT) AS personUuid, p.hospital_number AS hospitalNumber,
                                     CAST(EXTRACT(YEAR FROM AGE(CAST(current_date AS DATE), p.date_of_birth)) AS INTEGER) AS age, INITCAP(p.sex) AS sex,
                                     p.date_of_birth AS dateOfBirth, facility.name AS facilityName, facility_lga.name AS lga, facility_state.name AS state,
                                     boui.code AS datimId, p.first_name AS firstName, p.surname AS surname, p.other_name AS otherName, p.contact_point->'contactPoint'->0->>'value' AS phoneNumber,
                                     p.marital_status->>'display' AS maritalStatus, p.address->'address'->0->>'city' AS address,
                                     p.employment_status->>'display' AS occupation, p.education->>'display' AS educationStatus
         FROM patient_person p
                  INNER JOIN base_organisation_unit facility ON facility.id = p.facility_id
                  INNER JOIN base_organisation_unit facility_lga ON facility_lga.id = facility.parent_organisation_unit_id
                  INNER JOIN base_organisation_unit facility_state ON facility_state.id = facility_lga.parent_organisation_unit_id
                  INNER JOIN base_organisation_unit_identifier boui
                             ON boui.organisation_unit_id = p.facility_id AND boui.name = 'DATIM_ID'
         WHERE p.archived = 0 AND p.facility_id = (select distinct (sm2.organisation_unit_id) from base_application_user_organisation_unit sm2)),
     patientResidencial as (select DISTINCT ON (personUuid) personUuid as personUuid11,
                                                            case when (addr ~ '^[0-9]+$') =TRUE
                                                                     then CAST((select name from base_organisation_unit where id = cast(addr as int)) AS TEXT) ELSE
                                                                     CAST((select name from base_organisation_unit where id = cast(facilityLga as int)) AS TEXT) end as lgaOfResidence,
                                                            CAST((select name from base_organisation_unit where id = (CASE WHEN stateResidence ~ '^[0-9]+$' THEN CAST(stateResidence AS INTEGER) ELSE null END)) AS TEXT) AS stateOfResidence
                            from (
                                     select CAST(pp.uuid AS TEXT) AS personUuid, facility_lga.parent_organisation_unit_id AS facilityLga, (jsonb_array_elements(pp.address->'address')->>'stateId') stateResidence, (jsonb_array_elements(pp.address->'address')->>'district') as addr from patient_person pp
                                                                                                                                                                                                                                                                                                LEFT JOIN base_organisation_unit facility_lga ON facility_lga.id = CAST (pp.organization->'id' AS INTEGER)
                                 ) dt)
SELECT
    bio.datimId                     AS datimCode,
    en.patientUuid             AS patientId,
	en.clientcode,
    bio.sex,
    bio.age,
    bio.maritalStatus,
    res.lgaOfResidence,
    res.stateOfResidence,
    en.dateOfVisit                  AS dateVisit,
    CASE
        WHEN htsCount.numberOfCounts > 1 THEN 'No'
        ELSE 'Yes'
        END                             AS firstTimeVisit,
    en.entryPoint,
    en.indexClientCode              AS indexClient,
    en.previouslyTestedThisYear     AS previouslyTested,
    en.categoryOfClients            AS targetGroup,
    en.source                       AS source,
    en.testingSetting			    AS testingSetting,
    en.pepfarModalities           AS modality,

    en.typeOfSession                AS counselingType,
    en.pregnancyStatus,
    en.indexTesting                 AS indexTesting,
    pre.dateOfPreviousHts           AS previousTestDate,
    pre.previousHtsResult           AS previousTestResult,
    htsCount.numberOfCounts         AS htsCount,

	CASE
    WHEN en.finalHivTestResult IS NULL OR en.finalHivTestResult = '' THEN 'Negative'
    ELSE en.finalHivTestResult
END AS finalHivTestResult,

    en.dateOfVisit                  AS dateOfHIVTesting,
    NULL::text                      AS prepOffered,
    NULL::text                      AS prepAccepted,
	latitude,
	longitude
FROM bio_data bio
         INNER JOIN htsEncounter en
                    ON en.patientUuid = bio.personUuid
         LEFT JOIN previousHts pre
                   ON pre.patient_uuid = en.patientUuid
         LEFT JOIN htsCounts htsCount
                   ON htsCount.patient_uuid = en.patientUuid
         LEFT JOIN patientResidencial res
                   ON res.personUuid11 = en.patientUuid
WHERE en.dateofvisit BETWEEN
          (CASE
               WHEN EXTRACT(MONTH FROM current_date) >= 10
                   THEN make_date(EXTRACT(YEAR FROM current_date)::int, 10, 1)
               ELSE make_date(EXTRACT(YEAR FROM current_date)::int - 1, 10, 1)
               END - INTERVAL '3 months')::date
          AND current_date