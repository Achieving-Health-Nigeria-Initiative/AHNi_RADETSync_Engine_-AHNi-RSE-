using System.Data;

namespace AHNiRSE.Reports;

/// <summary>
/// Maps HTS query rows to <see cref="Hts"/>. The first column name is the LAMISPlus report module's
/// (report-script/hts.sql); the second is the alias the older blob hts.sql used for the same value.
/// </summary>
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
                DatimCode = Get<string>(row, "datimid", "datimcode"),
                PatientId = Get<string>(row, "personuuid", "patientid"),
                ClientCode = Get<string>(row, "clientcode"),
                Sex = Get<string>(row, "sex"),
                Age = Get<int?>(row, "age"),
                MaritalStatus = Get<string>(row, "maritalstatus"),
                LgaOfResidence = Get<string>(row, "lgaofresidence"),
                StateOfResidence = Get<string>(row, "stateofresidence"),

                // ── Visit ─────────────────────────────────────────────────────
                DateVisit = Get<DateTime?>(row, "dateofvisit", "datevisit"),
                FirstTimeVisit = Get<string>(row, "firsttimevisit"),

                // ── Testing context ───────────────────────────────────────────
                EntryPoint = Get<string>(row, "entrypoint"),
                IndexClient = Get<string>(row, "indexclientcode", "indexclient"),
                PreviouslyTested = Get<string>(row, "previouslytestedthisyear", "previouslytested"),
                TargetGroup = Get<string>(row, "categoryofclients", "targetgroup"),
                Source = Get<string>(row, "source"),
                TestingSetting = Get<string>(row, "testingsetting"),
                Modality = Get<string>(row, "pepfarmodalities", "modality"),
                CounselingType = Get<string>(row, "typeofsession", "counselingtype"),
                PregnancyStatus = Get<string>(row, "pregnancystatus"),
                IndexTesting = Get<string>(row, "indextesting"),

                // ── Previous test ─────────────────────────────────────────────
                PreviousTestDate = Get<DateTime?>(row, "dateofprevioushts", "previoustestdate"),
                PreviousTestResult = Get<string>(row, "previoushtsresult", "previoustestresult"),
                HtsCount = Get<int?>(row, "numberofcounts", "htscount"),

                // ── HIV result ────────────────────────────────────────────────
                FinalHivTestResult = Get<string>(row, "finalhivtestresult"),
                // The report module reports the visit date as the date of HIV testing.
                DateOfHivTesting = Get<DateTime?>(row, "dateofvisit", "dateofhivtesting"),

                // ── Prevention (not in the report module's HTS query) ────────
                PrepOffered = Get<string>(row, "prepoffered"),
                PrepAccepted = Get<string>(row, "prepaccepted"),

                // ── Geolocation ───────────────────────────────────────────────
                Latitude = Get<decimal?>(row, "latitude"),
                Longitude = Get<decimal?>(row, "longitude"),

                // ── AHNi spoke sites ──────────────────────────────────────────
                SpokeSiteId = Get<string>(row, "spokesiteid"),
                SpokeSiteName = Get<string>(row, "spokesitename"),
                SpokeSiteCheck = Get<string>(row, "spokesitecheck"),
            });
        }

        return result;
    }

    /// <summary>Value of the first of <paramref name="columnNames"/> present in the result set.</summary>
    private static T? Get<T>(DataRow row, params string[] columnNames)
    {
        var columnName = Array.Find(columnNames, row.Table.Columns.Contains);
        if (columnName is null)
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
