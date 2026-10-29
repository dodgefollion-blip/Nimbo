using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Nimbo;

public sealed class LocalStore
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nimbo");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string HistoryPath => Path.Combine(_directory, "history.json");

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings) => SaveAtomic(SettingsPath, settings);

    public List<CleanupRecord> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<CleanupRecord>>(File.ReadAllText(HistoryPath)) ?? [];
        }
        catch { }
        return [];
    }

    public void AppendHistory(CleanupRecord record)
    {
        var history = LoadHistory();
        history.Insert(0, record);
        SaveAtomic(HistoryPath, history.Take(200).ToList());
    }

    private void SaveAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(_directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, _json));
        File.Move(temporary, path, true);
    }
}
