namespace GameHelper.Plugin
{
    public interface IPSettings;
}

namespace GameHelper.Plugin.Price
{
    public interface IPriceProvider;

    public static class PriceProviderRegistry
    {
        private static object? owner;
        public static IPriceProvider? Current { get; private set; }

        public static bool TryRegister(object providerOwner, IPriceProvider provider)
        {
            if (owner is not null && !ReferenceEquals(owner, providerOwner)) return false;
            owner = providerOwner;
            Current = provider;
            return true;
        }

        public static bool Unregister(object providerOwner)
        {
            if (!ReferenceEquals(owner, providerOwner)) return false;
            owner = null;
            Current = null;
            return true;
        }
    }
}
