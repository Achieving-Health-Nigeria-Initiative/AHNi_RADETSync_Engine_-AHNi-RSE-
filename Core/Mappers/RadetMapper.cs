using System.Data;
using AHNiRSE.Core.Models;
using System.Text.RegularExpressions;

namespace AHNiRSE.Application.Mappers;

public static class RadetMapper
{
    public static List<Radet> Map(DataTable table)
    {
        var result = new List<Radet>();

        foreach (DataRow row in table.Rows)
        {
            var item = new Radet
            {
                State = Get<string>(row, "state"),
                Lga = Get<string>(row, "lga"),
                LgaResidence = Get<string>(row, "lgaofresidence"),
                FacilityName = PrefixFacilityName(Get<string>(row, "state"), Get<string>(row, "facilityname")),
                DatimId = Get<string>(row, "datimid"),
                PatientId = Get<string>(row, "personuuid"),
                NdrPatientIdentifier = Get<string>(row, "ndrpatientidentifier"),
                HospitalNumber = Get<string>(row, "hospitalnumber"),
                UniqueId = Get<string>(row, "uniqueid"),
                HouseholdUniqueNo = Get<string>(row, "householduniqueno"),
                OvcUniqueId = Get<string>(row, "ovcuniqueid"),
                Sex = Get<string>(row, "gender"),
                TargetGroup = Get<string>(row, "targetgroup"),

                CurrentWeightKg = Get<decimal?>(row, "currentweight"),
                PregnancyStatus = Get<string>(row, "pregnancystatus"),
                DateOfBirth = Get<DateTime?>(row, "dateofbirth"),
                Age = Get<int?>(row, "age"),
                CareEntryPoint = Get<string>(row, "careentry"),

                DateOfRegistration = Get<DateTime?>(row, "dateofregistration"),
                EnrollmentDate = Get<DateTime?>(row, "dateofenrollment"),
                ArtStartDate = Get<DateTime?>(row, "artstartdate"),
                LastPickupDate = Get<DateTime?>(row, "lastpickupdate"),
                MonthsOfArvRefill = Get<decimal?>(row, "monthsofarvrefill"),

                RegimenLineAtArtStart = Get<string>(row, "regimenlineatstart"),
                RegimenAtArtStart = Get<string>(row, "regimenatstart"),

                DateStartCurrentArtRegimen = Get<DateTime?>(row, "dateofcurrentregimen"),
                CurrentRegimenLine = Get<string>(row, "currentregimenline"),
                CurrentArtRegimen = Get<string>(row, "currentartregimen"),

                ClinicalStagingAtLastVisit = Get<string>(row, "currentclinicalstage"),

                DateLastCd4Count = Get<DateTime?>(row, "dateoflastcd4count"),
                LastCd4Count = Get<string?>(row, "lastcd4count"),

                DateVlSampleCollection = Get<DateTime?>(row, "dateofviralloadsamplecollection"),
                DateCurrentVlSample = Get<DateTime?>(row, "dateofcurrentviralloadsample"),
                // Normalize raw viral load value into integer and store in CurrentViralLoad
                CurrentViralLoad = NormalizeViralLoadInt(Get<string?>(row, "currentviralload")),
                DateCurrentViralLoad = Get<DateTime?>(row, "dateofcurrentviralload"),

                ViralLoadIndication = Get<string>(row, "viralloadindication"),
                ViralLoadEligibilityStatus = Get<string>(row, "vleligibilitystatus"),
                DateVlEligibilityStatus = Get<DateTime?>(row, "dateofvleligibilitystatus"),

                CurrentArtStatus = Get<string>(row, "currentstatus"),
                DateCurrentArtStatus = Get<DateTime?>(row, "currentstatusdate"),

                ClientVerificationOutcome = Get<string>(row, "clientverificationoutcome"),

                CauseOfDeath = Get<string>(row, "causeofdeath"),
                VaCauseOfDeath = Get<string>(row, "vacauseofdeath"),

                PreviousArtStatus = Get<string>(row, "previousstatus"),
                ConfirmedDatePreviousArtStatus = Get<DateTime?>(row, "previousstatusdate"),

                ArtEnrollmentSetting = Get<string>(row, "enrollmentsetting"),

                DateTbScreening = Get<DateTime?>(row, "dateoftbscreened"),
                TbScreeningType = Get<string>(row, "tbscreeningtype"),
                CadScore = Get<int?>(row, "cadscore"),
                TbStatus = Get<string>(row, "tbstatus"),

                DateTbSampleCollection = Get<DateTime?>(row, "dateoftbsamplecollection"),
                TbDiagnosticTestType = Get<string>(row, "tbdiagnostictesttype"),
                DateTbResultReceived = Get<DateTime?>(row, "dateoftbdiagnosticresultreceived"),
                TbDiagnosticResult = Get<string>(row, "tbdiagnosticresult"),

                DateXrayTbDiagnosis = Get<DateTime?>(row, "treatmentmethoddate"),
                XrayTbResult = Get<string>(row, "resulttbscorecad"),

                DateTbTreatmentStart = Get<DateTime?>(row, "tbtreatmentstartdate"),
                TbType = Get<string>(row, "tbtreatementtype"),
                DateTbTreatmentCompletion = Get<DateTime?>(row, "tbcompletiondate"),
                TbTreatmentOutcome = Get<string>(row, "tbtreatmentoutcome"),

                EligibleForTpt = Get<string>(row, "eligibilitytpt"),
                DateTptStart = Get<DateTime?>(row, "dateofiptstart"),
                TptType = Get<string>(row, "ipttype"),
                TptCompletionDate = Get<DateTime?>(row, "iptcompletiondate"),
                TptCompletionStatus = Get<string>(row, "iptcompletionstatus"),

                DateEacStart = Get<DateTime?>(row, "dateofcommencementofeac"),
                NumberOfEacSessions = Get<int?>(row, "numberofeacsessioncompleted"),
                DateLastEacSession = Get<DateTime?>(row, "dateoflasteacsessioncompleted"),
                DateExtendedEacCompletion = Get<DateTime?>(row, "dateofextendeaccompletion"),

                DateRepeatVlSample = Get<DateTime?>(row, "dateofrepeatviralloadeacsamplecollection"),
                RepeatViralLoadPostEac = Get<string?>(row, "repeatviralloadresult"),
                DateRepeatVlResult = Get<DateTime?>(row, "dateofrepeatviralloadresult"),

                DateDevolvement = Get<DateTime?>(row, "dateofdevolvement"),
                ModelDevolvedTo = Get<string>(row, "modeldevolvedto"),

                DateCurrentDsd = Get<DateTime?>(row, "dateofcurrentdsd"),
                CurrentDsdModel = Get<string>(row, "currentdsdmodel"),
                CurrentDsdOutlet = Get<string>(row, "currentdsdoutlet"),
                DateReturnToFacility = Get<DateTime?>(row, "datereturntosite"),

                ScreeningForChronicConditions = null, // not in dataset
                CoMorbidities = null, // not in dataset

                DateCervicalCancerScreening = Get<DateTime?>(row, "dateofcervicalcancerscreening"),
                CervicalCancerScreeningType = Get<string>(row, "cervicalcancerscreeningtype"),
                CervicalCancerScreeningMethod = Get<string>(row, "cervicalcancerscreeningmethod"),
                CervicalCancerScreeningResult = Get<string>(row, "resultofcervicalcancerscreening"),
                DatePrecancerTreatment = Get<DateTime?>(row, "treatmentmethoddate"),
                PrecancerTreatmentMethod = Get<string>(row, "cervicalcancertreatmentscreened"),

                DateBiometricsEnrolled = Get<DateTime?>(row, "datebiometricsenrolled"),
                FingersCaptured = Get<int?>(row, "numberoffingerscaptured"),
                DateBiometricsRecapture = Get<DateTime?>(row, "datebiometricsrecaptured"),
                FingersRecaptured = Get<int?>(row, "numberoffingersrecaptured"),

                CaseManager = Get<string>(row, "casemanager")
            };

            result.Add(item);
        }

        return result;
    }

