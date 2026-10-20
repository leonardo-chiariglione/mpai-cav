using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Ocr;   // OcrAimProcessor, OcrFactory
using Mpai.Mmc.Cts;    // CtsAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-NPR-V2.5 Module (Newspaper Reading).
// The Controller builds the NPR composite from its L3; this provider supplies the leaves it names:
//   MMC-OCR - Optical Character Recognition (the lines of the page, with their boxes)
//   MMC-CTS - Column Text Selection         (the text of the column that was pointed at)
//   PAF-PMX - Portable Avatar Multiplexing  (the Portable Avatar that reads it)
// Acquisition of the page and of the pointing, and the speaking, are the User Agent's.
public sealed class NprProvider : IAimProvider
{
    private readonly AmdStore _store;

    public NprProvider(AmdStore store) => _store = store;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-OCR-V2.5-I01" or "1MMC-CTS-V2.5-I01" or "1PAF-PMX-V1.6-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "1MMC-OCR-V2.5-I01" => new OcrAimProcessor(aimName, OcrFactory.Create(aimName, settings), AimPortReader.Load(_store, aimName)),
            "1MMC-CTS-V2.5-I01" => new CtsAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-PMX-V1.6-I01" => new Mpai.Paf.Pmx.PmxAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"NprProvider does not provide '{aimName}'.")
        };
}