using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CreatioHelper.Tests;

public class MediatorHandlersTests
{
    [Fact]
    public async Task LoadSettingsHandler_ReturnsSettings()
    {
        var expected = new AppSettings { SitePath = "s" };
        var serviceMock = new Mock<ISettingsService>();
        serviceMock.Setup(s => s.Load()).Returns(expected);
        var handler = new LoadSettingsHandler(serviceMock.Object);

        var result = await handler.Handle(new LoadSettingsQuery(), CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SaveSettingsHandler_InvokesService()
    {
        var serviceMock = new Mock<ISettingsService>();
        var handler = new SaveSettingsHandler(serviceMock.Object);
        var settings = new AppSettings();

        await handler.Handle(new SaveSettingsCommand(settings), CancellationToken.None);

        serviceMock.Verify(s => s.Save(settings), Times.Once);
    }

    [Fact]
    public async Task Mediator_Dispatches_To_Handler()
    {
        var expected = new AppSettings { SitePath = "p" };
        var serviceMock = new Mock<ISettingsService>();
        serviceMock.Setup(s => s.Load()).Returns(expected);

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(serviceMock.Object);
        services.AddTransient<IRequestHandler<LoadSettingsQuery, AppSettings>, LoadSettingsHandler>();
        services.AddTransient<IMediator, Mediator>();
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new LoadSettingsQuery());

        Assert.Equal(expected, result);
    }
}
