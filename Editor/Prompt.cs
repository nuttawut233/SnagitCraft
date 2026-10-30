using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SnagitAnything.Editor;

/// <summary>Minimal modal number prompt (used by Resize).</summary>
public static class Prompt
{
    public static double? ForNumber(Window owner, string title, string label, double initial, double min, double max)
    {
        var box = new TextBox { Text = initial.ToString(), Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new Button { Content = "ตกลง", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "ยกเลิก", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title, Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 300,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, FontFamily = new FontFamily("Segoe UI"),
        };
        double? result = null;
        ok.Click += (_, _) =>
        {
            if (double.TryParse(box.Text, out double v) && v >= min && v <= max)
            {
                result = v;
                dlg.DialogResult = true;
            }
            else
            {
                box.Focus();
                box.SelectAll();
            }
        };
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); Keyboard.Focus(box); };
        dlg.ShowDialog();
        return result;
    }
}
