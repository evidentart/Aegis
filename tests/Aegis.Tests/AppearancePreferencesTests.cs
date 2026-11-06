using Aegis;
using Xunit;

namespace Aegis.Tests;

public sealed class AppearancePreferencesTests
{
    [Fact]
    public void DefaultsToLightAndRemembersAValidChoice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AegisAppearanceTests", Guid.NewGuid().ToString());
        try
        {
            var preferences = new AppearancePreferences(directory);
            Assert.Equal(AppearanceChoice.Light, preferences.Load());
            Assert.True(preferences.Save(AppearanceChoice.System));
            Assert.Equal(AppearanceChoice.System, new AppearancePreferences(directory).Load());
            File.WriteAllText(Path.Combine(directory, "appearance.json"), "invalid json");
            Assert.Equal(AppearanceChoice.Light, preferences.Load());
        }
        finally
        {
            var file = Path.Combine(directory, "appearance.json");
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
