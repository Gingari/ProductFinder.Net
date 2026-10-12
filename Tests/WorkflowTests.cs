using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using NPOI.SS.UserModel;
using NPOI.HSSF.UserModel;
using NPOI.XSSF.UserModel;
using ProductFinder.Models;
using ProductFinder.Services;
using ProductFinder.ViewModels;

internal static class WorkflowTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Load<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
    public static async Task Run(CatalogIndex index, IProductSearchService search)
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow-binding-check.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (var progressBar in xaml.Descendants(presentation + "ProgressBar"))
        {
            var binding = (string?)progressBar.Attribute("Value");
            if (binding?.StartsWith("{Binding ") == true) Check(binding.Contains("Mode=OneWay"), "Read-only progress must be OneWay");
        }
        Console.WriteLine("PASS explicit OneWay progress binding guard");
        foreach (var test in Load<List<ParserCase>>("request-parser-parity.json"))
        {
            Check(TextRequestParser.Preprocess(test.Text) == test.Preprocessed, "Preprocess: " + test.Text);
            Check(TextRequestParser.SplitItems(test.Text).SequenceEqual(test.Items), "Text items: " + test.Text);
            Check(TextRequestParser.LooksLikePriceTable(test.Text) == test.IsPriceTable, "Price detection");
            Check(TextRequestParser.ParsePriceTable(test.Text).SequenceEqual(test.PriceRows), "Price rows: " + test.Text);
        }
        foreach (var test in Load<List<QuantityCase>>("excel-quantity-parity.json"))
        {
            object? value = test.Value.ValueKind switch
            {
                JsonValueKind.Null => null, JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.Number => test.Value.GetDouble(), _ => test.Value.GetString()
            };
            Check(ExcelRowExtractor.ParseQuantity(value) == test.Expected, "Excel quantity: " + test.Value);
        }
        Console.WriteLine("PASS Python/C# request parsing and quantity rounding");
        var root = Path.Combine(Path.GetTempPath(), "ProductFinderExcel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var excel = new ExcelService();
            var output = Path.Combine(root, "Заказ.xlsx");
            var exportRows = new[] { new ExportRowSnapshot("001234", 2), new ExportRowSnapshot("=1+1", 3), new ExportRowSnapshot("СЕР-12345", 200001) };
            await excel.ExportAsync(output, exportRows, default);
            using (var stream = File.OpenRead(output))
            using (var book = WorkbookFactory.Create(stream))
            {
                var sheet = book.GetSheet("Товары");
                Check(book.NumberOfSheets == 1 && sheet.LastRowNum == 3, "Export structure");
                Check(sheet.GetRow(0).GetCell(0).StringCellValue == "Артикул" && sheet.GetRow(0).GetCell(1).StringCellValue == "Количество", "Export headers");
                Check(sheet.GetRow(1).GetCell(0).StringCellValue == "001234", "Leading zero preservation");
                Check(sheet.GetRow(2).GetCell(0).CellType == CellType.String && sheet.GetRow(2).GetCell(0).StringCellValue == "=1+1", "Formula-looking article stored as literal text");
                Check(sheet.GetRow(3).GetCell(1).CellType == CellType.Numeric && sheet.GetRow(3).GetCell(1).NumericCellValue == 200001, "Typed numeric quantity");
            }
            var previous = File.ReadAllBytes(output);
            try { await excel.ExportAsync(output, new[] { new ExportRowSnapshot("bad", 0) }, default); throw new Exception("Expected bad quantity error"); } catch (InvalidDataException) { }
            Check(File.ReadAllBytes(output).SequenceEqual(previous) && !Directory.GetFiles(root, "*.tmp").Any(), "Atomic export and temporary cleanup");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await excel.ExportAsync(output, exportRows, canceled.Token); throw new Exception("Expected canceled export"); } catch (OperationCanceledException) { }
            }
            Check(File.ReadAllBytes(output).SequenceEqual(previous), "Canceled export preserves destination");
            foreach (var extension in new[] { ".xlsx", ".xls" })
            {
                var path = Path.Combine(root, "Источник" + extension);
                CreateSource(path, extension == ".xls");
                var names = await excel.GetSheetNamesAsync(path, default);
                Check(names.SequenceEqual(new[] { "Примечание", "Заявка" }), "Sheet names: " + extension);
                var sheet = await excel.ReadSheetAsync(path, "Заявка", default);
                var header = ExcelRowExtractor.DetectHeader(sheet);
                var map = ExcelRowExtractor.DetectMapping(sheet, header);
                Check(header == 3 && map.ArticleColumn == 0 && map.NameColumn == 1 && map.QuantityColumn == 4, "Mapping: " + extension);
                var rows = ExcelRowExtractor.Extract(sheet, map);
                Check(rows.Count == 4 && rows[0].Article == "001234" && rows[0].QuantityText == "10", "Read and round: " + extension);
                Check(rows[1].QuantityText == "5", "Cached formula quantity: " + extension);
                Check(!rows[2].IsSelected && rows[3].QuantityText == "1", "Repeated header and default quantity: " + extension);
                rows[0].QuantityText = "8";
                var lines = ExcelRowExtractor.SelectedLines(rows);
                Check(lines.Count == 3 && lines[0].Quantity == 8, "Edited selection: " + extension);
                rows[0].QuantityText = "-1";
                try { ExcelRowExtractor.SelectedLines(rows); throw new Exception("Expected selection validation"); } catch (InvalidDataException) { }
            }
            var xlsm = Path.Combine(root, "Источник.xlsm");
            File.Copy(Path.Combine(root, "Источник.xlsx"), xlsm);
            using (var zip = ZipFile.Open(xlsm, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("[Content_Types].xml")!;
                XDocument xml;
                using (var source = entry.Open()) xml = XDocument.Load(source);
                foreach (var node in xml.Descendants().Where(e => (string?)e.Attribute("PartName") == "/xl/workbook.xml"))
                    node.SetAttributeValue("ContentType", "application/vnd.ms-excel.sheet.macroEnabled.main+xml");
                entry.Delete();
                using var target = zip.CreateEntry("[Content_Types].xml").Open();
                xml.Save(target);
            }
            Check((await excel.ReadSheetAsync(xlsm, "Заявка", default)).Rows.Count == 7, "XLSM cached-data import");
            Console.WriteLine("PASS XLSX/XLS/XLSM, sheets, columns, cached formulas and preview edits");
            Console.WriteLine("PASS export fields, typed values, leading zeros, literal text and atomic writes");
            var requests = new RequestImportService(search);
            var table = await requests.PrepareTableAsync(index, new[]
            {
                new RequestLine("ABC-12345", "исходное имя", 100000), new RequestLine("abc-12345", "другое", 100001),
                new RequestLine("MISSING-12345", "Нет в базе", 2), new RequestLine("", "Имя без артикула", 3)
            }, null, default);
            Check(table.InputCount == 4 && table.MergedCount == 3 && table.FoundCount == 1 && table.MissingCount == 2, "Duplicate summary");
            Check(table.Items[0].Quantity == 200001 && table.Items[0].Article == "ABC-12345" && table.Items[0].Confidence == ConfidenceState.Weak, "Merged canonical article");
            Check(table.Items[2].Article == "НЕ УКАЗАН" && table.Items[2].Confidence == ConfidenceState.Missing, "Name-only Excel row remains missing");
            Console.WriteLine("PASS duplicate merging, strict article lookup and missing row retention");
            var dialogs = new WorkflowDialogs { OpenPath = output, SavePath = Path.Combine(root, "От VM.xlsx"), ImportRows = new[] { new RequestLine("ABC-12345", "Автомат", 2), new RequestLine("abc-12345", "Автомат", 3) } };
            using (var vm = new MainWindowViewModel(new FixedLoader(index), search, dialogs, excel: excel))
            {
                await vm.InitializeAsync();
                Check(!vm.CanExport && vm.ImportExcelCommand.CanExecute(null), "Initial command enablement");
                await vm.ImportExcelAsync();
                Check(vm.ExportItems.Count == 1 && vm.ExportItems[0].Quantity == 5 && vm.CanExport, "VM Excel import command");
                await vm.ExportExcelAsync();
                Check(File.Exists(dialogs.SavePath), "VM export command");
                dialogs.TextRequest = new("ABC-12345 2 шт\nMISSING-12345 3 шт", SmartInsertMode.ArticlesOnly, false, false);
                await vm.SmartInsertAsync();
                Check(vm.ExportItems.Count == 2 && vm.ExportItems.Last().Article == "ABC-12345", "Strict text without missing rows");
                dialogs.TextRequest = dialogs.TextRequest with { KeepMissing = true };
                await vm.SmartInsertAsync();
                Check(vm.ExportItems.Count == 4 && vm.ExportItems.Last().Article == "АРТИКУЛ НЕ НАЙДЕН", "Strict text keeps missing");
                dialogs.TextRequest = new("Артикул\tНазвание\tКол-во\nABC-12345\tАвтомат\t2\nABC-12345\tАвтомат\t3", SmartInsertMode.Auto, false, false);
                await vm.SmartInsertAsync();
                Check(vm.ExportItems.Last().Quantity == 5, "Automatic pasted table and duplicate merge");
                var before = vm.ExportItems.Count;
                dialogs.TextRequest = new("Розетка 0 шт", SmartInsertMode.Auto, false, false);
                await vm.SmartInsertAsync();
                Check(vm.ExportItems.Count == before, "Zero text quantity skipped");
            }
            var scripted = new ScriptedSearch(index, search);
            dialogs.TextRequest = new("Успех 2 шт\nСомнение 3 шт\nПусто 4 шт\nСлабо 5 шт", SmartInsertMode.NamesOnly, true, false);
            dialogs.ChooseFirst = true;
            using (var vm = new MainWindowViewModel(new FixedLoader(index), scripted, dialogs, excel: excel))
            {
                await vm.InitializeAsync(); await vm.SmartInsertAsync();
                Check(vm.ExportItems.Count == 4 && scripted.LookupCalls == 0, "Names-only mode ignores exact article lookup");
                Check(vm.ExportItems[1].MatchExplanation.Contains("выбран пользователем") && vm.ExportItems[2].Article == "НЕ НАЙДЕНО" && vm.ExportItems[3].Confidence == ConfidenceState.Missing, "Interactive weak and unmatched branches");
                dialogs.ChooseFirst = false; dialogs.TextRequest = new("Сомнение 1 шт", SmartInsertMode.Auto, true, false);
                await vm.SmartInsertAsync();
                Check(vm.ExportItems.Last().Confidence == ConfidenceState.Weak && vm.ExportItems.Last().Candidates.Count > 0, "Canceled choice keeps best weak candidate");
            }
            dialogs.TextRequest = new("Сомнение 2 шт", SmartInsertMode.NamesOnly, false, false);
            var blocked = new BlockedSearch(search);
            using (var vm = new MainWindowViewModel(new FixedLoader(index), blocked, dialogs, excel: excel))
            {
                await vm.InitializeAsync();
                var task = vm.SmartInsertAsync();
                await blocked.Started.Task;
                Check(vm.IsProcessing && !vm.CanUseCatalog && vm.CancelOperationCommand.CanExecute(null), "Busy controls");
                vm.CancelOperationCommand.Execute(null);
                await task;
                Check(!vm.IsProcessing && vm.ExportItems.Count == 0 && vm.CanUseCatalog, "Atomic canceled import");
            }
            var pathRoot = Path.Combine(root, "Paths");Directory.CreateDirectory(pathRoot);
            File.WriteAllText(Path.Combine(pathRoot, "products.db"), "");Check(CatalogPathResolver.Resolve(pathRoot) == Path.Combine(pathRoot,"products.db"), "Root DB fallback");
            Directory.CreateDirectory(Path.Combine(pathRoot,"Resources"));File.WriteAllText(Path.Combine(pathRoot,"Resources","products.db"), "");
            Check(CatalogPathResolver.Resolve(pathRoot) == Path.Combine(pathRoot,"Resources","products.db"), "Resource DB priority");
            Console.WriteLine("PASS all workflow commands, four text modes, weak choice, cancellation and DB paths");
            var persistent = Path.Combine(AppContext.BaseDirectory,"export-preview.xlsx");File.Copy(output,persistent,true);
        }
        finally { Directory.Delete(root,true); }
    }
    private static void CreateSource(string path, bool xls)
    {
        using IWorkbook book = xls ? new HSSFWorkbook() : new XSSFWorkbook();
        book.CreateSheet("Примечание").CreateRow(0).CreateCell(0).SetCellValue("Описание");
        var sheet = book.CreateSheet("Заявка");sheet.CreateRow(0).CreateCell(0).SetCellValue("Заявка клиента");
        var header = sheet.CreateRow(2);var headers = new[] { "SKU", "Наименование", "Цена", "Склад шт", "Кол-во заказа" };
        for(var i=0;i<headers.Length;i++)header.CreateCell(i).SetCellValue(headers[i]);
        var first = sheet.CreateRow(3);first.CreateCell(0).SetCellValue("001234");first.CreateCell(1).SetCellValue("Розетка");first.CreateCell(4).SetCellValue(10.5);
        var second=sheet.CreateRow(4);second.CreateCell(0).SetCellValue("ABC-12345");second.CreateCell(1).SetCellValue("Автомат");var formula=second.CreateCell(4);formula.SetCellFormula("2+3");book.GetCreationHelper().CreateFormulaEvaluator().EvaluateFormulaCell(formula);
        var repeat=sheet.CreateRow(5);repeat.CreateCell(0).SetCellValue("Артикул");repeat.CreateCell(1).SetCellValue("Название");
        var last=sheet.CreateRow(6);last.CreateCell(0).SetCellValue("MISSING-12345");last.CreateCell(1).SetCellValue("Реле");last.CreateCell(4).SetCellValue("abc");
        using var stream=File.Create(path);book.Write(stream,leaveOpen:true);
    }
    private sealed record ParserCase(string Text,string Preprocessed,TextRequestItem[] Items,bool IsPriceTable,RequestLine[] PriceRows);
    private sealed record QuantityCase(JsonElement Value,int? Expected);
    private sealed class FixedLoader(CatalogIndex index):ICatalogLoader
    { public Task<CatalogLoadResult> LoadAsync(string path,IProgress<CatalogLoadProgress>? progress=null,CancellationToken cancellationToken=default)=>Task.FromResult(new CatalogLoadResult(index,false,null)); }
    private sealed class WorkflowDialogs:IDialogService
    {
        public string? OpenPath {get;set;}public string? SavePath {get;set;}public SmartInsertRequest? TextRequest {get;set;}
        public IReadOnlyList<RequestLine>? ImportRows {get;set;}public bool ChooseFirst {get;set;}
        public List<string> Messages {get;}=new();
        public string? OpenExcelFile()=>OpenPath;public string? SelectExportPath()=>SavePath;public SmartInsertRequest? RequestSmartInsert()=>TextRequest;
        public IReadOnlyList<RequestLine>? EditExcel(string path,IReadOnlyList<string> sheets)=>ImportRows;
        public void ShowError(string message)=>throw new Exception("Unexpected UI error: "+message);
        public void ShowInformation(string message)=>Messages.Add(message);
        public int? RequestQuantity(string name)=>1;
        public SearchResult? ChooseCandidate(string text,IReadOnlyList<SearchResult> candidates)=>ChooseFirst?candidates.FirstOrDefault():null;
    }
    private sealed class ScriptedSearch(CatalogIndex index,IProductSearchService actual):IProductSearchService
    {
        public int LookupCalls {get;private set;}
        public int? LookupArticle(CatalogIndex catalog,string article,bool strict=false){LookupCalls++;return actual.LookupArticle(catalog,article,strict);}
        public Task<IReadOnlyList<SearchResult>> SearchAsync(CatalogIndex catalog,string query,int limit,CancellationToken token)=>actual.SearchAsync(catalog,query,limit,token);
        public Task<IReadOnlyList<SearchResult>> FindCandidatesAsync(CatalogIndex catalog,string query,int limit,CancellationToken token)
        {
            IReadOnlyList<SearchResult> result=query.StartsWith("Успех")?new[]{new SearchResult(0,index.Products[0],.8)}:query.StartsWith("Сомнение")?new[]{new SearchResult(0,index.Products[0],.5)}:query.StartsWith("Слабо")?new[]{new SearchResult(0,index.Products[0],.1)}:Array.Empty<SearchResult>();
            return Task.FromResult(result);
        }
    }
    private sealed class BlockedSearch(IProductSearchService actual):IProductSearchService
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int? LookupArticle(CatalogIndex index,string article,bool strict=false)=>actual.LookupArticle(index,article,strict);
        public Task<IReadOnlyList<SearchResult>> SearchAsync(CatalogIndex index,string query,int limit,CancellationToken token)=>actual.SearchAsync(index,query,limit,token);
        public async Task<IReadOnlyList<SearchResult>> FindCandidatesAsync(CatalogIndex index,string query,int limit,CancellationToken token){Started.SetResult();await Task.Delay(Timeout.Infinite,token);return Array.Empty<SearchResult>();}
    }
}
