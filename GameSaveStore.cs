using System.IO;
using System.Text.Json;
using Signaller.GameLogic;

namespace Signaller;

internal static class GameSaveStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Signaller",
        "game-state.json");

    public static GameSaveData? Load()
    {
        if (!File.Exists(SavePath)) return null;

        var data = JsonSerializer.Deserialize<GameSaveData>(File.ReadAllText(SavePath), JsonOptions);
        return data ?? throw new InvalidDataException("The saved game file is empty.");
    }

    public static void Save(GameSaveData data)
    {
        var directory = Path.GetDirectoryName(SavePath)
            ?? throw new InvalidOperationException("The save directory could not be determined.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{SavePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporaryPath, SavePath, overwrite: true);
    }
}
