using System.Text.Json;
using System.Security.Cryptography;
using Kickl.Core.Session;
using Kickl.Core.Util;

namespace Kickl.Core.News;

/// <summary>
/// The reading list stores only what the reader explicitly saved:
/// public card metadata, never page contents, never read state, never a recommendation.
/// </summary>
public sealed class SavedItemsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly BrowserSession _session;

    public SavedItemsStore(string dataRoot, BrowserSession session)
    {
        var paths = new DataPaths(dataRoot);
        _path = Path.Combine(paths.ProfileRoot, "private", "saved-items.kpf");
        _legacyPath = Path.Combine(paths.Root, "shared", "pocket.json");
        _session = session;
    }

    public string PathOnDisk => _path;

    public List<NewsItem> Load()
    {
        if (!_session.IsUnlocked) return [];
        try
        {
            if (!File.Exists(_path))
            {
                MigrateLegacyFile();
                if (!File.Exists(_path)) return [];
            }
            var envelope = File.ReadAllBytes(_path);
            var plaintext = _session.OpenProfileData("saved-items", envelope);
            SavedItemsFile? file;
            try { file = JsonSerializer.Deserialize<SavedItemsFile>(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            return file?.Items ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    public bool Contains(string id) => Load().Any(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    public void Save(NewsItem item)
    {
        var items = Load();
        items.RemoveAll(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal));
        items.Add(item);
        Write(items);
    }

    public void Remove(string id)
    {
        var items = Load();
        if (items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.Ordinal)) > 0)
            Write(items);
    }

    public void Toggle(NewsItem item)
    {
        if (Contains(item.Id)) Remove(item.Id);
        else Save(item);
    }

    private void Write(List<NewsItem> items)
    {
        if (!_session.IsUnlocked) throw new InvalidOperationException("Unlock the profile before saving items.");
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var sorted = items
            .OrderByDescending(i => i.PublishedUtc)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SavedItemsFile { Items = sorted }, Json);
        var encrypted = _session.ProtectProfileData("saved-items", plain);
        CryptographicOperations.ZeroMemory(plain);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void MigrateLegacyFile()
    {
        if (!string.Equals(_session.Paths.ProfileId, "local", StringComparison.Ordinal)) return;
        if (!File.Exists(_legacyPath)) return;
        var legacy = JsonSerializer.Deserialize<SavedItemsFile>(File.ReadAllText(_legacyPath));
        if (legacy is null) return;
        Write(legacy.Items);
        File.Delete(_legacyPath);
    }

    private sealed class SavedItemsFile
    {
        public int Version { get; set; } = 1;
        public List<NewsItem> Items { get; set; } = [];
    }
}
