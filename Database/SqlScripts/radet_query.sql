WITH bio_data AS (
    SELECT DISTINCT (p.uuid) AS personUuid,
        p.hospital_number AS hospitalNumber,
        h.unique_id as uniqueId,
        EXTRACT(YEAR FROM AGE(current_date, date_of_birth)) AS age,
        INITCAP(p.sex) AS gender,
        p.date_of_birth AS dateOfBirth,
        --facility.name AS facilityName,
		CASE facility.name
			WHEN 'Adamawa Hospital Yola' THEN 'Adamawa Hospital'
			WHEN 'Boshong Health Clinic' THEN 'Boshong Clinic and Maternity'
			WHEN 'Cottage Hospital Gulak' THEN 'Gulak Cottage Hospital'
			WHEN 'First Referral Hospital Baissa' THEN 'Baissa First Referral Hospital'
			WHEN 'First Referal Hospital Donga' THEN 'Donga First Referral Hospital'
			WHEN 'First Referral Hospital Lau' THEN 'Lau First Referral Hospital'
			WHEN 'Federal Medical Centre Jalingo' THEN 'Jalingo Federal Medical Centre'
			WHEN 'First Referral Hospital Mutum Biyu' THEN 'Mutum Biyu First Referral Hospital'
			WHEN 'United Methodist Church of Nigeria UMCN Hospital' THEN 'United Methodist Church of Nigeria Rural Health Programme CSO'
			WHEN 'Christian Reformed Church in Nigeria CRCN Hospital Wukari' THEN 'Wukari Christian Reformed Church Of Nigeria Aids Action Committee'
			WHEN 'First Referral Hospital Ibi' THEN 'Ibi First Referral Hospital'
			WHEN 'First Referral Hospital Serti' THEN 'Serti First Referral Hospital'
			WHEN 'First Referral Hospital Sunkani' THEN 'Sunkani First Referral Hospital'
			WHEN 'General Hospital Bali' THEN 'Bali General Hospital'
			WHEN 'General Hospital Bambur' THEN 'Bambur General Hospital'
			WHEN 'General Hospital Gembu' THEN 'Gembu General Hospital'
			WHEN 'General Hospital Warwar' THEN 'Warwar General Hospital'
			WHEN 'General Hospital Wukari' THEN 'Wukari General Hospital'
			WHEN 'General Hospital Zing' THEN 'Zing General Hospital'
			WHEN 'St. Monica’s Health Centre Yakoko' THEN 'Yakoko St. Monica Health Centre'
			WHEN 'Government House Clinic' THEN 'Jalingo Government House Clinic'
			WHEN 'Mambilla Baptist Hospital' THEN 'Mambila Baptist Hospital'
			WHEN 'Rapha Hospital' THEN 'Rapah Hospital'
			WHEN 'Sancta Maria Clinic Bali' THEN 'Santa Maria Clinic'
			WHEN 'Taraba Specialist Hospital Jalingo.' THEN 'State Specialist Hospital'
			ELSE facility.name
    	END AS facilityName,
        facility_lga.name AS lga,
        facility_state.name AS state,
        boui.code AS datimId,
        tgroup.display AS targetGroup,
        eSetting.display AS enrollmentSetting,
        hac.visit_date AS artStartDate,
        hr.description AS regimenAtStart,
        p.date_of_registration as dateOfRegistration,
        h.date_of_registration as dateOfEnrollment,
        h.ovc_number AS ovcUniqueId,
        h.house_hold_number AS householdUniqueNo,
        ecareEntry.display AS careEntry,
        hrt.description AS regimenLineAtStart
    FROM patient_person p
        INNER JOIN base_organisation_unit facility ON facility.id = facility_id
        INNER JOIN base_organisation_unit facility_lga ON facility_lga.id = facility.parent_organisation_unit_id
        INNER JOIN base_organisation_unit facility_state ON facility_state.id = facility_lga.parent_organisation_unit_id
        INNER JOIN base_organisation_unit_identifier boui ON boui.organisation_unit_id = facility_id
        AND boui.name = 'DATIM_ID'
        INNER JOIN hiv_enrollment h ON h.person_uuid = p.uuid
        LEFT JOIN base_application_codeset tgroup ON tgroup.id = h.target_group_id
        LEFT JOIN base_application_codeset eSetting ON eSetting.id = h.enrollment_setting_id
        LEFT JOIN base_application_codeset ecareEntry ON ecareEntry.id = h.entry_point_id
        INNER JOIN hiv_art_clinical hac ON hac.hiv_enrollment_uuid = h.uuid
        AND hac.archived = 0
        INNER JOIN hiv_regimen hr ON hr.id = hac.regimen_id
        INNER JOIN hiv_regimen_type hrt ON hrt.id = hac.regimen_type_id
        AND hac.regimen_type_id IN (1, 2, 3, 4, 14, 16)
    WHERE h.archived = 0
        AND p.archived = 0
        AND hac.is_commencement = TRUE
        AND hac.visit_date >= '1980-01-01'
        AND hac.visit_date <= current_date
),
patient_lga as (
    select DISTINCT ON (personUuid) personUuid as personUuid11,
        case
            when (addr ~ '^[0-9\\\\.]+$') = TRUE then (
                select name
                from base_organisation_unit
                where id = cast(addr as int)
            )
            else (
                select name
                from base_organisation_unit
                where id = cast(facilityLga as int)
            )
        end as lgaOfResidence
    from (
            select pp.uuid AS personUuid,
                facility_lga.parent_organisation_unit_id AS facilityLga,
                (
                    jsonb_array_elements(pp.address -> 'address') ->> 'district'
                ) as addr
            from patient_person pp
                LEFT JOIN base_organisation_unit facility_lga ON facility_lga.id = CAST (
                    pp.organization -> 'id' AS INTEGER
                )
            WHERE pp.archived = 0
        ) dt
),
current_clinical AS (
    SELECT *
    FROM (
            SELECT tvs.person_uuid AS person_uuid10,
                hac.visit_date,
                CAST(tvs.capture_date AS DATE),
                CASE
                    WHEN hac.tb_screen IS NOT NULL THEN hac.visit_date
                    ELSE NULL
                END AS dateOfTbScreened1,
                (
                    CASE
                        WHEN INITCAP(pp.sex) = 'Male' THEN NULL
                        WHEN hac.pregnancy_status IS NOT NULL THEN preg.display
                    END
                ) AS pregnancyStatus,
                bac.display AS currentClinicalStage,
                body_weight AS currentWeight,
                tbs.display AS tbStatus1,
                ROW_NUMBER() OVER (
                    PARTITION BY hac.person_uuid
                    ORDER BY hac.visit_date DESC
                ) AS rnkkkk
            FROM hiv_art_clinical hac
                INNER JOIN triage_vital_sign tvs ON tvs.uuid = hac.vital_sign_uuid
                LEFT JOIN patient_person pp ON hac.person_uuid = pp.uuid
                INNER JOIN hiv_enrollment he ON he.person_uuid = hac.person_uuid
                LEFT JOIN base_application_codeset bac ON bac.id = hac.clinical_stage_id
                LEFT JOIN base_application_codeset preg ON preg.code = hac.pregnancy_status
                LEFT JOIN base_application_codeset tbs ON tbs.id = CASE
                    WHEN hac.tb_status ~ '^[0-9]+$' THEN CAST(hac.tb_status AS INTEGER)
                    ELSE 0
                END
            WHERE hac.archived = 0
                AND he.archived = 0
                AND tvs.archived = 0
                AND hac.visit_date BETWEEN '1980-01-01' AND current_date
        ) subQ
    where rnkkkk = 1
),
sample_collection_date AS (
    SELECT sample.date_sample_collected as dateOfViralLoadSampleCollection,
        patient_uuid as person_uuid120
    FROM (
            SELECT lt.viral_load_indication,
                sm.facility_id,
                CAST(sm.date_sample_collected AS DATE),
                sm.patient_uuid,
                sm.archived,
                ROW_NUMBER () OVER (
                    PARTITION BY sm.patient_uuid
                    ORDER BY date_sample_collected DESC
                ) as rnkk
            FROM public.laboratory_sample sm
                INNER JOIN public.laboratory_test lt ON lt.id = sm.test_id
            WHERE lt.lab_test_id = 16
                AND sm.archived = 0
                AND lt.viral_load_indication != 719
                AND sm.date_sample_collected IS NOT null
        ) as sample
    WHERE sample.rnkk = 1
        AND sample.date_sample_collected <= current_date
        AND (
            sample.archived is null
            OR sample.archived = 0
        )
),
tbstatus as (
    WITH cs AS (
        WITH FilteredObservations AS (
            SELECT id,
                person_uuid,
                date_of_observation AS dateOfTbScreened,
                (
                    CASE
                        WHEN data -> 'tbIptScreening' ->> 'status' = 'Presumptive TB and referred for evaluation' THEN 'Presumptive TB'
                        ELSE data -> 'tbIptScreening' ->> 'status'
                    END
                ) AS tbStatus,
                CASE
                    WHEN EXTRACT(
                        MONTH
                        FROM CAST (date_of_observation AS DATE)
                    ) BETWEEN 10 AND 12
                    OR EXTRACT(
                        MONTH
                        FROM CAST (date_of_observation AS DATE)
                    ) BETWEEN 1 AND 3 THEN 'October - March'
                    WHEN EXTRACT(
                        MONTH
                        FROM CAST (date_of_observation AS DATE)
                    ) BETWEEN 4 AND 9 THEN 'April - September'
                END AS reportingPeriod,
                EXTRACT(
                    YEAR
                    FROM date_of_observation
                ) AS yearOfReporting,
                data -> 'tbIptScreening' ->> 'tbScreeningType' AS tbScreeningType,
                ROW_NUMBER() OVER (
                    PARTITION BY person_uuid
                    ORDER BY date_of_observation DESC
                ) AS rowNums
            FROM hiv_observation
            WHERE type = 'Chronic Care'
                AND data IS NOT NULL
                AND archived = 0
                AND date_of_observation BETWEEN (CAST (current_date AS DATE) - INTERVAL '6 MONTHS') AND CAST(current_date AS DATE)
        ),
        FilteredLatestObservations AS (
            SELECT id,
                person_uuid,
                dateOfTbScreened,
                tbStatus,
                tbScreeningType,
                reportingPeriod,
                yearOfReporting
            FROM FilteredObservations
            WHERE rowNums = 1
        ),
        ReportingPeriod AS (
            SELECT CASE
                    WHEN EXTRACT(
                        MONTH
                        FROM CAST (current_date AS DATE)
                    ) BETWEEN 10 AND 12
                    OR EXTRACT(
                        MONTH
                        FROM CAST (current_date AS DATE)
                    ) BETWEEN 1 AND 3 THEN 'October - March'
                    WHEN EXTRACT(
                        MONTH
                        FROM CAST (current_date AS DATE)
                    ) BETWEEN 4 AND 9 THEN 'April - September'
                END AS currentReportingPeriod
        ),
        PresumptiveCheck AS (
            SELECT DISTINCT person_uuid,
                ho.date_of_observation dateScreened,
                ho.data -> 'tbIptScreening' ->> 'status' AS tbStatus,
                ho.data -> 'tbIptScreening' ->> 'tbScreeningType' tbScreeningType,
                CASE
                    WHEN EXTRACT(
                        MONTH
                        FROM CAST (ho.date_of_observation AS DATE)
                    ) BETWEEN 10 AND 12
                    OR EXTRACT(
                        MONTH
                        FROM CAST (ho.date_of_observation AS DATE)
                    ) BETWEEN 1 AND 3 THEN 'October - March'
                    WHEN EXTRACT(
                        MONTH
                        FROM CAST (ho.date_of_observation AS DATE)
                    ) BETWEEN 4 AND 9 THEN 'April - September'
                END AS preSumpReportingPeriod
            FROM hiv_observation ho
            WHERE ho.type = 'Chronic Care'
                AND ho.data IS NOT NULL
                AND ho.archived = 0
                AND ho.date_of_observation BETWEEN (CAST (current_date AS DATE) - INTERVAL '6 MONTHS') AND CAST(current_date AS DATE)
                AND ho.data -> 'tbIptScreening' ->> 'status' ILIKE 'Presumptive TB%'
        )
        SELECT lo.id,
            lo.person_uuid,
            CASE
                WHEN rp.currentReportingPeriod = pc.preSumpReportingPeriod THEN pc.tbStatus
                WHEN lo.reportingPeriod = rp.currentReportingPeriod THEN lo.tbStatus
                ELSE NULL
            END AS tbStatus,
            CASE
                WHEN rp.currentReportingPeriod = pc.preSumpReportingPeriod THEN pc.dateScreened
                WHEN lo.reportingPeriod = rp.currentReportingPeriod THEN lo.dateOfTbScreened
                ELSE NULL
            END AS dateOfTbScreened,
            CASE
                WHEN rp.currentReportingPeriod = pc.preSumpReportingPeriod THEN pc.tbScreeningType
                WHEN lo.reportingPeriod = rp.currentReportingPeriod THEN lo.tbScreeningType
                ELSE NULL
            END AS tbScreeningType
        FROM FilteredLatestObservations lo
            LEFT JOIN PresumptiveCheck pc ON pc.person_uuid = lo.person_uuid
            CROSS JOIN ReportingPeriod rp
    )
    SELECT *
    FROM cs
),
tblam AS (
    SELECT *
    FROM (
            SELECT CAST(lr.date_result_reported AS DATE) AS dateOfLastTbLam,
                lr.patient_uuid as personuuidtblam,
                lr.result_reported as tbLamResult,
                ROW_NUMBER () OVER (
                    PARTITION BY lr.patient_uuid
                    ORDER BY lr.date_result_reported DESC
                ) as rank2333
            FROM laboratory_result lr
                INNER JOIN public.laboratory_test lt on lr.test_id = lt.id
            WHERE lt.lab_test_id = 51
                AND lr.date_result_reported IS NOT NULL
                AND lr.date_result_reported <= current_date
                AND lr.date_result_reported >= '1980-01-01'
                AND lr.result_reported is NOT NULL
                AND lr.archived = 0
        ) as tblam
    WHERE tblam.rank2333 = 1
),
current_vl_result AS (
    SELECT *
    FROM (
            SELECT CAST(ls.date_sample_collected AS DATE) AS dateOfCurrentViralLoadSample,
                sm.patient_uuid as person_uuid130,
                sm.facility_id as vlFacility,
                sm.archived as vlArchived,
                acode.display as viralLoadIndication,
                sm.result_reported as currentViralLoad,
                CAST(sm.date_result_reported AS DATE) as dateOfCurrentViralLoad,
                ROW_NUMBER () OVER (
                    PARTITION BY sm.patient_uuid
                    ORDER BY date_result_reported DESC
                ) as rank2
            FROM public.laboratory_result sm
                INNER JOIN public.laboratory_test lt on sm.test_id = lt.id
                INNER JOIN public.laboratory_sample ls on ls.test_id = lt.id
                INNER JOIN public.base_application_codeset acode on acode.id = lt.viral_load_indication
            WHERE lt.lab_test_id = 16
                AND CAST(date_result_reported AS DATE) BETWEEN '1980-01-01' AND current_date
                AND lt.viral_load_indication != 719
                AND sm.date_result_reported IS NOT NULL
                AND sm.result_reported is NOT NULL
        ) as vl_result
    WHERE vl_result.rank2 = 1
        AND vl_result.dateOfCurrentViralLoad <= current_date
        AND (
            vl_result.vlArchived = 0
            OR vl_result.vlArchived is null
        )
),
careCardCD4 AS (
    SELECT visit_date,
        coalesce(cast(cd_4 as varchar), cd4_semi_quantitative) as cd_4,
        person_uuid AS cccd4_person_uuid
    FROM public.hiv_art_clinical
    WHERE is_commencement is true
        AND archived = 0
        AND cd_4 != 0
        AND visit_date <= current_date
),
labCD4 AS (
    SELECT *
    FROM (
            SELECT sm.patient_uuid AS cd4_person_uuid,
                sm.result_reported as cd4Lb,
                sm.date_result_reported as dateOfCD4Lb,
                ROW_NUMBER () OVER (
                    PARTITION BY sm.patient_uuid
                    ORDER BY date_result_reported DESC
                ) as rnk
            FROM public.laboratory_result sm
                INNER JOIN public.laboratory_test lt on sm.test_id = lt.id
            WHERE lt.lab_test_id IN (1, 50)
                AND sm.date_result_reported IS NOT NULL
                AND sm.archived = 0
                AND CAST(sm.date_result_reported AS DATE) <= current_date
        ) as cd4_result
    WHERE cd4_result.rnk = 1
),
tb_sample_collection AS (
    SELECT sample.created_by,
        CAST(sample.date_sample_collected AS DATE) as dateOfTbSampleCollection,
        patient_uuid as personTbSample
    FROM (
            SELECT llt.lab_test_name,
                sm.created_by,
                lt.viral_load_indication,
                sm.facility_id,
                sm.date_sample_collected,
                sm.patient_uuid,
                sm.archived,
                ROW_NUMBER () OVER (
                    PARTITION BY sm.patient_uuid
                    ORDER BY date_sample_collected DESC
                ) as rnkk
            FROM public.laboratory_sample sm
                INNER JOIN public.laboratory_test lt ON lt.id = sm.test_id
                INNER JOIN laboratory_labtest llt on llt.id = lt.lab_test_id
            WHERE lt.lab_test_id IN (65, 66, 51, 64, 67, 72, 71, 86, 58, 73)
                AND sm.archived = 0
                AND date_sample_collected IS NOT null
                AND sm.date_sample_collected <= current_date
        ) as sample
    WHERE sample.rnkk = 1
),
current_tb_result AS (
    WITH tb_test as (
        SELECT personTbResult,
            dateofTbDiagnosticResultReceived,
            dateOfTbSampleCollected,
            tbDiagnosticResult,
            coalesce(
                MAX(
                    CASE
                        WHEN lab_test_id = 66 THEN 'Chest X-ray'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 65 THEN 'Gene Xpert'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 51 THEN 'TB-LAM'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 64 THEN 'AFB Smear Microscopy'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 67 THEN 'Gene Xpert'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 72 THEN 'TrueNAT'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 71 THEN 'TB-LAM'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 86 THEN 'Cobas'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 73 THEN 'TB-LAM'
                    END
                ),
                MAX(
                    CASE
                        WHEN lab_test_id = 58 THEN 'TB-LAM'
                    END
                )
            ) as tbDiagnosticTestType
        FROM (
                SELECT sm.patient_uuid as personTbResult,
                    CASE
                        WHEN (
                            CAST(lr.date_result_reported AS DATE) > current_date
                            AND lr.result_reported IS NOT NULL
                        ) THEN NULL
                        ELSE lr.result_reported
                    END as tbDiagnosticResult,
                    CASE
                        WHEN CAST(lr.date_result_reported AS DATE) > current_date THEN NULL
                        ELSE CAST(lr.date_result_reported AS DATE)
                    END as dateofTbDiagnosticResultReceived,
                    cast(sm.date_sample_collected as date) AS dateOfTbSampleCollected,
                    lt.lab_test_id,
                    sm.date_sample_collected,
                    ROW_NUMBER() OVER (
                        PARTITION BY sm.patient_uuid
                        ORDER BY sm.date_sample_collected DESC
                    ) AS rnkkk
                FROM laboratory_sample sm
                    INNER JOIN laboratory_test lt on lt.id = sm.test_id
                    LEFT JOIN laboratory_result lr ON lt.id = lr.test_id
                WHERE lt.lab_test_id IN (65, 51, 64, 67, 72, 71, 86, 58, 73, 66)
                    and sm.archived = 0
                    AND sm.date_sample_collected IS NOT NULL
            ) AS tbSubQ
        where rnkkk = 1
        GROUP BY tbSubQ.personTbResult,
            tbSubQ.dateofTbDiagnosticResultReceived,
            tbSubQ.dateOfTbSampleCollected,
            tbDiagnosticResult
    )
    SELECT *
    FROM tb_test
),
tbTreatment AS (
    SELECT *
    FROM (
            SELECT COALESCE(
                    NULLIF(CAST(data -> 'tbIptScreening' ->> 'treatementType' AS text), ''),
                    ''
                ) as tbTreatementType,
                NULLIF(
                    CAST(
                        NULLIF(data -> 'tbIptScreening' ->> 'tbTreatmentStartDate', '') AS DATE
                    ),
                    NULL
                ) as tbTreatmentStartDate,
                CAST(data -> 'tbIptScreening' ->> 'treatmentOutcome' AS text) as tbTreatmentOutcome,
                NULLIF(
                    CAST(
                        NULLIF(data -> 'tbIptScreening' ->> 'completionDate', '') AS DATE
                    ),
                    NULL
                ) as tbCompletionDate,
                person_uuid as tbTreatmentPersonUuid,
                ROW_NUMBER() OVER (
                    PARTITION BY person_uuid
                    ORDER BY date_of_observation DESC
                )
            FROM public.hiv_observation
            WHERE type = 'Chronic Care'
                and archived = 0
        ) tbTreatment
    WHERE row_number = 1
        AND tbTreatmentStartDate IS NOT NULL
),
tbTreatmentNew AS (
    WITH tb_start AS (
        SELECT *
        FROM (
                SELECT person_uuid AS person_uuid,
                    date_of_observation as screeningDate,
                    NULLIF(
                        CAST(
                            NULLIF(data -> 'tptMonitoring' ->> 'tbTreatmentStartDate', '') AS DATE
                        ),
                        NULL
                    ) AS tbTreatmentStartDate,
                    data -> 'tbIptScreening' ->> 'tbTestResult' AS tbDiagnosticResult,
                    data -> 'tbIptScreening' ->> 'chestXrayResult' as chestXrayResult,
                    data -> 'tbIptScreening' ->> 'diagnosticTestType' AS tbDiagnosticTestType,
                    COALESCE(
                        NULLIF(data -> 'tptMonitoring' ->> 'tbType', ''),
                        NULLIF(data -> 'tbIptScreening' ->> 'tbType', '')
                    ) AS tbTreatmentType,
                    NULLIF(
                        CAST(
                            NULLIF(data -> 'tbIptScreening' ->> 'dateSpecimenSent', '') AS DATE
                        ),
                        NULL
                    ) AS specimenSentDate,
                    data -> 'tbIptScreening' ->> 'status' as screeningStatus,
                    data -> 'tbIptScreening' ->> 'dateOfDiagnosticTest' as dateOfDiagnosticTest,
                    data -> 'tbIptScreening' ->> 'tbScreeningType' AS tbScreeningType,
                    CAST(
                        NULLIF(data -> 'tbIptScreening' ->> 'cadScore', '') AS INTEGER
                    ) AS cadScore,
                    data -> 'tptMonitoring' ->> 'clinicallyEvaulated' AS clinicallyEvaulated,
                    data -> 'tbIptScreening' ->> 'chestXrayDone' AS chestXrayDone,
                    data -> 'tbIptScreening' ->> 'chestXrayResultTest' AS chestXrayResultTest,
                    data -> 'tbIptScreening' ->> 'dateOfChestXrayResultTestDone' AS dateOfChestXrayResultTestDone,
                    ROW_NUMBER() OVER (
                        PARTITION BY person_uuid
                        ORDER BY date_of_observation DESC
                    ) as rnk3
                FROM hiv_observation
                WHERE archived = 0
                    AND date_of_observation BETWEEN '1980-01-01' AND current_date
                    AND (
                        (
                            data -> 'tbIptScreening' ->> 'status' LIKE '%Presumptive TB'
                            or data -> 'tbIptScreening' ->> 'status' = 'No signs or symptoms of TB'
                        )
                    )
            ) subTc
        WHERE rnk3 = 1
    ),
    tb_completion AS (
        SELECT person_uuid AS person_uuid,
            NULLIF(
                CAST(
                    NULLIF(data -> 'tbIptScreening' ->> 'completionDate', '') AS DATE
                ),
                NULL
            ) AS completionDate,
            data -> 'tbIptScreening' ->> 'treatmentOutcome' AS treatmentOutcome
        FROM hiv_observation
        WHERE (
                data -> 'tbIptScreening' ->> 'treatmentOutcome' IS NOT NULL
                AND data -> 'tbIptScreening' ->> 'treatmentOutcome' != ''
            )
            AND archived = 0
    )
    SELECT COALESCE(ts.person_uuid, tc.person_uuid) AS person_uuid_tb,
        ts.tbTreatmentStartDate,
        COALESCE(ts.tbDiagnosticResult, ts.chestXrayResult) as tbDiagnosticResult,
        ts.tbDiagnosticTestType,
        ts.tbScreeningType,
        ts.screeningStatus,
        ts.tbTreatmentType,
        ts.screeningDate,
        ts.specimenSentDate,
        dateOfDiagnosticTest,
        tc.completionDate,
        tc.treatmentOutcome,
        ts.cadScore,
        ts.clinicallyEvaulated,
        ts.chestXrayDone,
        ts.chestXrayResultTest,
        ts.dateOfChestXrayResultTestDone
    FROM tb_start ts
        FULL OUTER JOIN tb_completion tc ON ts.person_uuid = tc.person_uuid
    order by screeningDate desc
),
pharmacy_details_regimen AS (
    select *
    from (
            select *,
                ROW_NUMBER() OVER (
                    PARTITION BY pr1.person_uuid40
                    ORDER BY pr1.lastPickupDate DESC
                ) as rnk3
            from (
                    SELECT p.person_uuid as person_uuid40,
                        COALESCE(ds_model.display, p.dsd_model_type) as dsdModel,
                        p.visit_date as lastPickupDate,
                        r.description as currentARTRegimen,
                        rt.description as currentRegimenLine,
                        p.next_appointment as nextPickupDate,
                        CAST(p.refill_period / 30.0 AS DECIMAL(10, 1)) AS monthsOfARVRefill
                    from public.hiv_art_pharmacy p
                        INNER JOIN public.hiv_art_pharmacy_regimens pr ON pr.art_pharmacy_id = p.id
                        INNER JOIN public.hiv_regimen r on r.id = pr.regimens_id
                        INNER JOIN public.hiv_regimen_type rt on rt.id = r.regimen_type_id
                        left JOIN base_application_codeset ds_model on ds_model.code = p.dsd_model_type
                    WHERE r.regimen_type_id in (1, 2, 3, 4, 14, 16)
                        AND p.archived = 0
                        AND p.visit_date >= '1980-01-01'
                        AND p.visit_date <= current_date
                ) as pr1
        ) as pr2
    where pr2.rnk3 = 1
),
negativeTbDiagnosticResults AS (
    SELECT sm.patient_uuid as personTbResult,
        CASE
            WHEN (
                CAST(lr.date_result_reported AS DATE) > current_date
                AND lr.result_reported IS NOT NULL
            ) THEN NULL
            ELSE lr.result_reported
        END as tbDiagnosticResult,
        CASE
            WHEN CAST(lr.date_result_reported AS DATE) > current_date THEN NULL
            ELSE CAST(lr.date_result_reported AS DATE)
        END as dateofTbDiagnosticResultReceived,
        cast(sm.date_sample_collected as date) AS dateOfTbSampleCollected,
        lt.lab_test_id,
        sm.date_sample_collected,
        ROW_NUMBER() OVER (
            PARTITION BY sm.patient_uuid
            ORDER BY sm.date_sample_collected DESC
        ) AS rnkkk
    FROM laboratory_sample sm
        INNER JOIN laboratory_test lt on lt.id = sm.test_id
        LEFT JOIN laboratory_result lr ON lt.id = lr.test_id
    WHERE lt.lab_test_id IN (86, 65, 67, 64, 58, 51, 73, 72, 71)
        and sm.archived = 0
        AND sm.date_sample_collected IS NOT NULL
        AND (
            lr.result_reported ILIKE '%negative%'
            OR lr.result_reported ILIKE '%MTB not detected%'
        )
),
eac as (
    with first_eac as (
        select *
        from (
                with current_eac as (
                    select id,
                        person_uuid,
                        uuid,
                        status,
                        ROW_NUMBER() OVER (
                            PARTITION BY person_uuid
                            ORDER BY id DESC
                        ) AS row
                    from hiv_eac
                    where archived = 0
                )
                select ce.id,
                    ce.person_uuid,
                    hes.eac_session_date,
                    ROW_NUMBER() OVER (
                        PARTITION BY hes.person_uuid
                        ORDER BY hes.eac_session_date ASC
                    ) AS row
                from hiv_eac_session hes
                    join current_eac ce on ce.uuid = hes.eac_id
                where ce.row = 1
                    and hes.archived = 0
                    and hes.eac_session_date between '1980-01-01' and current_date
                    and hes.status in ('FIRST EAC')
            ) as fes
        where row = 1
    ),
    last_eac as (
        select *
        from (
                with current_eac as (
                    select id,
                        person_uuid,
                        uuid,
                        status,
                        ROW_NUMBER() OVER (
                            PARTITION BY person_uuid
                            ORDER BY id DESC
                        ) AS row
                    from hiv_eac
                    where archived = 0
                )
                select ce.id,
                    ce.person_uuid,
                    hes.eac_session_date,
                    ROW_NUMBER() OVER (
                        PARTITION BY hes.person_uuid
                        ORDER BY hes.eac_session_date DESC
                    ) AS row
                from hiv_eac_session hes
                    join current_eac ce on ce.uuid = hes.eac_id
                where ce.row = 1
                    and hes.archived = 0
                    and hes.eac_session_date between '1980-01-01' and current_date
                    and hes.status in (
                        'FIRST EAC',
                        'SECOND EAC',
                        'THIRD EAC',
                        'FOURTH EAC',
                        'FIFTH EAC',
                        'SIXTH EAC'
                    )
            ) as les
        where row = 1
    ),
    eac_count as (
        SELECT person_uuid,
            no_eac_session
        FROM (
                SELECT person_uuid,
                    eac_id,
                    no_eac_session,
                    eac_session_date,
                    ROW_NUMBER () OVER (
                        PARTITION BY person_uuid
                        ORDER BY eac_session_date DESC
                    ) AS rnkk
                FROM (
                        SELECT person_uuid,
                            visit_id,
                            eac_id,
                            eac_session_date,
                            COUNT(eac_id) OVER (PARTITION BY eac_id) AS no_eac_session
                        FROM hiv_eac_session
                        WHERE archived = 0
                            AND eac_session_date between '1980-01-01' and current_date
                            AND status in (
                                'FIRST EAC',
                                'SECOND EAC',
                                'THIRD EAC',
                                'FOURTH EAC',
                                'FIFTH EAC',
                                'SIXTH EAC'
                            )
                        order by eac_session_date DESC
                    ) subQ
            ) countEac
        WHERE rnkk = 1
    ),
    extended_eac as (
        select *
        from (
                with current_eac as (
                    select id,
                        person_uuid,
                        uuid,
                        status,
                        ROW_NUMBER() OVER (
                            PARTITION BY person_uuid
                            ORDER BY id DESC
                        ) AS row
                    from hiv_eac
                    where archived = 0
                )
                select ce.id,
                    ce.person_uuid,
                    hes.eac_session_date,
                    ROW_NUMBER() OVER (
                        PARTITION BY hes.person_uuid
                        ORDER BY hes.eac_session_date DESC
                    ) AS row
                from hiv_eac_session hes
                    join current_eac ce on ce.uuid = hes.eac_id
                where ce.row = 1
                    and hes.archived = 0
                    and hes.status is not null
                    and hes.eac_session_date between '1980-01-01' and current_date
                    and hes.status not in (
                        'FIRST EAC',
                        'SECOND EAC',
                        'THIRD EAC',
                        'FOURTH EAC',
                        'FIFTH EAC',
                        'SIXTH EAC'
                    )
            ) as exe
        where row = 1
    ),
    post_eac_vl as (
        select *
        from (
                select lt.patient_uuid,
                    cast(ls.date_sample_collected as date),
                    lr.result_reported,
                    cast(lr.date_result_reported as date),
                    ROW_NUMBER() OVER (
                        PARTITION BY lt.patient_uuid
                        ORDER BY ls.date_sample_collected DESC
                    ) AS row
                from laboratory_test lt
                    left join laboratory_sample ls on ls.test_id = lt.id
                    left join laboratory_result lr on lr.test_id = lt.id
                where lt.viral_load_indication = 302
                    and lt.archived = 0
                    and ls.archived = 0
                    and ls.date_sample_collected between '1980-01-01' and current_date
            ) pe
        where row = 1
    )
    select fe.person_uuid as person_uuid50,
        fe.eac_session_date as dateOfCommencementOfEAC,
        le.eac_session_date as dateOfLastEACSessionCompleted,
        ec.no_eac_session as numberOfEACSessionCompleted,
        exe.eac_session_date as dateOfExtendEACCompletion,
        pvl.result_reported as repeatViralLoadResult,
        pvl.date_result_reported as DateOfRepeatViralLoadResult,
        pvl.date_sample_collected as dateOfRepeatViralLoadEACSampleCollection
    from first_eac fe
        left join last_eac le on le.person_uuid = fe.person_uuid
        left join eac_count ec on ec.person_uuid = fe.person_uuid
        left join extended_eac exe on exe.person_uuid = fe.person_uuid
        left join post_eac_vl pvl on pvl.patient_uuid = fe.person_uuid
),
dsd1 as (
    select person_uuid as person_uuid_dsd_1,
        dateOfDevolvement,
        modelDevolvedTo
    from (
            select d.person_uuid,
                d.date_devolved as dateOfDevolvement,
                bmt.display as modelDevolvedTo,
                ROW_NUMBER() OVER (
                    PARTITION BY d.person_uuid
                    ORDER BY d.date_devolved ASC
                ) AS row
            from dsd_devolvement d
                left join base_application_codeset bmt on bmt.code = d.dsd_type
            where d.archived = 0
                and d.date_devolved between '1980-01-01' and current_date
        ) d1
    where row = 1
),
dsd2 as (
    select d2.person_uuid as person_uuid_dsd_2,
        d2.dateOfCurrentDSD,
        d2.currentDSDModel,
        d2.dateReturnToSite,
        bac.display as currentDsdOutlet,
        dsdOutlet
    from (
            select d.person_uuid,
                d.date_devolved as dateOfCurrentDSD,
                bmt.display as currentDSDModel,
                d.date_return_to_site AS dateReturnToSite,
                outlet_name as dsdOutlet,
                ROW_NUMBER() OVER (
                    PARTITION BY d.person_uuid
                    ORDER BY d.date_devolved DESC
                ) AS row
            from dsd_devolvement d
                left join base_application_codeset bmt on bmt.code = d.dsd_type
            where d.archived = 0
                and d.date_devolved between '1980-01-01' and current_date
        ) d2
        left join base_application_codeset bac on bac.code = d2.dsdOutlet
    where d2.row = 1
),
biometric AS (
    SELECT DISTINCT ON (he.person_uuid) he.person_uuid AS person_uuid60,
        biometric_count.enrollment_date AS dateBiometricsEnrolled,
        biometric_count.count AS numberOfFingersCaptured,
        recapture_count.recapture_date AS dateBiometricsRecaptured,
        recapture_count.count AS numberOfFingersRecaptured,
        bst.biometric_status AS biometricStatus,
        bst.status_date
    FROM hiv_enrollment he
        LEFT JOIN (
            SELECT b.person_uuid,
                CASE
                    WHEN COUNT(b.person_uuid) > 10 THEN 10
                    ELSE COUNT(b.person_uuid)
                END,
                MAX(enrollment_date) enrollment_date
            FROM biometric b
            WHERE archived = 0
                AND (
                    recapture = 0
                    or recapture is null
                )
            GROUP BY b.person_uuid
        ) biometric_count ON biometric_count.person_uuid = he.person_uuid
        LEFT JOIN (
            SELECT b.person_uuid,
                max_capture.max_capture_date AS recapture_date,
                b.recapture,
                CASE
                    WHEN COUNT(b.person_uuid) > 10 THEN 10
                    ELSE COUNT(b.person_uuid)
                END
            FROM biometric b
                LEFT JOIN (
                    select person_uuid,
                        max(enrollment_date) max_capture_date
                    from biometric
                    group by person_uuid
                ) max_capture ON b.person_uuid = max_capture.person_uuid
            where b.enrollment_date = max_capture.max_capture_date
                AND b.archived = 0
                AND b.recapture != 0
                and b.recapture is NOT null
            group by 1,
                2,
                3
            order by b.person_uuid
        ) recapture_count ON recapture_count.person_uuid = he.person_uuid
        LEFT JOIN (
            SELECT DISTINCT ON (person_id) person_id,
                biometric_status,
                MAX(status_date) OVER (
                    PARTITION BY person_id
                    ORDER BY status_date DESC
                ) AS status_date
            FROM hiv_status_tracker
            WHERE archived = 0
        ) bst ON bst.person_id = he.person_uuid
    WHERE he.archived = 0
),
current_regimen AS (
    SELECT DISTINCT ON (regiment_table.person_uuid) regiment_table.person_uuid AS person_uuid70,
        start_or_regimen AS dateOfCurrentRegimen,
        regiment_table.max_visit_date,
        regiment_table.regimen
    FROM (
            SELECT MIN(visit_date) start_or_regimen,
                MAX(visit_date) max_visit_date,
                regimen,
                person_uuid
            FROM (
                    SELECT hap.id,
                        hap.person_uuid,
                        hap.visit_date,
                        hivreg.description AS regimen,
                        ROW_NUMBER() OVER (
                            ORDER BY person_uuid,
                                visit_date
                        ) rn1,
                        ROW_NUMBER() OVER (
                            PARTITION BY hivreg.description
                            ORDER BY person_uuid,
                                visit_date
                        ) rn2
                    FROM public.hiv_art_pharmacy AS hap
                        INNER JOIN (
                            SELECT MAX(hapr.id) AS id,
                                art_pharmacy_id,
                                regimens_id,
                                hr.description
                            FROM public.hiv_art_pharmacy_regimens AS hapr
                                INNER JOIN hiv_regimen AS hr ON hapr.regimens_id = hr.id
                            WHERE hr.regimen_type_id IN (1, 2, 3, 4, 14, 16)
                            GROUP BY art_pharmacy_id,
                                regimens_id,
                                hr.description
                        ) AS hapr ON hap.id = hapr.art_pharmacy_id
                        and hap.archived = 0
                        INNER JOIN hiv_regimen AS hivreg ON hapr.regimens_id = hivreg.id
                        INNER JOIN hiv_regimen_type AS hivregtype ON hivreg.regimen_type_id = hivregtype.id
                        AND hivreg.regimen_type_id IN (1, 2, 3, 4, 14, 16)
                        AND hap.visit_date BETWEEN '1980-01-01' AND current_date
                    ORDER BY person_uuid,
                        visit_date
                ) t
            GROUP BY person_uuid,
                regimen,
                rn1 - rn2
            ORDER BY MIN(visit_date)
        ) AS regiment_table
        INNER JOIN (
            SELECT DISTINCT MAX(visit_date) AS max_visit_date,
                person_uuid
            FROM hiv_art_pharmacy hap
                INNER JOIN hiv_art_pharmacy_regimens hapr ON hapr.art_pharmacy_id = hap.id
                INNER JOIN hiv_regimen AS hr ON hapr.regimens_id = hr.id
            WHERE hr.regimen_type_id IN (1, 2, 3, 4, 14, 16)
                AND hap.archived = 0
                AND hap.visit_date BETWEEN '1980-01-01' AND current_date
            GROUP BY person_uuid
        ) AS hap ON regiment_table.person_uuid = hap.person_uuid
    WHERE regiment_table.max_visit_date = hap.max_visit_date
    GROUP BY regiment_table.person_uuid,
        regiment_table.regimen,
        regiment_table.max_visit_date,
        start_or_regimen
),
iptNew AS (
    WITH tpt_completed AS (
        SELECT *
        FROM (
                SELECT person_uuid AS person_uuid,
                    data -> 'tptMonitoring' ->> 'endedTpt' AS endedTpt,
                    NULLIF(
                        CAST(
                            NULLIF(data -> 'tptMonitoring' ->> 'dateTptEnded', '') AS DATE
                        ),
                        NULL
                    ) AS tptCompletionDate,
                    data -> 'tptMonitoring' ->> 'outComeOfIpt' AS tptCompletionStatus,
                    data -> 'tbIptScreening' ->> 'outcome' AS completion_tptPreventionOutcome,
                    ROW_NUMBER () OVER (
                        PARTITION BY person_uuid
                        ORDER BY date_of_observation DESC
                    ) rowNum
                FROM hiv_observation
                WHERE data -> 'tptMonitoring' ->> 'endedTpt' = 'Yes'
                    AND data -> 'tbIptScreening' ->> 'outcome' IS NOT NULL
                    AND data -> 'tbIptScreening' ->> 'outcome' != ''
                    AND data -> 'tptMonitoring' ->> 'outComeOfIpt' IS NOT NULL
                    AND data -> 'tptMonitoring' ->> 'outComeOfIpt' != ''
                    AND archived = 0
            ) subTc
        WHERE rowNum = 1
    ),
    pt_screened AS (
        SELECT person_uuid AS person_uuid,
            data -> 'tptMonitoring' ->> 'tptRegimen' AS tptType,
            NULLIF(
                CAST(
                    NULLIF(data -> 'tptMonitoring' ->> 'dateTptStarted', '') AS DATE
                ),
                NULL
            ) AS tptStartDate,
            data -> 'tptMonitoring' ->> 'eligibilityTpt' AS eligibilityTpt
        FROM hiv_observation
        WHERE (
                data -> 'tptMonitoring' ->> 'eligibilityTpt' IS NOT NULL
                AND data -> 'tptMonitoring' ->> 'eligibilityTpt' != ''
            )
            AND (
                data -> 'tbIptScreening' ->> 'outcome' IS NOT NULL
                AND data -> 'tbIptScreening' ->> 'outcome' != ''
                AND data -> 'tbIptScreening' ->> 'outcome' != 'Currently on TPT'
            )
    )
    SELECT COALESCE(tc.person_uuid, ts.person_uuid) AS person_uuid,
        ts.tptType,
        ts.tptStartDate,
        ts.eligibilityTpt,
        tc.endedTpt,
        tc.tptCompletionDate,
        tc.tptCompletionStatus
    FROM pt_screened ts
        FULL OUTER JOIN tpt_completed tc ON ts.person_uuid = tc.person_uuid
),

