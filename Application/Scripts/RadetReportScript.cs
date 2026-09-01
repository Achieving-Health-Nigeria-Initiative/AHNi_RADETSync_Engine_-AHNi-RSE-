using System.Data;
using AHNiRSE.Application.Mappers;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Application.Scripts;

/// <summary>
/// RADET report â€" six-sheet workbook.
///
/// Sheet 1 â€" RADET          (radet_query_new.sql       â€" typed model, strongly mapped)
/// Sheet 2 â€" HTS            (hts.sql                   â€" typed model, strongly mapped)
/// Sheet 3 â€" Index          (index_report.sql          â€" raw DataTable, column names = headers)
/// Sheet 4 â€" PMTCT HTS      (pmtct_hts_report.sql      â€" raw DataTable)
/// Sheet 5 â€" Maternal       (maternal_cohort_report.sql â€" raw DataTable)
/// Sheet 6 â€" Prep           (prep_query.sql            â€" raw DataTable)
///
/// Sheets 3-5 are NON-FATAL: if any SQL download or query fails the sheet is
/// written as empty ("No data") and the upload still completes.
///
/// DATE SAFETY â€" all DateTime.* calls replaced with CycleContext:
///   fileName  = $"..._{context.RunDate:yyyy_MM_dd}.xlsx"     â† WAT, locked at cycle start
///   blobDest  = $"RADET/{context.RunDate:yyyy-MM-dd}/..."    â† same value, guaranteed match
///   ResolveDates(cfg, context.WatToday)                      â† WAT, same source
///
/// Memory strategy:
///   â€¢ RADET and HTS DataTables disposed immediately after model-mapping.
///   â€¢ Index / PMTCTHTS / Maternal DataTables are held through ExportMultiSheetAsync
///     (ExcelExporter reads them column-by-column during workbook construction),
///     then disposed en-bloc before the upload stream allocates.
///   â€¢ The temp .xlsx is deleted after upload so %TEMP% stays clean.
/// </summary>
public class RadetReportScript(
    IStorageProvider storage,
    IDatabaseService database,
    IExcelExporter exporter,
    IOptions<ReportSettings> options,
    ILogger<RadetReportScript> logger) : IReportScript
{
    public string Name => "RADET";

    // â"€â"€ RADET header map â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
    private static readonly Dictionary<string, string> RadetHeaderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["State"] = "State",
            ["Lga"] = "L.G.A",
            ["LgaResidence"] = "LGA Of Residence",
            ["FacilityName"] = "Facility Name",
            ["DatimId"] = "DatimId",
            ["PatientId"] = "Patient ID",
            ["NdrPatientIdentifier"] = "NDR Patient Identifier",
            ["HospitalNumber"] = "Hospital Number",
            ["UniqueId"] = "Unique Id",
            ["HouseholdUniqueNo"] = "Household Unique No",
            ["OvcUniqueId"] = "OVC Unique ID",
            ["Sex"] = "Sex",
            ["CurrentWeightKg"] = "Current Weight (kg)",
            ["PregnancyStatus"] = "Pregnancy Status",
            ["DateOfBirth"] = "Date of Birth (yyyy-mm-dd)",
            ["Age"] = "Age",
            ["CareEntryPoint"] = "Care Entry Point",
            ["DateOfRegistration"] = "Date of Registration",
            ["EnrollmentDate"] = "Enrollment Date (yyyy-mm-dd)",
            ["ArtStartDate"] = "ART Start Date (yyyy-mm-dd)",
            ["LastPickupDate"] = "Last Pickup Date (yyyy-mm-dd)",
            ["MonthsOfArvRefill"] = "Months of ARV Refill",
            ["RegimenLineAtArtStart"] = "Regimen Line at ART Start",
            ["RegimenAtArtStart"] = "Regimen at ART Start",
            ["DateStartCurrentArtRegimen"] = "Date of Start of Current ART Regimen",
            ["CurrentRegimenLine"] = "Current Regimen Line",
            ["CurrentArtRegimen"] = "Current ART Regimen",
            ["ClinicalStagingAtLastVisit"] = "Clinical Staging at Last Visit",
            ["DateLastCd4Count"] = "Date of Last CD4 Count",
            ["LastCd4Count"] = "Last CD4 Count",
            ["DateVlSampleCollection"] = "Date of Viral Load Sample Collection (yyyy-mm-dd)",
            ["DateCurrentVlSample"] = "Date of Current ViralLoad Result Sample (yyyy-mm-dd)",
            ["CurrentViralLoad"] = "Current Viral Load (c/ml)",
            ["DateCurrentViralLoad"] = "Date of Current Viral Load (yyyy-mm-dd)",
            ["ViralLoadIndication"] = "Viral Load Indication",
            ["ViralLoadEligibilityStatus"] = "Viral Load Eligibility Status",
            ["DateVlEligibilityStatus"] = "Date of Viral Load Eligibility Status",
            ["CurrentArtStatus"] = "Current ART Status",
            ["DateCurrentArtStatus"] = "Date of Current ART Status",
            ["ClientVerificationOutcome"] = "Client Verification Outcome",
            ["CauseOfDeath"] = "Cause of Death",
            ["VaCauseOfDeath"] = "VA Cause of Death",
            ["PreviousArtStatus"] = "Previous ART Status",
            ["ConfirmedDatePreviousArtStatus"] = "Confirmed Date of Previous ART Status",
            ["ArtEnrollmentSetting"] = "ART Enrollment Setting",
            ["DateTbScreening"] = "Date of TB Screening (yyyy-mm-dd)",
            ["TbScreeningType"] = "TB Screening Type",
            ["CadScore"] = "CAD Score",
            ["TbStatus"] = "TB Status",
            ["DateTbSampleCollection"] = "Date of TB Sample Collection (yyyy-mm-dd)",
            ["TbDiagnosticTestType"] = "TB Diagnostic Test Type",
            ["DateTbResultReceived"] = "Date of TB Diagnostic Result Received (yyyy-mm-dd)",
            ["TbDiagnosticResult"] = "TB Diagnostic Result",
            ["DateXrayTbDiagnosis"] = "Date of Additional TB Diagnosis Result using XRAY",
            ["XrayTbResult"] = "Additional TB Diagnosis Result using XRAY",
            ["DateTbTreatmentStart"] = "Date of Start of TB Treatment (yyyy-mm-dd)",
            ["TbType"] = "TB Type (new, relapsed etc)",
            ["DateTbTreatmentCompletion"] = "Date of Completion of TB Treatment (yyyy-mm-dd)",
            ["TbTreatmentOutcome"] = "TB Treatment Outcome",
            ["EligibleForTpt"] = "Eligible for TPT",
            ["DateTptStart"] = "Date of TPT Start (yyyy-mm-dd)",
            ["TptType"] = "TPT Type",
            ["TptCompletionDate"] = "TPT Completion Date (yyyy-mm-dd)",
            ["TptCompletionStatus"] = "TPT Completion Status",
            ["DateEacStart"] = "Date of Commencement of EAC (yyyy-mm-dd)",
            ["NumberOfEacSessions"] = "Number of EAC Sessions Completed",
            ["DateLastEacSession"] = "Date of Last EAC Session Completed",
            ["DateExtendedEacCompletion"] = "Date of Extended EAC Completion (yyyy-mm-dd)",
            ["DateRepeatVlSample"] = "Date of Repeat Viral Load - Post EAC VL Sample Collected (yyyy-mm-dd)",
            ["RepeatViralLoadPostEac"] = "Repeat Viral Load Result (c/ml) - POST EAC",
            ["DateRepeatVlResult"] = "Date of Repeat Viral Load Result - POST EAC VL",
            ["DateDevolvement"] = "Date of Devolvement",
            ["ModelDevolvedTo"] = "Model Devolved To",
            ["DateCurrentDsd"] = "Date of Current DSD",
            ["CurrentDsdModel"] = "Current DSD Model",
            ["CurrentDsdOutlet"] = "Current DSD Outlet",
            ["DateReturnToFacility"] = "Date of Return of DSD Client to Facility (yyyy-mm-dd)",
            ["ScreeningForChronicConditions"] = "Screening for Chronic Conditions",
            ["CoMorbidities"] = "Co-morbidities",
            ["DateCervicalCancerScreening"] = "Date of Cervical Cancer Screening (yyyy-mm-dd)",
            ["CervicalCancerScreeningType"] = "Cervical Cancer Screening Type",
            ["CervicalCancerScreeningMethod"] = "Cervical Cancer Screening Method",
            ["CervicalCancerScreeningResult"] = "Result of Cervical Cancer Screening",
            ["DatePrecancerTreatment"] = "Date of Precancerous Lesions Treatment (yyyy-mm-dd)",
            ["PrecancerTreatmentMethod"] = "Precancerous Lesions Treatment Methods",
            ["DateBiometricsEnrolled"] = "Date Biometrics Enrolled (yyyy-mm-dd)",
            ["FingersCaptured"] = "Number of Fingers Captured",
            ["DateBiometricsRecapture"] = "Date Biometrics Recapture (yyyy-mm-dd)",
            ["FingersRecaptured"] = "Number of Fingers Recaptured",
            ["CaseManager"] = "Case Manager",
        };

    // ── PMTCT HTS header map (16 columns — matches pmtct_hts_report.sql final SELECT) ──────────────────
    private static readonly Dictionary<string, string> PmtctHtsHeaderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["datimCode"]                                  = "datimCode",
            ["patientId"]                                  = "Patient ID",
            ["motherHospitalNum"]                          = "Mother's Hospital Num",
            ["age"]                                        = "Age",
            ["ancSetting"]                                 = "ANC Setting",
            ["modality"]                                   = "Modality",
            ["pointOfEntry"]                               = "Point of Entry",
            ["dateOfRegistrationInIndexPregnancy"]         = "Date of registration in index pregnancy",
            ["gestationalAgeWeeksAtFirstAncVisit"]         = "Gestational Age (Weeks) @ First ANC visit",
            ["dateTestedForHiv"]                           = "Date Tested for HIV",
            ["hivTestResult"]                              = "HIV Test Result",
            ["syphillisTestResult"]                        = "Syphillis Test Result",
            ["dateOfMaternalRetesting"]                    = "Date Of Maternal Retesting",
            ["previouslyKnownHivPositiveStatus"]           = "Previously Known HIV +Ve Status",
            ["facilityEnrolledIn"]                         = "Facility Enrolled In",
            ["linkedToSyphilisTreatment"]                  = "Linked to Syphilis Treatment",
        };

    // ── Maternal Cohort header map (48 columns — matches maternal_cohort_report.sql final SELECT) ────
    private static readonly Dictionary<string, string> MaternalCohortHeaderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["datimCode"]                                         = "datimCode",
            ["patientId"]                                         = "Patient ID",
            ["motherHospitalNumber"]                              = "Mother's Hospital Number",
            ["age"]                                               = "Age",
            ["ancSetting"]                                        = "ANC Setting",
            ["pointOfEntry"]                                      = "Point of Entry",
            ["dateOfInitialVisit"]                                = "Date of Initial Visit",
            ["dateOfIndexAncRegistration"]                        = "Date of Index ANC Registration",
            ["syphilisTestResult"]                                = "Syphilis Test Result",
            ["treatedForSyphilis"]                                = "Treated for Syphilis",
            ["tbScreeningStatus"]                                 = "TB screening Status",
            ["motherArtStartDate"]                                = "Mother's ART Start Date",
            ["timingOfArtInitiationInMother"]                     = "Timing of ART initiation in mother",
            ["gaAtLastVisitWeeks"]                                = "GA at last visit (weeks)",
            ["motherCurrentArtStatus"]                            = "Mother's Current ART Status",
            ["dueDateForVlSampleCollectionAt32Weeks"]             = "Due Date for VL Sample collection @ 32 weeks",
            ["dateForVlSampleCollectionAt32Weeks"]                = "Date for VL sample collection @ 32 weeks",
            ["currentViralLoadResult"]                            = "Current Viral load Result",
            ["dateOfCurrentVl"]                                   = "Date of Current VL",
            ["dateOfDelivery"]                                    = "Date of Delivery",
            ["placeOfDelivery"]                                   = "Place of Delivery",
            ["modeOfDelivery"]                                    = "Mode of Delivery",
            ["fetalOutcomeChildStatus"]                           = "Fetal outcome (Child status)",
            ["childHospitalIdNumber"]                             = "Child's hospital ID number",
            ["sexChild"]                                          = "Sex - Child",
            ["birthWeight"]                                       = "Birth Weight",
            ["dateOfArvProphylaxisCommencement"]                  = "Date of ARV Prophylaxis Commencemment",
            ["typeOfProphylaxis"]                                 = "Type of Prophylaxis (ePNP or regular)",
            ["dateOfCtxCotrimoxazole"]                            = "Date of CTX (Cotrimoxazole)",
            ["currentInfantFeedingOptions"]                       = "Current infant feeding options",
            ["dateOfFirstDnaPcrSampleCollection"]                 = "Date of First DNA PCR Sample collection",
            ["resultOfFirstDnaPcrTest"]                           = "Result of first DNA PCR test",
            ["dateFirstDnaPcrResultWasReceived"]                  = "Date first DNA PCR result was received",
            ["dateOfSecondDnaPcrTestSampleCollection"]            = "Date of Second DNA PCR test sample collection",
            ["resultOfSecondDnaPcrTest"]                          = "Result of second DNA PCR test",
            ["dateSecondDnaPcrResultWasReceived"]                 = "Date second DNA PCR result was received",
            ["dateOfThirdDnaPcrTestSampleCollection"]             = "Date of third DNA PCR test sample collection",
            ["resultOfThirdDnaPcrTest"]                           = "Result of third DNA PCR test",
            ["dateThirdDnaPcrResultWasReceived"]                  = "Date third DNA PCR result was received",
            ["dateOfFourthDnaPcrTestSampleCollection"]            = "Date of fourth DNA PCR test sample collection",
            ["resultOfFourthDnaPcrTest"]                          = "Result of fourth DNA PCR test",
            ["dateFourthDnaPcrResultWasReceived"]                 = "Date fourth DNA PCR result was received",
            ["dateOfConfirmatoryDnaPcrTestSampleCollection"]      = "Date of confirmatory DNA PCR test sample collection",
            ["resultOfConfirmatoryDnaPcrTest"]                    = "Result of confirmatory DNA PCR test",
            ["dateConfirmatoryDnaPcrResultWasReceived"]           = "Date confirmatory DNA PCR result was received",
            ["dateOfChildFinalOutcomeTest"]                       = "Date of Child Final Outcome test",
            ["resultChildFinalOutcome"]                           = "Result (Child Final Outcome)",
            ["childArtStartDate"]                                 = "Child's ART Start Date",
        };

    // -- Prep header map (91 columns -- matches prep_query.sql final SELECT) ------------------
    private static readonly Dictionary<string, string> PrepHeaderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Facility Id (Datim)"]                          = "Facility Id (Datim)",
            ["State"]                                        = "State",
            ["LGA"]                                          = "LGA",
            ["Facility Name"]                                = "Facility Name",
            ["Patient Identifier"]                           = "Patient Identifier",
            ["Hospital Number"]                              = "Hospital Number",
            ["HTS Client Code"]                              = "HTS Client Code",
            ["Sex"]                                          = "Sex",
            ["Age"]                                          = "Age",
            ["Date Of Birth (yyyy-mm-dd)"]                   = "Date Of Birth (yyyy-mm-dd)",
            ["Phone Number"]                                 = "Phone Number",
            ["Marital Status"]                               = "Marital Status",
            ["LGA of Residence"]                             = "LGA of Residence",
            ["State Of Residence"]                           = "State Of Residence",
            ["Education"]                                    = "Education",
            ["Occupation"]                                   = "Occupation",
            ["Pregnancy Status"]                             = "Pregnancy Status",
            ["PrEP Setting"]                                 = "PrEP Setting",
            ["Population Type"]                              = "Population Type",
            ["Visit Type"]                                   = "Visit Type",
            ["Service Status"]                               = "Service Status",
            ["Eligible for PrEP"]                            = "Eligible for PrEP",
            ["Date Eligible for PrEP"]                       = "Date Eligible for PrEP",
            ["Offered PrEP"]                                 = "Offered PrEP",
            ["Date offered PrEP"]                            = "Date offered PrEP",
            ["Willing to Commence PREP"]                     = "Willing to Commence PREP",
            ["Date willing to commence PrEP"]                = "Date willing to commence PrEP",
            ["Reasons for Declining PrEP"]                   = "Reasons for Declining PrEP",
            ["Date Of Registration (yyyy-mm-dd)"]            = "Date Of Registration (yyyy-mm-dd)",
            ["Date Of Commencement (yyyy-mm-dd)"]            = "Date Of Commencement (yyyy-mm-dd)",
            ["Baseline Regimen"]                             = "Baseline Regimen",
            ["Prep Type"]                                    = "Prep Type",
            ["Previous clinic PrEP Type"]                    = "Previous clinic PrEP Type",
            ["Baseline Weight (kg)"]                         = "Baseline Weight (kg)",
            ["Baseline Height (cm)"]                         = "Baseline Height (cm)",
            ["Baseline Creatinine"]                          = "Baseline Creatinine",
            ["Baseline Hepatitis B"]                         = "Baseline Hepatitis B",
            ["Baseline Hepatitis C"]                         = "Baseline Hepatitis C",
            ["HIV status at PrEP Initiation"]                = "HIV status at PrEP Initiation",
            ["Baseline Urinalysis"]                          = "Baseline Urinalysis",
            ["Baseline Urinalysis Date"]                     = "Baseline Urinalysis Date",
            ["Baseline Liver Function Test"]                 = "Baseline Liver Function Test",
            ["Baseline Liver Function Test Date"]            = "Baseline Liver Function Test Date",
            ["Baseline AST"]                                 = "Baseline AST",
            ["Baseline ALT"]                                 = "Baseline ALT",
            ["Baseline HBsAG"]                               = "Baseline HBsAG",
            ["Baseline HB/PCV"]                              = "Baseline HB/PCV",
            ["Baseline WBC"]                                 = "Baseline WBC",
            ["Baseline Chest Xray"]                          = "Baseline Chest Xray",
            ["Baseline Lipid Profile"]                       = "Baseline Lipid Profile",
            ["Previous clinic Visit Regimen"]                = "Previous clinic Visit Regimen",
            ["Current Prep Type"]                            = "Current Prep Type",
            ["Current Regimen"]                              = "Current Regimen",
            ["Drug refill period (duration)"]                = "Drug refill period (duration)",
            ["Date Of Last Pickup (yyyy-mm-dd)"]             = "Date Of Last Pickup (yyyy-mm-dd)",
            ["Date of Previous Visit"]                       = "Date of Previous Visit",
            ["Current Status"]                               = "Current Status",
            ["Date Of Current Status (yyyy-mm-dd)"]          = "Date Of Current Status (yyyy-mm-dd)",
            ["Previous Status"]                              = "Previous Status",
            ["Previous Status Date"]                         = "Previous Status Date",
            ["History of Drug-Drug Interactions"]            = "History of Drug-Drug Interactions",
            ["Current Weight (kg)"]                          = "Current Weight (kg)",
            ["Current Height (cm)"]                          = "Current Height (cm)",
            ["Current HIV Status"]                           = "Current HIV Status",
            ["Date of Current HIV Status (yyyy-mm-dd)"]      = "Date of Current HIV Status (yyyy-mm-dd)",
            ["Current Urinalysis"]                           = "Current Urinalysis",
            ["Date of Current Urinalysis"]                   = "Date of Current Urinalysis",
            ["Date of Current ALT"]                          = "Date of Current ALT",
            ["Current ALT"]                                  = "Current ALT",
            ["Date of Current HBsAG"]                        = "Date of Current HBsAG",
            ["Current HBsAG"]                                = "Current HBsAG",
            ["Date of Current HB/PCV"]                       = "Date of Current HB/PCV",
            ["Current HB/PCV"]                               = "Current HB/PCV",
            ["Date of Current WBC"]                          = "Date of Current WBC",
            ["Current WBC"]                                  = "Current WBC",
            ["Date of Current Liver Function Test"]          = "Date of Current Liver Function Test",
            ["Current Liver Function Test"]                  = "Current Liver Function Test",
            ["Date of Current AST"]                          = "Date of Current AST",
            ["Current AST"]                                  = "Current AST",
            ["Date of Current Creatinine"]                   = "Date of Current Creatinine",
            ["Current Creatinine"]                           = "Current Creatinine",
            ["Date of Current Chest Xray"]                   = "Date of Current Chest Xray",
            ["Current Chest Xray"]                           = "Current Chest Xray",
            ["Date of Current Lipid Profile"]                = "Date of Current Lipid Profile",
            ["Current Lipid Profile"]                        = "Current Lipid Profile",
            ["PrEP Discontinuation Type"]                    = "PrEP Discontinuation Type",
            ["Reasons for discontinuation/Stopped"]          = "Reasons for discontinuation/Stopped",
            ["Date of Discontinuation/Stopped"]              = "Date of Discontinuation/Stopped",
            ["Date Of HIV Enrollment (yyyy-mm-dd)"]          = "Date Of HIV Enrollment (yyyy-mm-dd)",
            ["Healthcare Worker"]                            = "Healthcare Worker",
            ["Noted Side Effects"]                           = "Noted Side Effects",
        };

    // -- HTS header map -----------------------------------------------------------------------
    private static readonly Dictionary<string, string> HtsHeaderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DatimCode"] = "datimcode",
            ["PatientId"] = "patientid",
            ["ClientCode"] = "clientcode",
            ["Sex"] = "sex",
            ["Age"] = "age",
            ["MaritalStatus"] = "maritalstatus",
            ["LgaOfResidence"] = "lgaofresidence",
            ["StateOfResidence"] = "stateofresidence",
            ["DateVisit"] = "datevisit",
            ["FirstTimeVisit"] = "firsttimevisit",
            ["EntryPoint"] = "entrypoint",
            ["IndexClient"] = "indexclient",
            ["PreviouslyTested"] = "previouslytested",
            ["TargetGroup"] = "targetgroup",
            ["Source"] = "source",
            ["TestingSetting"] = "testingsetting",
            ["Modality"] = "modality",
            ["CounselingType"] = "counselingtype",
            ["PregnancyStatus"] = "pregnancystatus",
            ["IndexTesting"] = "indextesting",
            ["PreviousTestDate"] = "previoustestdate",
            ["PreviousTestResult"] = "previoustestresult",
            ["HtsCount"] = "htscount",
            ["FinalHivTestResult"] = "finalhivtestresult",
            ["DateOfHivTesting"] = "dateofhivtesting",
            ["PrepOffered"] = "prepoffered",
            ["PrepAccepted"] = "prepaccepted",
            ["PatientUuid"] = "patientUuid",
            ["FirstName"] = "firstName",
            ["Surname"] = "surname",
            ["OtherName"] = "otherName",
            ["PhoneNumber"] = "phoneNumber",
            ["ClientAddress"] = "clientAddress",
            ["Latitude"] = "latitude",
            ["Longitude"] = "longitude",
        };

    // â"€â"€ Main entry point â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
 #if false
    public async Task<(int RowCount, string UploadUrl)> RunAsync(
        CycleContext context,
        CancellationToken ct = default)
    {
        // â"€â"€ 1. Config â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        var cfg = options.Value.Scripts
            .FirstOrDefault(s => string.Equals(s.Name, Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No '{Name}' config in Reports:Scripts.");

        var htsCfg = FindScript("HTS");
        var indexCfg = FindScript("Index");
        var pmtctCfg = FindScript("PMTCTHTS");
        var maternalCfg = FindScript("Maternal");

        // â"€â"€ 2. Reporting period â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        var (startDate, endDate) = ResolveDates(cfg, context.WatToday);

        logger.LogInformation(
            "[RADET] CycleId={Id} | RunDate={RunDate} | WatToday={WatToday} | Period={Start}â†’{End}",
            context.CycleId,
            context.RunDate.ToString("yyyy-MM-dd"),
            context.WatToday.ToString("yyyy-MM-dd"),
            startDate, endDate);

        // â"€â"€ 3. Facility identity â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        var facility = database.GetFacilityInfo();
        var safeName = SanitizeName(facility.FacilityName);

        // â"€â"€ 4. File paths â€" both derived from context.RunDate â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        var dateSuffix = context.RunDate.ToString("yyyy_MM_dd");
        var folderDate = context.RunDate.ToString("yyyy-MM-dd");
        var fileName = $"{safeName}_{dateSuffix}.xlsx";
        var localOutput = Path.Combine(
            Path.GetTempPath(), "AHNi-RSE", context.CycleId, fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(localOutput)!);

        using var parallelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queryParameters = new QueryParameters
        {
            FacilityId = cfg.FacilityId,
            StartDate = startDate,
            EndDate = endDate
        };

        var htsTask = RunNonFatalHtsQueryAsync(
            htsCfg?.BlobPath ?? "hts/hts.sql",
            parallelCts.Token);
        var indexTask = RunNonFatalDataTableQueryAsync(
            "Index",
            indexCfg?.BlobPath ?? "index_report.sql",
            queryParameters,
            parallelCts.Token);
        var pmtctTask = RunNonFatalDataTableQueryAsync(
            "PMTCT HTS",
            pmtctCfg?.BlobPath ?? "pmtct_hts_report.sql",
            queryParameters,
            parallelCts.Token);
        var maternalTask = RunNonFatalDataTableQueryAsync(
            "Maternal Cohort",
            maternalCfg?.BlobPath ?? "maternal_cohort_report.sql",
            queryParameters,
            parallelCts.Token);

        // â"€â"€ 5. RADET query (Sheet 1 â€" fatal if it fails) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        List<Radet> radetRows;
        int radetCount;
        try
        {
            var radetSqlPath = await storage.DownloadAsync(cfg.BlobPath, ct);
            if (!File.Exists(radetSqlPath))
                throw new FileNotFoundException($"RADET SQL missing: {cfg.BlobPath}");

            var radetSql = await File.ReadAllTextAsync(radetSqlPath, ct);
            var radetData = await database.ExecuteScriptAsync(radetSql, queryParameters, ct);
            radetRows = RadetMapper.Map(radetData);
            radetCount = radetData.Rows.Count;
            radetData.Dispose();
            radetData = null!;
        }
        catch
        {
            parallelCts.Cancel();
            await AwaitBackgroundQueriesSafelyAsync(htsTask, indexTask, pmtctTask, maternalTask, prepTask);
            throw;
        }

        logger.LogInformation("[RADET] RADET query returned {Rows} row(s).", radetCount);
        if (radetCount == 0)
            logger.LogWarning(
                "[RADET] Zero rows â€" FacilityId={Fid}, Period={Start}â†’{End}. " +
                "Verify facility has active patients for this period.",
                cfg.FacilityId, startDate, endDate);

        // â"€â"€ 6. HTS query (Sheet 2 â€" non-fatal) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        List<Hts> htsRows;
        int htsCount = 0;
        try
        {
            var htsBlobPath = htsCfg?.BlobPath ?? "hts/hts.sql";
            var htsSqlPath = await storage.DownloadAsync(htsBlobPath, ct);
            if (!File.Exists(htsSqlPath))
                throw new FileNotFoundException($"HTS SQL missing: {htsBlobPath}");

            var htsSql = await File.ReadAllTextAsync(htsSqlPath, ct);
            var htsData = await database.ExecuteScriptAsync(htsSql, ct);
            htsCount = htsData.Rows.Count;
            htsRows = HtsMapper.Map(htsData);
            htsData.Dispose();
            htsData = null!;

            logger.LogInformation("[RADET] HTS query returned {Rows} row(s).", htsCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] HTS query failed â€" Sheet 2 will be empty.");
            htsRows = [];
        }

        // â"€â"€ 7. Index query (Sheet 3 â€" non-fatal) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        DataTable? indexData = null;
        int indexCount = 0;
        try
        {
            var blobPath = indexCfg?.BlobPath ?? "index_report.sql";
            var sqlPath = await storage.DownloadAsync(blobPath, ct);
            if (!File.Exists(sqlPath))
                throw new FileNotFoundException($"Index SQL missing: {blobPath}");

            var sql = await File.ReadAllTextAsync(sqlPath, ct);
            indexData = await database.ExecuteScriptAsync(sql, new QueryParameters
            {
                FacilityId = cfg.FacilityId,
                StartDate = startDate,
                EndDate = endDate
            }, ct);
            indexCount = indexData.Rows.Count;
            logger.LogInformation("[RADET] Index query returned {Rows} row(s).", indexCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] Index query failed â€" Sheet 3 will be empty.");
            indexData = null;
        }

        // â"€â"€ 8. PMTCT HTS query (Sheet 4 â€" non-fatal) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        DataTable? pmtctHtsData = null;
        int pmtctHtsCount = 0;
        try
        {
            var blobPath = pmtctCfg?.BlobPath ?? "pmtct_hts_report.sql";
            var sqlPath = await storage.DownloadAsync(blobPath, ct);
            if (!File.Exists(sqlPath))
                throw new FileNotFoundException($"PMTCT HTS SQL missing: {blobPath}");

            var sql = await File.ReadAllTextAsync(sqlPath, ct);
            pmtctHtsData = await database.ExecuteScriptAsync(sql, new QueryParameters
            {
                FacilityId = cfg.FacilityId,
                StartDate = startDate,
                EndDate = endDate
            }, ct);
            pmtctHtsCount = pmtctHtsData.Rows.Count;
            logger.LogInformation("[RADET] PMTCT HTS query returned {Rows} row(s).", pmtctHtsCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] PMTCT HTS query failed â€" Sheet 4 will be empty.");
            pmtctHtsData = null;
        }

        // â"€â"€ 9. Maternal Cohort query (Sheet 5 â€" non-fatal) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        DataTable? maternalData = null;
        int maternalCount = 0;
        try
        {
            var blobPath = maternalCfg?.BlobPath ?? "maternal_cohort_report.sql";
            var sqlPath = await storage.DownloadAsync(blobPath, ct);
            if (!File.Exists(sqlPath))
                throw new FileNotFoundException($"Maternal SQL missing: {blobPath}");

            var sql = await File.ReadAllTextAsync(sqlPath, ct);
            maternalData = await database.ExecuteScriptAsync(sql, new QueryParameters
            {
                FacilityId = cfg.FacilityId,
                StartDate = startDate,
                EndDate = endDate
            }, ct);
            maternalCount = maternalData.Rows.Count;
            logger.LogInformation("[RADET] Maternal Cohort query returned {Rows} row(s).", maternalCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] Maternal Cohort query failed â€" Sheet 5 will be empty.");
            maternalData = null;
        }

        // â"€â"€ 10. Build five-sheet workbook â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        //
        // Sheets 1-2: typed models (reflection + HeaderMap)
        // Sheets 3-5: raw DataTable (column names become headers automatically)
        //
        // DataTable sheets are held in memory through ExportMultiSheetAsync â€"
        // ClosedXML reads them cell-by-cell while building the workbook.
        // They are disposed en-bloc immediately after SaveAs() returns.
        var sheets = new List<ExcelSheetDefinition>
        {
            // Sheet 1 â€" RADET (typed)
            new(SheetName: "RADET",
                Rows:      radetRows.Cast<object>().ToList(),
                HeaderMap: RadetHeaderMap),

            // Sheet 2 â€" HTS (typed)
            new(SheetName: "HTS",
                Rows:      htsRows.Cast<object>().ToList(),
                HeaderMap: HtsHeaderMap),

            // Sheet 3 â€" Index (raw DataTable â€" null â†’ "No data" placeholder)
            new(SheetName: "Index",
                RawData:   indexData),

            // Sheet 4 â€" PMTCT HTS (raw DataTable; HeaderMap used as fallback headers when RawData is null)
            new(SheetName: "PMTCT HTS",
                RawData:   pmtctHtsData,
                HeaderMap: PmtctHtsHeaderMap),

            // Sheet 5 â€" Maternal Cohort (raw DataTable; HeaderMap used as fallback headers when RawData is null)
            new(SheetName: "Maternal Cohort",
                RawData:   maternalData,
                HeaderMap: MaternalCohortHeaderMap),
        };

        await exporter.ExportMultiSheetAsync(sheets, localOutput, ct);

        // â"€â"€ Free all data before the upload stream allocates â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        radetRows = null!;
        htsRows = null!;
        indexData?.Dispose();
        pmtctHtsData?.Dispose();
        maternalData?.Dispose();
        indexData = pmtctHtsData = maternalData = null;

        // â"€â"€ 11. Upload â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        var blobDest = $"RADET/{folderDate}/{fileName}";
        var storagePath = await storage.UploadAsync(localOutput, blobDest, ct);

        TryDeleteTempFile(localOutput);

        logger.LogInformation(
            "[RADET] âœ" CycleId={Id} | RADET={R} | HTS={H} | Index={I} | PMTCTHTS={P} | Maternal={M} rows | {Dest}",
            context.CycleId, radetCount, htsCount, indexCount, pmtctHtsCount, maternalCount, blobDest);

        return (radetCount, storagePath);
    }

    // â"€â"€ Helpers â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

 #endif

    public async Task<(int RowCount, string UploadUrl)> RunAsync(
        CycleContext context,
        CancellationToken ct = default)
    {
        var cfg = options.Value.Scripts
            .FirstOrDefault(s => string.Equals(s.Name, Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No '{Name}' config in Reports:Scripts.");

        var htsCfg = FindScript("HTS");
        var indexCfg = FindScript("Index");
        var pmtctCfg = FindScript("PMTCTHTS");
        var maternalCfg = FindScript("Maternal");
        var prepCfg = FindScript("Prep");
        var ahdCfg  = FindScript("AHD");

        var (startDate, endDate) = ResolveDates(cfg, context.WatToday);

        logger.LogInformation(
            "[RADET] CycleId={Id} | RunDate={RunDate} | WatToday={WatToday} | Period={Start}->{End}",
            context.CycleId,
            context.RunDate.ToString("yyyy-MM-dd"),
            context.WatToday.ToString("yyyy-MM-dd"),
            startDate,
            endDate);

        var facility = database.GetFacilityInfo();
        var safeName = SanitizeName(facility.FacilityName);
        var dateSuffix = context.RunDate.ToString("yyyy_MM_dd");
        var folderDate = context.RunDate.ToString("yyyy-MM-dd");
        var fileName = $"{safeName}_{dateSuffix}.xlsx";
        var localOutput = Path.Combine(Path.GetTempPath(), "AHNi-RSE", context.CycleId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(localOutput)!);

        using var parallelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queryParameters = new QueryParameters
        {
            FacilityId = cfg.FacilityId,
            StartDate = startDate,
            EndDate = endDate
        };

        var htsTask = RunNonFatalHtsQueryAsync(
            htsCfg?.BlobPath ?? "hts/hts.sql",
            parallelCts.Token);
        var indexTask = RunNonFatalDataTableQueryAsync(
            "Index",
            indexCfg?.BlobPath ?? "index_report.sql",
            queryParameters,
            parallelCts.Token);
        var pmtctTask = RunNonFatalDataTableQueryAsync(
            "PMTCT HTS",
            pmtctCfg?.BlobPath ?? "pmtct_hts_report.sql",
            queryParameters,
            parallelCts.Token);
        var maternalTask = RunNonFatalDataTableQueryAsync(
            "Maternal Cohort",
            maternalCfg?.BlobPath ?? "maternal_cohort_report.sql",
            queryParameters,
            parallelCts.Token);
        var prepTask = RunNonFatalDataTableQueryAsync(
            "Prep",
            prepCfg?.BlobPath ?? "prep_query.sql",
            queryParameters,
            parallelCts.Token);
        var ahdTask = RunNonFatalDataTableQueryAsync(
            "AHD",
            ahdCfg?.BlobPath ?? "ahd_query_raw.sql",
            queryParameters,
            parallelCts.Token);

        List<Radet> radetRows;
        int radetCount;
        try
        {
            var radetSqlPath = await storage.DownloadAsync(cfg.BlobPath, ct);
            if (!File.Exists(radetSqlPath))
                throw new FileNotFoundException($"RADET SQL missing: {cfg.BlobPath}");

            var radetSql = await File.ReadAllTextAsync(radetSqlPath, ct);
            using var radetData = await database.ExecuteScriptAsync(radetSql, queryParameters, ct);
            radetRows = RadetMapper.Map(radetData);
            radetCount = radetData.Rows.Count;
        }
        catch
        {
            parallelCts.Cancel();
            await AwaitBackgroundQueriesSafelyAsync(htsTask, indexTask, pmtctTask, maternalTask, prepTask, ahdTask);
            throw;
        }

        logger.LogInformation("[RADET] RADET query returned {Rows} row(s).", radetCount);
        if (radetCount == 0)
        {
            logger.LogWarning(
                "[RADET] Zero rows - FacilityId={Fid}, Period={Start}->{End}. Verify facility has active patients for this period.",
                cfg.FacilityId,
                startDate,
                endDate);
        }

        var htsResult = await htsTask;
        var indexResult = await indexTask;
        var pmtctResult = await pmtctTask;
        var maternalResult = await maternalTask;
        var prepResult = await prepTask;
        var ahdResult = await ahdTask;

        var sheets = new List<ExcelSheetDefinition>
        {
            new(SheetName: "RADET",
                Rows:      radetRows.Cast<object>().ToList(),
                HeaderMap: RadetHeaderMap),
            new(SheetName: "HTS",
                Rows:      htsResult.Rows.Cast<object>().ToList(),
                HeaderMap: HtsHeaderMap),
            new(SheetName: "Index",           RawData: indexResult.Data),
            new(SheetName: "PMTCT HTS",       RawData: pmtctResult.Data,    HeaderMap: PmtctHtsHeaderMap),
            new(SheetName: "Maternal Cohort", RawData: maternalResult.Data, HeaderMap: MaternalCohortHeaderMap),
            new(SheetName: "Prep",            RawData: prepResult.Data,    HeaderMap: PrepHeaderMap),
            new(SheetName: "AHD",             RawData: ahdResult.Data),
        };

        try
        {
            await exporter.ExportMultiSheetAsync(sheets, localOutput, ct);
            var blobDest = $"RADET/{folderDate}/{fileName}";
            var storagePath = await storage.UploadAsync(localOutput, blobDest, ct);
            TryDeleteTempFile(localOutput);

            logger.LogInformation(
                "[RADET] COMPLETE CycleId={Id} | RADET={R} | HTS={H} | Index={I} | PMTCTHTS={P} | Maternal={M} | Prep={Pr} | AHD={Ahd} rows | {Dest}",
                context.CycleId,
                radetCount,
                htsResult.Count,
                indexResult.Count,
                pmtctResult.Count,
                maternalResult.Count,
                prepResult.Count,
                ahdResult.Count,
                blobDest);

            return (radetCount, storagePath);
        }
        finally
        {
            indexResult.Data?.Dispose();
            pmtctResult.Data?.Dispose();
            maternalResult.Data?.Dispose();
            prepResult.Data?.Dispose();
            ahdResult.Data?.Dispose();
        }
    }

    private async Task<(List<Hts> Rows, int Count)> RunNonFatalHtsQueryAsync(
        string blobPath,
        CancellationToken ct)
    {
        try
        {
            var sqlPath = await storage.DownloadAsync(blobPath, ct);
            if (!File.Exists(sqlPath))
                throw new FileNotFoundException($"HTS SQL missing: {blobPath}");

            var sql = await File.ReadAllTextAsync(sqlPath, ct);
            using var data = await database.ExecuteScriptAsync(sql, ct);
            var count = data.Rows.Count;
            var rows = HtsMapper.Map(data);
            logger.LogInformation("[RADET] HTS query returned {Rows} row(s).", count);
            return (rows, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] HTS query failed - Sheet 2 will be empty.");
            return ([], 0);
        }
    }

    private async Task<(DataTable? Data, int Count)> RunNonFatalDataTableQueryAsync(
        string sheetName,
        string blobPath,
        QueryParameters parameters,
        CancellationToken ct)
    {
        try
        {
            var sqlPath = await storage.DownloadAsync(blobPath, ct);
            if (!File.Exists(sqlPath))
                throw new FileNotFoundException($"{sheetName} SQL missing: {blobPath}");

            var sql = await File.ReadAllTextAsync(sqlPath, ct);
            var data = await database.ExecuteScriptAsync(sql, parameters, ct);
            var count = data.Rows.Count;
            logger.LogInformation("[RADET] {Sheet} query returned {Rows} row(s).", sheetName, count);
            return (data, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] {Sheet} query failed - sheet will be empty.", sheetName);
            return (null, 0);
        }
    }

    private static async Task AwaitBackgroundQueriesSafelyAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private ScriptConfig? FindScript(string name) =>
        options.Value.Scripts.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes the default reporting period (full previous calendar month).
    /// Always receives watToday from CycleContext â€" never DateTime.UtcNow / Today.
    /// </summary>
    private static (DateOnly start, DateOnly end) ResolveDates(ScriptConfig cfg, DateOnly watToday)
    {
        var lastMonth = watToday.AddMonths(-1);
        var defaultStart = new DateOnly(lastMonth.Year, lastMonth.Month, 1);
        var defaultEnd = defaultStart.AddMonths(1).AddDays(-1);

        var start = string.IsNullOrWhiteSpace(cfg.StartDate)
            ? defaultStart : DateOnly.Parse(cfg.StartDate);

        var end = string.IsNullOrWhiteSpace(cfg.EndDate)
            ? defaultEnd : DateOnly.Parse(cfg.EndDate);

        if (start > end)
            throw new ArgumentException($"StartDate {start} > EndDate {end}");

        return (start, end);
    }

    private static void TryDeleteTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* non-fatal */ }
    }

    private static string SanitizeName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "UnknownFacility"
            : string.Concat(name.Trim().Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}

