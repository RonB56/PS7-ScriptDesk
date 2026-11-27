using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class ContextualWizardHelpRegressionTests
{
    [Fact]
    public void SharedHelpFactoryAndGridRegistrationPreserveSupportedTooltipAndStyles()
    {
        WpfTestDispatcher.Run(() =>
        {
            var window = new Window();
            var panel = new StackPanel();
            var first = new TextBox();
            var second = new TextBox();
            var grid = new DataGrid();
            var column = new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding("Value") };
            var cellBaseStyle = new Style(typeof(DataGridCell));
            cellBaseStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7)));
            var headerBaseStyle = new Style(typeof(DataGridColumnHeader));
            headerBaseStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5)));
            column.CellStyle = cellBaseStyle;
            column.HeaderStyle = headerBaseStyle;
            grid.Columns.Add(column);
            panel.Children.Add(first);
            panel.Children.Add(second);
            panel.Children.Add(grid);
            window.Content = panel;
            NameScope.SetNameScope(window, new NameScope());
            window.RegisterName("TitleBox", first);
            window.RegisterName("VersionBox", second);
            window.RegisterName("EndpointGrid", grid);

            ContextualWizardHelp.Apply(window,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TitleBox"] = "Title\nFirst description.",
                    ["VersionBox"] = "Version\nSecond description.",
                    ["EndpointGrid"] = "Grid help"
                },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Value"] = "Value help\nA concise editor description." });

            var firstTip = Assert.IsType<ToolTip>(first.ToolTip);
            var secondTip = Assert.IsType<ToolTip>(second.ToolTip);
            Assert.True(firstTip.StaysOpen);
            Assert.NotSame(firstTip, secondTip);
            Assert.Equal("Title\nFirst description.", AutomationProperties.GetHelpText(first));
            Assert.Equal(500, ToolTipService.GetInitialShowDelay(first));
            Assert.Equal(60000, ToolTipService.GetShowDuration(first));
            Assert.True(ToolTipService.GetShowOnDisabled(first));
            Assert.IsType<Border>(firstTip.Content);
            Assert.IsType<StackPanel>(Assert.IsType<Border>(firstTip.Content).Child);

            Assert.Same(cellBaseStyle, column.CellStyle!.BasedOn);
            Assert.Same(headerBaseStyle, column.HeaderStyle!.BasedOn);
            Assert.Null(column.ElementStyle);
            Assert.Null(column.EditingElementStyle);

            var independentTip = ContextualWizardHelp.CreateTooltip("Independent\nFactory content.");
            Assert.True(independentTip.StaysOpen);
            Assert.IsType<Border>(independentTip.Content);
            Assert.NotSame(firstTip, independentTip);
        });
    }
}
