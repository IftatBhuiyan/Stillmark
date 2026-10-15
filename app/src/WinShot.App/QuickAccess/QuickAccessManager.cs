using System.Windows;
using System.IO;
using WinShot.Core.Naming;
using WinShot.Core.Settings;

namespace WinShot.App.QuickAccess;

/// <summary>
/// Owns the stack of floating quick-access cards in the bottom-left corner of the
/// primary work area. Newest card sits at the bottom; older cards slide up.
/// </summary>
public sealed class QuickAccessManager
{
    private const int MaxCards = 5;
    private const double EdgeGap = 14;   // visual gap between card and screen edge
    private const double CardGap = 10;

    private readonly SettingsManager _settings;
    private readonly List<QuickAccessWindow> _cards = new();

    public QuickAccessManager(SettingsManager settings)
    {
        _settings = settings;
    }

    internal bool CloseAfterDrag => _settings.Current.QuickAccessCloseAfterDrag;

    public void Show(CaptureDelivery delivery, string filePath)
    {
        var card = new QuickAccessWindow(this, delivery, filePath, _settings.Current.QuickAccessAutoHideSeconds,
            Math.Clamp(_settings.Current.QuickAccessScale, 0.7, 1.5));
        _cards.Add(card);
        while (_cards.Count > MaxCards)
        {
            var oldest = _cards[0];
            _cards.RemoveAt(0);
            oldest.FadeOutAndClose();
        }
        card.Loaded += (_, _) => Restack(entering: card);
        card.Show();
    }

    internal void NotifyClosed(QuickAccessWindow card)
    {
        if (_cards.Remove(card))
            Restack(entering: null);
    }

    internal async Task<string> SaveImageAsync(CaptureDelivery delivery)
    {
        EncodedImagePayload payload = delivery.Payload
            ?? await StillImageEncodingService.EncodePngAsync(delivery.Image);
        return await ImageSaveService.SaveAsync(
            payload,
            _settings.Current.Clone(),
            DateTime.Now);
    }

    internal Task<string> SaveMediaAsync(string sourcePath)
    {
        string saveDirectory = _settings.Current.SaveDirectory;
        return Task.Run(() => SaveMediaCore(sourcePath, saveDirectory));
    }

    private static string SaveMediaCore(string sourcePath, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The media file is no longer available.", sourcePath);

        Directory.CreateDirectory(directory);
        string extension = Path.GetExtension(sourcePath);
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string stagingPath = Path.Combine(directory, $".{Guid.NewGuid():N}.partial");
        bool committed = false;
        try
        {
            File.Copy(sourcePath, stagingPath, overwrite: false);
            for (int attempt = 1;
                 attempt <= OutputFileReservation.DefaultMaxAttempts;
                 attempt++)
            {
                string suffix = attempt == 1 ? "" : $" {attempt}";
                string destination = Path.Combine(
                    directory,
                    baseName + suffix + extension);
                try
                {
                    File.Move(stagingPath, destination);
                    committed = true;
                    return destination;
                }
                catch (IOException) when (
                    File.Exists(destination) || Directory.Exists(destination))
                {
                }
            }

            throw new IOException(
                $"Unable to save the media file after " +
                $"{OutputFileReservation.DefaultMaxAttempts} name attempts.");
        }
        finally
        {
            if (!committed)
                File.Delete(stagingPath);
        }
    }

    internal void OpenEditor(CaptureDelivery delivery) =>
        new Editor.EditorWindow(delivery.Image, _settings).Show();

    internal void OpenPin(CaptureDelivery delivery) =>
        new PinWindow(delivery.Image, delivery.SourceRect, _settings).Show();

    /// <summary>
    /// Hides all cards so they don't appear in a capture. Returns true if any were visible.
    /// Uses Hide()/Show() — setting Window.Visibility is dispatcher-deferred and would not
    /// take effect before a synchronous screen grab.
    /// </summary>
    public bool HideAll()
    {
        bool anyVisible = false;
        foreach (var card in _cards)
        {
            if (card.IsVisible)
            {
                anyVisible = true;
                card.Hide();
            }
        }
        return anyVisible;
    }

    public void RestoreAll()
    {
        foreach (var card in _cards)
        {
            if (!card.IsVisible)
                card.Show();
        }
    }

    private void Restack(QuickAccessWindow? entering)
    {
        var workArea = SystemParameters.WorkArea;
        bool onRight = _settings.Current.QuickAccessPosition == "Right";
        double cardBottom = workArea.Bottom - EdgeGap;

        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            var card = _cards[i];
            double height = card.CardHeight;
            if (height <= 0)
                continue;
            double cardLeft = onRight
                ? workArea.Right - EdgeGap - card.CardWidth
                : workArea.Left + EdgeGap;
            double cardTop = cardBottom - height;
            if (card == entering)
                card.EnterAt(cardLeft, cardTop, fromRight: onRight);
            else
                card.AnimateTo(cardLeft, cardTop);
            cardBottom = cardTop - CardGap;
        }
    }
}
