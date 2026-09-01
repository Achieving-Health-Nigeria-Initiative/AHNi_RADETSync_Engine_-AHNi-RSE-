/*
 * ─────────────────────────────────────────────────────────────────────────────
 * Index Testing Elicitation Report
 * Source : LAMIS / NDR PostgreSQL Database
 *
 * NOTE: hie.offered_ins and hie.accepted_ins are VARCHAR columns (not boolean).
 *       hie.partner_tested_positive is also VARCHAR — compared as string.
 * ─────────────────────────────────────────────────────────────────────────────
 */

SELECT

    boui.code                                                AS "datimCode",

    hc.client_code                                           AS "Index client code/unique ID",

    CAST(
        EXTRACT(YEAR FROM AGE(NOW(), pp.date_of_birth))
    AS INTEGER)                                              AS "Age",

    INITCAP(COALESCE(pp.sex, hc.extra->>'gender'))           AS "Sex",

    ep_code.display                                          AS "Index client entry point",

    -- Date offered: when the elicitation record was created
    CAST(hie.date_created AS DATE)                           AS "Date offered index testing",

    -- Accepted Index Testing — hie.accepted_ins is VARCHAR ('true','false','Yes','No', or codeset ID)
    CASE
        WHEN LOWER(hie.accepted_ins) IN ('true','yes','1') THEN 'Yes'
        WHEN LOWER(hie.accepted_ins) IN ('false','no','0') THEN 'No'
        WHEN offered_codeset.display IS NOT NULL             THEN offered_codeset.display
        ELSE hie.accepted_ins
    END                                                      AS "Accepted Index Testing",

    CAST(hie.date_created AS DATE)                           AS "Date of Elicitation",

    hie.uuid                                                 AS "Index Contact ID",

    CASE
        WHEN hie.dob IS NOT NULL
        THEN CAST(EXTRACT(YEAR FROM AGE(NOW(), hie.dob)) AS INTEGER)
        ELSE NULL
    END                                                      AS "Index Contact's Age",

    se.display                                               AS "Index Contact's Sex",

    relationship.display                                     AS "Relationship of contact to index",

    -- Type of contact from extra JSON if available
    hie.extra->>'typeOfContact'                              AS "Type of contact",

    noti_method.display                                      AS "Counseling, Referral and Support services",

    -- HIV Test Status — partner_tested_positive is VARCHAR
    CASE
        WHEN LOWER(hie.partner_tested_positive::TEXT) IN ('true','yes','1','positive') THEN 'Positive'
        WHEN LOWER(hie.partner_tested_positive::TEXT) IN ('false','no','0','negative') THEN 'Negative'
        ELSE NULL
    END                                                      AS "HIV Test Status",

    CASE
        WHEN LOWER(hie.partner_tested_positive::TEXT) IN ('true','yes','1','positive') THEN 'Positive'
        WHEN LOWER(hie.partner_tested_positive::TEXT) IN ('false','no','0','negative') THEN 'Negative'
        ELSE NULL
    END                                                      AS "HIV Test Result",

    TO_CHAR(
        hie.date_partner_came_for_testing, 'DD-MM-YYYY'
    )                                                        AS "Date of HTS (dd-mm-yyyy)",

    -- ⚠ No person_uuid in hts_index_elicitation.
    -- Uncomment subquery below and adjust join key if needed:
    -- (SELECT he.date_of_registration
    --  FROM patient_person pp2
    --  INNER JOIN hiv_enrollment he ON he.person_uuid = pp2.uuid AND he.archived = 0
    --  WHERE pp2.date_of_birth = hie.dob
    --    AND pp2.facility_id   = hie.facility_id
    --  ORDER BY he.date_of_registration LIMIT 1)
    NULL::DATE                                               AS "Date linked to Treatment & Care"


FROM hts_client hc

LEFT JOIN patient_person pp
       ON pp.uuid = hc.person_uuid

INNER JOIN hts_index_elicitation hie
        ON hie.hts_client_uuid = hc.uuid
       AND hie.archived = 0

LEFT JOIN base_application_codeset ep_code
       ON ep_code.code = hc.testing_setting

LEFT JOIN base_application_codeset se
       ON se.id = hie.sex

LEFT JOIN base_application_codeset noti_method
       ON noti_method.id = hie.notification_method

LEFT JOIN base_application_codeset relationship
       ON relationship.id = hie.relationship_with_index_client

-- Resolve AcceptedIns codeset ID stored as string in JSON
-- Handles both integer string ('191') and NULL safely
LEFT JOIN base_application_codeset offered_codeset
       ON offered_codeset.id = (
           CASE
               WHEN (hc.index_notification_services_elicitation ->> 'AcceptedIns')
                    ~ '^[0-9]+$'
               THEN (hc.index_notification_services_elicitation ->> 'AcceptedIns')::BIGINT
               ELSE NULL
           END
       )

LEFT JOIN base_organisation_unit facility
       ON facility.id = hc.facility_id

LEFT JOIN base_organisation_unit state
       ON state.id = facility.parent_organisation_unit_id

LEFT JOIN base_organisation_unit lga
       ON lga.id = state.parent_organisation_unit_id

LEFT JOIN base_organisation_unit_identifier boui
       ON boui.organisation_unit_id = hc.facility_id
      AND boui.name = 'DATIM_ID'

WHERE
    hc.archived = 0
    -- all facilities: datim_code filter removed
    AND hc.date_visit BETWEEN CAST('1980-01-01' AS DATE)
                          AND CURRENT_DATE

ORDER BY
    boui.code,
    hc.client_code,
    hie.date_created;
