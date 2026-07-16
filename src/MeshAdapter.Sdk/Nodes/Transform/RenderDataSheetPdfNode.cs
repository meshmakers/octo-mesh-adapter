using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Renders a structured data sheet (title, subtitle, labelled sections and an
/// optional footer note) into a single base64-encoded PDF using QuestPDF. The
/// node is domain-agnostic: the accounting cover-sheet content is assembled by
/// the pipeline into the model this node consumes. See
/// <see cref="RenderDataSheetPdfNodeConfiguration"/> for the model shape.
/// </summary>
[NodeConfiguration(typeof(RenderDataSheetPdfNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class RenderDataSheetPdfNode(NodeDelegate next) : IPipelineNode
{
    static RenderDataSheetPdfNode()
    {
        // meshmakers GmbH qualifies for the free QuestPDF Community license.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<RenderDataSheetPdfNodeConfiguration>();

        if (dataContext.Get<JsonNode>(config.Path) is not JsonObject model)
        {
            throw MeshAdapterPipelineExecutionException.DataSheetModelInvalid(nodeContext, config.Path);
        }

        var title = AsString(Prop(model, "title"));
        var subtitle = AsString(Prop(model, "subtitle"));
        var footerHeading = AsString(Prop(model, "footerHeading"));
        var footerText = AsString(Prop(model, "footerText"));

        var sections = new List<(string Heading, List<(string Label, string Value)> Rows)>();
        if (Prop(model, "sections") is JsonArray sectionArray)
        {
            foreach (var sectionNode in sectionArray)
            {
                if (sectionNode is not JsonObject section)
                {
                    continue;
                }

                var rows = new List<(string, string)>();
                if (Prop(section, "rows") is JsonArray rowArray)
                {
                    foreach (var rowNode in rowArray)
                    {
                        if (rowNode is JsonObject row)
                        {
                            rows.Add((AsString(Prop(row, "label")), AsString(Prop(row, "value"))));
                        }
                    }
                }

                sections.Add((AsString(Prop(section, "heading")), rows));
            }
        }

        byte[] bytes;
        try
        {
            bytes = Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(2, Unit.Centimetre);
                        page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken4));

                        page.Header().Column(header =>
                        {
                            if (!string.IsNullOrEmpty(title))
                            {
                                header.Item().Text(title).FontSize(18).Bold();
                            }

                            if (!string.IsNullOrEmpty(subtitle))
                            {
                                header.Item().PaddingTop(2).Text(subtitle)
                                    .FontSize(11).FontColor(Colors.Grey.Darken1);
                            }

                            header.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                        });

                        page.Content().PaddingVertical(10).Column(content =>
                        {
                            content.Spacing(14);

                            foreach (var section in sections)
                            {
                                content.Item().Column(sectionColumn =>
                                {
                                    if (!string.IsNullOrEmpty(section.Heading))
                                    {
                                        sectionColumn.Item().PaddingBottom(4).Text(section.Heading)
                                            .FontSize(11).Bold().FontColor(Colors.Blue.Darken2);
                                    }

                                    foreach (var (label, value) in section.Rows)
                                    {
                                        sectionColumn.Item().PaddingVertical(1).Row(row =>
                                        {
                                            row.ConstantItem(170).Text(label)
                                                .FontColor(Colors.Grey.Darken1);
                                            row.RelativeItem().Text(value);
                                        });
                                    }
                                });
                            }

                            if (!string.IsNullOrEmpty(footerText))
                            {
                                content.Item().PaddingTop(6).Border(1).BorderColor(Colors.Grey.Lighten1)
                                    .Background(Colors.Grey.Lighten4).Padding(8).Column(note =>
                                    {
                                        if (!string.IsNullOrEmpty(footerHeading))
                                        {
                                            note.Item().Text(footerHeading).Bold();
                                        }

                                        note.Item().PaddingTop(2).Text(footerText);
                                    });
                            }
                        });

                        page.Footer().AlignRight().Text(text =>
                        {
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                    });
                })
                .GeneratePdf();
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.PdfRenderFailed(nodeContext, ex);
        }

        nodeContext.Debug($"Rendered data-sheet PDF ({bytes.Length} bytes, {sections.Count} sections)");

        dataContext.Set(config.TargetPath, Convert.ToBase64String(bytes),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static JsonNode? Prop(JsonObject obj, string name)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string AsString(JsonNode? node)
    {
        return node?.ToString() ?? string.Empty;
    }
}
