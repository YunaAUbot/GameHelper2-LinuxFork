using GameHelper.Plugin.Price;
using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class RegistryLifecycleTests
{
    [Fact]
    public void LifecycleFailsClosedAndOnlyUnregistersItsOwnRegistration()
    {
        var incumbentOwner = new object();
        var incumbent = new StubProvider();
        Assert.True(PriceProviderRegistry.TryRegister(incumbentOwner, incumbent));

        var contenderOwner = new object();
        var contender = new StubProvider();
        var lifecycle = new ProviderRegistration(contenderOwner, contender);

        Assert.False(lifecycle.Register());
        Assert.Same(incumbent, PriceProviderRegistry.Current);
        Assert.False(lifecycle.Unregister());
        Assert.Same(incumbent, PriceProviderRegistry.Current);

        Assert.True(PriceProviderRegistry.Unregister(incumbentOwner));
        Assert.True(lifecycle.Register());
        Assert.Same(contender, PriceProviderRegistry.Current);
        Assert.True(lifecycle.Unregister());
        Assert.Null(PriceProviderRegistry.Current);
    }

    private sealed class StubProvider : IPriceProvider;
}