ipt as (
    with ipt_c as (
        select person_uuid,
            date_completed as iptCompletionDate,
            iptCompletionStatus
        from (
                select person_uuid,
                    cast(ipt ->> 'dateCompleted' as date) as date_completed,
                    COALESCE(
                        NULLIF(CAST(ipt ->> 'completionStatus' AS text), ''),
                        ''
                    ) AS iptCompletionStatus,
                    row_number () over (
                        partition by person_uuid
                        order by cast(ipt ->> 'dateCompleted' as date) desc
                    ) as rnk
                from hiv_art_pharmacy
                where (
                        ipt ->> 'dateCompleted' is not null
                        and ipt ->> 'dateCompleted' != 'null'
                        and ipt ->> 'dateCompleted' != ''
                        AND TRIM(ipt ->> 'dateCompleted') <> ''
                    )
                    and archived = 0
                    AND ipt ->> 'completionStatus' != ''
                    AND ipt ->> 'completionStatus' != 'null'
                    AND ipt ->> 'completionStatus' <> ''
            ) ic
        where ic.rnk = 1
    ),
    ipt_s as (
        SELECT person_uuid,
            visit_date as dateOfIptStart,
            regimen_name as iptType
        FROM (
                SELECT h.person_uuid,
                    h.visit_date,
                    CAST(pharmacy_object ->> 'regimenName' AS VARCHAR) AS regimen_name,
                    ROW_NUMBER() OVER (
                        PARTITION BY h.person_uuid
                        ORDER BY h.visit_date ASC
                    ) AS rnk
                FROM hiv_art_pharmacy h
                    INNER JOIN jsonb_array_elements(h.extra -> 'regimens') WITH ORDINALITY p(pharmacy_object) ON TRUE
                    INNER JOIN hiv_regimen hr ON hr.description = CAST(p.pharmacy_object ->> 'regimenName' AS VARCHAR)
                    INNER JOIN hiv_regimen_type hrt ON hrt.id = hr.regimen_type_id
                    AND hrt.id = 15
                    AND hrt.id NOT IN (1, 2, 3, 4, 14, 16)
                WHERE hrt.id = 15
                    AND h.archived = 0
            ) AS ic
        WHERE ic.rnk = 1
    ),
    ipt_c_cs as (
        SELECT person_uuid,
            iptStartDate,
            iptCompletionSCS,
            iptCompletionDSC
        FROM (
                SELECT person_uuid,
                    CASE
                        WHEN (
                            data -> 'tbIptScreening' ->> 'dateTPTStart'
                        ) IS NULL
                        OR (
                            data -> 'tbIptScreening' ->> 'dateTPTStart'
                        ) = ''
                        OR (
                            data -> 'tbIptScreening' ->> 'dateTPTStart'
                        ) = ' ' THEN NULL
                        ELSE CAST(
                            (
                                data -> 'tbIptScreening' ->> 'dateTPTStart'
                            ) AS DATE
                        )
                    END as iptStartDate,
                    data -> 'tptMonitoring' ->> 'outComeOfIpt' as iptCompletionSCS,
                    CASE
                        WHEN (data -> 'tptMonitoring' ->> 'date') = 'null'
                        OR (data -> 'tptMonitoring' ->> 'date') = ''
                        OR (data -> 'tptMonitoring' ->> 'date') = ' ' THEN NULL
                        else cast(data -> 'tptMonitoring' ->> 'date' as date)
                    END as iptCompletionDSC,
                    ROW_NUMBER() OVER (
                        PARTITION BY person_uuid
                        ORDER BY CASE
                                WHEN (data -> 'tptMonitoring' ->> 'date') = 'null'
                                OR (data -> 'tptMonitoring' ->> 'date') = ''
                                OR (data -> 'tptMonitoring' ->> 'date') = ' ' THEN NULL
                                else cast(data -> 'tptMonitoring' ->> 'date' as date)
                            END DESC
                    ) AS ipt_c_sc_rnk
                FROM hiv_observation
                WHERE type = 'Chronic Care'
                    AND archived = 0
                    AND (data -> 'tptMonitoring' ->> 'date') IS NOT NULL
                    AND (data -> 'tptMonitoring' ->> 'date') != 'null'
            ) AS ipt_ccs
        WHERE ipt_c_sc_rnk = 1
    )
    select ipt_s.person_uuid as personuuid80,
        (
            CASE
                WHEN coalesce(ipt_c_cs.iptCompletionDSC, ipt_c.iptCompletionDate) > current_date THEN NULL
                ELSE coalesce(ipt_c_cs.iptCompletionDSC, ipt_c.iptCompletionDate)
            END
        ) as iptCompletionDate,
        (
            CASE
                WHEN coalesce(ipt_c_cs.iptCompletionDSC, ipt_c.iptCompletionDate) > current_date THEN NULL
                ELSE coalesce(ipt_c_cs.iptCompletionSCS, ipt_c.iptCompletionStatus)
            END
        ) as iptCompletionStatus,
        COALESCE(ipt_s.dateOfIptStart, ipt_c_cs.iptStartDate) AS dateOfIptStart,
        ipt_s.iptType
    from ipt_s
        left join ipt_c on ipt_s.person_uuid = ipt_c.person_uuid
        left join ipt_c_cs on ipt_s.person_uuid = ipt_c_cs.person_uuid
),
ipt_s as (
    SELECT person_uuid,
        visit_date as dateOfIptStart,
        regimen_name as iptType
    FROM (
            SELECT h.person_uuid,
                h.visit_date,
                CAST(pharmacy_object ->> 'regimenName' AS VARCHAR) AS regimen_name,
                ROW_NUMBER() OVER (
                    PARTITION BY h.person_uuid
                    ORDER BY h.visit_date ASC
                ) AS rnk
            FROM hiv_art_pharmacy h
                INNER JOIN jsonb_array_elements(h.extra -> 'regimens') WITH ORDINALITY p(pharmacy_object) ON TRUE
                INNER JOIN hiv_regimen hr ON hr.description = CAST(p.pharmacy_object ->> 'regimenName' AS VARCHAR)
                INNER JOIN hiv_regimen_type hrt ON hrt.id = hr.regimen_type_id
                AND hrt.id = 15
                AND hrt.id NOT IN (1, 2, 3, 4, 14, 16)
            WHERE hrt.id = 15
                AND h.archived = 0
        ) AS ic
    WHERE ic.rnk = 1
),
cervical_cancer AS (
    select *
    from (
            select ho.person_uuid AS person_uuid90,
                ho.date_of_observation AS dateOfCervicalCancerScreening,
                ho.data ->> 'screenTreatmentMethodDate' AS treatmentMethodDate,
                cc_type.display AS cervicalCancerScreeningType,
                cc_method.display AS cervicalCancerScreeningMethod,
                cc_trtm.display AS cervicalCancerTreatmentScreened,
                cc_result.display AS resultOfCervicalCancerScreening,
                ROW_NUMBER() OVER (
                    PARTITION BY ho.person_uuid
                    ORDER BY ho.date_of_observation DESC
                ) AS row
            from hiv_observation ho
                LEFT JOIN base_application_codeset cc_type ON cc_type.code = CAST(ho.data ->> 'screenType' AS VARCHAR)
                LEFT JOIN base_application_codeset cc_method ON cc_method.code = CAST(ho.data ->> 'screenMethod' AS VARCHAR)
                LEFT JOIN base_application_codeset cc_result ON cc_result.code = CAST(ho.data ->> 'screeningResult' AS VARCHAR)
                LEFT JOIN base_application_codeset cc_trtm ON cc_trtm.code = CAST(ho.data ->> 'screenTreatment' AS VARCHAR)
            where ho.archived = 0
                and type = 'Cervical cancer'
        ) as cc
    where row = 1
),
ovc AS (
    SELECT DISTINCT ON (person_uuid) person_uuid AS personUuid100,
        ovc_number AS ovcNumber,
        house_hold_number AS householdNumber
    FROM hiv_enrollment
    WHERE archived = 0
),
-- ── Shared base scans: hiv_art_pharmacy and hiv_status_tracker each read
-- ── exactly ONCE and materialized; previous_previous / previous / current_status
-- ── all derive from these caches instead of repeating the full table scans.
pharmacy_base AS MATERIALIZED (
    SELECT
        hp.person_uuid,
        hp.visit_date,
        hp.visit_date + hp.refill_period + INTERVAL '29 day' AS due_date
    FROM hiv_art_pharmacy hp
    JOIN hiv_art_pharmacy_regimens pr ON pr.art_pharmacy_id = hp.id
    JOIN hiv_regimen r               ON r.id = pr.regimens_id
    JOIN hiv_enrollment h            ON h.person_uuid = hp.person_uuid AND h.archived = 0
    WHERE hp.archived = 0
      AND hp.visit_date <= current_date
      AND r.regimen_type_id IN (1, 2, 3, 4, 14, 16)
),
status_base AS MATERIALIZED (
    SELECT ls.person_id,
           ls.hiv_status,
           ls.cause_of_death,
           ls.va_cause_of_death,
           ls.status_date
    FROM hiv_status_tracker ls
    JOIN hiv_enrollment he ON he.person_uuid = ls.person_id
    WHERE ls.archived = 0
      AND ls.status_date <= current_date
),
previous_previous AS (
    WITH pp_pharm AS (
        SELECT person_uuid, visit_date, due_date,
               ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY visit_date DESC) AS rn
        FROM pharmacy_base
        WHERE visit_date <= get_pre_previous_quarter_end()
    ),
    pp_pharm1 AS (
        SELECT person_uuid,
            CASE WHEN due_date <= get_pre_previous_quarter_end() THEN 'IIT' ELSE 'Active' END AS status,
            CASE WHEN due_date <= get_pre_previous_quarter_end() THEN due_date ELSE visit_date END AS visit_date,
            visit_date AS maxdate
        FROM pp_pharm WHERE rn = 1
    ),
    pp_stat AS (
        SELECT person_id, hiv_status, cause_of_death, va_cause_of_death, status_date,
               ROW_NUMBER() OVER (PARTITION BY person_id ORDER BY status_date DESC) AS rn
        FROM status_base
        WHERE status_date <= get_pre_previous_quarter_end()
    ),
    pp_stat1 AS (SELECT * FROM pp_stat WHERE rn = 1)
    SELECT p.person_uuid AS prePrePersonUuid,
        CASE
            WHEN s.hiv_status ILIKE '%death%' OR s.hiv_status ILIKE '%died%'              THEN 'Died'
            WHEN s.status_date > p.maxdate AND (
                 s.hiv_status ILIKE '%stop%' OR s.hiv_status ILIKE '%out%'
                 OR s.hiv_status ILIKE '%invalid%')                                        THEN s.hiv_status
            ELSE p.status
        END AS status,
        CASE
            WHEN s.hiv_status ILIKE '%death%' OR s.hiv_status ILIKE '%died%'              THEN s.status_date
            WHEN s.status_date > p.maxdate AND (
                 s.hiv_status ILIKE '%stop%' OR s.hiv_status ILIKE '%out%'
                 OR s.hiv_status ILIKE '%invalid%')                                        THEN s.status_date
            ELSE p.visit_date
        END AS status_date,
        s.cause_of_death,
        s.va_cause_of_death
    FROM pp_pharm1 p
    LEFT JOIN pp_stat1 s ON s.person_id = p.person_uuid
),
previous AS (
    WITH pre_pharm AS (
        SELECT person_uuid, visit_date, due_date,
               ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY visit_date DESC) AS rn
        FROM pharmacy_base
        WHERE visit_date <= get_previous_quarter_end()
    ),
    pre_pharm1 AS (
        SELECT person_uuid,
            CASE WHEN due_date <= get_previous_quarter_end() THEN 'IIT' ELSE 'Active' END AS status,
            CASE WHEN due_date <= get_previous_quarter_end() THEN due_date ELSE visit_date END AS visit_date,
            visit_date AS maxdate
        FROM pre_pharm WHERE rn = 1
    ),
    pre_stat AS (
        SELECT person_id, hiv_status, cause_of_death, va_cause_of_death, status_date,
               ROW_NUMBER() OVER (PARTITION BY person_id ORDER BY status_date DESC) AS rn
        FROM status_base
        WHERE status_date <= get_previous_quarter_end()
    ),
    pre_stat1 AS (SELECT * FROM pre_stat WHERE rn = 1)
    SELECT p.person_uuid AS prePersonUuid,
        CASE
            WHEN s.hiv_status ILIKE '%death%' OR s.hiv_status ILIKE '%died%'              THEN 'Died'
            WHEN s.status_date > p.maxdate AND (
                 s.hiv_status ILIKE '%stop%' OR s.hiv_status ILIKE '%out%'
                 OR s.hiv_status ILIKE '%invalid%')                                        THEN s.hiv_status
            ELSE p.status
        END AS status,
        CASE
            WHEN s.hiv_status ILIKE '%death%' OR s.hiv_status ILIKE '%died%'              THEN s.status_date
            WHEN s.status_date > p.maxdate AND (
                 s.hiv_status ILIKE '%stop%' OR s.hiv_status ILIKE '%out%'
                 OR s.hiv_status ILIKE '%invalid%')                                        THEN s.status_date
            ELSE p.visit_date
        END AS status_date,
        s.cause_of_death,
        s.va_cause_of_death
    FROM pre_pharm1 p
    LEFT JOIN pre_stat1 s ON s.person_id = p.person_uuid
),
current_status AS (
    WITH cur_pharm AS (
        SELECT person_uuid, visit_date, due_date,
               ROW_NUMBER() OVER (PARTITION BY person_uuid ORDER BY visit_date DESC) AS rn
        FROM pharmacy_base
        -- visit_date <= current_date already guaranteed by pharmacy_base
    ),
    cur_pharm1 AS (
        SELECT person_uuid,
            CASE WHEN due_date <= current_date THEN 'IIT' ELSE 'Active' END AS status,
            CASE WHEN due_date <= current_date THEN due_date ELSE visit_date END AS visit_date,
            visit_date AS maxdate
        FROM cur_pharm WHERE rn = 1
    ),
    cur_stat AS (
        SELECT person_id, hiv_status, cause_of_death, va_cause_of_death, status_date,
               ROW_NUMBER() OVER (PARTITION BY person_id ORDER BY status_date DESC) AS rn
        FROM status_base
        -- status_date <= current_date already guaranteed by status_base
    ),
    cur_stat1 AS (SELECT * FROM cur_stat WHERE rn = 1)
    SELECT p.person_uuid AS cuPersonUuid,
        CASE
            WHEN s.hiv_status ILIKE '%death%' OR s.hiv_status ILIKE '%died%'              THEN 'Died'
            WHEN s.status_date > p.maxdate AND (
                 s.hiv_status ILIKE '%stop%' OR s.hiv_status ILIKE '%out%'
                 OR s.hiv_status ILIKE '%invalid%'
                 OR s.hiv_status ILIKE '%art transfer in%')                                THEN s.hiv_status
            ELSE p.status
        END AS status,
        CASE
            WHEN s.hiv_status ILIKE '%death%' OR s.hiv_status ILIKE '%died%'              THEN s.status_date
            WHEN s.status_date > p.maxdate AND (
                 s.hiv_status ILIKE '%stop%' OR s.hiv_status ILIKE '%out%'
                 OR s.hiv_status ILIKE '%invalid%'
                 OR s.hiv_status ILIKE '%art transfer in%')                                THEN s.status_date
            ELSE p.visit_date
        END AS status_date,
        s.cause_of_death,
        s.va_cause_of_death
    FROM cur_pharm1 p
    LEFT JOIN cur_stat1 s ON s.person_id = p.person_uuid
),
naive_vl_data AS (
    SELECT pp.uuid AS nvl_person_uuid,
        EXTRACT(
            YEAR
            FROM AGE(NOW(), pp.date_of_birth)
        ) as age,
        ph.visit_date,
        ph.regimen
    FROM patient_person pp
        INNER JOIN (
            SELECT DISTINCT *
            FROM (
                    SELECT pharm.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY pharm.person_uuid
                            ORDER BY pharm.visit_date DESC
                        )
                    FROM (
                            SELECT DISTINCT *
                            FROM hiv_art_pharmacy hap
                                INNER JOIN hiv_art_pharmacy_regimens hapr
                                INNER JOIN hiv_regimen hr ON hr.id = hapr.regimens_id
                                INNER JOIN hiv_regimen_type hrt ON hrt.id = hr.regimen_type_id
                                INNER JOIN hiv_regimen_resolver hrr ON hrr.regimensys = hr.description ON hapr.art_pharmacy_id = hap.id
                            WHERE hap.archived = 0
                                AND hrt.id IN (1, 2, 3, 4, 14, 16)
                        ) pharm
                ) ph
            WHERE ph.row_number = 1
        ) ph ON ph.person_uuid = pp.uuid
    WHERE pp.uuid NOT IN (
            SELECT patient_uuid
            FROM (
                    SELECT COUNT(ls.patient_uuid),
                        ls.patient_uuid
                    FROM laboratory_sample ls
                        INNER JOIN laboratory_test lt ON lt.id = ls.test_id
                        AND lt.lab_test_id = 16
                    WHERE ls.archived = 0
                    GROUP BY ls.patient_uuid
                ) t
        )
),
crytococal_antigen as (
    select *
    from (
            select DISTINCT ON (lr.patient_uuid) lr.patient_uuid as personuuid12,
                CAST(lr.date_result_reported AS DATE) AS dateOfLastCrytococalAntigen,
                lr.result_reported AS lastCrytococalAntigen,
                ROW_NUMBER() OVER (
                    PARTITION BY lr.patient_uuid
                    ORDER BY lr.date_result_reported DESC
                ) as rowNum
            from public.laboratory_test lt
                inner join laboratory_result lr on lr.test_id = lt.id
            where lab_test_id = 52
                OR lab_test_id = 69
                OR lab_test_id = 70
                AND lr.date_result_reported IS NOT NULL
                AND lr.date_result_reported <= current_date
                AND lr.date_result_reported >= '1980-01-01'
                AND lr.result_reported is NOT NULL
                AND lr.archived = 0
        ) dt
    where rowNum = 1
),
vaCauseOfDeath AS (
    SELECT hst.hiv_status,
        hst.person_id,
        hst.cause_of_death,
        hst.va_cause_of_death,
        hst.status_date
    FROM (
            SELECT *
            FROM (
                    SELECT DISTINCT (person_id) person_id,
                        status_date,
                        cause_of_death,
                        va_cause_of_death,
                        hiv_status,
                        ROW_NUMBER() OVER (
                            PARTITION BY person_id
                            ORDER BY status_date DESC
                        )
                    FROM hiv_status_tracker
                    WHERE hiv_status ilike '%Died%'
                        AND archived = 0
                        AND status_date <= current_date
                ) s
            WHERE s.row_number = 1
        ) hst
        INNER JOIN hiv_enrollment he ON he.person_uuid = hst.person_id
    WHERE hst.status_date <= current_date
),
case_manager AS (
    SELECT DISTINCT ON (cmp.person_uuid) person_uuid AS caseperson,
        cmp.case_manager_id,
        CONCAT(cm.first_name, ' ', cm.last_name) AS caseManager
    FROM (
            SELECT person_uuid,
                case_manager_id,
                ROW_NUMBER () OVER (
                    PARTITION BY person_uuid
                    ORDER BY id DESC
                )
            FROM case_manager_patients
        ) cmp
        INNER JOIN case_manager cm ON cm.id = cmp.case_manager_id
    WHERE cmp.row_number = 1
),
client_verification AS (
    SELECT *
    FROM (
            select person_uuid,
                data -> 'attempt' -> 0 ->> 'outcome' AS clientVerificationOutCome,
                data -> 'attempt' -> 0 ->> 'verificationStatus' AS clientVerificationStatus,
                CAST (data -> 'attempt' -> 0 ->> 'dateOfAttempt' AS DATE) AS dateOfOutcome,
                ROW_NUMBER() OVER (
                    PARTITION BY person_uuid
                    ORDER BY CAST(data -> 'attempt' -> 0 ->> 'dateOfAttempt' AS DATE) DESC
                )
            from public.hiv_observation
            where type = 'Client Verification'
                AND archived = 0
                AND CAST(data -> 'attempt' -> 0 ->> 'dateOfAttempt' AS DATE) <= current_date
                AND CAST(data -> 'attempt' -> 0 ->> 'dateOfAttempt' AS DATE) >= '1980-01-01'
        ) clientVerification
    WHERE row_number = 1
        AND dateOfOutcome IS NOT NULL
),
chronic_condition AS (
    SELECT DISTINCT ON (person_uuid) person_uuid AS cc_person_uuid,
        NULLIF(TRIM(
            CASE WHEN data -> 'chronicCondition' ->> 'hypertensive' = 'Yes' THEN 'Hypertension' ELSE '' END
            || CASE WHEN data -> 'chronicCondition' ->> 'hypertensive' = 'Yes'
                    AND data -> 'chronicCondition' ->> 'diabetic'     = 'Yes' THEN ', ' ELSE '' END
            || CASE WHEN data -> 'chronicCondition' ->> 'diabetic'     = 'Yes' THEN 'Diabetes'    ELSE '' END
        ), '')                                        AS screeningForChronicConditions,
        NULLIF(TRIM(
            CASE WHEN data -> 'chronicCondition' ->> 'hypertensive' = 'Yes' THEN 'Hypertension' ELSE '' END
            || CASE WHEN data -> 'chronicCondition' ->> 'hypertensive' = 'Yes'
                    AND data -> 'chronicCondition' ->> 'diabetic'     = 'Yes' THEN ', ' ELSE '' END
            || CASE WHEN data -> 'chronicCondition' ->> 'diabetic'     = 'Yes' THEN 'Diabetes'    ELSE '' END
        ), '')                                        AS coMorbidities
    FROM hiv_observation
    WHERE type     = 'Chronic Care'
      AND archived = 0
      AND data -> 'chronicCondition' IS NOT NULL
      AND (
          data -> 'chronicCondition' ->> 'hypertensive' = 'Yes'
          OR data -> 'chronicCondition' ->> 'diabetic'  = 'Yes'
      )
    ORDER BY person_uuid, date_of_observation DESC
)
SELECT DISTINCT ON (bd.personUuid)
    -- ── 1  State ──────────────────────────────────────────────────────────
    bd.state                                                              AS "State",
    -- ── 2  L.G.A ──────────────────────────────────────────────────────────
    bd.lga                                                                AS "L.G.A",
    -- ── 3  LGA Of Residence ───────────────────────────────────────────────
    p_lga.lgaOfResidence                                                  AS "LGA Of Residence",
    -- ── 4  Facility Name ──────────────────────────────────────────────────
    bd.facilityName                                                       AS "Facility Name",
    -- ── 5  DatimId ────────────────────────────────────────────────────────
    bd.datimId                                                            AS "DatimId",
    -- ── 6  Patient ID ─────────────────────────────────────────────────────
    bd.personUuid                                                         AS "Patient ID",
    -- ── 7  NDR Patient Identifier ─────────────────────────────────────────
    CONCAT(bd.datimId, '_', bd.personUuid)                               AS "NDR Patient Identifier",
    -- ── 8  Hospital Number ────────────────────────────────────────────────
    bd.hospitalNumber                                                     AS "Hospital Number",
    -- ── 9  Unique Id ──────────────────────────────────────────────────────
    bd.uniqueId                                                           AS "Unique Id",
    -- ── 10 Household Unique No ────────────────────────────────────────────
    bd.householdUniqueNo                                                  AS "Household Unique No",
    -- ── 11 OVC Unique ID ──────────────────────────────────────────────────
    bd.ovcUniqueId                                                        AS "OVC Unique ID",
    -- ── 12 Sex ────────────────────────────────────────────────────────────
    bd.gender                                                             AS "Sex",
    -- ── 13 Target group ───────────────────────────────────────────────────
    bd.targetGroup                                                        AS "Target group",
    -- ── 14 Current Weight (kg) ────────────────────────────────────────────
    c.currentWeight                                                       AS "Current Weight (kg)",
    -- ── 15 Pregnancy Status ───────────────────────────────────────────────
    c.pregnancyStatus                                                     AS "Pregnancy Status",
    -- ── 16 Date of Birth ──────────────────────────────────────────────────
    bd.dateOfBirth                                                        AS "Date of Birth (yyyy-mm-dd)",
    -- ── 17 Age ────────────────────────────────────────────────────────────
    bd.age                                                                AS "Age",
    -- ── 18 Care Entry Point ───────────────────────────────────────────────
    bd.careEntry                                                          AS "Care Entry Point",
    -- ── 19 Date of Registration ───────────────────────────────────────────
    bd.dateOfRegistration                                                 AS "Date of Registration",
    -- ── 20 Enrollment Date ────────────────────────────────────────────────
    bd.dateOfEnrollment                                                   AS "Enrollment Date (yyyy-mm-dd)",
    -- ── 21 ART Start Date ─────────────────────────────────────────────────
    bd.artStartDate                                                       AS "ART Start Date (yyyy-mm-dd)",
    -- ── 22 Last Pickup Date ───────────────────────────────────────────────
    pdr.lastPickupDate                                                    AS "Last Pickup Date (yyyy-mm-dd)",
    -- ── 23 Months of ARV Refill ───────────────────────────────────────────
    pdr.monthsOfARVRefill                                                 AS "Months of ARV Refill",
    -- ── 24 Regimen Line at ART Start ──────────────────────────────────────
    bd.regimenLineAtStart                                                 AS "Regimen Line at ART Start",
    -- ── 25 Regimen at ART Start ───────────────────────────────────────────
    bd.regimenAtStart                                                     AS "Regimen at ART Start",
    -- ── 26 Date of Start of Current ART Regimen ───────────────────────────
    ca.dateOfCurrentRegimen                                               AS "Date of Start of Current ART Regimen",
    -- ── 27 Current Regimen Line ───────────────────────────────────────────
    pdr.currentRegimenLine                                                AS "Current Regimen Line",
    -- ── 28 Current ART Regimen ────────────────────────────────────────────
    pdr.currentARTRegimen                                                 AS "Current ART Regimen",
    -- ── 29 Clinical Staging at Last Visit ────────────────────────────────
    c.currentClinicalStage                                                AS "Clinical Staging at Last Visit",
    -- ── 30 Date of Last CD4 Count ─────────────────────────────────────────
    CASE
        WHEN cd.dateOfCd4Lb  IS NOT NULL THEN CAST(cd.dateOfCd4Lb  AS DATE)
        WHEN ccd.visit_date  IS NOT NULL THEN CAST(ccd.visit_date   AS DATE)
        ELSE NULL
    END                                                                   AS "Date of Last CD4 Count",
    -- ── 31 Last CD4 Count ─────────────────────────────────────────────────
    CASE
        WHEN cd.cd4Lb  IS NOT NULL THEN cd.cd4Lb
        WHEN ccd.cd_4  IS NOT NULL THEN CAST(ccd.cd_4 AS VARCHAR)
        ELSE NULL
    END                                                                   AS "Last CD4 Count",
    -- ── 32 Date of Viral Load Sample Collection ───────────────────────────
    scd.dateOfViralLoadSampleCollection                                   AS "Date of Viral Load Sample Collection (yyyy-mm-dd)",
    -- ── 33 Date of Current ViralLoad Result Sample ────────────────────────
    cvlr.dateOfCurrentViralLoadSample                                     AS "Date of Current ViralLoad Result Sample (yyyy-mm-dd)",
    -- ── 34 Current Viral Load (c/ml) ──────────────────────────────────────
    cvlr.currentViralLoad                                                 AS "Current Viral Load (c/ml)",
    -- ── 35 Date of Current Viral Load ─────────────────────────────────────
    cvlr.dateOfCurrentViralLoad                                           AS "Date of Current Viral Load (yyyy-mm-dd)",
    -- ── 36 Viral Load Indication ──────────────────────────────────────────
    cvlr.viralLoadIndication                                              AS "Viral Load Indication",
    -- ── 37 Viral Load Eligibility Status ──────────────────────────────────
    (
        CASE
            WHEN prepre.status ILIKE '%DEATH%' THEN FALSE
            WHEN prepre.status ILIKE '%out%'   THEN FALSE
            WHEN pre.status    ILIKE '%DEATH%' THEN FALSE
            WHEN pre.status    ILIKE '%out%'   THEN FALSE
            WHEN ct.status     ILIKE '%IIT%'   THEN FALSE
            WHEN ct.status     ILIKE '%out%'   THEN FALSE
            WHEN ct.status     ILIKE '%DEATH%' THEN FALSE
            WHEN ct.status     ILIKE '%stop%'  THEN FALSE
            WHEN (nvd.age >= 15 AND nvd.regimen ILIKE '%DTG%'
                  AND bd.artStartDate + 91  < current_date
                  AND ct.status     ILIKE '%ACTIVE%'
                  AND prepre.status ILIKE '%ACTIVE%') THEN TRUE
            WHEN (nvd.age >= 15 AND nvd.regimen NOT ILIKE '%DTG%'
                  AND bd.artStartDate + 181 < current_date
                  AND ct.status     ILIKE '%ACTIVE%'
                  AND prepre.status ILIKE '%ACTIVE%') THEN TRUE
            WHEN (nvd.age <= 15
                  AND bd.artStartDate + 181 < current_date
                  AND ct.status     ILIKE '%ACTIVE%'
                  AND prepre.status ILIKE '%ACTIVE%') THEN TRUE
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad, '[^0-9]','','g'),'') AS INTEGER) IS NULL
                 AND scd.dateOfViralLoadSampleCollection IS NULL
                 AND cvlr.dateOfCurrentViralLoad         IS NULL
                 AND CAST(bd.artStartDate AS DATE) + 181 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%' THEN TRUE
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad, '[^0-9]','','g'),'') AS INTEGER) IS NULL
                 AND scd.dateOfViralLoadSampleCollection IS NOT NULL
                 AND cvlr.dateOfCurrentViralLoad         IS NULL
                 AND CAST(bd.artStartDate AS DATE) + 91  < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%' THEN TRUE
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad, '[^0-9]','','g'),'') AS INTEGER) < 1000
                 AND (scd.dateOfViralLoadSampleCollection < cvlr.dateOfCurrentViralLoad
                      OR scd.dateOfViralLoadSampleCollection IS NULL)
                 AND CAST(cvlr.dateOfCurrentViralLoad AS DATE) + 181 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%' THEN TRUE
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad, '[^0-9]','','g'),'') AS INTEGER) < 1000
                 AND (scd.dateOfViralLoadSampleCollection > cvlr.dateOfCurrentViralLoad
                      OR cvlr.dateOfCurrentViralLoad IS NULL)
                 AND CAST(scd.dateOfViralLoadSampleCollection AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%' THEN TRUE
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad, '[^0-9]','','g'),'') AS INTEGER) > 1000
                 AND (scd.dateOfViralLoadSampleCollection < cvlr.dateOfCurrentViralLoad
                      OR scd.dateOfViralLoadSampleCollection IS NULL)
                 AND CAST(cvlr.dateOfCurrentViralLoad AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%' THEN TRUE
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad, '[^0-9]','','g'),'') AS INTEGER) > 1000
                 AND (scd.dateOfViralLoadSampleCollection > cvlr.dateOfCurrentViralLoad
                      OR cvlr.dateOfCurrentViralLoad IS NULL)
                 AND CAST(scd.dateOfViralLoadSampleCollection AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%' THEN TRUE
            ELSE FALSE
        END
    )                                                                     AS "Viral Load Eligibility Status",
    -- ── 38 Date of Viral Load Eligibility Status ───────────────────────────
    (
        CASE
            WHEN prepre.status ILIKE '%DEATH%' THEN NULL
            WHEN prepre.status ILIKE '%out%'   THEN NULL
            WHEN pre.status    ILIKE '%DEATH%' THEN NULL
            WHEN pre.status    ILIKE '%out%'   THEN NULL
            WHEN ct.status     ILIKE '%IIT%'   THEN NULL
            WHEN ct.status     ILIKE '%out%'   THEN NULL
            WHEN ct.status     ILIKE '%DEATH%' THEN NULL
            WHEN ct.status     ILIKE '%stop%'  THEN NULL
            WHEN (nvd.age >= 15 AND nvd.regimen ILIKE '%DTG%'
                  AND bd.artStartDate + 91 < current_date
                  AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%')
                THEN CAST(bd.artStartDate + 91 AS DATE)
            WHEN (nvd.age >= 15 AND nvd.regimen NOT ILIKE '%DTG%'
                  AND bd.artStartDate + 181 < current_date
                  AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%')
                THEN CAST(bd.artStartDate + 181 AS DATE)
            WHEN (nvd.age <= 15
                  AND bd.artStartDate + 181 < current_date
                  AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%')
                THEN CAST(bd.artStartDate + 181 AS DATE)
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad,'[^0-9]','','g'),'') AS INTEGER) IS NULL
                 AND scd.dateOfViralLoadSampleCollection IS NULL
                 AND cvlr.dateOfCurrentViralLoad         IS NULL
                 AND CAST(bd.artStartDate AS DATE) + 181 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%'
                THEN CAST(bd.artStartDate AS DATE) + 181
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad,'[^0-9]','','g'),'') AS INTEGER) IS NULL
                 AND scd.dateOfViralLoadSampleCollection IS NOT NULL
                 AND cvlr.dateOfCurrentViralLoad         IS NULL
                 AND CAST(bd.artStartDate AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%'
                THEN CAST(bd.artStartDate AS DATE) + 91
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad,'[^0-9]','','g'),'') AS INTEGER) < 1000
                 AND (scd.dateOfViralLoadSampleCollection < cvlr.dateOfCurrentViralLoad
                      OR scd.dateOfViralLoadSampleCollection IS NULL)
                 AND CAST(cvlr.dateOfCurrentViralLoad AS DATE) + 181 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%'
                THEN CAST(cvlr.dateOfCurrentViralLoad AS DATE) + 181
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad,'[^0-9]','','g'),'') AS INTEGER) < 1000
                 AND (scd.dateOfViralLoadSampleCollection > cvlr.dateOfCurrentViralLoad
                      OR cvlr.dateOfCurrentViralLoad IS NULL)
                 AND CAST(scd.dateOfViralLoadSampleCollection AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%'
                THEN CAST(scd.dateOfViralLoadSampleCollection AS DATE) + 91
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad,'[^0-9]','','g'),'') AS INTEGER) > 1000
                 AND (scd.dateOfViralLoadSampleCollection < cvlr.dateOfCurrentViralLoad
                      OR scd.dateOfViralLoadSampleCollection IS NULL)
                 AND CAST(cvlr.dateOfCurrentViralLoad AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%'
                THEN CAST(cvlr.dateOfCurrentViralLoad AS DATE) + 91
            WHEN CAST(NULLIF(REGEXP_REPLACE(cvlr.currentViralLoad,'[^0-9]','','g'),'') AS INTEGER) > 1000
                 AND (scd.dateOfViralLoadSampleCollection > cvlr.dateOfCurrentViralLoad
                      OR cvlr.dateOfCurrentViralLoad IS NULL)
                 AND CAST(scd.dateOfViralLoadSampleCollection AS DATE) + 91 < current_date
                 AND ct.status ILIKE '%ACTIVE%' AND prepre.status ILIKE '%ACTIVE%'
                THEN CAST(scd.dateOfViralLoadSampleCollection AS DATE) + 91
            ELSE NULL
        END
    )                                                                     AS "Date of Viral Load Eligibility Status",
    -- ── 39 Current ART Status ─────────────────────────────────────────────
    (
        CASE
            WHEN (pre.status ILIKE '%IIT%' OR pre.status ILIKE '%stop%')
                 AND ct.status ILIKE '%ACTIVE%'             THEN 'Active Restart'
            WHEN ct.status ILIKE '%ACTIVE%'                 THEN 'Active'
            WHEN ct.status ILIKE '%ART Transfer In%'        THEN ''
            WHEN prepre.status ILIKE '%DEATH%'              THEN 'Died'
            WHEN prepre.status ILIKE '%out%'                THEN 'Transferred Out'
            WHEN pre.status    ILIKE '%DEATH%'              THEN 'Died'
            WHEN pre.status    ILIKE '%out%'                THEN 'Transferred Out'
            WHEN ct.status     ILIKE '%IIT%'                THEN 'IIT'
            WHEN ct.status     ILIKE '%out%'                THEN 'Transferred Out'
            WHEN ct.status     ILIKE '%DEATH%'              THEN 'Died'
            WHEN pre.status ILIKE '%ACTIVE%'
                 AND ct.status ILIKE '%ACTIVE%'             THEN 'Active'
            ELSE REPLACE(ct.status, '_', ' ')
        END
    )                                                                     AS "Current ART Status",
    -- ── 40 Date of Current ART Status ─────────────────────────────────────
    CAST(
        (
            CASE
                WHEN ct.status ILIKE '%ACTIVE%'          THEN ct.status_date
                WHEN ct.status ILIKE '%ART Transfer In%' THEN ct.status_date
                WHEN prepre.status ILIKE '%DEATH%'       THEN prepre.status_date
                WHEN prepre.status ILIKE '%out%'         THEN prepre.status_date
                WHEN pre.status    ILIKE '%DEATH%'       THEN pre.status_date
                WHEN pre.status    ILIKE '%out%'         THEN pre.status_date
                WHEN ct.status ILIKE '%IIT%'  THEN CASE
                    WHEN (pre.status ILIKE '%DEATH%' OR pre.status ILIKE '%out%'
                          OR pre.status ILIKE '%stop%') THEN pre.status_date
                    ELSE ct.status_date END
                WHEN ct.status ILIKE '%stop%' THEN CASE
                    WHEN (pre.status ILIKE '%DEATH%' OR pre.status ILIKE '%out%'
                          OR pre.status ILIKE '%IIT%')  THEN pre.status_date
                    ELSE ct.status_date END
                WHEN ct.status ILIKE '%out%'  THEN CASE
                    WHEN (pre.status ILIKE '%DEATH%' OR pre.status ILIKE '%stop%'
                          OR pre.status ILIKE '%IIT%')  THEN pre.status_date
                    ELSE ct.status_date END
                WHEN (pre.status ILIKE '%IIT%' OR pre.status ILIKE '%stop%')
                     AND ct.status ILIKE '%ACTIVE%'   THEN ct.status_date
                WHEN pre.status ILIKE '%ACTIVE%'
                     AND ct.status ILIKE '%ACTIVE%'   THEN ct.status_date
                ELSE ct.status_date
            END
        ) AS DATE
    )                                                                     AS "Date of Current ART Status",
    -- ── 41 Client Verification Outcome ────────────────────────────────────
    cvl.clientVerificationOutCome                                         AS "Client Verification Outcome",
    -- ── 42 Cause of Death ─────────────────────────────────────────────────
    COALESCE(vaod.cause_of_death, ct.cause_of_death)                      AS "Cause of Death",
    -- ── 43 VA Cause of Death ──────────────────────────────────────────────
    COALESCE(vaod.va_cause_of_death, ct.va_cause_of_death)                AS "VA Cause of Death",
    -- ── 44 Previous ART Status ────────────────────────────────────────────
    (
        CASE
            WHEN prepre.status ILIKE '%DEATH%'            THEN 'Died'
            WHEN prepre.status ILIKE '%out%'              THEN 'Transferred Out'
            WHEN pre.status    ILIKE '%DEATH%'            THEN 'Died'
            WHEN pre.status    ILIKE '%out%'              THEN 'Transferred Out'
            WHEN (prepre.status ILIKE '%IIT%' OR prepre.status ILIKE '%stop%')
                 AND pre.status ILIKE '%ACTIVE%'          THEN 'Active Restart'
            WHEN prepre.status ILIKE '%ACTIVE%'
                 AND pre.status ILIKE '%ACTIVE%'          THEN 'Active'
            ELSE REPLACE(pre.status, '_', ' ')
        END
    )                                                                     AS "Previous ART Status",
    -- ── 45 Confirmed Date of Previous ART Status ──────────────────────────
    CAST(
        (
            CASE
                WHEN prepre.status ILIKE '%DEATH%' THEN prepre.status_date
                WHEN prepre.status ILIKE '%out%'   THEN prepre.status_date
                WHEN pre.status    ILIKE '%DEATH%' THEN pre.status_date
                WHEN pre.status    ILIKE '%out%'   THEN pre.status_date
                WHEN (prepre.status ILIKE '%IIT%' OR prepre.status ILIKE '%stop%')
                     AND pre.status ILIKE '%ACTIVE%'      THEN pre.status_date
                WHEN prepre.status ILIKE '%ACTIVE%'
                     AND pre.status ILIKE '%ACTIVE%'      THEN pre.status_date
                ELSE pre.status_date
            END
        ) AS DATE
    )                                                                     AS "Confirmed Date of Previous ART Status",
    -- ── 46 ART Enrollment Setting ─────────────────────────────────────────
    bd.enrollmentSetting                                                  AS "ART Enrollment Setting",
    -- ── 47 Date of TB Screening ───────────────────────────────────────────
    tbS.dateOfTbScreened                                                  AS "Date of TB Screening (yyyy-mm-dd)",
    -- ── 48 TB Screening Type ──────────────────────────────────────────────
    tbS.tbScreeningType                                                   AS "TB Screening Type",
    -- ── 49 CAD Score ──────────────────────────────────────────────────────
    tbTmentNew.cadScore                                                   AS "CAD Score",
    -- ── 50 TB Status ──────────────────────────────────────────────────────
    tbS.tbStatus                                                          AS "TB Status",
    -- ── 51 Date of TB Sample Collection ──────────────────────────────────
    tbSample.dateOfTbSampleCollection                                     AS "Date of TB Sample Collection (yyyy-mm-dd)",
    -- ── 52 TB Diagnostic Test Type ────────────────────────────────────────
    tbResult.tbDiagnosticTestType                                         AS "TB Diagnostic Test Type",
    -- ── 53 Date of TB Diagnostic Result Received ──────────────────────────
    tbResult.dateofTbDiagnosticResultReceived                             AS "Date of TB Diagnostic Result Received (yyyy-mm-dd)",
    -- ── 54 TB Diagnostic Result ───────────────────────────────────────────
    tbResult.tbDiagnosticResult                                           AS "TB Diagnostic Result",
    -- ── 55 Date of Additional TB Diagnosis Result using XRAY ──────────────
    (
        CASE
            WHEN (tbTmentNew.clinicallyEvaulated = 'Yes'
                  AND tbTmentNew.tbScreeningType ILIKE '%Chest X-Ray with CAD and/or Symptom screening%'
                  AND tbTmentNew.chestXrayDone = 'Yes'
                  AND negativeTb.tbDiagnosticResult IS NOT NULL
                  AND tbTmentNew.cadScore >= 40)
                THEN CAST(tbTmentNew.dateOfChestXrayResultTestDone AS DATE)
            ELSE NULL
        END
    )                                                                     AS "Date of Additional TB Diagnosis Result using XRAY",
    -- ── 56 Additional TB Diagnosis Result using XRAY ──────────────────────
    (
        CASE
            WHEN (tbTmentNew.clinicallyEvaulated = 'Yes'
                  AND tbTmentNew.tbScreeningType ILIKE '%Chest X-Ray with CAD and/or Symptom screening%'
                  AND tbTmentNew.chestXrayDone = 'Yes'
                  AND tbTmentNew.cadScore >= 40
                  AND negativeTb.tbDiagnosticResult IS NOT NULL)
                THEN tbTmentNew.chestXrayResultTest
            ELSE NULL
        END
    )                                                                     AS "Additional TB Diagnosis Result using XRAY",
    -- ── 57 Date of Start of TB Treatment ─────────────────────────────────
    COALESCE(tbTmentNew.tbTreatmentStartDate, tbTment.tbTreatmentStartDate) AS "Date of Start of TB Treatment (yyyy-mm-dd)",
    -- ── 58 TB Type ────────────────────────────────────────────────────────
    (
        CASE
            WHEN COALESCE(tbTmentNew.tbTreatmentType, tbTment.tbTreatementType)
                     IN ('New','Relapse','Relapsed') THEN 'New/Relapse'
            ELSE COALESCE(tbTmentNew.tbTreatmentType, tbTment.tbTreatementType)
        END
    )                                                                     AS "TB Type (new, relapsed etc)",
    -- ── 59 Date of Completion of TB Treatment ────────────────────────────
    COALESCE(tbTmentNew.completionDate, tbTment.tbCompletionDate)         AS "Date of Completion of TB Treatment (yyyy-mm-dd)",
    -- ── 60 TB Treatment Outcome ───────────────────────────────────────────
    COALESCE(tbTmentNew.treatmentOutcome, tbTment.tbTreatmentOutcome)     AS "TB Treatment Outcome",
    -- ── 61 Eligible for TPT ───────────────────────────────────────────────
    iptN.eligibilityTpt                                                   AS "Eligible for TPT",
    -- ── 62 Date of TPT Start ──────────────────────────────────────────────
    iptStart.dateOfIptStart                                               AS "Date of TPT Start (yyyy-mm-dd)",
    -- ── 63 TPT Type ───────────────────────────────────────────────────────
    iptStart.iptType                                                      AS "TPT Type",
    -- ── 64 TPT Completion Date ────────────────────────────────────────────
    COALESCE(CAST(iptN.tptCompletionDate AS DATE), ipt.iptCompletionDate) AS "TPT Completion Date (yyyy-mm-dd)",
    -- ── 65 TPT Completion Status ──────────────────────────────────────────
    (
        CASE
            WHEN COALESCE(iptN.tptCompletionStatus, ipt.iptCompletionStatus) = 'IPT Completed'
                THEN 'Treatment completed'
            ELSE COALESCE(iptN.tptCompletionStatus, ipt.iptCompletionStatus)
        END
    )                                                                     AS "TPT Completion Status",
    -- ── 66 Date of Commencement of EAC ────────────────────────────────────
    e.dateOfCommencementOfEAC                                             AS "Date of Commencement of EAC (yyyy-mm-dd)",
    -- ── 67 Number of EAC Sessions Completed ───────────────────────────────
    e.numberOfEACSessionCompleted                                         AS "Number of EAC Sessions Completed",
    -- ── 68 Date of Last EAC Session Completed ─────────────────────────────
    e.dateOfLastEACSessionCompleted                                       AS "Date of Last EAC Session Completed",
    -- ── 69 Date of Extended EAC Completion ────────────────────────────────
    e.dateOfExtendEACCompletion                                           AS "Date of Extended EAC Completion (yyyy-mm-dd)",
    -- ── 70 Date of Repeat VL - Post EAC VL Sample Collected ───────────────
    e.dateOfRepeatViralLoadEACSampleCollection                            AS "Date of Repeat Viral Load - Post EAC VL Sample Collected (yyyy-mm-dd)",
    -- ── 71 Repeat Viral Load Result - POST EAC ────────────────────────────
    e.repeatViralLoadResult                                               AS "Repeat Viral Load Result (c/ml) - POST EAC",
    -- ── 72 Date of Repeat Viral Load Result - POST EAC VL ─────────────────
    e.DateOfRepeatViralLoadResult                                         AS "Date of Repeat Viral Load Result - POST EAC VL",
    -- ── 73 Date of Devolvement ────────────────────────────────────────────
    dsd1.dateOfDevolvement                                                AS "Date of Devolvement",
    -- ── 74 Model Devolved To ──────────────────────────────────────────────
    dsd1.modelDevolvedTo                                                  AS "Model Devolved To",
    -- ── 75 Date of Current DSD ────────────────────────────────────────────
    dsd2.dateOfCurrentDSD                                                 AS "Date of Current DSD",
    -- ── 76 Current DSD Model ──────────────────────────────────────────────
    dsd2.currentDSDModel                                                  AS "Current DSD Model",
    -- ── 77 Current DSD Outlet ─────────────────────────────────────────────
    dsd2.currentDsdOutlet                                                 AS "Current DSD Outlet",
    -- ── 78 Date of Return of DSD Client to Facility ───────────────────────
    dsd2.dateReturnToSite                                                 AS "Date of Return of DSD Client to Facility (yyyy-mm-dd)",
    -- ── 79 Screening for Chronic Conditions ────────────────────────────────
    chc.screeningForChronicConditions                                     AS "Screening for Chronic Conditions",
    -- ── 80 Co-morbidities ──────────────────────────────────────────────────
    chc.coMorbidities                                                     AS "Co-morbidities",
    -- ── 81 Date of Cervical Cancer Screening ─────────────────────────────
    cc.dateOfCervicalCancerScreening                                      AS "Date of Cervical Cancer Screening (yyyy-mm-dd)",
    -- ── 82 Cervical Cancer Screening Type ────────────────────────────────
    cc.cervicalCancerScreeningType                                        AS "Cervical Cancer Screening Type",
    -- ── 83 Cervical Cancer Screening Method ──────────────────────────────
    cc.cervicalCancerScreeningMethod                                      AS "Cervical Cancer Screening Method",
    -- ── 84 Result of Cervical Cancer Screening ───────────────────────────
    cc.resultOfCervicalCancerScreening                                    AS "Result of Cervical Cancer Screening",
    -- ── 85 Date of Precancerous Lesions Treatment ────────────────────────
    cc.treatmentMethodDate                                                AS "Date of Precancerous Lesions Treatment (yyyy-mm-dd)",
    -- ── 86 Precancerous Lesions Treatment Methods ────────────────────────
    cc.cervicalCancerTreatmentScreened                                    AS "Precancerous Lesions Treatment Methods",
    -- ── 87 Date Biometrics Enrolled ───────────────────────────────────────
    b.dateBiometricsEnrolled                                              AS "Date Biometrics Enrolled (yyyy-mm-dd)",
    -- ── 88 Number of Fingers Captured ────────────────────────────────────
    b.numberOfFingersCaptured                                             AS "Number of Fingers Captured",
    -- ── 89 Date Biometrics Recapture ──────────────────────────────────────
    b.dateBiometricsRecaptured                                            AS "Date Biometrics Recapture (yyyy-mm-dd)",
    -- ── 90 Number of Fingers Recaptured ──────────────────────────────────
    b.numberOfFingersRecaptured                                           AS "Number of Fingers Recaptured",
    -- ── 91 Case Manager ───────────────────────────────────────────────────
    INITCAP(cm.caseManager)                                               AS "Case Manager"
