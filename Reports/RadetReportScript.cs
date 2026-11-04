using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Reports;

/// <summary>
/// RADET report – seven-sheet workbook. Every SQL script is downloaded each cycle from the
/// report-script folder of the autoreport blob container (there is no local copy).
///
/// Sheet 1 – RADET          (radet.sql                  – typed model, strongly mapped)
/// Sheet 2 – HTS            (hts.sql                    – typed model, strongly mapped)
/// Sheet 3 – Index          (index.sql                  – report module HTS Index (ICT), mapped to the sheet's 18 headers)
/// Sheet 4 – PMTCT HTS      (pmtct_hts.sql              – report module PMTCT HTS (Pepfar), mapped to the sheet's 16 headers)
/// Sheet 5 – Maternal       (maternal.sql               – report module query.yml maternal cohort, mapped to 48 headers)
/// Sheet 6 – Prep           (prep.sql                   – report module query.yml prep-query, headers as returned)
/// Sheet 7 – AHD            (ahd.sql                    – report module query.yml ahd-query, headers as returned)
///
/// Sheet 1 is FATAL: if the RADET SQL cannot be downloaded or run, nothing is uploaded.
/// Sheets 2-7 are NON-FATAL: if a SQL download or query fails, or the sheet's
/// Reports:Scripts entry has Enabled=false, the sheet keeps its header row with no data rows
/// and the upload still completes. Sheets 2-7 run in parallel with RADET.
///
/// DATE SAFETY – all DateTime.* calls replaced with CycleContext:
///   fileName  = $"..._{context.RunDate:yyyy_MM_dd}.xlsx"     ← WAT, locked at cycle start
///   blobDest  = $"RADET/{context.RunDate:yyyy-MM-dd}/..."    ← same value, guaranteed match
///   ResolveDates(cfg, context.WatToday)                      ← WAT, same source
///
/// Memory strategy:
///   • RADET and HTS DataTables disposed immediately after model-mapping.
///   • Index / PMTCTHTS / Maternal / Prep / AHD DataTables are held through
///     ExportMultiSheetAsync (ExcelExporter reads them during workbook construction)
///     and disposed once the workbook has been uploaded.
///   • The temp .xlsx is deleted after upload so %TEMP% stays clean.
/// </summary>
public class RadetReportScript(
    IStorageProvider storage,
    IDatabaseService database,
    IExcelExporter exporter,
    IOptions<ReportSettings> options,
    ILogger<RadetReportScript> logger) : IReportScript
{
    public string Name => "RADET";

    private const string DefaultRadetBlob = "report-script/radet.sql";

    /// <summary>RADET :start_date when Reports:Scripts RADET has no StartDate: the whole cohort.</summary>
    private static readonly DateOnly RadetDefaultStart = new(1980, 1, 1);

    /// <summary>
    /// Reports:Scripts entries that become extra sheets of this workbook (they are not separate
    /// IReportScripts), with the blob in the script container used when an entry has no BlobPath.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SheetScripts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTS"] = "report-script/hts.sql",
            ["Index"] = "report-script/index.sql",
            ["PMTCTHTS"] = "report-script/pmtct_hts.sql",
            ["Maternal"] = "report-script/maternal.sql",
            ["Prep"] = "report-script/prep.sql",
            ["AHD"] = "report-script/ahd.sql",
        };

    // ── Index headers (empty Index sheet keeps them) ──────────────────────────
    private static readonly Dictionary<string, string> IndexHeaderMap =
        IndexMapper.Headers.ToDictionary(h => h, h => h);

    // ── RADET header map ──────────────────────────────────────────────────────
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

    // ── PMTCT HTS / Maternal Cohort headers (an empty sheet keeps them) ──────────
    private static readonly Dictionary<string, string> PmtctHtsHeaderMap =
        SheetLayouts.PmtctHtsHeaders.ToDictionary(h => h, h => h);

    private static readonly Dictionary<string, string> MaternalCohortHeaderMap =
        SheetLayouts.MaternalCohortHeaders.ToDictionary(h => h, h => h);

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

    // ── Main entry point ──────────────────────────────────────────────────────

    public async Task<(int RowCount, string UploadUrl, DateTimeOffset? UploadedAt)> RunAsync(
        CycleContext context,
        CancellationToken ct = default)
    {
        var cfg = options.Value.Scripts
            .FirstOrDefault(s => string.Equals(s.Name, Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No '{Name}' config in Reports:Scripts.");

        var (startDate, endDate) = ResolveDates(DatesFrom(cfg, context), context.WatToday, _ => RadetDefaultStart);
        var facilityId = cfg.FacilityId > 0 ? cfg.FacilityId : await database.GetFacilityIdAsync(ct);
        var queryParameters = new QueryParameters
        {
            FacilityId = facilityId,
            StartDate = startDate,
            EndDate = endDate
        };

        logger.LogInformation(
            "[RADET] CycleId={Id} | RunDate={RunDate} | WatToday={WatToday} | FacilityId={FacilityId} | " +
            "Period={Start}->{End} | PreviousQuarterEnd={Previous} | PreviousPreviousQuarterEnd={PreviousPrevious}",
            context.CycleId,
            context.RunDate.ToString("yyyy-MM-dd"),
            context.WatToday.ToString("yyyy-MM-dd"),
            facilityId,
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            queryParameters.PreviousQuarterEnd.ToString("yyyy-MM-dd"),
            queryParameters.PreviousPreviousQuarterEnd.ToString("yyyy-MM-dd"));

        var facility = database.GetFacilityInfo();
        var safeName = SanitizeName(facility.FacilityName);
        var dateSuffix = context.RunDate.ToString("yyyy_MM_dd");
        var folderDate = context.RunDate.ToString("yyyy-MM-dd");
        var fileName = $"{safeName}_{dateSuffix}.xlsx";
        var localOutput = Path.Combine(Path.GetTempPath(), "AHNi-RSE", context.CycleId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(localOutput)!);

        using var parallelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var htsTask = RunNonFatalHtsQueryAsync(facilityId, context, parallelCts.Token);
        var indexTask = RunNonFatalShapedQueryAsync("Index", "Index", queryParameters, IndexMapper.Map, parallelCts.Token);

        // Default start dates are the ones the earlier blob scripts used (the report module's own default is 1985).
        var datim = new Dictionary<string, object?> { [SheetLayouts.DatimCode] = facility.DatimId };
        var pmtctTask = RunNonFatalShapedQueryAsync(
            "PMTCTHTS", "PMTCT HTS",
            SheetParameters("PMTCTHTS", facilityId, context, new DateOnly(1980, 1, 1)),
            rows => SheetProjection.Project(rows, SheetLayouts.PmtctHtsHeaders, SheetLayouts.PmtctHtsSources, datim),
            parallelCts.Token);
        var maternalTask = RunNonFatalShapedQueryAsync(
            "Maternal", "Maternal Cohort",
            SheetParameters("Maternal", facilityId, context, new DateOnly(1980, 1, 1)),
            rows => SheetProjection.Project(rows, SheetLayouts.MaternalCohortHeaders, SheetLayouts.MaternalCohortSources, datim),
            parallelCts.Token);
        // Prep and AHD: the report module queries return exactly the sheets' headers.
        var prepTask = RunNonFatalDataTableQueryAsync(
            "Prep", "Prep", SheetParameters("Prep", facilityId, context, new DateOnly(1985, 1, 1)), parallelCts.Token);
        var ahdTask = RunNonFatalDataTableQueryAsync(
            "AHD", "AHD", SheetParameters("AHD", facilityId, context, new DateOnly(1985, 1, 1)), parallelCts.Token);

        List<Radet> radetRows;
        int radetCount;
        try
        {
            var radetSql = await LoadSqlAsync("RADET", cfg, DefaultRadetBlob, ct);
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
                facilityId,
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
            new(SheetName: "Index",           RawData: indexResult.Data,    HeaderMap: IndexHeaderMap),
            new(SheetName: "PMTCT HTS",       RawData: pmtctResult.Data,    HeaderMap: PmtctHtsHeaderMap),
            new(SheetName: "Maternal Cohort", RawData: maternalResult.Data, HeaderMap: MaternalCohortHeaderMap),
            new(SheetName: "Prep",            RawData: prepResult.Data,    HeaderMap: PrepHeaderMap),
            new(SheetName: "AHD",             RawData: ahdResult.Data),
        };

        try
        {
            await exporter.ExportMultiSheetAsync(sheets, localOutput, ct);
            var blobDest = $"RADET/{folderDate}/{fileName}";
            var (storagePath, uploadedAt) = await storage.UploadAsync(localOutput, blobDest, ct);
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

            return (radetCount, storagePath, uploadedAt);
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
        long facilityId,
        CycleContext context,
        CancellationToken ct)
    {
        if (!IsSheetEnabled("HTS"))
        {
            logger.LogInformation("[RADET] HTS sheet disabled in Reports:Scripts - Sheet 2 will be empty.");
            return ([], 0);
        }

        try
        {
            var htsCfg = FindScript("HTS");
            var (startDate, endDate) = ResolveDates(DatesFrom(htsCfg, context), context.WatToday, HtsDefaultStart);
            var parameters = new QueryParameters { FacilityId = facilityId, StartDate = startDate, EndDate = endDate };
            logger.LogInformation(
                "[RADET] HTS period {Start}->{End}", startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

            var sql = await LoadSqlAsync("HTS", htsCfg, SheetScripts["HTS"], ct);
            using var data = await database.ExecuteScriptAsync(sql, parameters, ct);
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

    /// <summary>
    /// A sheet whose report module query has its own column names: runs it, then <paramref name="shape"/>
    /// turns the rows into the sheet's headers.
    /// </summary>
    private async Task<(DataTable? Data, int Count)> RunNonFatalShapedQueryAsync(
        string scriptName,
        string sheetName,
        QueryParameters parameters,
        Func<DataTable, DataTable> shape,
        CancellationToken ct)
    {
        var (data, count) = await RunNonFatalDataTableQueryAsync(scriptName, sheetName, parameters, ct);
        if (data is null)
            return (null, 0);

        try
        {
            var mapped = shape(data);
            if (!ReferenceEquals(mapped, data))
                data.Dispose();
            return (mapped, count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] {Sheet} rows could not be mapped - sheet will be empty.", sheetName);
            data.Dispose();
            return (null, 0);
        }
    }

    /// <summary>
    /// Query parameters for a sheet: the facility, and the sheet entry's StartDate (or
    /// <paramref name="defaultStart"/>) and EndDate (or today).
    /// </summary>
    private QueryParameters SheetParameters(string scriptName, long facilityId, CycleContext context, DateOnly defaultStart)
    {
        var (start, end) = ResolveDates(DatesFrom(FindScript(scriptName), context), context.WatToday, _ => defaultStart);
        logger.LogInformation(
            "[RADET] {Script} period {Start}->{End}", scriptName, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
        return new QueryParameters { FacilityId = facilityId, StartDate = start, EndDate = end };
    }

    private async Task<(DataTable? Data, int Count)> RunNonFatalDataTableQueryAsync(
        string scriptName,
        string sheetName,
        QueryParameters parameters,
        CancellationToken ct)
    {
        if (!IsSheetEnabled(scriptName))
        {
            logger.LogInformation("[RADET] {Sheet} sheet disabled in Reports:Scripts - sheet will be empty.", sheetName);
            return (null, 0);
        }

        try
        {
            var sql = await LoadSqlAsync(sheetName, FindScript(scriptName), SheetScripts[scriptName], ct);
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

    /// <summary>A sheet runs unless its Reports:Scripts entry exists and says Enabled=false.</summary>
    private bool IsSheetEnabled(string scriptName) => FindScript(scriptName)?.Enabled ?? true;

    /// <summary>
    /// Downloads a script's BlobPath (or <paramref name="defaultBlob"/>) from the blob script container.
    /// </summary>
    private async Task<string> LoadSqlAsync(string label, ScriptConfig? cfg, string defaultBlob, CancellationToken ct)
    {
        var blobPath = cfg?.BlobPath is { Length: > 0 } configured ? configured : defaultBlob;
        var sqlPath = await storage.DownloadAsync(blobPath, ct);
        if (!File.Exists(sqlPath))
            throw new FileNotFoundException($"{label} SQL missing: {blobPath}");

        return await File.ReadAllTextAsync(sqlPath, ct);
    }

    /// <summary>
    /// The script entry whose StartDate/EndDate apply: none for a force backfill, which always runs
    /// as of its own date with the default start dates.
    /// </summary>
    private static ScriptConfig? DatesFrom(ScriptConfig? cfg, CycleContext context)
        => context.Backfill ? null : cfg;

    /// <summary>
    /// Resolves the start and end dates from a script entry: EndDate or today, then StartDate or
    /// <paramref name="defaultStart"/> applied to that end date. Always receives watToday from
    /// CycleContext – never DateTime.UtcNow / Today.
    /// </summary>
    private static (DateOnly start, DateOnly end) ResolveDates(
        ScriptConfig? cfg,
        DateOnly watToday,
        Func<DateOnly, DateOnly> defaultStart)
    {
        var end = string.IsNullOrWhiteSpace(cfg?.EndDate)
            ? watToday : DateOnly.Parse(cfg.EndDate);

        var start = string.IsNullOrWhiteSpace(cfg?.StartDate)
            ? defaultStart(end) : DateOnly.Parse(cfg.StartDate);

        if (start > end)
            throw new ArgumentException($"StartDate {start} > EndDate {end}");

        return (start, end);
    }

    /// <summary>
    /// HTS start date when Reports:Scripts HTS has no StartDate: 1 July before the PEPFAR fiscal year
    /// (1 October - 30 September) that contains <paramref name="endDate"/>, i.e. the fiscal year plus the
    /// three months before it - the window the previous blob hts.sql used, but taken from the end date so
    /// a past period (EndDate 2025-09-30 -> 2024-07-01) gets that year's window.
    /// </summary>
    private static DateOnly HtsDefaultStart(DateOnly endDate)
    {
        var fiscalYearStart = new DateOnly(endDate.Month >= 10 ? endDate.Year : endDate.Year - 1, 10, 1);
        return fiscalYearStart.AddMonths(-3);
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

