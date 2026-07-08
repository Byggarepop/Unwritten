using ConventionSense.Core;

namespace ConventionSense.Core.Tests;

public class WilsonTests
{
    [Theory]
    // Known values from the validated Python prototype.
    [InlineData(15, 17, 0.66, 0.01)]
    [InlineData(21, 22, 0.78, 0.01)]
    public void MatchesKnownValues(int positives, int n, double expected, double tolerance)
    {
        Assert.InRange(Wilson.LowerBound(positives, n), expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void ZeroTrialsReturnsZero()
    {
        Assert.Equal(0.0, Wilson.LowerBound(0, 0));
    }

    [Fact]
    public void ZeroPositivesReturnsZero()
    {
        Assert.Equal(0.0, Wilson.LowerBound(0, 50), precision: 10);
    }

    [Fact]
    public void PerfectProportionStaysBelowOne()
    {
        double value = Wilson.LowerBound(10, 10);
        Assert.InRange(value, 0.0, 0.999);
    }

    [Fact]
    public void PenalizesSmallSamples()
    {
        // Same proportion (100%), but more evidence must score higher.
        Assert.True(Wilson.LowerBound(30, 30) > Wilson.LowerBound(3, 3));
    }

    [Fact]
    public void IsMonotonicInPositivesForFixedN()
    {
        Assert.True(Wilson.LowerBound(18, 20) > Wilson.LowerBound(12, 20));
    }
}
