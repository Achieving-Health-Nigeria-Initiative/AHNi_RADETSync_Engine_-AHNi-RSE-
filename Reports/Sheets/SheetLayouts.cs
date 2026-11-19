namespace AHNiRSE.Reports;

/// <summary>
/// Headers of the PMTCT HTS and Maternal Cohort sheets, and the report module column each one is filled from where
/// the names differ. PMTCT HTS follows the module's own export layout. Maternal Cohort follows the module's maternal
/// cohort (child follow-up) export in the same order, with "datimCode" (the facility's DATIM id) first, without
/// State, LGA and Facility, and with three headers kept as the earlier blob scripts named them (see
/// MaternalCohortSources; the doubled apostrophes in "Mother''s" are the module's own). Used with
/// <see cref="SheetProjection"/>.
/// </summary>
public static class SheetLayouts
{
    public const string DatimCode = "datimCode";

    /// <summary>
    /// PMTCT HTS sheet headers: the report module's PMTCT HTS (Pepfar) Excel export (Constants.PMTCT_HTS_HEADER)
    /// in the same order, without its S/No row counter, State and LGA (the query still returns those two).
    /// </summary>
    public static readonly string[] PmtctHtsHeaders =
    [
        "Facility",
        "Patient ID",
        "Mother's Hospital Num",
        "Age",
        "Marital Status",
        "Setting",
        "Point of Entry",
        "Referred from Spokes Site",
        "Modality",
        "Date of registration in index pregnancy",
        "Gravida",
        "Parity",
        "Previously Known HIV+ Result",
        "Type of HIV Test",
        "HIV Early Detect Result",
        "Suspected Acute Infection",
        "Viral Load",
        "Date Tested for HIV",
        "HIV Test Result",
        "Known HBV Positive",
        "Hepatitis B Test Result",
        "Hepatitis B Treatment and Referral",
        "Date Tested for Hepatitis C",
        "Hepatitis C Test Result",
        "Syphilis Test Result",
        "Syphilis Treatment & Referral Result",
        "Date Of Maternal Retesting",
        "Maternal Retesting Result",
        "TB Screening Status",
        "Referred for Evaluation (Presumptive)",
        "Partner Tested for HIV",
        "Partner Tested for Syphilis",
        "Partner Tested for Hepatitis - B",
        "Referred To",
    ];

    /// <summary>Report module PMTCT HTS query column behind each header, as fillPmtctHtsDataMapper fills it; null = left empty there too.</summary>
    public static readonly IReadOnlyDictionary<string, string?> PmtctHtsSources =
        new Dictionary<string, string?>
        {
            ["Facility"] = "facilityname",
            ["Patient ID"] = "personuuid",
            ["Mother's Hospital Num"] = "hospitalnumber",
            ["Age"] = "age",
            ["Marital Status"] = "maritalstatus",
            ["Setting"] = "setting",
            ["Point of Entry"] = "facilitysetting",
            ["Referred from Spokes Site"] = "refferedfrom",
            ["Modality"] = "pepfarmodalities",
            ["Date of registration in index pregnancy"] = "date",
            ["Gravida"] = "gravida",
            ["Parity"] = "parity",
            ["Previously Known HIV+ Result"] = "previouslyknownhivpositive",
            ["Type of HIV Test"] = "typeofhivtest",
            ["HIV Early Detect Result"] = "hivearlydetect",
            ["Suspected Acute Infection"] = null,
            ["Viral Load"] = "hivearlydetectviralload",
            ["Date Tested for HIV"] = "dateofvisit",
            ["HIV Test Result"] = "finalhivtestresult",
            ["Known HBV Positive"] = "knownpositive",
            ["Hepatitis B Test Result"] = "hbtestresult",
            ["Hepatitis B Treatment and Referral"] = "hbtreatment",
            ["Date Tested for Hepatitis C"] = "datetestedforhepatitisc",
            ["Hepatitis C Test Result"] = "hepatitisc",
            ["Syphilis Test Result"] = "syphilistestresult",
            ["Syphilis Treatment & Referral Result"] = "syphilistreatment",
            ["Date Of Maternal Retesting"] = "dateofmaternalrestesting",
            ["Maternal Retesting Result"] = "maternalrestesting",
            ["TB Screening Status"] = "tbscreeningstatus",
            ["Referred for Evaluation (Presumptive)"] = "tbreferred",
            ["Partner Tested for HIV"] = "partnertestedhiv",
            ["Partner Tested for Syphilis"] = "partnertestedsyphilis",
            ["Partner Tested for Hepatitis - B"] = "partnertestedhb",
            ["Referred To"] = "partnerreferal",
        };

