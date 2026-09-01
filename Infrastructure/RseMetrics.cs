using Prometheus;

namespace AHNiRSE.Infrastructure;

public static class RseMetrics
{
    public static readonly Counter FacilitiesProcessed = Metrics
        .CreateCounter("rse_facilities_processed_total", "Total facilities successfully processed");

    public static readonly Counter FacilitiesFailed = Metrics
        .CreateCounter("rse_facilities_failed_total", "Total facilities that failed processing");

    public static readonly Counter FacilitiesSkipped = Metrics
        .CreateCounter("rse_facilities_skipped_total", "Total facilities skipped (already succeeded)");

    public static readonly Gauge LastCycleDurationSeconds = Metrics
        .CreateGauge("rse_last_cycle_duration_seconds", "Duration of the last full cycle in seconds");

    public static readonly Gauge LastCycleTimestamp = Metrics
        .CreateGauge("rse_last_cycle_timestamp_seconds", "Unix timestamp of last completed cycle");

    public static readonly Histogram FacilityDurationSeconds = Metrics
        .CreateHistogram("rse_facility_duration_seconds", "Per-facility processing duration",
            new HistogramConfiguration { Buckets = Histogram.LinearBuckets(0, 30, 20) });
}
