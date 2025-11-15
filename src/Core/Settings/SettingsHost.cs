using System.Threading;

namespace ClopWindows.Core.Settings;

public static class SettingsHost
{
    private static readonly Lazy<SettingsStore> LazyStore = new(() => new SettingsStore(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static SettingsStore Store => LazyStore.Value;

    public static void EnsureInitialized() => _ = Store;

    public static T Get<T>(SettingKey<T> key) => Store.Get(key);

    public static void Set<T>(SettingKey<T> key, T value) => Store.Set(key, value);

    public static SettingsSnapshot Snapshot => Store.Snapshot();

    public static event EventHandler<SettingChangedEventArgs> SettingChanged
    {
        add => Store.SettingChanged += value;
        remove => Store.SettingChanged -= value;
    }
}