    public static readonly string[] MaternalCohortHeaders =
    [
        "datimCode",
        "Patient ID",
        "Mother''s Hospital Number",
        "Mother''s Unique ID",
        "Mother''s Date of Birth",
        "Age",
        "Marital Status",
        "ANC Setting",
        "Point of Entry",
        "Date of Initial Visit",
        "Date of Index ANC Registration",
        "LMP Date",
        "Gestational Age (Weeks) @ First ANC visit",
        "Gravida",
        "Parity",
        "PCV @ANC registration",
        "Hepatitis B Test Result",
        "Treated for Hepatitis B",
        "Syphilis Test Result",
        "Treated for Syphilis",
        "TB screening Status",
        "Date tested for HIV",
        "Type of HIV test",
        "Mother''s ART Start Date",
        "Timing of ART initiation in mother",
        "Current Pregnancy Status",
        "GA at last visit (weeks)",
        "Mother''s Current ART Status",
        "Visit Status",
        "Due Date for VL Sample collection @ 32 weeks",
        "Date for VL sample collection @ 32 weeks",
        "VL result at 32-36 weeks GA",
        "Date of VL result at 32-36 weeks GA",
        "Current Viral load Result",
        "Date of Current VL",
        "Date of Delivery",
        "Place of Delivery",
        "Mode of Delivery",
        "Fetal outcome (Child status)",
        "Child''s hospital ID number",
        "Sex - Child",
        "Birth Weight",
        "Hepatitis B Vaccination - Birth Dose",
        "Date of Hepatitis B Vaccination - 2nd Dose",
        "Date of Hepatitis B Vaccination - 3rd Dose",
        "Date of ARV Prophylaxis Commencemment",
        "Type of Prophylaxis (ePNP or regular)",
        "Date of CTX (Cotrimoxazole)",
        "Current infant feeding options",
        "Child Outcome @last visit",
        "Date of First DNA PCR Sample collection",
        "Result of first DNA PCR test",
        "Date first DNA PCR result was received",
        "Date of Second DNA PCR test sample collection",
        "Result of second DNA PCR test",
        "Date second DNA PCR result was received",
        "Date of third DNA PCR test sample collection",
        "Result of third DNA PCR test",
        "Date third DNA PCR result was received",
        "Date of fourth DNA PCR test sample collection",
        "Result of fourth DNA PCR test",
        "Date fourth DNA PCR result was received",
        "Date of confirmatory DNA PCR test sample collection",
        "Result of confirmatory DNA PCR test",
        "Date confirmatory DNA PCR result was received",
        "Date of Rapid Antibody Sample Collection",
        "Rapid Test Type",
        "Result of Rapid Antitbody Test",
        "Rapid Antibody Test Result",
        "Date of Child Final Outcome test",
        "Result (Child Final Outcome)",
        "Child''s ART Start Date",
        "Child Unique ID",
    ];

    /// <summary>Report module maternal cohort columns whose names differ from the sheet's; every other header has a same-named column.</summary>
    public static readonly IReadOnlyDictionary<string, string?> MaternalCohortSources =
        new Dictionary<string, string?>
        {
            ["Timing of ART initiation in mother"] = "Mother''s Status\tTiming of ART initiation in mother",
            ["Fetal outcome (Child status)"] = "Birth Outcome",
            ["Date of CTX (Cotrimoxazole)"] = "Age at CTX (Cotrimoxazole)",
        };
}
