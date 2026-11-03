using Mpai.Core;

namespace Mpai.Aims.Ocr;

// MMC-OCR-V2.5: a page's image in, its lines out - a Text Object whose Basic Text
// Objects are the lines, in reading order, each with its box on the page and its
// confidence. The image is read only from the Visual Object's data.
//
// WHERE IT READS. Given a point of the page (in its pixels), only a band of the page about the point, in the full width of the page, is read: the person is
// reading a column, and some twenty to fifty lines of it are enough. Given none, the band at the top of the page. The boxes of the lines are in the
// pixels of the whole page.
public interface IOcrAim
{
    Task<TextObject> ProcessAsync(BasicVisualObject image, (double X, double Y)? around = null);
}
