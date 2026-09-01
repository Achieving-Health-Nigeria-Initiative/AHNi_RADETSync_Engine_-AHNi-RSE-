using System.Data;
using AHNiRSE.Core.Models;

namespace AHNiRSE.Application.Mappers;

public static class HtsMapper
{
    public static List<Hts> Map(DataTable table)
    {
        var result = new List<Hts>(table.Rows.Count);

        foreach (DataRow row in table.Rows)
        {
            result.Add(new Hts
            {
                // ── Identity / Location ───────────────────────────────────────
                DatimCode = Get<string>(row, "datimcode"),
                PatientId = Get<string>(row, "patientid"),
                ClientCode = Get<string>(row, "clientcode"),
                Sex = Get<string>(row, "sex"),
                Age = Get<int?>(row, "age"),
                MaritalStatus = Get<string>(row, "maritalstatus"),
                LgaOfResidence = Get<string>(row, "lgaofresidence"),
                StateOfResidence = Get<string>(row, "stateofresidence"),

                // ── Visit ─────────────────────────────────────────────────────
                DateVisit = Get<DateTime?>(row, "datevisit"),
                FirstTimeVisit = Get<string>(row, "firsttimevisit"),

                // ── Testing context ───────────────────────────────────────────
                EntryPoint = Get<string>(row, "entrypoint"),
                IndexClient = Get<string>(row, "indexclient"),
                PreviouslyTested = Get<string>(row, "previouslytested"),
                TargetGroup = Get<string>(row, "targetgroup"),
                Source = Get<string>(row, "source"),
                TestingSetting = Get<string>(row, "testingsetting"),
                Modality = Get<string>(row, "modality"),
                CounselingType = Get<string>(row, "counselingtype"),
                PregnancyStatus = Get<string>(row, "pregnancystatus"),
                IndexTesting = Get<string>(row, "indextesting"),

                // ── Previous test ─────────────────────────────────────────────
                // SQL alias is "previousTestDate" (not "previousvisitdate")
                PreviousTestDate = Get<DateTime?>(row, "previoustestdate"),
                PreviousTestResult = Get<string>(row, "previoustestresult"),
                HtsCount = Get<int?>(row, "htscount"),

                // ── HIV result ────────────────────────────────────────────────
                FinalHivTestResult = Get<string>(row, "finalhivtestresult"),
                DateOfHivTesting = Get<DateTime?>(row, "dateofhivtesting"),

                // ── Prevention ────────────────────────────────────────────────
                PrepOffered = Get<string>(row, "prepoffered"),
                PrepAccepted = Get<string>(row, "prepaccepted"),

                // ── Geolocation ───────────────────────────────────────────────
                Latitude = Get<decimal?>(row, "latitude"),
                Longitude = Get<decimal?>(row, "longitude"),
            });
        }

        return result;
    }

    private static T? Get<T>(DataRow row, string columnName)
    {
        if (!row.Table.Columns.Contains(columnName))
            return default;

        var value = row[columnName];

        if (value is null || value == DBNull.Value)
            return default;

        if (value is string s && string.IsNullOrWhiteSpace(s))
            return default;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        try
        {
            if (targetType == typeof(string))
                return (T)(object)value.ToString()!;

            if (targetType == typeof(DateTime))
                return DateTime.TryParse(value.ToString(), out var dt) ? (T)(object)dt : default;

            if (targetType == typeof(int))
                return int.TryParse(value.ToString(), out var i) ? (T)(object)i : default;

            if (targetType == typeof(decimal))
                return decimal.TryParse(value.ToString(), out var d) ? (T)(object)d : default;

            return (T)Convert.ChangeType(value, targetType);
        }
        catch
        {
            return default;
        }
    }
}
