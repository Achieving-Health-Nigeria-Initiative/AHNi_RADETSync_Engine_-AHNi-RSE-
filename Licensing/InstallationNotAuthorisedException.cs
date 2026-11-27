namespace AHNiRSE.Licensing;

/// <summary>
/// A force backfill stopped by the licence check (this PC is not registered in BayCentral for its DATIM ID).
/// BackfillWorker puts the job back to pending, without using an attempt, so it runs once the PC is accepted.
/// </summary>
public sealed class InstallationNotAuthorisedException(string message) : InvalidOperationException(message);
