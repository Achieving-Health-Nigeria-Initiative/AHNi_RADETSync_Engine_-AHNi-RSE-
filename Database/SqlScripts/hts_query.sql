WITH htsReport AS (
SELECT hc.client_code AS clientCode, htsCounts.numberOfCounts AS htsCount,
(CASE WHEN hc.person_uuid IS NULL THEN INITCAP(hc.extra->>'first_name') ELSE INITCAP(pp.first_name) END) AS firstName,
(CASE WHEN hc.person_uuid IS NULL THEN INITCAP(hc.extra->>'surname') ELSE INITCAP(pp.surname) END) AS surname,
(CASE WHEN hc.person_uuid IS NULL THEN INITCAP(hc.extra->>'middile_name') ELSE INITCAP(pp.other_name) END) AS otherName,
(CASE WHEN hc.person_uuid IS NULL THEN INITCAP(hc.extra->>'gender') ELSE INITCAP(pp.sex) END) AS sex,
(CASE WHEN hc.person_uuid IS NULL THEN CAST(hc.extra->>'age' AS INTEGER)
ELSE CAST(EXTRACT(YEAR from AGE(current_date,  pp.date_of_birth)) AS INTEGER )
END) AS age,
(CASE WHEN hc.person_uuid IS NOT NULL THEN pp.date_of_birth
WHEN hc.person_uuid IS NULL AND LENGTH(hc.extra->>'date_of_birth') > 0
AND hc.extra->>'date_of_birth' != '' THEN CAST(NULLIF(hc.extra->>'date_of_birth', '') AS DATE) ELSE NULL END) AS dateOfBirth,
 (CASE WHEN hc.person_uuid IS NULL THEN hc.extra->>'phone_number'
ELSE pp.contact_point->'contactPoint'->0->'value'->>0 END) AS phoneNumber,
 (CASE WHEN hc.person_uuid IS NULL THEN hc.extra->>'marital_status'
ELSE pp.marital_status->>'display' END) AS maritalStatus,
(CASE WHEN hc.person_uuid IS NULL
THEN hc.extra->>'lga_of_residence' ELSE lgaOfResidence.lgaOfResidence END) AS LGAOfResidence,
(CASE WHEN hc.person_uuid IS NULL
 THEN hc.extra->>'state_of_residence' ELSE res_state.name END) AS StateOfResidence,
 facility.name AS facility,
 state.name AS state,
 lga.name AS lga,
 pp.uuid AS patientId,
pp.education->>'display' as education,
pp.employment_status->>'display' as occupation,
boui.code as datimCode,
hc.others->>'latitude' AS HTSLatitude,
hc.others->>'longitude' AS HTSLongitude,
(CASE WHEN hc.person_uuid IS NULL THEN hc.extra->>'client_address' ELSE r.address END) AS clientAddress,
hc.date_visit AS dateVisit,
htsPrevious.date_visit AS previousVisitDate, htsPrevious.hiv_test_result AS previousTestResult,
(CASE WHEN hc.first_time_visit IS true THEN 'Yes' ELSE 'No' END) firstTimeVisit,
hc.num_children AS numberOfChildren,
hc.num_wives AS numberOfWives,
(CASE WHEN hc.index_client IS true THEN 'Yes' ELSE 'No' END) indexClient,
(CASE WHEN hc.hiv_test_result = 'Positive' THEN 'No'
 WHEN hc.prep_offered IS true THEN 'Yes' ELSE 'No' END)  AS prepOffered,
(CASE WHEN hc.hiv_test_result = 'Positive' THEN 'No'
WHEN hc.prep_accepted IS true THEN 'Yes' ELSE 'No' END) AS prepAccepted,
(CASE WHEN hc.previously_tested IS true THEN 'Yes' ELSE 'No' END) AS previouslyTested,
tg.display AS targetGroup,
(CASE WHEN rf.display IN ('FP') THEN NULL ELSE rf.display END) AS referredFrom,
(CASE WHEN hrs.testing_setting IN ('FP') THEN NULL ELSE (select display from base_application_codeset where code = hrs.testing_setting) END) AS testingSetting,
tc.display AS counselingType,
(CASE
WHEN INITCAP(pp.sex) = 'Male' THEN NULL
WHEN preg.display IS NOT NULL THEN preg.display
END ) AS pregnancyStatus,
(select display from base_application_codeset where code = hrs.entry_point) AS entryPoint,
(CASE
WHEN preg.display='Breastfeeding' THEN 'Yes'
WHEN preg.display IS NULL THEN NULL
ELSE 'No'
END) AS breastFeeding,
it.display AS indexType,
(CASE WHEN hc.recency->>'optOutRTRI' ILIKE 'true' THEN 'Yes'
WHEN hc.recency->>'optOutRTRI' ILIKE 'false' THEN 'No'
WHEN hc.recency->>'optOutRTRI' != NULL THEN hc.recency->>'optOutRTRI'
ELSE NULL END) AS IfRecencyTestingOptIn,
hc.recency->>'rencencyId' AS RecencyID,
hc.recency->>'optOutRTRITestName' AS recencyTestType,
(CASE WHEN hc.recency->>'optOutRTRITestDate' IS NOT NULL
 AND hc.recency->>'optOutRTRITestDate' != '' AND LENGTH(hc.recency->>'optOutRTRITestDate') > 0
 THEN CAST(NULLIF(hc.recency->>'optOutRTRITestDate', '') AS DATE)
 WHEN hc.recency->>'sampleTestDate' IS NOT NULL
 AND hc.recency->>'sampleTestDate' != '' AND LENGTH(hc.recency->>'sampleTestDate') > 0
 THEN CAST(NULLIF(hc.recency->>'sampleTestDate', '') AS DATE) ELSE NULL END) AS recencyTestDate,
(CASE WHEN hc.recency->>'receivedResultDate' IS NOT NULL
  AND hc.recency->>'receivedResultDate' != '' AND LENGTH(hc.recency->>'receivedResultDate') > 0
  THEN CAST(NULLIF(hc.recency->>'receivedResultDate', '') AS DATE) ELSE NULL END) AS viralLoadReceivedResultDate,
(CASE
 WHEN hc.recency->>'rencencyInterpretation' IS NOT NULL
 AND hc.recency->>'rencencyInterpretation' ILIKE '%Long%' THEN 'RTRI Longterm'
 WHEN hc.recency->>'rencencyInterpretation' IS NOT NULL
 AND hc.recency->>'rencencyInterpretation' ILIKE '%Recent%' THEN 'RTRI Recent'
 ELSE hc.recency->>'rencencyInterpretation' END) AS recencyInterpretation,
hc.recency->>'finalRecencyResult' AS finalRecencyResult,
hc.recency->>'viralLoadResultClassification' AS viralLoadResult,
CAST(NULLIF(hc.recency->>'sampleCollectedDate', '') AS DATE) AS viralLoadSampleCollectionDate,
hc.recency->>'viralLoadConfirmationResult' AS viralLoadConfirmationResult,
CAST(NULLIF(hc.recency->>'viralLoadConfirmationTestDate', '') AS DATE) AS viralLoadConfirmationDate,
hc.risk_stratification_code AS Assessmentcode,
modality_code.display AS modality,
(CASE WHEN hc.syphilis_testing->>'syphilisTestResult' ILIKE 'Yes'
THEN 'Reactive' WHEN hc.syphilis_testing->>'syphilisTestResult' ILIKE 'No' THEN 'Non-Reactive' ELSE '' END) As syphilisTestResult,
CASE WHEN hc.syphilis_testing->>'syphilisTestResult' IN ('Yes', 'No') THEN hc.date_visit ELSE NULL END AS syphilisTestDate,
(CASE WHEN hc.hepatitis_testing->>'hepatitisBTestResult' ILIKE 'Yes'
 THEN 'Positive' WHEN hc.hepatitis_testing->>'hepatitisBTestResult' ILIKE 'No' THEN 'Negative' ELSE '' END) AS hepatitisBTestResult,
CASE WHEN hc.hepatitis_testing->>'hepatitisBTestResult' IN ('Yes', 'No') THEN hc.date_visit ELSE NULL END AS hepatitisBTestDate,
(CASE WHEN hc.hepatitis_testing->>'hepatitisCTestResult' ILIKE 'Yes'
 THEN 'Positive' WHEN hc.hepatitis_testing->>'hepatitisCTestResult' ILIKE 'No' THEN 'Negative' ELSE '' END) AS hepatitisCTestResult,
CASE WHEN hc.hepatitis_testing->>'hepatitisCTestResult' IN ('Yes', 'No') THEN hc.date_visit ELSE NULL END AS hepatitisCTestDate,
hc.cd4->>'cd4Count' AS CD4Type,
hc.cd4->>'cd4SemiQuantitative' AS CD4TestResult,
(CASE WHEN hc.hiv_test_result2 = 'Positive' THEN 'Positive'
WHEN  hc.hiv_test_result ='Negative' THEN 'Negative'
WHEN  hc.hiv_test_result ='Positive' AND hc.hiv_test_result2='Negative' THEN 'Negative'
WHEN  hc.hiv_test_result ='Positive' AND hc.hiv_test_result2 IS NULL THEN 'Positive'
WHEN hc.test1->>'result' ILIKE 'Yes' THEN 'Positive' ELSE 'Negative' END) AS finalHIVTestResult,
he.person_uuid AS patientUuid,
(CASE WHEN LENGTH(hc.test1->>'date') > 0 AND hc.test1->>'date' !=''  THEN CAST(NULLIF(hc.test1->>'date', '') AS DATE)
WHEN hc.date_visit IS NOT NULL THEN hc.date_visit ELSE NULL END)dateOfHIVTesting,
CAST(post_test_counseling->>'condomProvidedToClientCount' AS VARCHAR) AS numberOfCondomsGiven,
CAST(post_test_counseling->>'lubricantProvidedToClientCount' AS VARCHAR) AS numberOfLubricantsGiven,
CAST (riskScore.totalRiskScore AS VARCHAR) AS totalRiskScore, hc.source, hc.referred_for_sti AS refferedForSti,
hc.others->>'adhocCode' AS TesterName,
(CASE WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting IN ('FACILITY_HTS_TEST_SETTING_ANC', 'FACILITY_HTS_TEST_SETTING_RETESTING', 'FACILITY_HTS_TEST_SETTING_L&D', 'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING') THEN ''
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_WARD_INPATIENT' THEN 'Inpatient'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_CT' THEN 'CT'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_TB' THEN 'TB'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_FP' THEN 'FP'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_STI' THEN 'STI'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting IN ('FACILITY_HTS_TEST_SETTING_SNS', 'FACILITY_HTS_TEST_SETTING_INDEX', 'FACILITY_HTS_TEST_SETTING_EMERGENCY', 'FACILITY_HTS_TEST_SETTING_BLOOD_BANK', 'FACILITY_HTS_TEST_SETTING_PEDIATRIC', 'FACILITY_HTS_TEST_SETTING_MALNUTRITION','FACILITY_HTS_TEST_SETTING_PREP_TESTING', 'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY', 'FACILITY_HTS_TEST_SETTING_OTHERS_(SPECIFY)')  THEN 'Others'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_STANDALONE_HTS' THEN 'Standalone'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting IN ('COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING', 'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES','COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX', 'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW') THEN 'Pregnant Women (Community)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting IN ('COMMUNITY_HTS_TEST_SETTING_INDEX', 'COMMUNITY_HTS_TEST_SETTING_OTHERS','COMMUNITY_HTS_TEST_SETTING_SNS', 'COMMUNITY_HTS_TEST_SETTING_CT', 'COMMUNITY_HTS_TEST_SETTING_OVC') THEN 'Others (Community)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting IN ('COMMUNITY_HTS_TEST_SETTING_OUTREACH', 'COMMUNITY_HTS_TEST_SETTING_STANDALONE_HTS') THEN 'Outreach (Community)'
END) AS gonModalities,
(CASE WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_ANC' THEN 'PMTCT (ANC1 Only)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting IN ('FACILITY_HTS_TEST_SETTING_RETESTING', 'FACILITY_HTS_TEST_SETTING_L&D' ) THEN 'PMTCT (Post ANC1: Pregnancy/L&D)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_POST_NATAL_WARD_BREASTFEEDING' THEN 'PMTCT (Post ANC1: Breastfeeding)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_WARD_INPATIENT' THEN 'Inpatient'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_CT' THEN 'VCT'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_TB' THEN 'TB_STAT/OtherPITC'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting IN ('FACILITY_HTS_TEST_SETTING_FP', 'FACILITY_HTS_TEST_SETTING_BLOOD_BANK', 'FACILITY_HTS_TEST_SETTING_STANDALONE_HTS','FACILITY_HTS_TEST_SETTING_OTHERS_(SPECIFY)', 'FACILITY_HTS_TEST_SETTING_OTHERS') THEN 'Other PITC'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting ='FACILITY_HTS_TEST_SETTING_STI' THEN 'STI'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting ='FACILITY_HTS_TEST_SETTING_SNS' THEN 'SNS'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting ='FACILITY_HTS_TEST_SETTING_INDEX' THEN 'Index'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting ='FACILITY_HTS_TEST_SETTING_EMERGENCY' THEN 'Emergency'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_PEDIATRIC' THEN 'Pediatric'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_MALNUTRITION' THEN 'Malnutrition'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_PREP_TESTING' THEN 'PrEP_CT HTS'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_FACILITY' AND hrs.testing_setting = 'FACILITY_HTS_TEST_SETTING_SPOKE_HEALTH_FACILITY' THEN 'PMTCT (ANC1 Only)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting IN ('COMMUNITY_HTS_TEST_SETTING_CONGREGATIONAL_SETTING', 'COMMUNITY_HTS_TEST_SETTING_DELIVERY_HOMES','COMMUNITY_HTS_TEST_SETTING_TBA_ORTHODOX', 'COMMUNITY_HTS_TEST_SETTING_TBA_RT-HCW') THEN 'PMTCT (ANC1 Only)'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting = 'COMMUNITY_HTS_TEST_SETTING_INDEX' THEN 'Index'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting IN ('COMMUNITY_HTS_TEST_SETTING_OTHERS', 'COMMUNITY_HTS_TEST_SETTING_OVC', 'COMMUNITY_HTS_TEST_SETTING_STANDALONE_HTS') THEN 'Other Community Platforms'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting = 'COMMUNITY_HTS_TEST_SETTING_SNS' THEN 'SNS'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting = 'COMMUNITY_HTS_TEST_SETTING_CT' THEN 'VCT'
WHEN hrs.entry_point = 'HTS_ENTRY_POINT_COMMUNITY' AND hrs.testing_setting = 'COMMUNITY_HTS_TEST_SETTING_OUTREACH' THEN 'Mobile'
END)AS pepfarModalities, hc.risk_assessment->>'mlScore' AS mlScore, hc.risk_assessment->>'mlStatus' AS mlStatus,
ROW_NUMBER() OVER ( PARTITION BY hc.person_uuid ORDER BY hc.date_visit DESC, hc.date_created DESC) AS rnkk
FROM hts_client hc
LEFT JOIN base_application_codeset tg ON tg.code = hc.target_group
LEFT JOIN base_application_codeset it ON it.id = hc.relation_with_index_client
LEFT JOIN base_application_codeset rf ON rf.id = hc.referred_from
LEFT JOIN base_application_codeset ts ON ts.code = hc.testing_setting
LEFT JOIN base_application_codeset tc ON tc.id = hc.type_counseling
LEFT JOIN base_application_codeset preg ON preg.id = hc.pregnant
LEFT JOIN base_application_codeset relation ON relation.id = hc.relation_with_index_client
LEFT JOIN hts_risk_stratification hrs ON hrs.code = hc.risk_stratification_code
LEFT JOIN base_application_codeset modality_code ON modality_code.code = hrs.modality
LEFT JOIN patient_person pp ON pp.uuid=hc.person_uuid
LEFT JOIN (SELECT * FROM (SELECT
p.id,
p.address ->>'{address,0,city}' as clientcity,
p.address ->> '{address,0,line,0}' as clientaddress,
(jsonb_array_elements(p.address->'address')->>'stateId') AS stateid,
(jsonb_array_elements(p.address->'address')->>'city') as address
FROM patient_person p) as result WHERE stateid ~ '^[0-9]+$' ) r ON r.id=pp.id
LEFT JOIN base_organisation_unit res_state ON res_state.id=CAST(r.stateid AS BIGINT)
LEFT JOIN base_organisation_unit facility ON facility.id=hc.facility_id
LEFT JOIN base_organisation_unit state ON state.id=facility.parent_organisation_unit_id
LEFT JOIN base_organisation_unit lga ON lga.id=state.parent_organisation_unit_id
LEFT JOIN base_organisation_unit_identifier boui ON boui.organisation_unit_id=hc.facility_id AND boui.name='DATIM_ID'
LEFT JOIN (select DISTINCT ON (personUuid) personUuid as personUuid11,
case when (addr ~ '^[0-9.]+$') =TRUE
 then (select name from base_organisation_unit where id = cast(addr as int)) ELSE
(select name from base_organisation_unit where id = cast(facilityLga as int)) end as lgaOfResidence
from (
 select pp.uuid AS personUuid, facility_lga.parent_organisation_unit_id AS facilityLga, (jsonb_array_elements(pp.address->'address')->>'district') as addr from patient_person pp
LEFT JOIN base_organisation_unit facility_lga ON facility_lga.id = CAST (pp.organization->'id' AS INTEGER)
) dt ) lgaOfResidence ON lgaOfResidence.personUuid11 = hc.person_uuid
LEFT JOIN hiv_enrollment he ON he.person_uuid = hc.person_uuid
LEFT JOIN (
SELECT person_uuid, COUNT(person_uuid) AS numberOfCounts from hts_client where archived = 0 AND hiv_test_result IS NOT NULL
group by 1
) htsCounts ON htsCounts.person_uuid = hc.person_uuid
LEFT JOIN (
SELECT * FROM (
select person_uuid,date_visit, hiv_test_result,
ROW_NUMBER() OVER ( PARTITION BY person_uuid ORDER BY date_visit DESC) AS rnk
FROM hts_client
WHERE archived = 0
GROUP BY person_uuid,date_visit, hiv_test_result
) pre where rnk = 2
) htsPrevious ON htsPrevious.person_uuid = hc.person_uuid
LEFT JOIN (
SELECT person_uuid, diagnosedWithTb + lastHivTestDone + whatWasTheResult + lastHivTestHadAnal + lastHivTestVaginalOral
+ lastHivTestInjectedDrugs + lastHivTestBasedOnRequest + lastHivTestForceToHaveSex + lastHivTestBloodTransfusion + lastHivTestPainfulUrination
 AS totalRiskScore FROM(
SELECT DISTINCT ON (person_uuid) person_uuid,
CASE WHEN risk_assessment->>'diagnosedWithTb' = 'true' THEN 1 ELSE 0 END AS diagnosedWithTb,
CASE WHEN risk_assessment->>'lastHivTestDone' = 'true' THEN 1 ELSE 0 END AS lastHivTestDone,
CASE WHEN risk_assessment->>'whatWasTheResult' = 'true' THEN 1 ELSE 0 END AS whatWasTheResult,
CASE WHEN risk_assessment->>'lastHivTestHadAnal' = 'true' THEN 1 ELSE 0 END AS lastHivTestHadAnal,
CASE WHEN risk_assessment->>'lastHivTestVaginalOral' = 'true' THEN 1 ELSE 0 END AS lastHivTestVaginalOral,
CASE WHEN risk_assessment->>'lastHivTestInjectedDrugs' = 'true' THEN 1 ELSE 0 END AS lastHivTestInjectedDrugs,
CASE WHEN risk_assessment->>'lastHivTestBasedOnRequest' = 'true' THEN 1 ELSE 0 END AS lastHivTestBasedOnRequest,
CASE WHEN risk_assessment->>'lastHivTestForceToHaveSex' = 'true' THEN 1 ELSE 0 END AS lastHivTestForceToHaveSex,
CASE WHEN risk_assessment->>'lastHivTestBloodTransfusion' = 'true' THEN 1 ELSE 0 END AS lastHivTestBloodTransfusion,
CASE WHEN risk_assessment->>'lastHivTestPainfulUrination' = 'true' THEN 1 ELSE 0 END AS lastHivTestPainfulUrination
FROM hts_risk_stratification
) totalRisk
group by person_uuid, totalrisk.diagnosedwithtb, totalrisk.lastHivTestDone ,totalrisk.whatWasTheResult,totalrisk.lastHivTestHadAnal,totalrisk.lastHivTestVaginalOral,
totalrisk.lastHivTestInjectedDrugs,totalrisk.lastHivTestBasedOnRequest, totalrisk.lastHivTestForceToHaveSex, totalrisk.lastHivTestBloodTransfusion, totalrisk.lastHivTestPainfulUrination
) riskScore ON riskScore.person_uuid = hc.person_uuid
WHERE hc.archived=0
  -- all facilities: datim_code filter removed
  AND hc.date_visit >='1980-01-01' AND hc.date_visit <= current_date
 GROUP BY hc.client_code,
        hc.person_uuid, htsPrevious.hiv_test_result,
        hc.extra,
        pp.first_name,
        pp.marital_status,
        pp.surname,
        pp.other_name,
        pp.sex,
        pp.date_of_birth,
        pp.contact_point,
        facility.name,
        state.name,
        lga.name,
        he.person_uuid,
        htsPrevious.date_visit,
        tg.display,
        rf.display,
        ts.display,
        tc.display,
        preg.display,
        it.display,
        hc.recency->>'optOutRTRI',
        hc.recency->>'rencencyId',
        hc.recency->>'optOutRTRITestName',
        hc.recency->>'finalRecencyResult',
        hc.recency->>'viralLoadResultClassification',
        hc.source, hc.risk_assessment,
        hc.referred_for_sti,
        hc.others->>'adhocCode', hc.testing_setting, hrs.testing_setting,
lgaofresidence.lgaofresidence, hc.date_created,
res_state.name, pp.uuid, pp.education, pp.employment_status, boui.code, hc.others, r.address, hc.date_visit, hc.first_time_visit, hc.num_children,
hc.num_wives, hc.index_client,hc.hiv_test_result, hc.prep_offered, hc.prep_accepted, hc.previously_tested, hrs.entry_point,
hc.recency, hc.risk_stratification_code, modality_code.display, hc.syphilis_testing,hc.hepatitis_testing, hc.cd4, hc.hiv_test_result2,
hc.test1, hc.post_test_counseling, riskscore.totalriskscore, htscounts.numberofcounts)
SELECT
 	datimCode,
	 patientid,
	 sex,
	  age,
	  maritalStatus,
	  LGAOfResidence,
    StateOfResidence,
	  dateVisit,
	  firstTimeVisit,
	   entryPoint,
	   indexclient,
	   previouslyTested,
	   targetGroup,
	   referredFrom,
	   testingSetting,
	  pepfarmodalities as  modality,
	     counselingType,
		 pregnancystatus,
		 indextype,
		 previouslytested as previousTestDate,
		 previousTestResult,
		 htsCount,
		  finalHIVTestResult,
		  dateOfHIVTesting,
		  prepOffered,
    prepAccepted

		 
	

FROM htsReport
WHERE rnkk = 1
  AND finalHIVTestResult IS NOT NULL
  AND finalHIVTestResult <> ''
  AND dateVisit BETWEEN
      CASE
          WHEN EXTRACT(MONTH FROM CURRENT_DATE) >= 10
              THEN MAKE_DATE(EXTRACT(YEAR FROM CURRENT_DATE)::INT, 10, 1)
          ELSE MAKE_DATE((EXTRACT(YEAR FROM CURRENT_DATE) - 1)::INT, 10, 1)
      END
      AND CURRENT_DATE;