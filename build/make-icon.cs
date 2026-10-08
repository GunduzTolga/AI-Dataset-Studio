#:project ../src/AIDatasetStudio.Desktop/AIDatasetStudio.Desktop.csproj
#:package Avalonia.Headless@12.1.3
#:package Avalonia.Skia@12.1.3
#:property ManagePackageVersionsCentrally=false
#:property OutputType=Exe

// Renders the brand mark (the welcome screen's gradient square with the Sparkle symbol) and
// writes the application icons: app.png (Linux, 512 px), app.ico (Windows) and app.icns (macOS).
// Run from the repository root:  dotnet run build/make-icon.cs
using System.Buffers.Binary;
using AIDatasetStudio.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "src/AIDatasetStudio.Desktop/Assets");
Directory.CreateDirectory(output);

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

byte[] Render(int pixels)
{
    // A little margin around the rounded square, as platform icon grids expect.
    var inset = pixels * 0.0625;
    var square = pixels - (2 * inset);
    var mark = new Border
    {
        Width = square,
        Height = square,
        Margin = new Thickness(inset),
        CornerRadius = new CornerRadius(square * 0.27),
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#6366F1"), 0), new GradientStop(Color.Parse("#9333EA"), 1) },
        },
        Child = new SymbolIcon
        {
            Symbol = Symbol.Sparkle,
            IconVariant = IconVariant.Filled,
            FontSize = square * 0.56,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
    var root = new Panel { Width = pixels, Height = pixels, Children = { mark } };
    root.Measure(new Size(pixels, pixels));
    root.Arrange(new Rect(0, 0, pixels, pixels));
    Dispatcher.UIThread.RunJobs();
    using var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96, 96));
    bitmap.Render(root);
    using var stream = new MemoryStream();
#pragma warning disable CS0618 // the default PNG encoding is what we want
    bitmap.Save(stream);
#pragma warning restore CS0618
    return stream.ToArray();
}

// Each size is rendered on its own so small icons stay crisp.
var sizes = new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 };
var png = sizes.ToDictionary(size => size, Render);

File.WriteAllBytes(Path.Combine(output, "app.png"), png[512]);
File.WriteAllBytes(Path.Combine(output, "app.ico"), Ico([16, 24, 32, 48, 64, 128, 256]));
File.WriteAllBytes(Path.Combine(output, "app.icns"), Icns());
Console.WriteLine($"Icons written to {output}");

// ICO with PNG-compressed entries (Windows Vista and later).
byte[] Ico(int[] entries)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)entries.Length);
    var offset = 6 + (16 * entries.Length);
    foreach (var size in entries)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png[size].Length);
        writer.Write(offset);
        offset += png[size].Length;
    }

    foreach (var size in entries)
    {
        writer.Write(png[size]);
    }

    return stream.ToArray();
}

// ICNS with PNG entries: 16, 32, 64, 128, 256, 512 and 1024 px (the last as 512@2x).
byte[] Icns()
{
    (string Type, int Size)[] entries =
        [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];
    using var body = new MemoryStream();
    Span<byte> length = stackalloc byte[4];
    foreach (var (type, size) in entries)
    {
        body.Write(System.Text.Encoding.ASCII.GetBytes(type));
        BinaryPrimitives.WriteInt32BigEndian(length, png[size].Length + 8);
        body.Write(length);
        body.Write(png[size]);
    }

    using var file = new MemoryStream();
    file.Write("icns"u8);
    BinaryPrimitives.WriteInt32BigEndian(length, (int)body.Length + 8);
    file.Write(length);
    body.WriteTo(file);
    return file.ToArray();
}