    private static long? NormalizeViralLoadInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        // common tokens mapping to undetectable
        var lower = s.ToLowerInvariant();
        if (lower.Contains("not detected") || lower == "tnd" || lower.Contains("targetnotdetected") || lower.Contains("notdetected") || lower.Contains("not detected") || lower.Contains("notdet"))
            return 0;

        // tokens indicating invalid/unparseable
        if (Regex.IsMatch(lower, "invalid|failed|rejected|pending|pendind|pended|error|nd|nr|not available|not avai"))
            return null;

        // remove spaces and common thousand separators
        s = s.Replace(",", string.Empty).Replace(" ", string.Empty);

        // handle leading '<' (e.g. <20) -> treat as 0 (undetectable)
        if (s.StartsWith("<") || s.StartsWith("≤"))
        {
            // try extract number
            var m = Regex.Match(s, "\\d+(\\.\\d+)?");
            if (m.Success)
                return 0;
            return 0;
        }

        // remove any non-digit/non-dot characters at start/end
        var numMatch = Regex.Match(s, "-?\\d+(\\.\\d+)?");
        if (!numMatch.Success)
            return null;

        if (!double.TryParse(numMatch.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return null;

        if (double.IsNaN(d) || double.IsInfinity(d))
            return null;

        if (d < 0)
            return null;

        // Round to nearest integer
        try
        {
            var rounded = Convert.ToInt64(Math.Round(d));
            return rounded;
        }
        catch
        {
            // overflow or other issue
            return null;
        }
    }



    private static T? Get<T>(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column))
            return default;
        var value = row[column];
        if (value == null || value == DBNull.Value)
            return default;
        if (value is string str && string.IsNullOrWhiteSpace(str))
            return default;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            // 🔹 DateTime
            if (targetType == typeof(DateTime))
            {
                if (DateTime.TryParse(value.ToString(), out var dt))
                    return (T)(object)dt;
                return default;
            }

            // 🔹 Decimal
            if (targetType == typeof(decimal))
            {
                if (decimal.TryParse(value.ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return (T)(object)d;
                return default;
            }

            // 🔹 Fallback — handles int, long, bool, string, etc.
            // Convert.ChangeType correctly converts "6.0" → 6 for int targets
            return (T)Convert.ChangeType(value,
                        targetType,
                        System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return default;
        }
    }

    private static string? PrefixFacilityName(string? state, string? facilityName)
    {
        if (string.IsNullOrWhiteSpace(facilityName))
            return facilityName;

        var prefix = state?.Trim().ToLower() switch
        {
            "adamawa" => "ad ",
            "borno" => "bo ",
            "taraba" => "ta ",
            "yobe" => "yo ",
            _ => string.Empty
        };

        return prefix + facilityName;
    }
}

