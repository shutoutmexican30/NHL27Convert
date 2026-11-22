// Loads every implicit ControlTemplate defined in App.xaml by instantiating its visual tree
// (FrameworkTemplate.LoadContent) under a runtime-parsed copy of the theme. A broken template
// throws here instead of crashing the app at startup.
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace XamlSmoke;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        int failures = 0;
        Application app;
        try
        {
            var path = Path.GetFullPath(args.Length > 0 ? args[0] : "src/Suite.App/App.xaml");
            var text = File.ReadAllText(path).Replace("x:Class=\"Suite.App.App\"", ""); // x:Class is compile-time only in loose XAML
            app = (Application)XamlReader.Parse(text);
            Console.WriteLine($"resources parsed: {app.Resources.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: App.xaml did not parse: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        foreach (var type in new[]
        {
            typeof(Button), typeof(TextBox), typeof(ComboBox), typeof(ComboBoxItem),
            typeof(ListBox), typeof(ListBoxItem), typeof(TabControl), typeof(TabItem),
            typeof(GroupBox), typeof(DataGrid), typeof(DataGridColumnHeader),
            typeof(DataGridCell), typeof(DataGridRow), typeof(ScrollBar),
        })
        {
            if (app.Resources[type] is not Style style) { Console.WriteLine($"no implicit style: {type.Name}"); continue; }
            var template = style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value as FrameworkTemplate;
            if (template is null) { Console.WriteLine($"ok (no template): {type.Name}"); continue; }
            try
            {
                template.LoadContent(); // parses the template and builds the tree
                Console.WriteLine($"ok: {type.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL: {type.Name}: {ex.GetType().Name}: {ex.Message}");
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    Console.WriteLine($"    caused by: {inner.GetType().Name}: {inner.Message}");
            }
        }

        Console.WriteLine(failures == 0 ? "SMOKE PASS" : $"SMOKE FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
