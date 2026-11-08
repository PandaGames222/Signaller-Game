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

    private static readonly string SaveDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Signaller");
    private static readonly string LegacySavePath = Path.Combine(SaveDirectory, "game-state.json");

    public static GameSaveData? Load(GameStyle style)
    {
        var path = SavePathFor(style);
        if (!File.Exists(path))
        {
            if (style != GameStyle.Original || !File.Exists(LegacySavePath)) return null;
            path = LegacySavePath;
        }

        var data = JsonSerializer.Deserialize<GameSaveData>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The saved game file is empty.");
        if ((data.Version >= GameEngine.SaveVersion && data.Style != style)
            || (data.Version < GameEngine.SaveVersion && style != GameStyle.Original))
            throw new InvalidDataException($"The saved game does not belong to {style} mode.");
        return data;
    }

    public static void Save(GameSaveData data, GameStyle style)
    {
        if (data.Style != style)
            throw new InvalidDataException("The saved game style does not match its save slot.");
        Directory.CreateDirectory(SaveDirectory);

        var path = SavePathFor(style);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var contents = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        using (var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string SavePathFor(GameStyle style) =>
        Path.Combine(SaveDirectory, $"game-state-{style.ToString().ToLowerInvariant()}.json");
}
