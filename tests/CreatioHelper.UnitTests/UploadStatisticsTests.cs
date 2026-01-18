using CreatioHelper.Domain.Entities.Statistics;
using Xunit;

namespace CreatioHelper.Tests;

public class UploadStatisticsTests
{
    [Fact]
    public void NewStatistics_HasDefaultValues()
    {
        var stats = new UploadStatistics();

        Assert.Equal(0, stats.TotalUploads);
        Assert.Equal(0, stats.SuccessfulUploads);
        Assert.Equal(0, stats.FailedUploads);
        Assert.Equal(0, stats.TotalBytesUploaded);
        Assert.Equal(0, stats.BytesSavedByDelta);
        Assert.Equal(100.0, stats.SuccessRate);
        Assert.Null(stats.LastFileName);
        Assert.Null(stats.LastUploadAt);
    }

    [Fact]
    public void RecordUpload_IncrementsCounters()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("test.txt", 1000, 2000, 5, 3, true, TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, stats.TotalUploads);
        Assert.Equal(1, stats.SuccessfulUploads);
        Assert.Equal(0, stats.FailedUploads);
        Assert.Equal(1000, stats.TotalBytesUploaded);
        Assert.Equal(2000, stats.TotalBytesWithoutDelta);
        Assert.Equal(1000, stats.BytesSavedByDelta);
        Assert.Equal(5, stats.TotalBlocksTransferred);
        Assert.Equal(3, stats.BlocksSkippedByDelta);
        Assert.Equal(1, stats.DeltaUploads);
        Assert.Equal(0, stats.FullUploads);
        Assert.Equal("test.txt", stats.LastFileName);
        Assert.NotNull(stats.LastUploadAt);
    }

    [Fact]
    public void RecordUpload_FullUpload_IncrementsFullUploadsCounter()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("test.txt", 5000, 5000, 10, 0, false, TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, stats.FullUploads);
        Assert.Equal(0, stats.DeltaUploads);
        Assert.Equal(5000, stats.TotalBytesUploaded);
        Assert.Equal(0, stats.BytesSavedByDelta);
    }

    [Fact]
    public void RecordFailure_IncrementsFailedCounter()
    {
        var stats = new UploadStatistics();

        stats.RecordFailure("test.txt", "Connection error");

        Assert.Equal(1, stats.TotalUploads);
        Assert.Equal(0, stats.SuccessfulUploads);
        Assert.Equal(1, stats.FailedUploads);
        Assert.Equal("test.txt", stats.LastFileName);
        Assert.Equal("Connection error", stats.LastError);
        Assert.NotNull(stats.LastErrorAt);
    }

    [Fact]
    public void SuccessRate_CalculatesCorrectly()
    {
        var stats = new UploadStatistics();

        // 3 successful, 1 failed = 75% success rate
        stats.RecordUpload("file1.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(10));
        stats.RecordUpload("file2.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(10));
        stats.RecordUpload("file3.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(10));
        stats.RecordFailure("file4.txt", "Error");

        Assert.Equal(75.0, stats.SuccessRate);
    }

    [Fact]
    public void DeltaUploadRatio_CalculatesCorrectly()
    {
        var stats = new UploadStatistics();

        // 2 delta, 1 full = 66.67% delta ratio
        stats.RecordUpload("file1.txt", 100, 200, 1, 1, true, TimeSpan.FromMilliseconds(10));
        stats.RecordUpload("file2.txt", 150, 300, 1, 1, true, TimeSpan.FromMilliseconds(10));
        stats.RecordUpload("file3.txt", 500, 500, 5, 0, false, TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, stats.DeltaUploads);
        Assert.Equal(1, stats.FullUploads);
        Assert.Equal(66.666666666666671, stats.DeltaUploadRatio, 5);
    }

    [Fact]
    public void DeltaEfficiency_CalculatesCorrectly()
    {
        var stats = new UploadStatistics();

        // Total without delta: 1000, actual uploaded: 400, saved: 600 = 60% efficiency
        stats.RecordUpload("file1.txt", 200, 500, 2, 3, true, TimeSpan.FromMilliseconds(10));
        stats.RecordUpload("file2.txt", 200, 500, 2, 3, true, TimeSpan.FromMilliseconds(10));

        Assert.Equal(400, stats.TotalBytesUploaded);
        Assert.Equal(1000, stats.TotalBytesWithoutDelta);
        Assert.Equal(600, stats.BytesSavedByDelta);
        Assert.Equal(60.0, stats.DeltaEfficiency);
    }

    [Fact]
    public void AverageUploadTime_CalculatesCorrectly()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("file1.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(100));
        stats.RecordUpload("file2.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(200));
        stats.RecordUpload("file3.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(300));

        // Average of 100, 200, 300 = 200ms
        Assert.Equal(200.0, stats.AverageUploadTimeMs);
    }

    [Fact]
    public void AverageUploadSpeed_CalculatesCorrectly()
    {
        var stats = new UploadStatistics();

        // 1000 bytes in 100ms = 10000 bytes/second
        stats.RecordUpload("file1.txt", 1000, 1000, 1, 0, false, TimeSpan.FromMilliseconds(100));

        Assert.Equal(10000.0, stats.AverageUploadSpeedBytesPerSecond);
    }

    [Fact]
    public void FastestAndSlowestUpload_TrackedCorrectly()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("file1.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(150));
        stats.RecordUpload("file2.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(50));
        stats.RecordUpload("file3.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(300));

        Assert.Equal(50, stats.FastestUploadMs);
        Assert.Equal(300, stats.SlowestUploadMs);
    }

    [Fact]
    public void Reset_ClearsAllValues()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("file1.txt", 1000, 2000, 5, 3, true, TimeSpan.FromMilliseconds(100));
        stats.RecordFailure("file2.txt", "Error");

        stats.Reset();

        Assert.Equal(0, stats.TotalUploads);
        Assert.Equal(0, stats.SuccessfulUploads);
        Assert.Equal(0, stats.FailedUploads);
        Assert.Equal(0, stats.TotalBytesUploaded);
        Assert.Equal(0, stats.BytesSavedByDelta);
        Assert.Null(stats.LastFileName);
        Assert.Null(stats.LastError);
        Assert.Null(stats.LastUploadAt);
    }

    [Fact]
    public void ToSummary_ReturnsExpectedKeys()
    {
        var stats = new UploadStatistics();
        stats.RecordUpload("test.txt", 500, 1000, 3, 2, true, TimeSpan.FromMilliseconds(100));

        var summary = stats.ToSummary();

        Assert.Contains("totalUploads", summary.Keys);
        Assert.Contains("successfulUploads", summary.Keys);
        Assert.Contains("failedUploads", summary.Keys);
        Assert.Contains("successRate", summary.Keys);
        Assert.Contains("totalBytesUploaded", summary.Keys);
        Assert.Contains("bytesSavedByDelta", summary.Keys);
        Assert.Contains("deltaEfficiency", summary.Keys);
        Assert.Contains("fullUploads", summary.Keys);
        Assert.Contains("deltaUploads", summary.Keys);
        Assert.Contains("deltaUploadRatio", summary.Keys);
        Assert.Contains("averageUploadTimeMs", summary.Keys);
        Assert.Contains("averageSpeedFormatted", summary.Keys);
    }

    [Fact]
    public void GetFormattedBytesUploaded_FormatsCorrectly()
    {
        var stats = new UploadStatistics();

        // Test KB - use Contains to handle locale-specific decimal separator
        stats.RecordUpload("file1.txt", 5000, 5000, 1, 0, false, TimeSpan.FromMilliseconds(10));
        Assert.Contains("5", stats.GetFormattedBytesUploaded());
        Assert.Contains("KB", stats.GetFormattedBytesUploaded());

        // Reset and test MB
        stats.Reset();
        stats.RecordUpload("file2.txt", 5_000_000, 5_000_000, 1, 0, false, TimeSpan.FromMilliseconds(10));
        Assert.Contains("5", stats.GetFormattedBytesUploaded());
        Assert.Contains("MB", stats.GetFormattedBytesUploaded());

        // Reset and test GB
        stats.Reset();
        stats.RecordUpload("file3.txt", 5_000_000_000, 5_000_000_000, 1, 0, false, TimeSpan.FromMilliseconds(10));
        Assert.Contains("5", stats.GetFormattedBytesUploaded());
        Assert.Contains("GB", stats.GetFormattedBytesUploaded());
    }

    [Fact]
    public void FirstUploadAt_SetOnlyOnFirstUpload()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("file1.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(10));
        var firstUploadTime = stats.FirstUploadAt;

        Thread.Sleep(10);
        stats.RecordUpload("file2.txt", 100, 100, 1, 0, false, TimeSpan.FromMilliseconds(10));

        Assert.Equal(firstUploadTime, stats.FirstUploadAt);
        Assert.NotEqual(firstUploadTime, stats.LastUploadAt);
    }

    [Fact]
    public void MultipleUploads_AggregatesCorrectly()
    {
        var stats = new UploadStatistics();

        stats.RecordUpload("file1.txt", 1000, 2000, 5, 5, true, TimeSpan.FromMilliseconds(100));
        stats.RecordUpload("file2.txt", 500, 500, 3, 0, false, TimeSpan.FromMilliseconds(50));
        stats.RecordUpload("file3.txt", 2000, 4000, 10, 10, true, TimeSpan.FromMilliseconds(200));
        stats.RecordFailure("file4.txt", "Error");

        Assert.Equal(4, stats.TotalUploads);
        Assert.Equal(3, stats.SuccessfulUploads);
        Assert.Equal(1, stats.FailedUploads);
        Assert.Equal(3500, stats.TotalBytesUploaded);
        Assert.Equal(6500, stats.TotalBytesWithoutDelta);
        Assert.Equal(3000, stats.BytesSavedByDelta);
        Assert.Equal(18, stats.TotalBlocksTransferred);
        Assert.Equal(15, stats.BlocksSkippedByDelta);
        Assert.Equal(2, stats.DeltaUploads);
        Assert.Equal(1, stats.FullUploads);
    }
}
