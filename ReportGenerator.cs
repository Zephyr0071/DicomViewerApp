using System;
using System.IO;
using System.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Previewer; // Optional

namespace DicomViewerApp
{
    public static class ReportGenerator
    {
        public static void GeneratePdfReport(string targetPath, string originalFileName, string clinicalData, Bitmap currentImage)
        {
            // QuestPDF requires this declaration for free community use
            QuestPDF.Settings.License = LicenseType.Community;

            // Convert the WinForms Bitmap to a byte array so the PDF engine can read it
            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                if (currentImage != null)
                {
                    // FIX 1: Explicitly tell C# to use System.Drawing's ImageFormat
                    currentImage.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    imageBytes = ms.ToArray();
                }
                else
                {
                    imageBytes = Array.Empty<byte>();
                }
            }

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(11).FontFamily(Fonts.Arial));

                    // ─── HEADER ───
                    page.Header().Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text("Cardiovascular Hemodynamic Report").SemiBold().FontSize(20).FontColor(Colors.Blue.Darken2);
                            col.Item().Text($"Generated: {DateTime.Now:MMMM dd, yyyy HH:mm}");
                            // Change this line:
                            col.Item().Text($"File ID: {Path.GetFileName(originalFileName)}").FontSize(9).FontColor(Colors.Grey.Medium);
                        });
                    });

                    // ─── CONTENT ───
                    page.Content().PaddingVertical(1, Unit.Centimetre).Column(col =>
                    {
                        // Section 1: Visual Snapshot
                        // FIX 2: Move PaddingBottom() BEFORE Text()
                        col.Item().PaddingBottom(5).Text("1. Ultrasound / Flow Visualization").SemiBold().FontSize(14);

                        if (imageBytes.Length > 0)
                        {
                            col.Item().Image(imageBytes).FitArea();
                        }
                        else
                        {
                            col.Item().Text("No image available.").Italic().FontColor(Colors.Grey.Medium);
                        }

                        col.Item().PaddingTop(20);

                        // Section 2: Clinical Data & Metrics
                        col.Item().PaddingBottom(5).Text("2. Quantitative Metrics & Metadata").SemiBold().FontSize(14);
                        col.Item().Background(Colors.Grey.Lighten4).Padding(10).Text(clinicalData).FontSize(10).FontFamily(Fonts.Consolas);

                        col.Item().PaddingTop(20);

                        // Section 3: Diagnostic Notes
                        col.Item().PaddingBottom(5).Text("3. Physician Notes").SemiBold().FontSize(14);
                        col.Item().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingBottom(20);
                        col.Item().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingBottom(20);
                        col.Item().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingBottom(20);
                    });

                    // ─── FOOTER ───
                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("Page ");
                        x.CurrentPageNumber();
                        x.Span(" of ");
                        x.TotalPages();
                    });
                });
            })
            .GeneratePdf(targetPath);
        }
    }
    }
