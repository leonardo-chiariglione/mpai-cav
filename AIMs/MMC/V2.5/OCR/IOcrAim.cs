using Mpai.Core;

namespace Mpai.Aims.Ocr;

// MMC-OCR-V2.5: a page's image in, its lines out - a Text Object whose Basic Text
// Objects are the lines, in reading order, each with its box on the page and its
// confidence. The image is read only from the Visual Object's data.
public interface IOcrAim
{
    Task<TextObject> ProcessAsync(BasicVisualObject image);
}
