using System.Xml.Linq;

internal static class UiLayoutTests
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteAgentLink.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? Directory.GetCurrentDirectory();
        var broker = Path.Combine(root, "src", "AgentLink.Broker");
        XDocument Read(string relative) => XDocument.Load(Path.Combine(broker, relative));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var window = Read("MainWindow.xaml");
        var home = Read(Path.Combine("Views", "ConnectionsView.xaml"));
        var resources = Read("App.xaml");
        var settings = Read(Path.Combine("Views", "SettingsView.xaml"));
        check(settings.Descendants(ui + "TextBox").Any(element =>
                (string?)element.Attribute("Text") == "{x:Bind Model.DiagnosticsDirectory}" &&
                (string?)element.Attribute("IsReadOnly") == "True") &&
            settings.Descendants(ui + "Button").Any(element => (string?)element.Attribute("Click") == "OpenDiagnosticsClicked"),
            "Settings exposes the actual private diagnostic directory and an explicit local open action.");
        check(settings.Descendants(ui + "TextBlock").Any(element =>
            (string?)element.Attribute("Text") == "{x:Bind Model.DiagnosticsStatus, Mode=OneWay}"),
            "Settings shows whether bounded file diagnostics are available.");
        check(window.Descendants(ui + "TitleBar").Count() == 1, "The app uses a single native WinUI titlebar.");
        check((string?)window.Descendants(ui + "NavigationView").Single().Attribute("IsTitleBarAutoPaddingEnabled") == "False",
            "Navigation does not reserve a second titlebar strip below the app titlebar.");
        check(!window.Descendants(ui + "NavigationView.Header").Any(), "Navigation does not duplicate page headings.");
        check(!home.Descendants(ui + "TextBlock").Any(element => (string?)element.Attribute("Text") == "Home"),
            "Home begins with this computer, not an extra page heading above its controls.");
        check(!resources.Descendants().Attributes(x + "Key").Any(attribute => attribute.Value.StartsWith("Hero", StringComparison.Ordinal)),
            "The marketing hero and its custom gradient resources are removed.");
        var toggle = home.Descendants(ui + "ToggleSwitch").Single();
        check((string?)toggle.Attribute("OnContent") == "Yes" && (string?)toggle.Attribute("OffContent") == "No" &&
            (string?)toggle.Attribute("AutomationProperties.Name") == "Allow agents to use this device",
            "Home has one accessible device-access Yes/No switch.");
        check(home.Descendants(ui + "TextBlock").Any(element => (string?)element.Attribute("Text") == "Connected computers") &&
            home.Descendants(ui + "ItemsControl").Count() == 2,
            "Computer connections and authenticated agent presence have separate lists.");
        var copy = home.Descendants(ui + "Button").Single(element => (string?)element.Attribute("Click") == "CopyMcpClicked");
        check(copy.Ancestors(ui + "Expander").Any(), "Per-computer MCP configuration is tucked into details.");
        check(!home.Descendants().Any(element => element.Attribute("MinWidth") is { } width && int.TryParse(width.Value, out var value) && value > 300),
            "Home does not force wide desktop-only controls at the 480px minimum.");
        foreach (var file in new[] { "ConnectionsView.xaml", "AgentsView.xaml", "ActivityView.xaml", "SettingsView.xaml" })
        {
            var page = Read(Path.Combine("Views", file));
            check(page.Descendants(ui + "ScrollViewer").Any(element =>
                    (string?)element.Attribute("HorizontalScrollBarVisibility") == "Disabled"),
                $"{file} supports vertical scrolling without requiring a wide horizontal page.");
            check(!page.Descendants().Attributes().Any(attribute =>
                    attribute.Name.LocalName is "Click" && (attribute.Value.Contains("Pair") ||
                    attribute.Value.Contains("Verify") || attribute.Value.Contains("Revoke") || attribute.Value.Contains("Review"))),
                $"{file} exposes no obsolete approval or pairing actions.");
        }
    }
}
