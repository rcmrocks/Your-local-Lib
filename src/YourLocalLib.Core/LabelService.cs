using Net.Codecrete.QrCodeGenerator;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace YourLocalLib.Core;

public static class LabelService
{
    static LabelService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void WritePdf(string path, IReadOnlyList<LocationInfo> shelves)
    {
        var labels = shelves.Select(shelf => (shelf, Png(shelf.Code))).ToList();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(36);
                page.DefaultTextStyle(style => style.FontSize(18).FontColor(Colors.Black));
                page.Content().Column(column =>
                {
                    column.Spacing(28);
                    column.Item().Text(AppBrand.Name).FontSize(14).FontColor(Colors.Grey.Darken2);
                    column.Item().Text("Shelf labels").FontSize(28);
                    foreach (var (shelf, png) in labels)
                    {
                        column.Item().Row(row =>
                        {
                            row.ConstantItem(180).Image(png);
                            row.RelativeItem().AlignMiddle().PaddingLeft(24).Column(text =>
                            {
                                text.Item().Text(shelf.Path).FontSize(22);
                                text.Item().PaddingTop(6).Text(shelf.Code).FontSize(14).FontColor(Colors.Grey.Darken2);
                            });
                        });
                    }
                });
            });
        }).GeneratePdf(path);
    }

    public static byte[] Png(string code)
    {
        var qr = QrCode.EncodeText(code, QrCode.Ecc.Medium);
        var modules = new bool[qr.Size, qr.Size];
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                modules[y, x] = qr.GetModule(x, y);
        return PngUtil.FromModules(modules, 8, 2);
    }
}
