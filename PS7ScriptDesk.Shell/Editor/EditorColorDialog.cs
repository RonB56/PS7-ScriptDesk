using System;
using System.Windows;
using System.Windows.Controls;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PS7ScriptDesk.Shell.Editor
{
    internal static class EditorColorDialog
    {
        internal static string? Prompt(Window owner, string title, string instruction, WpfColor initialColor, Action<string, string> showValidationMessage)
        {
            var result = (string?)null;
            var initialHex = EditorColorUtilities.FormatColorHex(initialColor);

            var dialog = new Window
            {
                Owner = owner,
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = owner.TryFindResource("Theme.Surface.Primary") as WpfBrush ?? WpfBrushes.White,
                Foreground = owner.TryFindResource("Theme.Text.Primary") as WpfBrush ?? WpfBrushes.Black
            };

            var root = new StackPanel
            {
                Margin = new Thickness(16),
                MinWidth = 360
            };

            var instructionText = new TextBlock
            {
                Text = instruction,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var input = new WpfTextBox
            {
                Text = initialHex,
                MinWidth = 220,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var preview = new Border
            {
                Height = 28,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = owner.TryFindResource("Theme.Border.Strong") as WpfBrush ?? WpfBrushes.Gray,
                Background = EditorColorUtilities.CreateFrozenBrush(initialColor),
                Margin = new Thickness(0, 0, 0, 14)
            };

            input.TextChanged += (_, _) =>
            {
                if (EditorColorUtilities.TryNormalizeHexColor(input.Text, out var normalizedHex) && EditorColorUtilities.TryParseHexColor(normalizedHex, out var parsedColor))
                {
                    preview.Background = EditorColorUtilities.CreateFrozenBrush(parsedColor);
                }
            };

            var buttons = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHorizontalAlignment.Right
            };

            var okButton = new WpfButton
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 82,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var cancelButton = new WpfButton
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 82
            };

            okButton.Click += (_, _) =>
            {
                if (!EditorColorUtilities.TryNormalizeHexColor(input.Text, out var normalizedHex))
                {
                    showValidationMessage("Invalid Color", "Please enter a valid color in #RRGGBB format. Example: #0F4C81");
                    input.Focus();
                    input.SelectAll();
                    return;
                }

                result = normalizedHex;
                dialog.DialogResult = true;
            };

            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(instructionText);
            root.Children.Add(input);
            root.Children.Add(preview);
            root.Children.Add(buttons);
            dialog.Content = root;

            input.SelectAll();
            input.Focus();

            return dialog.ShowDialog() == true ? result : null;
        }
    }
}
