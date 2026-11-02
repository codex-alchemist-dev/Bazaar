using Bazaar.Client.LocalInference.Benchmark;

namespace Bazaar.Client.LocalInference.Tests.Benchmark;

public class BenchmarkStatisticsTests
{
    [Fact]
    public void Nearest_rank_p50_and_p95_for_a_known_list()
    {
        double[] values = [15, 20, 35, 40, 50];

        Assert.Equal(35.0, BenchmarkStatistics.NearestRankPercentile(values, 50));
        Assert.Equal(50.0, BenchmarkStatistics.NearestRankPercentile(values, 95));
    }

    [Fact]
    public void Nearest_rank_uses_unsorted_input_and_ten_values()
    {
        double[] values = [9, 1, 10, 2, 8, 3, 7, 4, 6, 5];

        Assert.Equal(5.0, BenchmarkStatistics.NearestRankPercentile(values, 50));
        Assert.Equal(10.0, BenchmarkStatistics.NearestRankPercentile(values, 95));
        Assert.Equal(1.0, BenchmarkStatistics.NearestRankPercentile(values, 10));
    }

    [Fact]
    public void Single_value_is_every_percentile()
    {
        double[] values = [12.5];

        Assert.Equal(12.5, BenchmarkStatistics.NearestRankPercentile(values, 50));
        Assert.Equal(12.5, BenchmarkStatistics.NearestRankPercentile(values, 95));
    }

    [Fact]
    public void Empty_list_has_no_percentile()
    {
        double[] values = [];

        Assert.Null(BenchmarkStatistics.NearestRankPercentile(values, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void Percentile_outside_zero_to_hundred_is_rejected(double percentile)
    {
        double[] values = [1, 2];

        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStatistics.NearestRankPercentile(values, percentile));
    }
}
