using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ProductFinder.Services;

public static class WritableCatalogStorage
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductFinder", "Catalog", "products.db");
    public static Task<string> EnsureAsync(string seedPath, string? destinationPath = null, CancellationToken token = default) => Task.Run(() =>
    {
        var destination = Path.GetFullPath(destinationPath ?? DefaultPath);
        if (File.Exists(destination)) return destination;
        if (!File.Exists(seedPath)) throw new FileNotFoundException("Не найдена исходная products.db. Положите её в Resources рядом с приложением.", seedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = seedPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 15 }.ToString()))
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                source.Open(); copy.Open(); source.BackupDatabase(copy);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);
}
