using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

public static class SubtitleText
{
    /// <summary>Reads a subtitle, refusing anything too big to be one. Closes the stream.</summary>
    public static async Task<byte[]> ReadBytes(Stream stream, CancellationToken token)
    {
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > Srt.MaxBytes)
                {
                    throw new TimingException("That subtitle is too large.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    // Timings read the same in any encoding, as long as UTF-16 is recognised.
    public static string Decode(byte[] b)
        => b.Length > 1 && b[0] == 0xFF && b[1] == 0xFE ? Encoding.Unicode.GetString(b)
            : b.Length > 1 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b)
            : Encoding.UTF8.GetString(b);
}
