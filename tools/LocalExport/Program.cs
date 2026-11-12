using System.Data;
using AHNiRSE.Application.Mappers;
using AHNiRSE.Core.Models;
using AHNiRSE.Infrastructure.Export;
using Microsoft.Extensions.Logging.Abstractions;

// Minimal tool to run HTS mapper and export to local path

var outDir = Path.Combine(Path.GetTempPath(), "AHNi-RSE-Test");
Directory.CreateDirectory(outDir);
var filePath = Path.Combine(outDir, "hts_test.xlsx");

// build DataTable matching new headers
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

var row = dt.NewRow();
row["datimcode"] = "D123";
row["patientid"] = "P001";
row["clientcode"] = "C001";
row["sex"] = "M";
row["age"] = 30;
row["firsttimevisit"] = "Yes";
row["finalhivtestresult"] = "Negative";
dt.Rows.Add(row);

var mapped = AHNiRSE.Application.Mappers.HtsMapper.Map(dt);
Console.WriteLine($"Mapped {mapped.Count} HTS rows. First: Datim={mapped[0].DatimCode}, Patient={mapped[0].PatientId}, Client={mapped[0].ClientCode}");

var exporter = new AHNiRSE.Infrastructure.Export.ExcelExporter(NullLogger<AHNiRSE.Infrastructure.Export.ExcelExporter>.Instance);
var headerMap = new System.Collections.Generic.Dictionary<string,string>(System.StringComparer.OrdinalIgnoreCase)
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

await exporter.ExportAsync(mapped.Cast<object>(), filePath, headerMap);
Console.WriteLine($"Exported HTS workbook to: {filePath}");
return 0;
