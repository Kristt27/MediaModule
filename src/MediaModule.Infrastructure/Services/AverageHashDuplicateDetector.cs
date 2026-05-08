using System.Runtime.Versioning;
using System.Security.Cryptography;
using MediaModule.Application.Abstractions;

namespace MediaModule.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class AverageHashDuplicateDetector : IDuplicateDetector
{
    public Task<string> ComputePerceptualHashAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(filePath);
            using var resized = new System.Drawing.Bitmap(8, 8);
            using (var graphics = System.Drawing.Graphics.FromImage(resized))
            {
                graphics.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, 8, 8));
            }

            var gray = new byte[64];
            var index = 0;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var pixel = resized.GetPixel(x, y);
                    gray[index++] = (byte)((pixel.R + pixel.G + pixel.B) / 3);
                }
            }

            var avg = gray.Average(x => x);
            ulong bits = 0;
            for (var i = 0; i < gray.Length; i++)
            {
                if (gray[i] >= avg)
                {
                    bits |= 1UL << i;
                }
            }

            return Task.FromResult(bits.ToString("X16"));
        }
        catch
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            return Task.FromResult(Convert.ToHexString(hash));
        }
    }
}
