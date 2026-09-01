/*
 * ─────────────────────────────────────────────────────────────────────────────
 * Maternal Cohort Report — Optimized
 * Source : LAMIS / NDR PostgreSQL Database
 *
 * Columns: 48 (reduced from 65 — 17 columns removed per specification)
 * Removed: Mother's Unique ID, Mother's Date of Birth, Marital Status,
 *          Modality, LMP Date, Gestational Age @ First ANC, Gravida, Parity,
 *          PCV @ANC registration, Hepatitis B Test Result,
 *          Treated for Hepatitis B, Current Pregnancy Status, Visit Status,
 *          Mother's DSD Status, VL result at 32-36 weeks GA,
 *          Date of VL result at 32-36 weeks GA, Child Unique ID
 *
 * Renamed: datimid → datimCode
 *
 * All CTEs are unchanged from the original query.
 * ─────────────────────────────────────────────────────────────────────────────
 */

WITH maternalCohort AS (
    SELECT boui.code AS DatimId, pe.person_uuid, pe.hospital_number,
           pe.pmtct_enrollment_date, pe.gaweeks, pe.gravida,
           pe.art_start_date, art_time.display AS artStartTime,
           tb.display AS tbStatus, entry.display AS entryPoint, pe.lmp,
           facility_state.name statename, facility_lga.name lgaName,
           facility.name facilityName, pp.date_of_birth AS motherDob,
           EXTRACT(YEAR FROM AGE(CAST(CURRENT_DATE AS DATE), pp.date_of_birth)) AS motherAge,
           pp.marital_status->>'display' AS maritalStatus,
           ROW_NUMBER() OVER (PARTITION BY pe.person_uuid ORDER BY pe.pmtct_enrollment_date DESC) rnkk
    FROM public.pmtct_enrollment pe
    LEFT JOIN patient_person pp                     ON pp.uuid = pe.person_uuid
    LEFT JOIN base_organisation_unit facility        ON facility.id = pp.facility_id
    LEFT JOIN base_organisation_unit_identifier boui ON boui.organisation_unit_id = pp.facility_id AND boui.name = 'DATIM_ID'
    LEFT JOIN base_organisation_unit facility_lga    ON facility_lga.id = facility.parent_organisation_unit_id
    LEFT JOIN base_organisation_unit facility_state  ON facility_state.id = facility_lga.parent_organisation_unit_id
    LEFT JOIN base_application_codeset art_time      ON art_time.code = pe.art_start_time
    LEFT JOIN base_application_codeset tb            ON tb.code = pe.tb_status
    LEFT JOIN base_application_codeset entry         ON entry.code = pe.entry_point
    WHERE pe.pmtct_enrollment_date BETWEEN '1980-01-01' AND CURRENT_DATE
      -- all facilities: datim_code filter removed
),

pmtctAnc AS (
    SELECT person_uuid, first_anc_date, lmp, gaweeks, gravida, parity,
           hepatitisb, tested_hepatitisb, treated_hepatitisb,
           modality.display anc_setting, test_result_syphilis,
           treated_syphilis,
           ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY first_anc_date DESC) rankk
    FROM pmtct_anc panc
    LEFT JOIN base_application_codeset modality ON modality.code = panc.anc_setting
    WHERE panc.first_anc_date BETWEEN '1980-01-01' AND CURRENT_DATE
),

pmtctMotherVisitation AS (
    SELECT person_uuid, date_of_visit, visitstatus.display visit_status,
           dsd_model, date_of_viral_load, ga_of_viral_load,
           result_of_viral_load,
           FLOOR((CURRENT_DATE - date_of_viral_load) / 7.0) AS weeks_between,
           ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY date_of_visit DESC) rnkk
    FROM pmtct_mother_visitation pm
    LEFT JOIN base_application_codeset visitstatus ON visitstatus.code = visit_status
    WHERE pm.date_of_visit BETWEEN '1980-01-01' AND CURRENT_DATE
),

pmtctDelivery AS (
    SELECT person_uuid, date_of_delivery,
           place.display   place_of_delivery,
           mode.display    mode_of_delivery,
           child.display   child_status,
           feeding.display feeding_decision,
           uuid
    FROM pmtct_delivery pmd
    LEFT JOIN base_application_codeset place   ON place.code   = pmd.place_of_delivery
    LEFT JOIN base_application_codeset mode    ON mode.code    = pmd.mode_of_delivery
    LEFT JOIN base_application_codeset child   ON child.code   = pmd.child_status
    LEFT JOIN base_application_codeset feeding ON feeding.code = pmd.feeding_decision
    WHERE pmd.date_of_delivery BETWEEN '1980-01-01' AND CURRENT_DATE
),

