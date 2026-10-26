using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace ClopWindows.Core.Imaging;

/// <summary>
/// Managed implementation of the SSIMULACRA2 perceptual image-quality metric (Cloudinary, BSD-3).
/// Scores are "higher is better": 100 = identical, ~90 = visually lossless, ~80 = very high quality,
/// ~70 = high quality, below 50 = obviously degraded. Unlike plain SSIM it works in the XYB opsin colour
/// space, looks at six scales and also penalises ringing/blocking ("artifacts") and lost detail, which makes
/// it far better at ranking JPEG/WebP/AVIF encodes than the luma-only SSIM the app used before.
///
/// The recursive Gaussian of the reference implementation has a finite impulse response, so it is evaluated
/// here as an explicit separable FIR filter (zero padded, like the reference). Validated against the
/// reference C++ scores in <c>Ssimulacra2Tests</c>.
/// </summary>
internal static class Ssimulacra2
{
    private const int NumScales = 6;
    private const int MinDimension = 8;
    private const float C2 = 0.0009f;

    // 9-tap kernel derived from libjxl's RecursiveGaussian(sigma = 1.5): centre tap first, symmetric.
    private static readonly float[] Kernel = BuildKernel();
    private const int KernelRadius = 4;

    // Final-score weights: ((channel * 6 + scale) * 2 + norm) * 3 + {ssim, artifact, detailLost}.
    private static readonly double[] Weights =
    {
        0.0, 0.0007376606707406586, 0.0, 0.0, 0.0007793481682867309, 0.0, 0.0, 0.0004371155730107379, 0.0,
        1.1041726426657346, 0.00066284834129271, 0.00015231632783718752, 0.0, 0.0016406437456599754, 0.0,
        1.8422455520539298, 11.441172603757666, 0.0, 0.0007989109436015163, 0.000176816438078653, 0.0,
        1.8787594979546387, 10.94906990605142, 0.0, 0.0007289346991508072, 0.9677937080626833, 0.0,
        0.00014003424285435884, 0.9981766977854967, 0.00031949755934435053, 0.0004550992113792063, 0.0, 0.0,
        0.0013648766163243398, 0.0, 0.0, 0.0, 0.0, 0.0, 7.466890328078848, 0.0, 17.445833984131262,
        0.0006235601634041466, 0.0, 0.0, 6.683678146179332, 0.00037724407979611296, 1.027889937768264,
        225.20515300849274, 0.0, 0.0, 19.213238186143016, 0.0011401524586618361, 0.001237755635509985,
        176.39317598450694, 0.0, 0.0, 24.43300999870476, 0.28520802612117757, 0.0004485436923833408, 0.0, 0.0,
        0.0, 34.77906344483772, 44.835625328877896, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
        0.0008680556573291698, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0005313191874358747, 0.0, 0.00016533814161379112,
        0.0, 0.0, 0.0, 0.0, 0.0, 0.0004179171803251336, 0.0017290828234722833, 0.0, 0.0020827005846636437,
        0.0, 0.0, 8.826982764996862, 23.19243343998926, 0.0, 95.1080498811086, 0.9863978034400682,
        0.9834382792465353, 0.0012286405048278493, 171.2667255897307, 0.9807858872435379, 0.0, 0.0, 0.0,
        0.0005130064588990679, 0.0, 0.00010854057858411537
    };

    private static readonly float[] SrgbToLinear = BuildSrgbLut();

    /// <summary>Smallest edge (px) the metric can score.</summary>
    public static int MinimumEdge => MinDimension;

