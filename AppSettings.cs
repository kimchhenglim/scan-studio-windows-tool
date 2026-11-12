using System.Text.Json;

namespace ScanStudio;

public sealed class AppSettings
{
    public string ScannerId { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.Pdf;
    public ColorMode ColorMode { get; set; } = ColorMode.Color;
    public int Dpi { get; set; } = 300;
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans");
    public bool MultiPage { get; set; } = true;
    public bool OpenFolderAfterSave { get; set; } = true;
    public int SettingsPanelWidth { get; set; } = 380;
    public CropShape CropShape { get; set; } = CropShape.Rectangle;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScanStudio", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-critical */ }
    }
}
