using System;
using System.Collections.Generic;

namespace ProductFinder.Models;

public sealed record EtmCandidate(string Name, string Manufacturer, string Url, string SelectorKey, string Query)
{
    public string LinkLabel => Url.Length > 0 ? "Карточка товара" : "Открыть карточку";
}
public sealed record EtmProduct(string Article, string Name, string Manufacturer, string Url);
public sealed record CatalogEntry(long RowId, string Article, string Name, string Manufacturer);
public enum EtmDuplicateAction { Keep, Replace }
public sealed record EtmDuplicateDecision(EtmDuplicateAction Action, CatalogEntry Existing);
public sealed record EtmSaveResult(CatalogEntry Product, string SourceUrl, bool Added, bool Replaced, string? BackupPath);
public sealed class CatalogChangedException() : InvalidOperationException("Каталог изменился после проверки. Нажмите «Сохранить» ещё раз и подтвердите актуальную запись.");
public sealed class EtmAccessDeniedException(string message) : InvalidOperationException(message);
public sealed record EtmSearchSnapshot(string Query, IReadOnlyList<EtmCandidate> Items, string Error, bool Empty);