FROM bio_data bd
    LEFT JOIN patient_lga p_lga          ON p_lga.personUuid11       = bd.personUuid
    LEFT JOIN pharmacy_details_regimen pdr ON pdr.person_uuid40       = bd.personUuid
    LEFT JOIN current_clinical c          ON c.person_uuid10          = bd.personUuid
    LEFT JOIN sample_collection_date scd  ON scd.person_uuid120       = bd.personUuid
    LEFT JOIN current_vl_result cvlr      ON cvlr.person_uuid130      = bd.personUuid
    LEFT JOIN labCD4 cd                   ON cd.cd4_person_uuid        = bd.personUuid
    LEFT JOIN careCardCD4 ccd             ON ccd.cccd4_person_uuid     = bd.personUuid
    LEFT JOIN eac e                       ON e.person_uuid50           = bd.personUuid
    LEFT JOIN biometric b                 ON b.person_uuid60           = bd.personUuid
    LEFT JOIN current_regimen ca          ON ca.person_uuid70          = bd.personUuid
    LEFT JOIN ipt ipt                     ON ipt.personUuid80          = bd.personUuid
    LEFT JOIN iptNew iptN                 ON iptN.person_uuid          = bd.personUuid
    LEFT JOIN cervical_cancer cc          ON cc.person_uuid90          = bd.personUuid
    LEFT JOIN ovc ov                      ON ov.personUuid100          = bd.personUuid
    LEFT JOIN current_status ct           ON ct.cuPersonUuid           = bd.personUuid
    LEFT JOIN previous pre                ON pre.prePersonUuid         = ct.cuPersonUuid
    LEFT JOIN previous_previous prepre    ON prepre.prePrePersonUuid   = ct.cuPersonUuid
    LEFT JOIN naive_vl_data nvd           ON nvd.nvl_person_uuid       = bd.personUuid
    LEFT JOIN tb_sample_collection tbSample ON tbSample.personTbSample = bd.personUuid
    LEFT JOIN tbTreatment tbTment         ON tbTment.tbTreatmentPersonUuid = bd.personUuid
    LEFT JOIN tbTreatmentNew tbTmentNew   ON tbTmentNew.person_uuid_tb = bd.personUuid
    LEFT JOIN current_tb_result tbResult  ON tbResult.personTbResult   = bd.personUuid
    LEFT JOIN crytococal_antigen crypt    ON crypt.personuuid12         = bd.personUuid
    LEFT JOIN tbstatus tbS                ON tbS.person_uuid           = bd.personUuid
    LEFT JOIN tblam tbl                   ON tbl.personuuidtblam       = bd.personUuid
    LEFT JOIN dsd1 dsd1                   ON dsd1.person_uuid_dsd_1    = bd.personUuid
    LEFT JOIN dsd2 dsd2                   ON dsd2.person_uuid_dsd_2    = bd.personUuid
    LEFT JOIN case_manager cm             ON cm.caseperson             = bd.personUuid
    LEFT JOIN client_verification cvl     ON cvl.person_uuid           = bd.personUuid
    LEFT JOIN vaCauseOfDeath vaod         ON vaod.person_id            = bd.personUuid
    LEFT JOIN negativeTbDiagnosticResults negativeTb ON negativeTb.personTbResult = bd.personUuid
        AND negativeTb.dateOfTbSampleCollected = tbTmentNew.specimenSentDate
    LEFT JOIN chronic_condition chc        ON chc.cc_person_uuid        = bd.personUuid
    LEFT JOIN ipt_s iptStart              ON iptStart.person_uuid      = bd.personUuid