using System.Text.Json;

namespace Aegis;

public enum AppearanceChoice { Light, Dark, System }

public sealed class AppearancePreferences
{
    private readonly string _path;

    public AppearancePreferences(string directory) => _path = Path.Combine(directory, "appearance.json");

    public AppearanceChoice Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<string>(File.ReadAllText(_path));
            return value switch
            {
                nameof(AppearanceChoice.Dark) => AppearanceChoice.Dark,
                nameof(AppearanceChoice.System) => AppearanceChoice.System,
                _ => AppearanceChoice.Light
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppearanceChoice.Light;
        }
    }

    public bool Save(AppearanceChoice choice)
    {
        if (!Enum.IsDefined(choice)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(choice.ToString()));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
