namespace AHNiRSE.Application.Mappers;

/// <summary>
/// Headers of the PMTCT HTS and Maternal Cohort sheets, as the earlier blob scripts produced them (order and
/// spelling, including the doubled apostrophes in "Mother''s"), and the report module column each one is filled
/// from where the names differ. Used with <see cref="SheetProjection"/>; "datimCode" is the facility's DATIM id.
/// </summary>
public static class SheetLayouts
{
    public const string DatimCode = "datimCode";

    public static readonly string[] PmtctHtsHeaders =
    [
        "datimCode",
        "Patient ID",
        "Mother's Hospital Num",
        "Age",
        "ANC Setting",
        "Modality",
        "Point of Entry",
        "Date of registration in index pregnancy",
        "Gestational Age (Weeks) @ First ANC visit",
        "Date Tested for HIV",
        "HIV Test Result",
        "Syphillis Test Result",
        "Date Of Maternal Retesting",
        "Previously Known HIV +Ve Status",
        "Facility Enrolled In",
        "Linked to Syphilis Treatment",
    ];

    /// <summary>Report module PMTCT HTS (Pepfar) columns, as its Excel export uses them (fillPmtctHtsDataMapper); null = not in that report.</summary>
    public static readonly IReadOnlyDictionary<string, string?> PmtctHtsSources =
        new Dictionary<string, string?>
        {
            ["Patient ID"] = "personuuid",
            ["Mother's Hospital Num"] = "hospitalnumber",
            ["Age"] = "age",
            ["ANC Setting"] = "setting",
            ["Modality"] = "pepfarmodalities",
            ["Point of Entry"] = "facilitysetting",
            ["Date of registration in index pregnancy"] = "date",
            ["Gestational Age (Weeks) @ First ANC visit"] = null,
            ["Date Tested for HIV"] = "dateofvisit",
            ["HIV Test Result"] = "finalhivtestresult",
            ["Syphillis Test Result"] = "syphilistestresult",
            ["Date Of Maternal Retesting"] = "dateofmaternalrestesting",
            ["Previously Known HIV +Ve Status"] = "previouslyknownhivpositive",
            ["Facility Enrolled In"] = null,
            ["Linked to Syphilis Treatment"] = "syphilistreatment",
        };

    public static readonly string[] MaternalCohortHeaders =
    [
        "datimCode",
        "Patient ID",
        "Mother''s Hospital Number",
        "Age",
        "ANC Setting",
        "Point of Entry",
        "Date of Initial Visit",
        "Date of Index ANC Registration",
        "Syphilis Test Result",
        "Treated for Syphilis",
        "TB screening Status",
        "Mother''s ART Start Date",
        "Timing of ART initiation in mother",
        "GA at last visit (weeks)",
        "Mother''s Current ART Status",
        "Due Date for VL Sample collection @ 32 weeks",
        "Date for VL sample collection @ 32 weeks",
        "Current Viral load Result",
        "Date of Current VL",
        "Date of Delivery",
        "Place of Delivery",
        "Mode of Delivery",
        "Fetal outcome (Child status)",
        "Child''s hospital ID number",
        "Sex - Child",
        "Birth Weight",
        "Date of ARV Prophylaxis Commencemment",
        "Type of Prophylaxis (ePNP or regular)",
        "Date of CTX (Cotrimoxazole)",
        "Current infant feeding options",
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
        "Date of Child Final Outcome test",
        "Result (Child Final Outcome)",
        "Child''s ART Start Date",
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