    /// <summary>
    /// Scores <paramref name="distorted"/> against <paramref name="reference"/>. Both buffers are tightly packed
    /// 8-bit sRGB (<paramref name="channels"/> = 3) or non-premultiplied RGBA (4). Transparent images are scored
    /// against a dark and a light backdrop and the worse result wins, as the reference tool does.
    /// </summary>
    public static double Compute(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> distorted, int width, int height, int channels, CancellationToken cancellationToken = default)
    {
        if (channels is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var expected = (long)width * height * channels;
        if (width < MinDimension || height < MinDimension || reference.Length < expected || distorted.Length < expected)
        {
            throw new ArgumentException("Images must be at least 8x8 and match the declared dimensions.");
        }

        if (channels == 3)
        {
            return ComputeBackdrop(reference, distorted, width, height, 3, 0f, cancellationToken);
        }

        var dark = ComputeBackdrop(reference, distorted, width, height, 4, 0.1f, cancellationToken);
        var light = ComputeBackdrop(reference, distorted, width, height, 4, 0.9f, cancellationToken);
        return Math.Min(dark, light);
    }

    private static double ComputeBackdrop(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> distorted, int width, int height, int channels, float backdrop, CancellationToken token)
    {
        var lin1 = ToLinearPlanes(reference, width, height, channels, backdrop);
        var lin2 = ToLinearPlanes(distorted, width, height, channels, backdrop);

        var scales = new (double[] Ssim, double[] Edge)[NumScales];
        var scaleCount = 0;
        int w = width, h = height, gateW = width, gateH = height;

        for (var scale = 0; scale < NumScales; scale++)
        {
            token.ThrowIfCancellationRequested();
            if (gateW < MinDimension || gateH < MinDimension)
            {
                break;
            }

            var next1 = Downsample(lin1, w, h, out var nextW, out var nextH);
            var next2 = Downsample(lin2, w, h, out _, out _);

            ToXyb(lin1, w * h);
            ToXyb(lin2, w * h);

            scales[scaleCount++] = ComputeScale(lin1, lin2, w, h, token);

            lin1 = next1;
            lin2 = next2;
            gateW = w;
            gateH = h;
            w = nextW;
            h = nextH;
        }

        return FinalScore(scales, scaleCount);
    }

    private static (double[] Ssim, double[] Edge) ComputeScale(float[][] xyb1, float[][] xyb2, int w, int h, CancellationToken token)
    {
        var ssim = new double[6];
        var edge = new double[12];
        var pixels = w * h;
        var perChannel = new (double S0, double S1, double[] Edge)[3];

        // The three XYB channels are independent from here on, which also keeps peak memory at one channel's worth.
        Parallel.For(0, 3, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(3, Environment.ProcessorCount), CancellationToken = token }, c =>
        {
            var a = xyb1[c];
            var b = xyb2[c];
            var tmp = new float[pixels];
            var mu1 = new float[pixels];
            var mu2 = new float[pixels];
            var s11 = new float[pixels];
            var s22 = new float[pixels];
            var s12 = new float[pixels];

            Blur(a, mu1, tmp, w, h);
            Blur(b, mu2, tmp, w, h);
            BlurProduct(a, a, s11, tmp, w, h);
            BlurProduct(b, b, s22, tmp, w, h);
            BlurProduct(a, b, s12, tmp, w, h);

            double sum0 = 0, sum1 = 0;
            var e = new double[4];
            for (var i = 0; i < pixels; i++)
            {
                var m1 = mu1[i];
                var m2 = mu2[i];
                var dm = m1 - m2;
                var numM = 1.0f - dm * dm;
                var numS = 2.0f * (s12[i] - m1 * m2) + C2;
                var denS = (s11[i] - m1 * m1) + (s22[i] - m2 * m2) + C2;
                var d = Math.Max(0.0, 1.0 - (double)(numM * numS / denS));
                sum0 += d;
                sum1 += Pow4(d);

                var num = 1.0 + Math.Abs(b[i] - m2);
                var den = 1.0 + Math.Abs(a[i] - m1);
                var d1 = num / den - 1.0;
                var artifact = Math.Max(d1, 0.0);
                var lost = Math.Max(-d1, 0.0);
                e[0] += artifact;
                e[1] += Pow4(artifact);
                e[2] += lost;
                e[3] += Pow4(lost);
            }

            perChannel[c] = (sum0, sum1, e);
        });

        var inv = 1.0 / pixels;
        for (var c = 0; c < 3; c++)
        {
            var (s0, s1, e) = perChannel[c];
            ssim[c * 2] = inv * s0;
            ssim[c * 2 + 1] = Math.Sqrt(Math.Sqrt(inv * s1));
            edge[c * 4] = inv * e[0];
            edge[c * 4 + 1] = Math.Sqrt(Math.Sqrt(inv * e[1]));
            edge[c * 4 + 2] = inv * e[2];
            edge[c * 4 + 3] = Math.Sqrt(Math.Sqrt(inv * e[3]));
        }

        return (ssim, edge);
    }

    private static double FinalScore((double[] Ssim, double[] Edge)[] scales, int count)
    {
        double ssim = 0;
        var i = 0;
        for (var c = 0; c < 3; c++)
        {
            for (var s = 0; s < count; s++)
            {
                for (var n = 0; n < 2; n++)
                {
                    ssim += Weights[i++] * Math.Abs(scales[s].Ssim[c * 2 + n]);
                    ssim += Weights[i++] * Math.Abs(scales[s].Edge[c * 4 + n]);
                    ssim += Weights[i++] * Math.Abs(scales[s].Edge[c * 4 + n + 2]);
                }
            }

            // Like the reference, a short pyramid keeps consuming weights contiguously (no skipping).
        }

        ssim *= 0.9562382616834844;
        ssim = 2.326765642916932 * ssim - 0.020884521182843837 * ssim * ssim + 6.248496625763138e-05 * ssim * ssim * ssim;
        return ssim > 0 ? 100.0 - 10.0 * Math.Pow(ssim, 0.6276336467831387) : 100.0;
    }

