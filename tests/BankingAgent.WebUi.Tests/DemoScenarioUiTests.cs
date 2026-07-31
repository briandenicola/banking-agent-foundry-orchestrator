using BankingAgent.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using webui.Pages;
using Xunit;

namespace BankingAgent.WebUi.Tests;

public sealed class DemoScenarioUiTests
{
    [Fact]
    public void IndexModel_ExposesEveryGuidedScenario()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("orchestrator"))
            .Returns(new HttpClient
            {
                BaseAddress = new Uri("https://orchestrator.example")
            });
        var model = new IndexModel(
            factory.Object,
            NullLogger<IndexModel>.Instance);

        Assert.Equal(
            DemoScenarioCatalog.All.Select(scenario => scenario.Id),
            model.DemoScenarios.Select(scenario => scenario.Id));
    }
}
