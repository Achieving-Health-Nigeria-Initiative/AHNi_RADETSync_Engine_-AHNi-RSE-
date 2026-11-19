using System.Data;

namespace AHNiRSE.Reports;

/// <summary>
/// Shapes the Index sheet like the report module's HTS Index Excel export (Constants.FAMILY_INDEX_HEADER,
/// in that order), followed by the index client's age, sex and date of birth from patient_person.
/// report-script/index.sql returns these headers itself; the current module query (lower-case column names,
/// no index client details) is mapped onto them the way fillFamilyIndexMapper does.
/// </summary>
public static class IndexMapper
{
    /// <summary>The Index sheet headers, in order.</summary>
    public static readonly string[] Headers =
    [
        "State",
        "LGA",
        "Facility",
        "Facility Id (Datim)",
        "Patient ID",
        "Index Client Surname",
        "Index Client Other Names",
        "Index client code/unique ID",
        "Index client entry point",
        "Client Category",
        "Date offered index testing",
        "Offered Index Testing",
        "Accepted Index Testing",
        "Date of Elicitation",
        "Index Contact ID",
        "Name of contact",
        "Relationship to Index",
        "Index Contact's Sex",
        "Index Contact's Age",
        "Index Contact's Phone number",
        "Index Contact's Address",
        "Notification Method",
        "Attempts",
        "Known HIV Positive",
        "Date of HTS (dd-mm-yyyy)",
        "HIV Test Result",
        "Date linked to Treatment & Care",
        "Unique ART number (UAN)",
        "Date Enrolled in OVC",
        "OVC_Id",
        "Index Client Age",
        "Index Client Sex",
        "Index Client Date of Birth",
    ];

    /// <summary>
    /// Header → column of the unmodified module query (ReportRepository.getFamilyIndexReport), as
    /// fillFamilyIndexMapper fills them; null = that query does not return it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> ModuleQueryColumns =
        new Dictionary<string, string?>
        {
            ["State"] = "state",
            ["LGA"] = "lga",
            ["Facility"] = "facilityname",
            ["Facility Id (Datim)"] = "datimid",
            ["Patient ID"] = "patientuuid",
            ["Index Client Surname"] = "surname",
            ["Index Client Other Names"] = "firstname",
            ["Index client code/unique ID"] = "clientcode",
            ["Index client entry point"] = "entrypoint",
            ["Client Category"] = "clientcategory",
            ["Date offered index testing"] = "dateofservice",
            ["Offered Index Testing"] = "offeredpns",
            ["Accepted Index Testing"] = "acceptedpns",
            ["Date of Elicitation"] = "dateofservice",
            ["Index Contact ID"] = "contactcode",
            ["Name of contact"] = "nameofindexclient",
            ["Relationship to Index"] = "relationshiptoindex",
            ["Index Contact's Sex"] = "sex",
            ["Index Contact's Age"] = "age",
            ["Index Contact's Phone number"] = "phone",
            ["Index Contact's Address"] = "address",
            ["Notification Method"] = "notificationmethod",
            ["Attempts"] = "attempts",
            ["Known HIV Positive"] = "knownhivpositive",
            ["Date of HTS (dd-mm-yyyy)"] = "datetestedhiv",
            ["HIV Test Result"] = "hivtestresult",
            ["Date linked to Treatment & Care"] = "dateenrolledart",
            ["Unique ART number (UAN)"] = "uan",
            ["Date Enrolled in OVC"] = "dateenrolledovc",
            ["OVC_Id"] = "ovcid",
            ["Index Client Age"] = null,
            ["Index Client Sex"] = null,
            ["Index Client Date of Birth"] = null,
        };

    /// <summary>
    /// Returns the rows under <see cref="Headers"/>, in order. Columns index.sql adds for older RSE builds are
    /// left out.
    /// </summary>
    public static DataTable Map(DataTable source) =>
        source.Columns.Contains("Facility Id (Datim)")
            ? SheetProjection.Project(source, Headers)
            : SheetProjection.Project(source, Headers, ModuleQueryColumns);
}
