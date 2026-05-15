using PayDay.Services;

namespace PayDay.Tests;

public class PayoffCalculatorTests
{
    [Fact]
    public void EstimatePayoff_ZeroAPR_ReturnsCeilingOfOwedOverPayment()
    {
        Assert.Equal(10, PayoffCalculator.EstimatePayoff(owed: 1000, payment: 100, apr: 0));
        // Not divisible — ceiling
        Assert.Equal(11, PayoffCalculator.EstimatePayoff(owed: 1050, payment: 100, apr: 0));
    }

    [Fact]
    public void EstimatePayoff_PositiveAPR_MatchesAmortizationFormula()
    {
        // $1000 owed, $100/mo, 12% APR (1%/mo) → ~11 months
        var months = PayoffCalculator.EstimatePayoff(owed: 1000, payment: 100, apr: 12);
        Assert.NotNull(months);
        Assert.InRange(months!.Value, 10, 12);
    }

    [Fact]
    public void EstimatePayoff_PaymentBelowInterest_ReturnsNeverPaysOff()
    {
        // $1000 owed, 24% APR (2%/mo) → interest = $20/mo. Payment of $15 can't cover.
        var months = PayoffCalculator.EstimatePayoff(owed: 1000, payment: 15, apr: 24);
        Assert.Equal(int.MaxValue, months);
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(1000, 0, 0)]
    [InlineData(-100, 100, 0)]
    [InlineData(1000, -10, 0)]
    public void EstimatePayoff_InvalidInputs_ReturnNull(double owed, double payment, double apr)
    {
        Assert.Null(PayoffCalculator.EstimatePayoff(owed, payment, apr));
    }
}