    private static double Pow4(double x)
    {
        x *= x;
        return x * x;
    }

    // ---- colour ------------------------------------------------------------------------------------------

    private static float[][] ToLinearPlanes(ReadOnlySpan<byte> pixels, int width, int height, int channels, float backdrop)
    {
        var count = width * height;
        var planes = new[] { new float[count], new float[count], new float[count] };
        var lut = SrgbToLinear;

        if (channels == 3)
        {
            for (var i = 0; i < count; i++)
            {
                var o = i * 3;
                planes[0][i] = lut[pixels[o]];
                planes[1][i] = lut[pixels[o + 1]];
                planes[2][i] = lut[pixels[o + 2]];
            }

            return planes;
        }

        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            var alpha = pixels[o + 3] / 255f;
            var inverse = (1f - alpha) * backdrop;
            planes[0][i] = EncodedToLinear(alpha * (pixels[o] / 255f) + inverse);
            planes[1][i] = EncodedToLinear(alpha * (pixels[o + 1] / 255f) + inverse);
            planes[2][i] = EncodedToLinear(alpha * (pixels[o + 2] / 255f) + inverse);
        }

        return planes;
    }

    private static float EncodedToLinear(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static float[] BuildSrgbLut()
    {
        var lut = new float[256];
        for (var i = 0; i < 256; i++)
        {
            lut[i] = EncodedToLinear(i / 255f);
        }

        return lut;
    }

    private static void ToXyb(float[][] planes, int count)
    {
        const double m00 = 0.30, m01 = 1.0 - 0.078 - 0.30, m02 = 0.078;
        const double m10 = 0.23, m11 = 1.0 - 0.078 - 0.23, m12 = 0.078;
        const double m20 = 0.24342268924547819, m21 = 0.20476744424496821, m22 = 1.0 - m20 - m21;
        const double bias = 0.0037930732552754493;
        var negCbrtBias = -Math.Cbrt(bias);

        var rP = planes[0];
        var gP = planes[1];
        var bP = planes[2];

        Parallel.For(0, (count + 65535) / 65536, block =>
        {
            var start = block * 65536;
            var end = Math.Min(count, start + 65536);
            for (var i = start; i < end; i++)
            {
                double r = rP[i], g = gP[i], b = bP[i];
                var l = Math.Cbrt(Math.Max(m00 * r + m01 * g + m02 * b + bias, 0.0)) + negCbrtBias;
                var m = Math.Cbrt(Math.Max(m10 * r + m11 * g + m12 * b + bias, 0.0)) + negCbrtBias;
                var s = Math.Cbrt(Math.Max(m20 * r + m21 * g + m22 * b + bias, 0.0)) + negCbrtBias;
                var x = 0.5 * (l - m);
                var y = 0.5 * (l + m);
                rP[i] = (float)(x * 14.0 + 0.42);
                gP[i] = (float)(y + 0.01);
                bP[i] = (float)((s - y) + 0.55);
            }
        });
    }

    private static float[][] Downsample(float[][] planes, int width, int height, out int newWidth, out int newHeight)
    {
        newWidth = (width + 1) / 2;
        newHeight = (height + 1) / 2;
        var nw = newWidth;
        var nh = newHeight;
        var result = new[] { new float[nw * nh], new float[nw * nh], new float[nw * nh] };

        for (var c = 0; c < 3; c++)
        {
            var src = planes[c];
            var dst = result[c];
            Parallel.For(0, nh, y =>
            {
                var y0 = Math.Min(2 * y, height - 1) * width;
                var y1 = Math.Min(2 * y + 1, height - 1) * width;
                var row = y * nw;
                for (var x = 0; x < nw; x++)
                {
                    var x0 = Math.Min(2 * x, width - 1);
                    var x1 = Math.Min(2 * x + 1, width - 1);
                    dst[row + x] = (src[y0 + x0] + src[y0 + x1] + src[y1 + x0] + src[y1 + x1]) * 0.25f;
                }
            });
        }

        return result;
    }

    // ---- blur --------------------------------------------------------------------------------------------

    private static void BlurProduct(float[] a, float[] b, float[] destination, float[] scratch, int w, int h)
    {
        var product = new float[a.Length];
        for (var i = 0; i < product.Length; i++)
        {
            product[i] = a[i] * b[i];
        }

        Blur(product, destination, scratch, w, h);
    }

    private static void Blur(float[] source, float[] destination, float[] scratch, int w, int h)
    {
        var k = Kernel;
        var r = KernelRadius;

        // Horizontal pass (zero padded).
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                float sum = 0;
                var from = Math.Max(0, x - r);
                var to = Math.Min(w - 1, x + r);
                for (var xx = from; xx <= to; xx++)
                {
                    sum += k[xx - x + r] * source[row + xx];
                }

                scratch[row + x] = sum;
            }
        }

        // Vertical pass, row-vectorised.
        var lanes = Vector<float>.Count;
        for (var y = 0; y < h; y++)
        {
            var from = Math.Max(0, y - r);
            var to = Math.Min(h - 1, y + r);
            var outRow = y * w;
            var x = 0;
            for (; x <= w - lanes; x += lanes)
            {
                var acc = Vector<float>.Zero;
                for (var yy = from; yy <= to; yy++)
                {
                    acc += k[yy - y + r] * new Vector<float>(scratch, yy * w + x);
                }

                acc.CopyTo(destination, outRow + x);
            }

            for (; x < w; x++)
            {
                float sum = 0;
                for (var yy = from; yy <= to; yy++)
                {
                    sum += k[yy - y + r] * scratch[yy * w + x];
                }

                destination[outRow + x] = sum;
            }
        }
    }

    /// <summary>Evaluates libjxl's recursive Gaussian (sigma 1.5) on an impulse to recover its FIR kernel.</summary>
    private static float[] BuildKernel()
    {
        const double sigma = 1.5;
        var radius = Math.Round(3.2795 * sigma + 0.2546, MidpointRounding.AwayFromZero);
        var pi2r = Math.PI / (2.0 * radius);
        double[] omega = { pi2r, 3.0 * pi2r, 5.0 * pi2r };

        var p1 = 1.0 / Math.Tan(0.5 * omega[0]);
        var p3 = -1.0 / Math.Tan(0.5 * omega[1]);
        var p5 = 1.0 / Math.Tan(0.5 * omega[2]);
        var r1 = p1 * p1 / Math.Sin(omega[0]);
        var r3 = -p3 * p3 / Math.Sin(omega[1]);
        var r5 = p5 * p5 / Math.Sin(omega[2]);

        var negHalfSigma2 = -0.5 * sigma * sigma;
        var recipRadius = 1.0 / radius;
        var rho = new double[3];
        for (var i = 0; i < 3; i++)
        {
            rho[i] = Math.Exp(negHalfSigma2 * omega[i] * omega[i]) * recipRadius;
        }

        var d13 = p1 * r3 - r1 * p3;
        var d35 = p3 * r5 - r3 * p5;
        var d51 = p5 * r1 - r5 * p1;
        var recipD13 = 1.0 / d13;
        var zeta15 = d35 * recipD13;
        var zeta35 = d51 * recipD13;

        double[] a = { p1, p3, p5, r1, r3, r5, zeta15, zeta35, 1.0 };
        double[] t =
        {
            a[4] * a[8] - a[5] * a[7], a[2] * a[7] - a[1] * a[8], a[1] * a[5] - a[2] * a[4],
            a[5] * a[6] - a[3] * a[8], a[0] * a[8] - a[2] * a[6], a[2] * a[3] - a[0] * a[5],
            a[3] * a[7] - a[4] * a[6], a[1] * a[6] - a[0] * a[7], a[0] * a[4] - a[1] * a[3]
        };
        var det = a[0] * t[0] + a[1] * t[3] + a[2] * t[6];
        var idet = 1.0 / det;
        for (var i = 0; i < 9; i++)
        {
            a[i] = t[i] * idet;
        }

        double[] gamma = { 1.0, radius * radius - sigma * sigma, zeta15 * rho[0] + zeta35 * rho[1] + rho[2] };
        var n2 = new double[3];
        var d1 = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var beta = a[i * 3] * gamma[0] + a[i * 3 + 1] * gamma[1] + a[i * 3 + 2] * gamma[2];
            n2[i] = -beta * Math.Cos(omega[i] * (radius + 1.0));
            d1[i] = -2.0 * Math.Cos(omega[i]);
        }

        const int length = 41;
        var impulse = new double[length];
        impulse[length / 2] = 1.0;
        var response = new double[length];
        var n = (int)radius;
        for (var i = 0; i < 3; i++)
        {
            double y1 = 0, y2 = 0;
            for (var pos = -n + 1; pos < length; pos++)
            {
                var left = pos - n - 1;
                var right = pos + n - 1;
                var sum = (left >= 0 ? impulse[left] : 0.0) + (right < length ? impulse[right] : 0.0);
                var y = n2[i] * sum - d1[i] * y1 - y2;
                y2 = y1;
                y1 = y;
                if (pos >= 0)
                {
                    response[pos] += y;
                }
            }
        }

        // Support is exactly [-(radius-1), radius-1]; keep radius 4 => 9 taps.
        var kernel = new float[2 * KernelRadius + 1];
        var centre = length / 2;
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] = (float)response[centre - KernelRadius + i];
        }

        return kernel;
    }
}
