using System.Data;
using System.IO;
using ClosedXML.Excel;
using AHNiRSE.Core.Models;
using AHNiRSE.Application.Mappers;
using AHNiRSE.Infrastructure.Export;
using Microsoft.Extensions.Logging.Abstractions;

// Minimal local runner that uses the project's HtsMapper and ExcelExporter to
// write an HTS workbook locally without touching blob storage.

Directory.SetCurrentDirectory(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")));

// Create a DataTable with the new HTS headers
var dt = new DataTable();
var cols = new[]
{
    "datimcode","patientid","clientcode","sex","age","maritalstatus",
    "lgaofresidence","stateofresidence","datevisit","firsttimevisit",
    "entrypoint","indexclient","previouslytested","targetgroup","source",
    "testingsetting","modality","counselingtype","pregnancystatus",
    "indextesting","previoustestdate","previoustestresult","htscount",
    "finalhivtestresult","dateofhivtesting","prepoffered","prepaccepted"
};
foreach (var c in cols) dt.Columns.Add(c);

// Add a sample row
var row = dt.NewRow();
row["datimcode"] = "D123";
row["patientid"] = "P001";
row["clientcode"] = "C001";
row["sex"] = "F";
row["age"] = 29;
row["datevisit"] = DateTime.UtcNow;
row["firsttimevisit"] = "Yes";
row["entrypoint"] = "OPD";
row["indexclient"] = "No";
row["previouslytested"] = "No";
row["targetgroup"] = "General";
row["source"] = "Facility";
row["testingsetting"] = "Clinic";
row["modality"] = "Walk-in";
row["counselingtype"] = "Standard";
row["pregnancystatus"] = "No";
row["indextesting"] = "None";
row["previoustestdate"] = DBNull.Value;
row["previoustestresult"] = DBNull.Value;
row["htscount"] = 1;
row["finalhivtestresult"] = "Negative";
row["dateofhivtesting"] = DateTime.UtcNow;
row["prepoffered"] = "No";
row["prepaccepted"] = "No";
dt.Rows.Add(row);

// Map to model
var models = AHNiRSE.Application.Mappers.HtsMapper.Map(dt);
Console.WriteLine($"Mapped {models.Count} Hts rows. First patientId={models[0].PatientId}, clientCode={models[0].ClientCode}");

// Prepare exporter
var exporter = new AHNiRSE.Infrastructure.Export.ExcelExporter(NullLogger<AHNiRSE.Infrastructure.Export.ExcelExporter>.Instance);

var headerMap = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
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
};

var tmp = Path.Combine(Path.GetTempPath(), "HTS_RSE_Test.xlsx");
if (File.Exists(tmp)) File.Delete(tmp);

await exporter.ExportAsync(models.Cast<object>(), tmp, headerMap);

Console.WriteLine($"Exported workbook to: {tmp}");

// Show header row values
using var wb = new XLWorkbook(tmp);
var ws = wb.Worksheets.First();
var headers = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
Console.WriteLine("Headers:");
foreach (var h in headers) Console.WriteLine(" - " + h);

Console.WriteLine("Done.");
