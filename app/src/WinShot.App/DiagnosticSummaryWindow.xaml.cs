using System.Runtime.InteropServices;
using System.Windows;

namespace WinShot.App;

public partial class DiagnosticSummaryWindow : Window
{
    private readonly string _summary;

    internal DiagnosticSummaryWindow(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        InitializeComponent();
        WindowTheming.ApplyDarkTitleBar(this);
        _summary = summary;
        SummaryText.Text = summary;
        Loaded += (_, _) => CopyButton.Focus();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_summary);
            StatusText.Text = "Summary copied. Nothing was sent automatically.";
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            App.LogError(ex);
            StatusText.Text = "Stillmark could not access the clipboard. Select the summary and copy it manually.";
            SummaryText.Focus();
            SummaryText.SelectAll();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnEmailReport(object sender, RoutedEventArgs e)
    {
        string body = "What happened?\r\n\r\nSteps to reproduce:\r\n1. \r\n\r\nWhat did you expect?\r\n\r\n--- Diagnostic summary ---\r\n" + _summary;
        string address = "mailto:support@getstillmark.com?subject="
            + Uri.EscapeDataString($"Stillmark {AppInfo.Version} — issue report")
            + "&body=" + Uri.EscapeDataString(body);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
            StatusText.Text = "Email draft requested. Add the issue details and send it from your email app. Nothing has been sent by Stillmark.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "No email app could be opened. Copy the summary and email your report to support@getstillmark.com using webmail.";
        }
    }
}
