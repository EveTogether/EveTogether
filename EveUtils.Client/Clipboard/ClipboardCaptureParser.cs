using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;

namespace EveUtils.Client.Clipboard;

public sealed class ClipboardCaptureParser(IFitTextImporter fitTextImporter)
{
    public FitImportResult ParseFit(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Fit)
            throw new ArgumentException("The clipboard capture is not a fit.", nameof(capture));

        return fitTextImporter.Import(capture.Text);
    }

    public IReadOnlyList<ClipboardInventoryItem> ParseInventory(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Inventory)
            throw new ArgumentException("The clipboard capture is not an inventory listing.", nameof(capture));

        return ClipboardInventoryParser.Parse(capture.Text);
    }

    public IReadOnlyList<ClipboardSignatureRow> ParseSignatures(ClipboardCapture capture)
    {
        if (capture.Shape is not ClipboardShape.Signature)
            throw new ArgumentException("The clipboard capture is not a signature list.", nameof(capture));

        return ClipboardSignatureParser.Parse(capture.Text);
    }
}
