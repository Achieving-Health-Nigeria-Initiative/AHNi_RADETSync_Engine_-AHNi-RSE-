using System.Data;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;

namespace AHNiRSE.Export;

public class FiveSheetExcelExporter
{
    private static readonly string[] SheetNames = { "CombinedRADET", "CombinedHTS", "CombineIndex", "CombinePMTCTHTS", "CombineMaternal" };

    // HTS SQL uses unquoted lowercase aliases — map them to display names before writing.
    // All other sheets use quoted SQL aliases that are already the correct display names.
    private static readonly Dictionary<string, string> HtsHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["datimcode"] = "datimCode",
            ["facility"] = "Facility",
            ["state"] = "State",
            ["lga"] = "LGA",
            ["patientid"] = "patientId",
            ["patientuuid"] = "patientUuid",
            ["clientcode"] = "clientCode",
            ["firstname"] = "firstName",
            ["surname"] = "surname",
            ["othername"] = "otherName",
            ["sex"] = "sex",
            ["age"] = "age",
            ["dateofbirth"] = "dateOfBirth",
            ["phonenumber"] = "phoneNumber",
            ["maritalstatus"] = "maritalStatus",
            ["education"] = "education",
            ["occupation"] = "occupation",
            ["lgaofresidence"] = "LGAOfResidence",
            ["stateofresidence"] = "StateOfResidence",
            ["clientaddress"] = "clientAddress",
            ["htslatitude"] = "HTSLatitude",
            ["htslongitude"] = "HTSLongitude",
            ["datevisit"] = "dateVisit",
            ["previousvisitdate"] = "previousvisitdate",
            ["previoustestdate"] = "previousvisitdate",
            ["previoustestresult"] = "previoustestresult",
            ["firsttimevisit"] = "firstTimeVisit",
            ["htscount"] = "htscount",
            ["numberofchildren"] = "numberOfChildren",
            ["numberofwives"] = "numberOfWives",
            ["entrypoint"] = "entrypoint",
            ["indexclient"] = "indexClient",
            ["previouslytested"] = "previouslyTested",
            ["targetgroup"] = "targetGroup",
            ["referredfrom"] = "referredFrom",
            ["testingsetting"] = "testingSetting",
            ["modality"] = "modality",
            ["gonmodalities"] = "gonmodalities",
            ["pepfarmodalities"] = "pepfarmodalities",
            ["counselingtype"] = "counselingType",
            ["pregnancystatus"] = "pregnancyStatus",
            ["breastfeeding"] = "breastFeeding",
            ["indextype"] = "indexType",
            ["assessmentcode"] = "Assessmentcode",
            ["source"] = "source",
            ["refferedforsti"] = "refferedforsti",
            ["testername"] = "testername",
            ["totalriskscore"] = "totalriskscore",
            ["mlscore"] = "mlscore",
            ["mlstatus"] = "mlstatus",
            ["rnkk"] = "rnkk",
            ["prepoffered"] = "prepOffered",
            ["prepaccepted"] = "prepAccepted",
            ["numberofcondomsgiven"] = "numberOfCondomsGiven",
            ["numberoflubricantsgiven"] = "numberOfLubricantsGiven",
            ["finalhivtestresult"] = "finalHIVTestResult",
            ["dateofhivtesting"] = "dateOfHIVTesting",
            ["ifrecencytestingoptin"] = "ifrecencytestingoptin",
            ["recencyid"] = "RecencyID",
            ["recencytesttype"] = "recencyTestType",
            ["recencytestdate"] = "recencyTestDate",
            ["recencyinterpretation"] = "recencyInterpretation",
            ["finalrecencyresult"] = "finalRecencyResult",
            ["viralloadresult"] = "viralLoadResult",
            ["viralloadsamplecollectiondate"] = "viralLoadSampleCollectionDate",
            ["viralloadconfirmationresult"] = "viralLoadConfirmationResult",
            ["viralloadconfirmationdate"] = "viralLoadConfirmationDate",
            ["viralloadreceivedresultdate"] = "viralLoadReceivedResultDate",
            ["syphilistestresult"] = "syphilisTestResult",
            ["syphilistestdate"] = "syphilisTestDate",
            ["hepatitisbtestresult"] = "hepatitisBTestResult",
            ["hepatitisbtestdate"] = "hepatitisBTestDate",
            ["hepatitisctestresult"] = "hepatitisCTestResult",
            ["hepatitisctestdate"] = "hepatitisCTestDate",
            ["cd4type"] = "CD4Type",
            ["cd4testresult"] = "CD4TestResult",
        };

    public string Export(
        DataTable radet,
        DataTable hts,
        DataTable index,
        DataTable pmtctHts,
        DataTable maternal,
        string outputPath)
    {
        // Rename HTS columns in-place so MiniExcel uses display names as headers.
        // All other sheets already have display names from quoted SQL aliases.
        ApplyHeaderMap(hts, HtsHeaders);

        // MiniExcel streams each sheet row-by-row directly to the xlsx file —
        // no in-memory DOM, dramatically faster and lighter than ClosedXML at 700k+ rows.
        var sheets = new Dictionary<string, object>
        {
            [SheetNames[0]] = radet,
            [SheetNames[1]] = hts,
            [SheetNames[2]] = index,
            [SheetNames[3]] = pmtctHts,
            [SheetNames[4]] = maternal,
        };

        MiniExcel.SaveAs(outputPath, sheets, overwriteFile: true);
        return outputPath;
    }

    /// <summary>
    /// Renames DataTable columns in-place using the provided map (case-insensitive).
    /// Columns not in the map are left unchanged.
    /// </summary>
    private static void ApplyHeaderMap(DataTable table, Dictionary<string, string> headerMap)
    {
        foreach (DataColumn col in table.Columns)
        {
            if (headerMap.TryGetValue(col.ColumnName, out var displayName))
                col.ColumnName = displayName;
        }
    }
}