childInformation AS (
    SELECT hospital_number, date_of_delivery, sex.display sex,
           body_weight, mother_person_uuid, uuid
    FROM pmtct_infant_information pii
    LEFT JOIN base_application_codeset sex ON sex.code = pii.sex
    WHERE pii.date_of_delivery BETWEEN '1980-01-01' AND CURRENT_DATE
),

childARV AS (
    SELECT infant_hospital_number, visit_date, infant_arv_time,
           arvtype.display infant_arv_type, ctx.display age_at_ctx,
           ROW_NUMBER() OVER (PARTITION BY infant_hospital_number ORDER BY visit_date DESC) rannk
    FROM pmtct_infant_arv pia
    LEFT JOIN base_application_codeset arvtype ON arvtype.code = pia.infant_arv_type
    LEFT JOIN base_application_codeset ctx     ON ctx.code     = pia.age_at_ctx
    WHERE pia.visit_date BETWEEN '1980-01-01' AND CURRENT_DATE
),

childPCR AS (
    SELECT
        infant_hospital_number,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_1ST_PCR%'
            THEN date_sample_collected END)            AS first_pcr_sample_date,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_1ST_PCR%'
            THEN codeset.display END)                  AS first_pcr_results,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_1ST_PCR%'
            THEN date_result_received_at_facility END) AS first_pcr_result_date,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_2ND_PCR%'
            THEN date_sample_collected END)            AS second_pcr_sample_date,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_2ND_PCR%'
            THEN codeset.display END)                  AS second_pcr_results,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_2ND_PCR%'
            THEN date_result_received_at_facility END) AS second_pcr_result_date,
        MAX(CASE WHEN test_type = 'INFANT_TESTING_PCR_CONFIRMATORY_PCR___IF_PREVIOUS_TEST_POSITIVE'
            THEN date_sample_collected END)            AS confirmatory_pcr_sample_date,
        MAX(CASE WHEN test_type = 'INFANT_TESTING_PCR_CONFIRMATORY_PCR___IF_PREVIOUS_TEST_POSITIVE'
            THEN codeset.display END)                  AS confirmatory_pcr_results,
        MAX(CASE WHEN test_type = 'INFANT_TESTING_PCR_CONFIRMATORY_PCR___IF_PREVIOUS_TEST_POSITIVE'
            THEN date_result_received_at_facility END) AS confirmatory_pcr_result_date,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_4TH_PCR%'
            THEN date_sample_collected END)            AS fourth_pcr_sample_date,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_4TH_PCR%'
            THEN codeset.display END)                  AS fourth_pcr_results,
        MAX(CASE WHEN test_type ILIKE '%INFANT_TESTING_PCR_4TH_PCR%'
            THEN date_result_received_at_facility END) AS fourth_pcr_result_date,
        MAX(CASE WHEN test_type = 'INFANT_TESTING_PCR_CONFIRMATORY_PCR'
            THEN date_sample_collected END)            AS general_confirmatory_pcr_sample_date,
        MAX(CASE WHEN test_type = 'INFANT_TESTING_PCR_CONFIRMATORY_PCR'
            THEN codeset.display END)                  AS general_confirmatory_pcr_results,
        MAX(CASE WHEN test_type = 'INFANT_TESTING_PCR_CONFIRMATORY_PCR'
            THEN date_result_received_at_facility END) AS general_confirmatory_pcr_result_date
    FROM pmtct_infant_pcr pcr
    LEFT JOIN base_application_codeset codeset ON codeset.code = pcr.results
    WHERE pcr.visit_date BETWEEN '1980-01-01' AND CURRENT_DATE
    GROUP BY pcr.infant_hospital_number
),

