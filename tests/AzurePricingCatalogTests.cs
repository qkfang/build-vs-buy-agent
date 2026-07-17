using Xunit;
using Proj37.CostEstimator.Web.Services;

namespace Proj37.CostEstimator.Tests;

public class AzurePricingCatalogTests
{
    [Fact]
    public void Region_and_currency_are_set()
    {
        Assert.Equal("australiaeast", AzurePricingCatalog.Region);
        Assert.Equal("USD", AzurePricingCatalog.Currency);
    }

    [Theory]
    [InlineData("B2")]
    [InlineData("P1v3")]
    [InlineData("S1")]
    public void AppServicePlan_known_skus_have_positive_price(string sku)
    {
        Assert.True(AzurePricingCatalog.AppServicePlanMonthly[sku] > 0);
    }

    [Fact]
    public void GetModelRate_falls_back_to_gpt4o_for_unknown()
    {
        var unknown = AzurePricingCatalog.GetModelRate("does-not-exist");
        var gpt4o = AzurePricingCatalog.GetModelRate("gpt-4o");
        Assert.Equal(gpt4o.InputPer1K, unknown.InputPer1K);
        Assert.Equal(gpt4o.OutputPer1K, unknown.OutputPer1K);
    }

    [Fact]
    public void Model_rates_are_positive()
    {
        var r = AzurePricingCatalog.GetModelRate("gpt-4o");
        Assert.True(r.InputPer1K > 0);
        Assert.True(r.OutputPer1K > 0);
    }
}
