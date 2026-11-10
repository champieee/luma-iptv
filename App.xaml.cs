using System.IO;
using System.Windows;

namespace LumaIPTV;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test"))
        {
            try { var result = await SelfTest.Run(); File.WriteAllText(e.Args.Last(), result); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args.Last(), ex.ToString()); Shutdown(1); }
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        var uiTest = Array.IndexOf(e.Args, "--ui-test");
        if (uiTest >= 0)
        {
            var dir = e.Args[uiTest + 1];
            try { await Task.Delay(500); File.WriteAllText(Path.Combine(dir,"ui-results.txt"),await window.RunUiChecks(dir)); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir,"ui-results.txt"),ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--demo")) window.OpenDemo();
        var capture = Array.IndexOf(e.Args, "--screenshot");
        if (capture >= 0 && e.Args.Length > capture + 1)
        {
            await Task.Delay(1800);
            window.SaveScreenshot(e.Args[capture + 1]);
            Shutdown();
        }
    }
}
