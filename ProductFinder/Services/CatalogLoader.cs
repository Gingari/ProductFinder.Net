using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

using ProductFinder.Models;

namespace ProductFinder.Services;


public sealed class CatalogLoader : ICatalogLoader
{
    private const int CacheVersion = 1;
    private const string Table = "catalog";
    private const string ArticleColumn = "Артикул";
    private const string NameColumn = "Название товара";
    private const string ManufacturerColumn = "Производитель";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public Task<CatalogLoadResult> LoadAsync(string databasePath,
        IProgress<CatalogLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);
        return Task.Run(() => Load(fullPath, progress, cancellationToken), cancellationToken);
    }

    private static CatalogLoadResult Load(string path, IProgress<CatalogLoadProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw new FileNotFoundException("Положите products.db рядом с исполняемым файлом приложения.", path);

        var initialStamp = GetStamp(path);
        var cachePath = GetCachePath(path);
        progress?.Report(new(null, "Проверка кэша каталога…"));
        if (!HasWal(path))
        {
            var cached = TryReadCache(cachePath, path, initialStamp, token);
            if (cached is not null && !HasWal(path) && GetStamp(path) == initialStamp)
            {
                progress?.Report(new(100, "Каталог загружен из кэша"));
                return new(new CatalogIndex(cached), true, null);
            }
        }

        progress?.Report(new(0, "Чтение и индексация каталога…"));
        var data = BuildIndex(path, progress, token);
        token.ThrowIfCancellationRequested();
        string? warning = null;
        if (!HasWal(path) && GetStamp(path) == initialStamp)
        {
            progress?.Report(new(99, "Сохранение кэша…"));
            warning = TryWriteCache(cachePath, path, initialStamp, data, token);
        }
        token.ThrowIfCancellationRequested();
        var index = new CatalogIndex(data);
        progress?.Report(new(100, "Каталог загружен"));
        return new(index, false, warning);
    }

    private static CatalogIndexData BuildIndex(string path, IProgress<CatalogLoadProgress>? progress, CancellationToken token)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 10
        }.ToString());
        connection.Open();
        token.ThrowIfCancellationRequested();
        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY; " +
                                  "PRAGMA cache_size=-131072; PRAGMA mmap_size=268435456;";
            pragmas.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction(deferred: true);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = $"PRAGMA table_info(\"{Table}\")";
            using var reader = schema.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (columns.Count == 0) throw new InvalidDataException("В products.db отсутствует таблица catalog.");
        foreach (var required in new[] { ArticleColumn, NameColumn })
            if (!columns.Contains(required)) throw new InvalidDataException($"В таблице catalog отсутствует колонка «{required}».");
        var data = new CatalogIndexData { HasManufacturer = columns.Contains(ManufacturerColumn) };
        long totalRows;
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT(*) FROM \"{Table}\"";
            totalRows = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = data.HasManufacturer
            ? $"SELECT \"{ArticleColumn}\", \"{NameColumn}\", \"{ManufacturerColumn}\" FROM \"{Table}\""
            : $"SELECT \"{ArticleColumn}\", \"{NameColumn}\" FROM \"{Table}\"";
        using (var reader = select.ExecuteReader())
        {
            var lastPercent = -1;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var article = ReadText(reader, 0);
                var name = ReadText(reader, 1);
                var manufacturer = data.HasManufacturer ? ReadText(reader, 2) : "";
                var articleKey = CatalogText.Normalize(article).Replace(" ", "");
                var tokens = CatalogText.Tokenize(CatalogText.Normalize($"{name} {manufacturer}"));
                var rowId = data.Products.Count;
                data.Products.Add(new CatalogProduct
                {
                    Article = article, Name = name, ManufacturerRaw = manufacturer,
                    ArticleKey = articleKey, NormalizedName = CatalogText.Normalize(name),
                    SearchTokens = tokens, Specs = CatalogText.ExtractSpecs(name)
                });
                if (articleKey.Length > 0)
                {
                    data.ByArticle.TryAdd(articleKey, rowId);
                    data.ByArticleNoDash.TryAdd(articleKey.Replace("-", ""), rowId);
                }
                foreach (var term in tokens)
                {
                    if (!data.Postings.TryGetValue(term, out var ids))
                        data.Postings[term] = ids = new List<int>();
                    ids.Add(rowId);
                }
                var percent = totalRows > 0 ? (int)Math.Min(98, (long)data.Products.Count * 98 / totalRows) : 98;
                if (percent != lastPercent)
                {
                    progress?.Report(new(percent, $"Загрузка каталога: {percent}%"));
                    lastPercent = percent;
                }
            }
        }
        transaction.Commit();
        token.ThrowIfCancellationRequested();
        data.Vocabulary = data.Postings.Where(p => p.Value.Count <= 5000 && p.Key.Length >= 4)
            .Select(p => p.Key).ToList();
        return data;
    }

    private static string ReadText(SqliteDataReader reader, int column) => reader.IsDBNull(column)
        ? "" : (Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? "").Trim();

    private static FileStamp GetStamp(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }
    private static bool HasWal(string path) => File.Exists(path + "-wal");
    private static string GetCachePath(string path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProductFinder", "CatalogCache", key + ".json");
    }
    private static CatalogIndexData? TryReadCache(string cachePath, string databasePath, FileStamp stamp, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(cachePath);
            var cache = JsonSerializer.Deserialize<CatalogCache>(stream, JsonOptions);
            token.ThrowIfCancellationRequested();
            if (cache is null || cache.Version != CacheVersion || cache.DatabasePath != databasePath ||
                cache.Length != stamp.Length || cache.LastWriteUtcTicks != stamp.LastWriteUtcTicks || cache.Data is null)
                return null;
            return IsValid(cache.Data, token) ? cache.Data : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
    private static bool IsValid(CatalogIndexData data, CancellationToken token)
    {
        if (data.Products is null || data.Postings is null || data.ByArticle is null ||
            data.ByArticleNoDash is null || data.Vocabulary is null) return false;
        foreach (var product in data.Products)
        {
            token.ThrowIfCancellationRequested();
            if (product is null || product.Article is null || product.Name is null ||
                product.ManufacturerRaw is null || product.NormalizedName is null || product.ArticleKey is null ||
                product.SearchTokens is null || product.SearchTokens.Any(t => t is null) || product.Specs is null ||
                product.Specs.Values.Any(v => v is null || v.Any(s => s is null))) return false;
        }
        foreach (var ids in data.Postings.Values)
        {
            token.ThrowIfCancellationRequested();
            if (ids is null || ids.Any(id => id < 0 || id >= data.Products.Count)) return false;
        }
        return !data.Vocabulary.Any(v => v is null) &&
            data.ByArticle.Values.All(id => id >= 0 && id < data.Products.Count) &&
            data.ByArticleNoDash.Values.All(id => id >= 0 && id < data.Products.Count);
    }
    private static string? TryWriteCache(string cachePath, string databasePath, FileStamp stamp,
        CatalogIndexData data, CancellationToken token)
    {
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new CatalogCache
                {
                    Version = CacheVersion, DatabasePath = databasePath,
                    Length = stamp.Length, LastWriteUtcTicks = stamp.LastWriteUtcTicks, Data = data
                }, JsonOptions);
            }
            token.ThrowIfCancellationRequested();
            if (HasWal(databasePath) || GetStamp(databasePath) != stamp) return null;
            File.Move(tempPath, cachePath, overwrite: true);
            return null;
        }
        catch (IOException e) { return e.Message; }
        catch (UnauthorizedAccessException e) { return e.Message; }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private sealed record FileStamp(long Length, long LastWriteUtcTicks);
    private sealed class CatalogCache
    {
        public CatalogCache() { }
        public int Version { get; set; }
        public string DatabasePath { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public CatalogIndexData? Data { get; set; }
    }
}
