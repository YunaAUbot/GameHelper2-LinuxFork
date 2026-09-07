namespace NinjaPricer;

using GameHelper.Plugin.Price;

public sealed class ProviderRegistration
{
    private readonly object owner;
    private readonly IPriceProvider provider;
    private bool isRegistered;

    public ProviderRegistration(object owner, IPriceProvider provider)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public bool IsRegistered => this.isRegistered;

    public bool Register()
    {
        this.isRegistered = PriceProviderRegistry.TryRegister(this.owner, this.provider);
        return this.isRegistered;
    }

    public bool Unregister()
    {
        if (!this.isRegistered)
        {
            return false;
        }

        var removed = PriceProviderRegistry.Unregister(this.owner);
        this.isRegistered = false;
        return removed;
    }
}
