using System;
using System.Collections.Generic;
using System.Text;

namespace AHNiRSE.Core.Models
{
    public class Radet
    {
        public string? State { get; set; }
        public string? Lga { get; set; }
        public string? LgaResidence { get; set; }
        public string? FacilityName { get; set; }
        public string? DatimId { get; set; }
        public string? PatientId { get; set; }
        public string? NdrPatientIdentifier { get; set; }
        public string? HospitalNumber { get; set; }
        public string? UniqueId { get; set; }
        public string? HouseholdUniqueNo { get; set; }
        public string? OvcUniqueId { get; set; }
        public string? Sex { get; set; }
        public string? TargetGroup { get; set; }
        public decimal? CurrentWeightKg { get; set; }
        public string? PregnancyStatus { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public int? Age { get; set; }
        public string? CareEntryPoint { get; set; }
        public DateTime? DateOfRegistration { get; set; }
        public DateTime? EnrollmentDate { get; set; }
        public DateTime? ArtStartDate { get; set; }
        public DateTime? LastPickupDate { get; set; }
        public decimal? MonthsOfArvRefill { get; set; }
        public string? RegimenLineAtArtStart { get; set; }
        public string? RegimenAtArtStart { get; set; }
        public DateTime? DateStartCurrentArtRegimen { get; set; }
        public string? CurrentRegimenLine { get; set; }
        public string? CurrentArtRegimen { get; set; }
        public string? ClinicalStagingAtLastVisit { get; set; }
        public DateTime? DateLastCd4Count { get; set; }
        public string? LastCd4Count { get; set; }
        public DateTime? DateVlSampleCollection { get; set; }
        public DateTime? DateCurrentVlSample { get; set; }
        // Normalized integer value for Current Viral Load (replaces raw string values).
        // Use long to safely store large viral-load counts.
        public long? CurrentViralLoad { get; set; }
        public DateTime? DateCurrentViralLoad { get; set; }
        public string? ViralLoadIndication { get; set; }
        public string? ViralLoadEligibilityStatus { get; set; }
        public DateTime? DateVlEligibilityStatus { get; set; }
        public string? CurrentArtStatus { get; set; }
        public DateTime? DateCurrentArtStatus { get; set; }
        public string? ClientVerificationOutcome { get; set; }
        public string? CauseOfDeath { get; set; }
        public string? VaCauseOfDeath { get; set; }
        public string? PreviousArtStatus { get; set; }
        public DateTime? ConfirmedDatePreviousArtStatus { get; set; }
        public string? ArtEnrollmentSetting { get; set; }
        public DateTime? DateTbScreening { get; set; }
        public string? TbScreeningType { get; set; }
        public int? CadScore { get; set; }
        public string? TbStatus { get; set; }
        public DateTime? DateTbSampleCollection { get; set; }
        public string? TbDiagnosticTestType { get; set; }
        public DateTime? DateTbResultReceived { get; set; }
        public string? TbDiagnosticResult { get; set; }
        public DateTime? DateXrayTbDiagnosis { get; set; }
        public string? XrayTbResult { get; set; }
        public DateTime? DateTbTreatmentStart { get; set; }
        public string? TbType { get; set; }
        public DateTime? DateTbTreatmentCompletion { get; set; }
        public string? TbTreatmentOutcome { get; set; }
        public string? EligibleForTpt { get; set; }
        public DateTime? DateTptStart { get; set; }
        public string? TptType { get; set; }
        public DateTime? TptCompletionDate { get; set; }
        public string? TptCompletionStatus { get; set; }
        public DateTime? DateEacStart { get; set; }
        public int? NumberOfEacSessions { get; set; }
        public DateTime? DateLastEacSession { get; set; }
        public DateTime? DateExtendedEacCompletion { get; set; }
        public DateTime? DateRepeatVlSample { get; set; }
        public string? RepeatViralLoadPostEac { get; set; }
        public DateTime? DateRepeatVlResult { get; set; }
        public DateTime? DateDevolvement { get; set; }
        public string? ModelDevolvedTo { get; set; }
        public DateTime? DateCurrentDsd { get; set; }
        public string? CurrentDsdModel { get; set; }
        public string? CurrentDsdOutlet { get; set; }
        public DateTime? DateReturnToFacility { get; set; }
        public string? ScreeningForChronicConditions { get; set; }
        public string? CoMorbidities { get; set; }
        public DateTime? DateCervicalCancerScreening { get; set; }
        public string? CervicalCancerScreeningType { get; set; }
        public string? CervicalCancerScreeningMethod { get; set; }
        public string? CervicalCancerScreeningResult { get; set; }
        public DateTime? DatePrecancerTreatment { get; set; }
        public string? PrecancerTreatmentMethod { get; set; }
        public DateTime? DateBiometricsEnrolled { get; set; }
        public int? FingersCaptured { get; set; }
        public DateTime? DateBiometricsRecapture { get; set; }
        public int? FingersRecaptured { get; set; }
        public string? CaseManager { get; set; }
    }
}

