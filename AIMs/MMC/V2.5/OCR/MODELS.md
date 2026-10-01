# The models of MMC-OCR-V2.5

Optical Character Recognition runs PaddleOCR's PP-OCRv5 models (Apache-2.0) through
RapidOcrNet 2.0.0 (Apache-2.0, NuGet), on ONNX Runtime; SkiaSharp decodes the image and
brings its native libraries for Windows and Linux.

The model files are not in the repository. They go in `Models/OCR` (on Linux
`/opt/mpai/Models/OCR`), named by the settings of `1MMC-OCR-V2.5-I01` in
`AIMs/aim-settings.json` and `MAS/Service/linux/aim-settings.json`, with their SHA-256.

| Setting | File | What | Where from |
|---|---|---|---|
| DetModel | `ch_PP-OCRv5_server_det.onnx` (88 MB) | text detection, the server model: finds the lines | RapidOCR's model list (`default_models.yaml` of RapidAI/RapidOCR), as D:\AI had it |
| ClsModel | `ch_ppocr_mobile_v2.0_cls_infer.onnx` | orientation: a line upside down is turned | carried by the RapidOcrNet package (`models/v5`) |
| RecModel | `latin_PP-OCRv5_rec_mobile_infer.onnx` | recognition of the latin alphabets | carried by the RapidOcrNet package |
| KeysFile | `ppocrv5_latin_dict.txt` | the characters RecModel recognises | carried by the RapidOcrNet package |

With `DetModel` not set the AIM uses the models the package carries, whose detection is
the smaller `ch_PP-OCRv5_mobile_det.onnx`. On the author's page (2481 x 3509 px, three
columns) the server model reads every word of the truth in about 5 s on the CPU
(`Test/Reports/ocr-npr-page.json`).

Other languages need their recognition model and dictionary (RapidOCR's list).
