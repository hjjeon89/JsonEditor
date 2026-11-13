using System;
using System.Linq;
using System.Windows;
using JsonEditor.ViewModels;

namespace JsonEditor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var win = new MainWindow();
                    win.Measure(new Size(1480, 920));
                    win.Arrange(new Rect(0, 0, 1480, 920));
                    win.UpdateLayout();

                    if (win.DataContext is MainViewModel vm)
                    {
                        foreach (var mode in new[] { "Split", "Structured", "RawCode" })
                        {
                            vm.ViewMode = mode;
                            win.UpdateLayout();
                        }
                    }

                    Console.WriteLine("JSONEDITOR_SMOKE_TEST_OK");
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("JSONEDITOR_SMOKE_TEST_FAIL: " + ex);
                    Environment.Exit(1);
                }
            }
        }
    }
}
