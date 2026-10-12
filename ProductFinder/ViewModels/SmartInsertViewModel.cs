using System.Collections.Generic;
using ProductFinder.Infrastructure;
using ProductFinder.Models;

namespace ProductFinder.ViewModels;

public sealed class SmartInsertViewModel : ObservableObject
{
    private string _text = "";
    private SmartInsertMode _mode;
    private bool _askForWeakMatch = true;
    private bool _keepMissing;
    public IReadOnlyList<SmartInsertModeOption> Modes { get; } = new[]
    {
        new SmartInsertModeOption(SmartInsertMode.Auto, "Авто: артикул, иначе название"),
        new SmartInsertModeOption(SmartInsertMode.ArticlesOnly, "Только артикулы — строго"),
        new SmartInsertModeOption(SmartInsertMode.NamesOnly, "Только названия — без поиска артикула"),
        new SmartInsertModeOption(SmartInsertMode.PriceTable, "Таблица прайса: артикул + название + количество")
    };
    public string Text { get => _text; set { if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(CanAccept)); } }
    public SmartInsertMode Mode
    {
        get => _mode;
        set { if (SetProperty(ref _mode, value)) { OnPropertyChanged(nameof(CanKeepMissing)); OnPropertyChanged(nameof(CanAskForWeakMatch)); } }
    }
    public bool AskForWeakMatch { get => _askForWeakMatch; set => SetProperty(ref _askForWeakMatch, value); }
    public bool KeepMissing { get => _keepMissing; set => SetProperty(ref _keepMissing, value); }
    public bool CanKeepMissing => Mode == SmartInsertMode.ArticlesOnly;
    public bool CanAskForWeakMatch => Mode is SmartInsertMode.Auto or SmartInsertMode.NamesOnly;
    public bool CanAccept => !string.IsNullOrWhiteSpace(Text);
    public SmartInsertRequest CreateRequest() => new(Text, Mode, AskForWeakMatch && CanAskForWeakMatch, KeepMissing && CanKeepMissing);
}
