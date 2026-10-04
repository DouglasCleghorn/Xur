static class ClientLibraryTests
{
    public static void Run(Action<bool,string> check)
    {
        var assets=Path.Combine(AppContext.BaseDirectory,"wwwroot","vendor","ag-grid");
        var script=Path.Combine(assets,"ag-grid-community.min.js");
        var notice=Path.Combine(assets,"LICENSE.txt");
        check(File.Exists(script)&&File.ReadAllText(script).Contains("agGrid"),"Normal builds include the locally served AG Grid JavaScript without a separate asset refresh");
        check(File.Exists(notice)&&File.ReadAllText(notice).Contains("AG GRID LTD")&&File.ReadAllText(notice).Contains("The MIT License"),"Restored browser libraries retain their upstream copyright and license in build output");
    }
}