currentStatus AS (
    SELECT person_uuid AS cuPersonUuid,
        (CASE
            WHEN hiv_status ILIKE '%DEATH%' OR hiv_status ILIKE '%Died%'           THEN 'Died'
            WHEN status_date > maxdate
             AND (hiv_status ILIKE '%stop%' OR hiv_status ILIKE '%out%'
               OR hiv_status ILIKE '%Invalid %' OR hiv_status ILIKE '%ART Transfer In%')
                                                                                    THEN hiv_status
            ELSE status
        END) AS status,
        (CASE
            WHEN hiv_status ILIKE '%DEATH%' OR hiv_status ILIKE '%Died%'           THEN status_date
            WHEN status_date > maxdate
             AND (hiv_status ILIKE '%stop%' OR hiv_status ILIKE '%out%'
               OR hiv_status ILIKE '%Invalid %' OR hiv_status ILIKE '%ART Transfer In%')
                                                                                    THEN status_date
            ELSE visit_date
        END) AS status_date,
        cause_of_death, va_cause_of_death
    FROM (
        SELECT pharmacy.person_uuid, pharmacy.visit_date maxdate,
               stat.hiv_status, stat.cause_of_death, stat.va_cause_of_death, stat.status_date,
               (CASE WHEN pharmacy.visit_date + pharmacy.refill_period + INTERVAL '29 day' <= CURRENT_DATE
                     THEN 'IIT' ELSE 'Active' END) status,
               (CASE WHEN pharmacy.visit_date + pharmacy.refill_period + INTERVAL '29 day' <= CURRENT_DATE
                     THEN pharmacy.visit_date + pharmacy.refill_period + INTERVAL '29 day'
                     ELSE pharmacy.visit_date END) AS visit_date
        FROM (
            SELECT ph.person_uuid, ph.visit_date, ph.refill_period,
                   ROW_NUMBER() OVER (PARTITION BY ph.person_uuid ORDER BY ph.visit_date DESC) AS rn
            FROM hiv_art_pharmacy ph
            INNER JOIN hiv_enrollment h               ON h.person_uuid = ph.person_uuid
            INNER JOIN public.hiv_art_pharmacy_regimens pr ON pr.art_pharmacy_id = ph.id
            INNER JOIN public.hiv_regimen r            ON r.id = pr.regimens_id
            INNER JOIN public.hiv_regimen_type rt      ON rt.id = r.regimen_type_id
            WHERE r.regimen_type_id IN (1,2,3,4,14,16)
              AND ph.visit_date <= CURRENT_DATE
        ) AS pharmacy
        LEFT JOIN (
            SELECT person_id, status_date, hiv_status, cause_of_death, va_cause_of_death
            FROM (
                SELECT hst.person_id, hst.status_date, hst.hiv_status,
                       hst.cause_of_death, hst.va_cause_of_death,
                       ROW_NUMBER() OVER (PARTITION BY hst.person_id ORDER BY hst.status_date DESC) AS rn
                FROM hiv_status_tracker hst
                WHERE hst.status_date <= CURRENT_DATE
            ) AS status
            WHERE status.rn = 1
        ) AS stat ON stat.person_id = pharmacy.person_uuid
        WHERE pharmacy.rn = 1
    ) statuss
),

current_vl_result AS (
    SELECT * FROM (
        SELECT CAST(ls.date_sample_collected AS DATE)     AS dateOfCurrentViralLoadSample,
               sm.patient_uuid                            AS person_uuid130,
               sm.facility_id                             AS vlFacility,
               acode.display                              AS viralLoadIndication,
               sm.result_reported                         AS currentViralLoad,
               CAST(sm.date_result_reported AS DATE)      AS dateOfCurrentViralLoad,
               ROW_NUMBER() OVER (PARTITION BY sm.patient_uuid ORDER BY ls.date_sample_collected DESC) AS rank2
        FROM public.laboratory_result sm
        INNER JOIN public.laboratory_test lt          ON sm.test_id = lt.id
        INNER JOIN public.laboratory_sample ls        ON ls.test_id = lt.id
        INNER JOIN public.base_application_codeset acode ON acode.id = lt.viral_load_indication
        WHERE lt.lab_test_id = 16
          AND CAST(ls.date_sample_collected AS DATE) BETWEEN '1980-01-01' AND CURRENT_DATE
          AND lt.viral_load_indication != 719
          AND sm.date_result_reported IS NOT NULL
          AND CAST(sm.date_result_reported AS DATE) <= CURRENT_DATE
          AND sm.result_reported IS NOT NULL
    ) AS vl_result
    WHERE vl_result.rank2 = 1
      AND vl_result.dateOfCurrentViralLoad <= CURRENT_DATE
)

