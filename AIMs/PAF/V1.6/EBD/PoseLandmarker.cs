// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using Mpai.Onnx;

namespace Mpai.Paf.Ebd;

// The 33 keypoints of the body, with BlazePose GHUM (MediaPipe Pose Landmarker "full", Apache-2.0), ONNX version of the OpenCV Zoo (converted from the TFLite model with tf2onnx).
//
// ONNX signature (read from the file): input [1,256,256,3] NHWC, RGB in [0,1]; output 'Identity' [1,195] = 39 landmarks of 5 numbers (x, y, z in pixels of the input, then the visibility and the
// presence as logits; the first 33 are the keypoints of the body, the last six are auxiliary), 'Identity_1' [1,1] the probability that a body is there.
//
// The model looks at one body: the picture is the Body Object, a picture of one body, put in the 256x256 square with its proportions kept (black bands at the sides). There is no search for the
// body (the person detector of MediaPipe is not used) and no rotation of the picture to the vertical of the body.
public sealed class PoseLandmarker : IDisposable
{
    public const int Size = 256;
    public const int Keypoints = 33;

    // The names of the keypoints used by the rules (MediaPipe numbering).
    public const int Nose = 0, LeftShoulder = 11, RightShoulder = 12, LeftElbow = 13, RightElbow = 14, LeftWrist = 15, RightWrist = 16, LeftHip = 23, RightHip = 24;

    private readonly InferenceSession _session;
    public PoseLandmarker(string modelPath) { _session = OnnxSessions.Create(modelPath); }

    // The keypoints as 33 x (x, y, z, visibility): x and y are fractions of the square (0 to 1, from the top left; the same unit on both axes, so the shapes are not stretched), z in the same unit,
    // the visibility a probability. Null if the model does not find a body (probability below MinPresence).
    public const float MinPresence = 0.5f;
    public float[]? Detect(byte[] picture)
    {
        using var image = Image.Load<Rgb24>(picture);
        int side = Math.Max(image.Width, image.Height);
        var input = new DenseTensor<float>(new[] { 1, Size, Size, 3 });
        using (var square = new Image<Rgb24>(Size, Size, new Rgb24(0, 0, 0)))
        {
            float scale = (float)Size / side;
            int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
            using var small = image.Clone(c => c.Resize(w, h));
            square.Mutate(c => c.DrawImage(small, new Point((Size - w) / 2, (Size - h) / 2), 1f));
            square.ProcessPixelRows(a =>
            {
                for (int y = 0; y < Size; y++)
                {
                    var row = a.GetRowSpan(y);
                    for (int x = 0; x < Size; x++) { input[0, y, x, 0] = row[x].R / 255f; input[0, y, x, 1] = row[x].G / 255f; input[0, y, x, 2] = row[x].B / 255f; }
                }
            });
        }
        using var results = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), input) });
        var landmarks = results.First(r => r.Name == "Identity").AsTensor<float>().ToArray();         // 195
        float presence = results.First(r => r.Name == "Identity_1").AsTensor<float>().ToArray()[0];
        if (presence < MinPresence) return null;
        var points = new float[Keypoints * 4];
        for (int i = 0; i < Keypoints; i++)
        {
            points[i * 4] = landmarks[i * 5] / Size; points[i * 4 + 1] = landmarks[i * 5 + 1] / Size; points[i * 4 + 2] = landmarks[i * 5 + 2] / Size;
            points[i * 4 + 3] = (float)(1.0 / (1.0 + Math.Exp(-landmarks[i * 5 + 3])));
        }
        return points;
    }

    public void Dispose() => _session.Dispose();
}
