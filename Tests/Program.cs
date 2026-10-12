using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using ProductFinder.Models;
using ProductFinder.Services;
using ProductFinder.ViewModels;
using Microsoft.Data.Sqlite;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static T Load<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)))!;
foreach (var t in Load<List<TextCase>>("text-parity.json"))
{
    Check(CatalogText.Normalize(t.Text) == t.Normalized, "Normalize: " + t.Text);
    Check(CatalogText.Tokenize(t.Normalized).Order().SequenceEqual(t.Tokens.Order()), "Tokens: " + t.Text);
    Check(CatalogText.ExtractCodes(t.Normalized).Order().SequenceEqual(t.Codes.Order()), "Codes: " + t.Text);
    Check(CatalogText.ExtractArticleCandidates(t.Text).SequenceEqual(t.ArticleCandidates), "Article candidates: " + t.Text);
    var specs = CatalogText.ExtractSpecs(t.Text);
    Check(specs.Count == t.Specs.Count && t.Specs.All(p => specs.TryGetValue(p.Key, out var actual) && actual.Order().SequenceEqual(p.Value.Order())), "Specs: " + t.Text);
}
Console.WriteLine("PASS Python/C# text normalization, tokens, codes, articles and specs");
foreach (var t in Load<List<SequenceCase>>("sequence-parity.json"))
{
    var matcher = new SequenceMatcher(t.First, t.Second);
    Check(Math.Abs(matcher.Ratio() - t.Ratio) < 1e-12, "Sequence ratio");
    Check(Math.Abs(matcher.QuickRatio() - t.Quick) < 1e-12, "Quick ratio");
    Check(Math.Abs(matcher.RealQuickRatio() - t.RealQuick) < 1e-12, "Real quick ratio");
}
Console.WriteLine("PASS difflib parity, Unicode and autojunk");
var loader = new CatalogLoader();
var db = Path.Combine(AppContext.BaseDirectory, "fixture.db");
var index = (await loader.LoadAsync(db)).Index;
var search = new ProductSearchService();
await SearchQualityTests.Run(index, search);
var dialogs = new FakeDialogs();
using (var vm = new MainWindowViewModel(new FakeLoader(index), search, dialogs))
{
    Check(!vm.CanUseCatalog, "Initially unloaded");
    await vm.InitializeAsync();
    Check(vm.IsCatalogReady && vm.CanUseCatalog && !vm.IsLoading, "Catalog ready");
    vm.SearchQuery = "ABC-12345";
    await vm.SearchAsync();
    Check(vm.SearchResults.Any(r => r.Article == "ABC-12345" && r.Score == 1), "VM search");
    vm.AddSearchResult(vm.SearchResults[0]);
    Check(vm.ExportItems.Count == 1 && vm.ExportItems[0].Quantity == 2, "Add searched product");
    var row = vm.ExportItems[0];
    var failed = false;
    try { row.Quantity = 0; } catch(ArgumentOutOfRangeException) { failed = true; }
    Check(failed && row.Quantity == 2, "Invalid quantity rejected");
    row.Quantity = 7;
    vm.ManualArticle = "ABC-12345"; vm.ManualQuantity = "3"; vm.AddManualItem();
    Check(vm.ExportItems.Count == 2 && vm.ExportItems[1].MatchText == "100%", "Manual known article");
    vm.ManualArticle = "MISSING-12345"; vm.ManualQuantity = "2"; vm.AddManualItem();
    Check(vm.ExportItems.Count == 3 && vm.ExportItems[2].Confidence == ConfidenceState.Missing, "Manual missing article retained");
    vm.ManualArticle = "ABC-12345"; vm.ManualQuantity = "-1"; vm.AddManualItem();
    Check(vm.ExportItems.Count == 3 && dialogs.Messages.Count > 0, "Invalid manual quantity");
    await vm.ChooseAlternativeAsync(row);
    Check(row.MatchExplanation.Contains("выбран пользователем") && row.Quantity == 7 && row.SourceText == "ABC-12345", "Alternative preserves source and quantity");
    vm.SetSelectedExportItems(vm.ExportItems.Take(2).ToArray());
    vm.DeleteSelectedCommand.Execute(null);
    Check(vm.ExportItems.Count == 1 && !vm.DeleteSelectedCommand.CanExecute(null), "Multiple deletion");
}
Console.WriteLine("PASS ViewModel search, quantity, manual add, alternatives and deletion");
var controlled = new ControlledSearch(search);
using (var vm = new MainWindowViewModel(new FakeLoader(index), controlled, dialogs))
{
    await vm.InitializeAsync();
    vm.SearchQuery = "old"; var oldTask = vm.SearchAsync();
    vm.SearchQuery = "new"; var newTask = vm.SearchAsync();
    controlled.New.SetResult(new[] { new SearchResult(1, index.Products[1], .9) });
    await newTask;
    controlled.Old.SetResult(new[] { new SearchResult(0, index.Products[0], .8) });
    await oldTask;
    Check(vm.SearchResults.Count == 1 && vm.SearchResults[0].RowId == 1 && !vm.IsSearching, "Old result cannot overwrite new");
}
var delayed = new DelayedLoader();
using (var vm = new MainWindowViewModel(delayed, search, dialogs))
{
    var loading = vm.InitializeAsync(); vm.Dispose(); delayed.Completion.SetResult(new(index, false, null)); await loading;
    Check(!vm.IsCatalogReady, "Closed VM ignores late loading");
}
Console.WriteLine("PASS stale search rejection and close during loading");
var temp = Path.Combine(Path.GetTempPath(), "ProductFinderTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var databases = new List<string>();
string CachePath(string path) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductFinder", "CatalogCache", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".json");
void Db(string path, string sql) { databases.Add(path); using var c=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Pooling=false}.ToString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery(); }
try
{
    var legacy=Path.Combine(temp,"legacy.db");Db(legacy,"CREATE TABLE catalog (\"Артикул\" TEXT, \"Название товара\" TEXT); INSERT INTO catalog VALUES ('OLD-12345','Реле');");
    var loaded=await loader.LoadAsync(legacy);Check(!loaded.FromCache && loaded.Index.Count==1 && !loaded.Index.HasManufacturer,"Legacy DB");
    Check((await loader.LoadAsync(legacy)).FromCache,"Cache hit");
    File.WriteAllText(CachePath(legacy),"{broken}");Check(!(await loader.LoadAsync(legacy)).FromCache,"Corrupt cache fallback");
    File.SetLastWriteTimeUtc(legacy,DateTime.UtcNow.AddSeconds(3));Check(!(await loader.LoadAsync(legacy)).FromCache,"Cache invalidation");
    var empty=Path.Combine(temp,"empty.db");Db(empty,"CREATE TABLE catalog (\"Артикул\" TEXT, \"Название товара\" TEXT);");Check((await loader.LoadAsync(empty)).Index.Count==0,"Empty DB");
    var bad=Path.Combine(temp,"bad.db");Db(bad,"CREATE TABLE catalog (other TEXT);");
    try { await loader.LoadAsync(bad);throw new Exception("Expected schema error"); }catch(InvalidDataException){}
    try { await loader.LoadAsync(Path.Combine(temp,"missing.db"));throw new Exception("Expected file error"); }catch(FileNotFoundException){}
    using var cts=new CancellationTokenSource();cts.Cancel();
    try { await loader.LoadAsync(legacy,cancellationToken:cts.Token);throw new Exception("Expected cancel"); }catch(OperationCanceledException){}
    var wal=Path.Combine(temp,"wal.db");databases.Add(wal);
    using(var writer=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=wal,Pooling=false}.ToString()))
    {
        writer.Open();using var cmd=writer.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE catalog (\"Артикул\" TEXT, \"Название товара\" TEXT); INSERT INTO catalog VALUES ('WAL-12345','Реле');";cmd.ExecuteNonQuery();
        Check((await loader.LoadAsync(wal)).Index.Count==1 && !File.Exists(CachePath(wal)),"Read WAL without cache");
    }
}
finally { foreach(var path in databases) if(File.Exists(CachePath(path))) File.Delete(CachePath(path)); Directory.Delete(temp,true); }
Console.WriteLine("PASS cache, optional columns, empty DB, errors, cancellation and WAL");
if (File.Exists(CachePath(db))) File.Delete(CachePath(db));
await WorkflowTests.Run(index, search);
Console.WriteLine("ALL TESTS PASSED");
sealed record TextCase(string Text,string Normalized,string[] Tokens,string[] Codes,string[] ArticleCandidates,Dictionary<string,string[]> Specs);
sealed record SequenceCase(string First,string Second,double Ratio,double Quick,double RealQuick);
sealed record Expected(string Article,string Name,string Manufacturer,double Score);
sealed record SearchCase(string Query,Expected[] Match,Expected[] Search,int? Lookup,int? StrictLookup);
sealed class FakeLoader(CatalogIndex index):ICatalogLoader
{ public Task<CatalogLoadResult> LoadAsync(string path,IProgress<CatalogLoadProgress>? progress=null,CancellationToken cancellationToken=default) => Task.FromResult(new CatalogLoadResult(index,false,null)); }
sealed class DelayedLoader:ICatalogLoader
{
    public TaskCompletionSource<CatalogLoadResult> Completion {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<CatalogLoadResult> LoadAsync(string path,IProgress<CatalogLoadProgress>? progress=null,CancellationToken cancellationToken=default)=>Completion.Task;
}
sealed class FakeDialogs:IDialogService
{
    public List<string> Messages {get;}=new();
    public void ShowError(string message)=>Messages.Add(message);
    public void ShowInformation(string message)=>Messages.Add(message);
    public string? OpenExcelFile()=>null;
    public string? SelectExportPath()=>null;
    public SmartInsertRequest? RequestSmartInsert()=>null;
    public IReadOnlyList<RequestLine>? EditExcel(string path,IReadOnlyList<string> sheets)=>null;
    public int? RequestQuantity(string productName)=>2;
    public SearchResult? ChooseCandidate(string sourceText,IReadOnlyList<SearchResult> candidates)=>candidates.LastOrDefault();
}
sealed class ControlledSearch(IProductSearchService search):IProductSearchService
{
    public TaskCompletionSource<IReadOnlyList<SearchResult>> Old {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<IReadOnlyList<SearchResult>> New {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int? LookupArticle(CatalogIndex index,string article,bool strict=false)=>search.LookupArticle(index,article,strict);
    public Task<IReadOnlyList<SearchResult>> SearchAsync(CatalogIndex index,string query,int limit,CancellationToken token)=>query=="old"?Old.Task:New.Task;
    public Task<IReadOnlyList<SearchResult>> FindCandidatesAsync(CatalogIndex index,string query,int limit,CancellationToken token)=>search.FindCandidatesAsync(index,query,limit,token);
}
