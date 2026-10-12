using System;
using System.Collections.Generic;
using System.Globalization;
using ProductFinder.Infrastructure;

namespace ProductFinder.Models;

public enum SelectionOrigin { Search, Article, Manual, Alternative, Scored, Unmatched, External }
public enum ConfidenceState { Normal, Confirmed, Weak, Missing }

public sealed class ExportItem : ObservableObject
{
    private string _article;
    private string _name;
    private string _manufacturer;
    private int _quantity;
    private double? _score;
    private SelectionOrigin _origin;
    private string _sourceUrl = "";
    private SearchResult? _assessment;

    public ExportItem(string article, int quantity, string name, string manufacturer,
        double? score, string sourceText, SelectionOrigin origin)
    {
        _article = article;
        _name = name;
        _manufacturer = manufacturer;
        _score = score;
        _origin = origin;
        SourceText = sourceText;
        Quantity = quantity;
    }

    public string Article => _article;
    public string Name => _name;
    public string Manufacturer => string.IsNullOrEmpty(_manufacturer) ? "—" : _manufacturer;
    public string SourceText { get; }
    public string SourceUrl => _sourceUrl;
    public bool CanParseEtm => !string.IsNullOrWhiteSpace(SourceText) || !string.IsNullOrWhiteSpace(Name);
    public bool NeedsEtmParsing => CanParseEtm;
    public double Score => _score ?? (_origin is SelectionOrigin.Article or SelectionOrigin.Alternative ? 1 : 0);
    public ConfidenceState DisplayConfidence => _assessment?.HasSpecConflict == true ? ConfidenceState.Missing : _assessment?.NeedsReview == true ? (MatchQuality.Classify(Score) == ConfidenceState.Missing ? ConfidenceState.Missing : ConfidenceState.Weak) : _origin switch
    {
        SelectionOrigin.Article or SelectionOrigin.Alternative or SelectionOrigin.External => ConfidenceState.Confirmed,
        SelectionOrigin.Manual or SelectionOrigin.Unmatched => ConfidenceState.Missing,
        _ => MatchQuality.Classify(Score)
    };
    public string MatchLabel => _assessment?.HasSpecConflict == true ? "Есть противоречия" : _assessment?.NeedsReview == true ? "Нужно проверить" : _origin switch
    {
        SelectionOrigin.External => "Выбрано на ЭТМ",
        SelectionOrigin.Manual or SelectionOrigin.Unmatched => "Нет в каталоге",
        SelectionOrigin.Article => "Найден по артикулу",
        SelectionOrigin.Alternative => "Выбрано пользователем",
        _ => MatchQuality.Label(DisplayConfidence)
    };
    public string MatchExplanation => _origin == SelectionOrigin.External
        ? "Товар выбран пользователем на ЭТМ. Источник: " + _sourceUrl
        : _assessment?.MatchExplanation ?? MatchLabel + ". Процент — оценка поиска, не вероятность.";
    public IReadOnlyList<SearchResult> Candidates { get; private set; } = Array.Empty<SearchResult>();
    public int Quantity
    {
        get => _quantity;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(Quantity), "Количество должно быть положительным целым числом.");
            SetProperty(ref _quantity, value);
        }
    }
    public string MatchText => _assessment?.NeedsReview == true ? MatchQuality.Percent(Score) : _origin switch
    {
        SelectionOrigin.External => "ЭТМ",
        SelectionOrigin.Article => "по артикулу",
        SelectionOrigin.Manual => "вручную",
        SelectionOrigin.Alternative => "выбрано",
        _ => (Math.Max(_score ?? 0, 0) * 100).ToString("F0", CultureInfo.InvariantCulture) + "%"
    };
    public ConfidenceState Confidence => _assessment?.HasSpecConflict == true ? ConfidenceState.Missing : _assessment?.NeedsReview == true ? (MatchQuality.Classify(Score) == ConfidenceState.Missing ? ConfidenceState.Missing : ConfidenceState.Weak) : _origin switch
    {
        SelectionOrigin.Article or SelectionOrigin.Alternative or SelectionOrigin.External => ConfidenceState.Confirmed,
        SelectionOrigin.Manual or SelectionOrigin.Unmatched => ConfidenceState.Missing,
        _ when _score < 0.34 => ConfidenceState.Missing,
        _ when _score < 0.62 => ConfidenceState.Weak,
        _ => ConfidenceState.Normal
    };
    public void SetCandidates(IReadOnlyList<SearchResult> candidates) => Candidates = candidates;
    public void SelectAlternative(SearchResult result)
    {
        _article = result.Article;
        _name = result.Name;
        _manufacturer = result.Product.Manufacturer;
        _score = null;
        _origin = SelectionOrigin.Alternative;
        _sourceUrl = "";
        _assessment = result;
        if (result.NeedsReview) _score = result.Score;
        foreach (var name in new[] { nameof(Article), nameof(Name), nameof(Manufacturer), nameof(MatchText), nameof(Confidence), nameof(DisplayConfidence), nameof(MatchLabel), nameof(MatchExplanation), nameof(Score), nameof(NeedsEtmParsing), nameof(CanParseEtm), nameof(SourceUrl) })
            OnPropertyChanged(name);
    }

    public void ApplyAssessment(SearchResult result)
    {
        _assessment = result;
        if (result.NeedsReview || _origin == SelectionOrigin.Scored) _score = result.Score;
        foreach (var name in new[] { nameof(MatchText), nameof(Confidence), nameof(DisplayConfidence), nameof(MatchLabel), nameof(MatchExplanation), nameof(Score) }) OnPropertyChanged(name);
    }

    public void ApplyEtmResult(EtmSaveResult result)
    {
        _article = result.Product.Article;
        _name = result.Product.Name;
        _manufacturer = result.Product.Manufacturer;
        _sourceUrl = result.SourceUrl;
        _origin = result.Added || result.Replaced ? SelectionOrigin.External : SelectionOrigin.Article;
        _score = _origin == SelectionOrigin.Article ? 1 : null;
        _assessment = null;
        Candidates = Array.Empty<SearchResult>();
        foreach (var name in new[] { nameof(Article), nameof(Name), nameof(Manufacturer), nameof(MatchText), nameof(Confidence), nameof(DisplayConfidence), nameof(MatchLabel), nameof(MatchExplanation), nameof(Score), nameof(NeedsEtmParsing), nameof(CanParseEtm), nameof(SourceUrl) })
            OnPropertyChanged(name);
    }
}
