using BLEStudio.Core;

namespace BLEStudio.Infrastructure;

public sealed class ProfileStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public IReadOnlyList<AdvertisementProfile> LoadAll(Action<string>? onError = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var results = new List<AdvertisementProfile>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try { results.Add(ProfileJson.Deserialize(File.ReadAllText(file))); }
            catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException) { onError?.Invoke($"Could not read profile {Path.GetFileName(file)}: {ex.Message}"); }
        }
        return results.OrderByDescending(x => x.ModifiedAt).ToArray();
    }
    public void Save(AdvertisementProfile profile)
    {
        _ = ProfileJson.Deserialize(ProfileJson.Serialize(profile));
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, $"{profile.Id:D}.json");
        if (File.Exists(path))
        {
            var old = ProfileJson.Deserialize(File.ReadAllText(path));
            var revisions = Path.Combine(DirectoryPath, "revisions", profile.Id.ToString("D"));
            Directory.CreateDirectory(revisions);
            File.Copy(path, Path.Combine(revisions, $"{old.Revision}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json"), true);
            profile.Revision = Math.Max(profile.Revision, old.Revision + 1);
        }
        profile.ModifiedAt = DateTimeOffset.UtcNow;
        AtomicFile.WriteText(path, ProfileJson.Serialize(profile));
    }
    public void Delete(Guid id) => File.Delete(Path.Combine(DirectoryPath, $"{id:D}.json"));
    public IReadOnlyList<string> Revisions(Guid id)
    {
        var path = Path.Combine(DirectoryPath, "revisions", id.ToString("D"));
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.json").OrderDescending().ToArray() : [];
    }
}

public static class AtomicFile
{
    public static void WriteText(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
