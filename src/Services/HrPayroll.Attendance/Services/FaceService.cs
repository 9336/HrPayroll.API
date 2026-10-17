using System.Runtime.InteropServices;
using FaceAiSharp;
using FaceAiSharp.Extensions;
using HrPayroll.BuildingBlocks.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HrPayroll.Attendance.Services;

/// <summary>A face found in a photo: its embedding and a small JPEG copy of the photo.</summary>
public record FaceScan(float[] Embedding, byte[] Jpeg, float Confidence);

/// <summary>
/// Face detection (SCRFD) and recognition (ArcFace) with local ONNX models - photos never leave the server.
/// Two photos of the same person score about 0.6–0.8, different people about 0 (<see cref="MatchThreshold"/>).
/// </summary>
public sealed class FaceService : IDisposable
{
    /// <summary>Similarity at or above which two faces are the same person.</summary>
    public const float MatchThreshold = 0.45f;
    /// <summary>Smallest face (px, in the 800-px working image) that is reliable to compare.</summary>
    private const int MinFaceSize = 90;
    private const float MinConfidence = 0.6f;

    private readonly Lazy<IFaceDetectorWithLandmarks> detector = new(() => FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks());
    private readonly Lazy<IFaceEmbeddingsGenerator> embedder = new(() => FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator());
    private readonly SemaphoreSlim gate = new(1, 1);   // the models are not thread-safe

    /// <summary>Finds exactly one clear face in the photo; throws an <see cref="AppException"/> with advice otherwise.</summary>
    public async Task<FaceScan> ScanAsync(byte[] photo, CancellationToken ct)
    {
        Image<Rgb24> image;
        try { image = Image.Load<Rgb24>(photo); }
        catch (Exception) { throw new AppException("The photo could not be read. Take the selfie again."); }

        using (image)
        {
            image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions { Size = new Size(800, 800), Mode = ResizeMode.Max }));
            var jpeg = Jpeg(image);

            await gate.WaitAsync(ct);
            try
            {
                var faces = detector.Value.DetectFaces(image).Where(f => f.Confidence >= MinConfidence).ToList();
                if (faces.Count == 0) throw new AppException("No face found. Hold the phone at eye level, face the camera in good light and try again.");
                if (faces.Count > 1) throw new AppException("More than one face is in the photo - only you should be in the selfie.");
                var face = faces[0];
                if (face.Box.Width < MinFaceSize || face.Box.Height < MinFaceSize)
                    throw new AppException("Your face is too small in the photo - hold the phone closer.");
                embedder.Value.AlignFaceUsingLandmarks(image, face.Landmarks!);
                return new FaceScan(embedder.Value.GenerateEmbedding(image), jpeg, face.Confidence ?? 0);
            }
            finally { gate.Release(); }
        }
    }

    public static float Similarity(float[] a, float[] b) => a.Dot(b);

    public static byte[] ToBytes(float[] embedding) => MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
    public static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    /// <summary>A 640-px JPEG copy kept for HR (smaller than the original selfie).</summary>
    private static byte[] Jpeg(Image<Rgb24> image)
    {
        using var copy = image.Clone(x => x.Resize(new ResizeOptions { Size = new Size(640, 640), Mode = ResizeMode.Max }));
        using var ms = new MemoryStream();
        copy.Save(ms, new JpegEncoder { Quality = 80 });
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (detector.IsValueCreated) (detector.Value as IDisposable)?.Dispose();
        if (embedder.IsValueCreated) (embedder.Value as IDisposable)?.Dispose();
        gate.Dispose();
    }
}
