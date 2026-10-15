using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.App.Editor;
using Xunit;

namespace WinShot.Core.Tests;

public class EditorDocumentTests
{
    [Fact]
    public void Add_BeyondUndoLimit_KeepsNewestFiftySnapshots()
    {
        var document = CreateDocument();

        for (int i = 0; i < 55; i++)
            document.Add(CreateAnnotation(i));

        for (int i = 0; i < 50; i++)
            document.Undo();

        Assert.Equal(5, document.Annotations.Count);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);

        for (int i = 0; i < 50; i++)
            document.Redo();

        Assert.Equal(55, document.Annotations.Count);
        Assert.Equal(Enumerable.Range(0, 55),
            document.Annotations.Cast<ShapeAnnotation>().Select(annotation => (int)annotation.Start.X));
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void UndoAndRedo_RestoreTheExpectedDocumentState()
    {
        var document = CreateDocument();
        document.Add(CreateAnnotation(1));
        document.Add(CreateAnnotation(2));

        document.Undo();

        var remaining = Assert.Single(document.Annotations);
        Assert.Equal(1, ((ShapeAnnotation)remaining).Start.X);
        Assert.True(document.CanUndo);
        Assert.True(document.CanRedo);

        document.Redo();

        Assert.Equal([1d, 2d],
            document.Annotations.Cast<ShapeAnnotation>().Select(annotation => annotation.Start.X));
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Add_AfterUndo_ClearsRedoHistory()
    {
        var document = CreateDocument();
        document.Add(CreateAnnotation(1));
        document.Add(CreateAnnotation(2));
        document.Undo();

        document.Add(CreateAnnotation(3));

        Assert.False(document.CanRedo);
        Assert.Equal([1d, 3d],
            document.Annotations.Cast<ShapeAnnotation>().Select(annotation => annotation.Start.X));

        document.Redo();

        Assert.Equal([1d, 3d],
            document.Annotations.Cast<ShapeAnnotation>().Select(annotation => annotation.Start.X));
    }

    [Fact]
    public void Undo_RestoresBackdropPresentationAlongsideMarkup()
    {
        var document = CreateDocument();
        document.Add(CreateAnnotation(1));

        document.PushUndo();
        document.Backdrop.Fill = Brushes.Red;
        document.Backdrop.Padding = 88;
        document.Backdrop.Shadow = false;
        document.NotifyChanged();

        document.Undo();

        Assert.Single(document.Annotations);
        Assert.Null(document.Backdrop.Fill);
        Assert.Equal(56, document.Backdrop.Padding);
        Assert.True(document.Backdrop.Shadow);
    }

    private static EditorDocument CreateDocument()
    {
        var background = BitmapSource.Create(
            pixelWidth: 1,
            pixelHeight: 1,
            dpiX: 96,
            dpiY: 96,
            pixelFormat: PixelFormats.Bgra32,
            palette: null,
            pixels: new byte[4],
            stride: 4);
        background.Freeze();
        return new EditorDocument(background);
    }

    private static ShapeAnnotation CreateAnnotation(int id) => new()
    {
        Kind = ToolKind.Rect,
        Start = new Point(id, id),
        End = new Point(id + 1, id + 1),
        Color = Colors.White,
        Thickness = 1,
    };
}
