using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ProductFinder.Models;

namespace ProductFinder.Services;

public sealed class CatalogWriteService(string databasePath, string? backupDirectory = null)
{
    private readonly string _path = Path.GetFullPath(databasePath);
    private readonly string _backupDirectory = backupDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductFinder", "CatalogBackups");
    private static string Key(string value) => CatalogText.Normalize(value).Replace(" ", "").Replace("-", "");
    private SqliteConnection Open(bool writable)
    {
        if (!File.Exists(_path)) throw new FileNotFoundException("Рабочая база не найдена.", _path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 15, ForeignKeys = true
        }.ToString());
        connection.Open();
        connection.CreateFunction<string, string>("pf_article_key", Key, isDeterministic: true);
        return connection;
    }
    private static HashSet<string> Columns(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(\"catalog\")";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("Артикул") || !columns.Contains("Название товара"))
            throw new InvalidDataException("Ожидается таблица catalog с колонками «Артикул» и «Название товара».");
        return columns;
    }
    private static IReadOnlyList<CatalogEntry> Find(SqliteConnection connection, string article, SqliteTransaction? transaction = null)
    {
        var columns = Columns(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var manufacturer = columns.Contains("Производитель") ? "COALESCE(\"Производитель\",'')" : "''";
        command.CommandText = $"SELECT rowid, COALESCE(\"Артикул\",''), COALESCE(\"Название товара\",''), {manufacturer} FROM \"catalog\" WHERE pf_article_key(\"Артикул\")=$key ORDER BY rowid";
        command.Parameters.AddWithValue("$key", Key(article));
        using var reader = command.ExecuteReader();
        var entries = new List<CatalogEntry>();
        while (reader.Read()) entries.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return entries;
    }
    public Task<IReadOnlyList<CatalogEntry>> FindAsync(string article, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open(false);
        return Find(connection, article);
    }, token);
    public Task<EtmSaveResult> SaveAsync(EtmProduct product, IReadOnlyList<CatalogEntry> expected, EtmDuplicateDecision? decision, CancellationToken token) => Task.Run(() =>
    {
        Validate(product);
        token.ThrowIfCancellationRequested();
        using var connection = Open(decision?.Action != EtmDuplicateAction.Keep);
        var before = Find(connection, product.Article);
        if (!before.SequenceEqual(expected)) throw new CatalogChangedException();
        if (before.Count > 0)
        {
            if (decision is null || !before.Contains(decision.Existing)) throw new CatalogChangedException();
            if (decision.Action == EtmDuplicateAction.Keep)
                return new EtmSaveResult(decision.Existing, "", false, false, null);
        }
        else if (decision is not null) throw new CatalogChangedException();
        Directory.CreateDirectory(_backupDirectory);
        var backup = Path.Combine(_backupDirectory, "products-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        { destination.Open(); connection.BackupDatabase(destination); }
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: false);
        var actual = Find(connection, product.Article, transaction);
        if (!actual.SequenceEqual(expected)) throw new CatalogChangedException();
        var columns = Columns(connection, transaction);
        if (!columns.Contains("Производитель"))
        {
            using var alter = connection.CreateCommand(); alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE \"catalog\" ADD COLUMN \"Производитель\" TEXT"; alter.ExecuteNonQuery();
        }
        using var write = connection.CreateCommand(); write.Transaction = transaction;
        if (actual.Count == 0)
            write.CommandText = "INSERT INTO \"catalog\" (\"Артикул\",\"Название товара\",\"Производитель\") VALUES ($article,$name,$manufacturer)";
        else
        {
            write.CommandText = "UPDATE \"catalog\" SET \"Артикул\"=$article,\"Название товара\"=$name,\"Производитель\"=$manufacturer WHERE rowid=$row";
            write.Parameters.AddWithValue("$row", decision!.Existing.RowId);
        }
        write.Parameters.AddWithValue("$article", product.Article.Trim());
        write.Parameters.AddWithValue("$name", product.Name.Trim());
        write.Parameters.AddWithValue("$manufacturer", product.Manufacturer.Trim());
        if (write.ExecuteNonQuery() != 1) throw new CatalogChangedException();
        var rowId = decision?.Existing.RowId;
        if (rowId is null)
        { using var id = connection.CreateCommand(); id.Transaction = transaction; id.CommandText = "SELECT last_insert_rowid()"; rowId = Convert.ToInt64(id.ExecuteScalar()); }
        using var audit = connection.CreateCommand(); audit.Transaction = transaction;
        audit.CommandText = "CREATE TABLE IF NOT EXISTS pf_etm_import_log (id INTEGER PRIMARY KEY, article TEXT NOT NULL, source_url TEXT NOT NULL, imported_at TEXT NOT NULL, action TEXT NOT NULL);";
        audit.ExecuteNonQuery();
        audit.CommandText = "INSERT INTO pf_etm_import_log (article,source_url,imported_at,action) VALUES ($article,$url,$time,$action)";
        audit.Parameters.AddWithValue("$article", product.Article.Trim()); audit.Parameters.AddWithValue("$url", product.Url);
        audit.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); audit.Parameters.AddWithValue("$action", actual.Count == 0 ? "insert" : "replace"); audit.ExecuteNonQuery();
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return new EtmSaveResult(new(rowId.Value, product.Article.Trim(), product.Name.Trim(), product.Manufacturer.Trim()), product.Url, actual.Count == 0, actual.Count > 0, backup);
    }, token);
    private static void Validate(EtmProduct product)
    {
        if (string.IsNullOrWhiteSpace(product.Article) || string.IsNullOrWhiteSpace(product.Name) || string.IsNullOrWhiteSpace(product.Manufacturer))
            throw new InvalidDataException("Для сохранения нужны название, артикул и производитель из карточки ЭТМ.");
        if (product.Article.Length > 256 || product.Name.Length > 2000 || product.Manufacturer.Length > 500)
            throw new InvalidDataException("Данные карточки превышают допустимую длину.");
        EtmUrl.Validate(product.Url);
    }
}
