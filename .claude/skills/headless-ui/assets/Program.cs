using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

// Renders the real MainWindow tab by tab and saves a PNG of each. Extend rather than
// rewrite: dialogs construct the same way (parameterless), pseudo-classes force hover and
// press, and TranslatePoint measures geometry - see SKILL.md for those snippets.
class Harness
{
    [STAThread]
    static void Main(string[] args)
    {
        InitAppState();

        AppBuilder.Configure<Nmkoder.App>()
            .UseSkia()
            // UseHeadlessDrawing = false is what routes drawing through Skia; true renders nothing.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // The lifetime is null, so App opens no window itself.
        var win = new Nmkoder.Views.MainWindow();
        win.Show();
        Pump(TimeSpan.FromSeconds(2)); // async startup settles on the dispatcher queue

        string outDir = args.Length > 0 ? args[0] : "shots";
        Directory.CreateDirectory(outDir);

        var tabs = win.FindControl<TabControl>("MainTabs");
        int count = tabs != null ? tabs.ItemCount : 1;
        for (int i = 0; i < count; i++)
        {
            if (tabs != null)
            {
                tabs.SelectedIndex = i;
                Pump(TimeSpan.FromMilliseconds(600));
            }
            using (var frame = win.CaptureRenderedFrame())
                frame.Save(Path.Combine(outDir, $"tab{i}.png"));
        }

        Console.WriteLine($"saved {count} shot(s) to {Path.GetFullPath(outDir)}");
    }

    // The real Program.Main runs Paths.Init() and Config.Init() before the UI comes up, and a
    // window shown without them logs "Failed to save settings to ''" into the visible log box
    // - noise in every screenshot. Both live in app-internal classes, so they are invoked by
    // name; the order is Program.Main's own. Config lands in a data/ folder beside the harness
    // exe (scratch, deleted with bin/), touching nothing of the app's real state.
    static void InitAppState()
    {
        var asm = typeof(Nmkoder.App).Assembly;
        foreach (var name in new[] { "Nmkoder.Data.Paths", "Nmkoder.IO.Config" })
            asm.GetType(name)
                ?.GetMethod("Init", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.Invoke(null, null);
    }

    // A frame captured before the queue drains shows half-loaded state; pumping in a loop
    // with short sleeps lets timer- and posted-work land the way it does in the running app.
    static void Pump(TimeSpan t)
    {
        var until = DateTime.UtcNow + t;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(20);
        }
    }
}