-- ── Final SELECT — 48 columns as specified ────────────────────────────────────
SELECT
    mc.DatimId                                                                  AS "datimCode",
    mc.person_uuid                                                              AS "Patient ID",
    mc.hospital_number                                                          AS "Mother''s Hospital Number",
    mc.motherAge                                                                AS "Age",
    pa.anc_setting                                                              AS "ANC Setting",
    mc.entryPoint                                                               AS "Point of Entry",
    COALESCE(pa.first_anc_date, mc.pmtct_enrollment_date)                       AS "Date of Initial Visit",
    pa.first_anc_date                                                           AS "Date of Index ANC Registration",
    pa.test_result_syphilis                                                     AS "Syphilis Test Result",
    pa.treated_syphilis                                                         AS "Treated for Syphilis",
    mc.tbStatus                                                                 AS "TB screening Status",
    mc.art_start_date                                                           AS "Mother''s ART Start Date",
    mc.artStartTime                                                             AS "Timing of ART initiation in mother",
    COALESCE(pmv.ga_of_viral_load, pa.gaweeks, mc.gaweeks)                      AS "GA at last visit (weeks)",
    cstatus.status                                                              AS "Mother''s Current ART Status",
    CAST(mc.lmp + INTERVAL '32 weeks' AS DATE)                                  AS "Due Date for VL Sample collection @ 32 weeks",
    (CASE WHEN pmv.weeks_between BETWEEN 32 AND 36
          THEN pmv.date_of_viral_load ELSE NULL END)                            AS "Date for VL sample collection @ 32 weeks",
    cvr.currentViralLoad                                                        AS "Current Viral load Result",
    cvr.dateOfCurrentViralLoad                                                  AS "Date of Current VL",
    pd.date_of_delivery                                                         AS "Date of Delivery",
    pd.place_of_delivery                                                        AS "Place of Delivery",
    pd.mode_of_delivery                                                         AS "Mode of Delivery",
    pd.child_status                                                             AS "Fetal outcome (Child status)",
    ci.hospital_number                                                          AS "Child''s hospital ID number",
    ci.sex                                                                      AS "Sex - Child",
    ci.body_weight                                                              AS "Birth Weight",
    cv.infant_arv_time                                                          AS "Date of ARV Prophylaxis Commencemment",
    cv.infant_arv_type                                                          AS "Type of Prophylaxis (ePNP or regular)",
    cv.age_at_ctx                                                               AS "Date of CTX (Cotrimoxazole)",
    pd.feeding_decision                                                         AS "Current infant feeding options",
    cpcr.first_pcr_sample_date                                                  AS "Date of First DNA PCR Sample collection",
    cpcr.first_pcr_results                                                      AS "Result of first DNA PCR test",
    cpcr.first_pcr_result_date                                                  AS "Date first DNA PCR result was received",
    cpcr.second_pcr_sample_date                                                 AS "Date of Second DNA PCR test sample collection",
    cpcr.second_pcr_results                                                     AS "Result of second DNA PCR test",
    cpcr.second_pcr_result_date                                                 AS "Date second DNA PCR result was received",
    cpcr.confirmatory_pcr_sample_date                                           AS "Date of third DNA PCR test sample collection",
    cpcr.confirmatory_pcr_results                                               AS "Result of third DNA PCR test",
    cpcr.confirmatory_pcr_result_date                                           AS "Date third DNA PCR result was received",
    cpcr.fourth_pcr_sample_date                                                 AS "Date of fourth DNA PCR test sample collection",
    cpcr.fourth_pcr_results                                                     AS "Result of fourth DNA PCR test",
    cpcr.fourth_pcr_result_date                                                 AS "Date fourth DNA PCR result was received",
    cpcr.general_confirmatory_pcr_sample_date                                   AS "Date of confirmatory DNA PCR test sample collection",
    cpcr.general_confirmatory_pcr_results                                       AS "Result of confirmatory DNA PCR test",
    cpcr.general_confirmatory_pcr_result_date                                   AS "Date confirmatory DNA PCR result was received",
    ''                                                                          AS "Date of Child Final Outcome test",
    ''                                                                          AS "Result (Child Final Outcome)",
    ''                                                                          AS "Child''s ART Start Date"

FROM maternalCohort mc
LEFT JOIN pmtctAnc              pa     ON pa.person_uuid          = mc.person_uuid
LEFT JOIN pmtctMotherVisitation pmv    ON pmv.person_uuid         = mc.person_uuid
                                       AND pmv.rnkk               = 1
LEFT JOIN pmtctDelivery         pd     ON pd.person_uuid          = mc.person_uuid
LEFT JOIN childInformation      ci     ON ci.mother_person_uuid   = mc.person_uuid
LEFT JOIN childARV              cv     ON cv.infant_hospital_number = ci.hospital_number
                                       AND cv.rannk               = 1
LEFT JOIN childPCR              cpcr   ON cpcr.infant_hospital_number = ci.hospital_number
LEFT JOIN current_vl_result     cvr    ON cvr.person_uuid130      = mc.person_uuid
LEFT JOIN currentStatus         cstatus ON cstatus.cupersonUuid   = mc.person_uuid;