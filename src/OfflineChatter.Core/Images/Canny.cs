using System;
using System.Collections.Generic;

namespace OfflineChatter.Core.Images;

/// <summary>
/// Контуры по Canny: серое → размытие Гаусса 5×5 → Собель → подавление немаксимумов → два порога с гистерезисом.
/// sd-server убрал встроенную предобработку (image_preprocess, canny=true), поэтому контуры считает программа.
/// Результат — белые линии на чёрном, как ждут img2img и ControlNet.
/// </summary>
public static class Canny
{
    /// <summary>
    /// Контуры из пикселей BGRA/RGBA (4 байта на пиксель, без выравнивания строк). Прозрачные пиксели считаются чёрными.
    /// Пороги — по модулю градиента Собеля (0…~1440), как в OpenCV при L2-норме.
    /// </summary>
    public static byte[] Detect(byte[] pixels, int width, int height, double low = 60, double high = 140)
    {
        if (width <= 0 || height <= 0) return Array.Empty<byte>();
        if (pixels.Length < width * height * 4) throw new ArgumentException("pixels too short", nameof(pixels));
        var gray = new float[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var o = i * 4;
            var a = pixels[o + 3] / 255f;
            // одинаковые веса для B и R — порядок каналов не важен с точностью, нужной контурам
            gray[i] = (0.114f * pixels[o] + 0.587f * pixels[o + 1] + 0.299f * pixels[o + 2]) * a;
        }
        return DetectGray(gray, width, height, low, high);
    }

    public static byte[] DetectGray(float[] gray, int width, int height, double low = 60, double high = 140)
    {
        var blurred = Blur(gray, width, height);
        var mag = new float[width * height];
        var dir = new byte[width * height];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                float P(int dx, int dy) => blurred[(y + dy) * width + x + dx];
                var gx = -P(-1, -1) - 2 * P(-1, 0) - P(-1, 1) + P(1, -1) + 2 * P(1, 0) + P(1, 1);
                var gy = -P(-1, -1) - 2 * P(0, -1) - P(1, -1) + P(-1, 1) + 2 * P(0, 1) + P(1, 1);
                var i = y * width + x;
                mag[i] = (float)Math.Sqrt(gx * gx + gy * gy);
                var angle = Math.Atan2(gy, gx) * 180 / Math.PI;
                if (angle < 0) angle += 180;
                dir[i] = angle < 22.5 || angle >= 157.5 ? (byte)0 : angle < 67.5 ? (byte)1 : angle < 112.5 ? (byte)2 : (byte)3;
            }
        }

        // Подавление немаксимумов: оставляем пиксель, только если он сильнее соседей поперёк контура
        var thin = new float[width * height];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                var m = mag[i];
                if (m <= 0) continue;
                float a, b;
                switch (dir[i])
                {
                    case 0: a = mag[i - 1]; b = mag[i + 1]; break;
                    case 1: a = mag[i - width + 1]; b = mag[i + width - 1]; break;
                    case 2: a = mag[i - width]; b = mag[i + width]; break;
                    default: a = mag[i - width - 1]; b = mag[i + width + 1]; break;
                }
                if (m >= a && m >= b) thin[i] = m;
            }
        }

        // Гистерезис: сильные пиксели — контур; слабые — только если связаны с сильным
        var result = new byte[width * height];
        var stack = new Stack<int>();
        for (var i = 0; i < thin.Length; i++)
        {
            if (thin[i] < high || result[i] != 0) continue;
            result[i] = 255;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                var px = p % width;
                var py = p / width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = px + dx;
                        var ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        var n = ny * width + nx;
                        if (result[n] == 0 && thin[n] >= low)
                        {
                            result[n] = 255;
                            stack.Push(n);
                        }
                    }
                }
            }
        }
        return result;
    }

    private static readonly float[] Kernel = { 1 / 16f, 4 / 16f, 6 / 16f, 4 / 16f, 1 / 16f };

    private static float[] Blur(float[] src, int w, int h)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float s = 0;
                for (var k = -2; k <= 2; k++) s += src[y * w + Clamp(x + k, w)] * Kernel[k + 2];
                tmp[y * w + x] = s;
            }
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float s = 0;
                for (var k = -2; k <= 2; k++) s += tmp[Clamp(y + k, h) * w + x] * Kernel[k + 2];
                dst[y * w + x] = s;
            }
        }
        return dst;
    }

    private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;
}
