using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Extensions;
using Nmkoder.IO;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// The fonts a burnt-in text subtitle track is rendered with.
    /// <para/>
    /// Nothing here takes styling away and nothing has to put it back: the "subtitles" filter opens the
    /// source file itself and hands the track to libass exactly as it sits there, so the colours, the
    /// borders, the positions, the overrides and the font *names* survive whatever the output container
    /// can or cannot store. The one part that is not carried in the track is the font *files*, and
    /// ffmpeg reads those out of the same file's attachments - which is why "N attachment tracks left
    /// out: MP4 cannot store attachment streams" is a statement about the output and not about the
    /// burn-in.
    /// <para/>
    /// It finds them by mimetype and by nothing else. <see cref="FfmpegFontMimeTypes"/> is the whole of
    /// what vf_subtitles.c will accept; there is no fallback on the file extension, and an attachment
    /// carrying no "filename" tag is skipped as well ("Font attachment has no filename, ignored"). A
    /// font tagged "application/octet-stream" - which some muxers write and which ffprobe then reports
    /// as codec "unknown" rather than "ttf" - is therefore invisible to the renderer, and libass
    /// substitutes whatever the system offers. That is the one way a burn-in loses styling here, and it
    /// looks exactly like the styling having been stripped: the text lands in the right place, in the
    /// right colour, in the wrong typeface.
    /// <para/>
    /// It is a fixable one. libass scans a directory of its own when the filter is given "fontsdir", so
    /// dumping the attachments to a folder gets them in through the door the mimetype closed. Measured:
    /// with the font uninstalled and the attachment tagged "application/octet-stream", the burnt-in
    /// frame is byte-identical with "fontsdir" to the one rendered with the font installed, and differs
    /// from the fallback render without it.
    /// </summary>
    class SubtitleFonts
    {
        /// <summary> ffmpeg's own list, from font_mimetypes[] in libavfilter/vf_subtitles.c - unchanged
        /// between 6.1 and current master. An attachment tagged with anything else is not a font as far
        /// as the burn-in is concerned, however plainly it is one. </summary>
        private static readonly string[] FfmpegFontMimeTypes =
        {
            "font/ttf", "font/otf", "font/sfnt", "font/woff", "font/woff2", "application/font-sfnt",
            "application/font-woff", "application/x-truetype-font", "application/vnd.ms-opentype",
            "application/x-font-ttf",
        };

        /// <summary> What a font attachment is called when its mimetype cannot be trusted to say so.
        /// Only the formats FreeType reads and a muxer actually carries - this is the question "would
        /// libass use this file", not "is this a font file". </summary>
        private static readonly string[] FontExtensions = { ".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2" };

        /// <summary> Whether this attachment is meant to be a font, by either of the two things that can
        /// say so. The extension is the half that matters: the mimetype being wrong is the whole problem
        /// being detected, so an attachment cannot be required to have a right one to count. </summary>
        public static bool IsFont(AttachmentStream a)
        {
            if (a == null)
                return false;

            if (HasKnownMimeType(a))
                return true;

            return FontExtensions.Contains(Path.GetExtension(Tag(a.Filename)).ToLower());
        }

        /// <summary> Whether the "subtitles" filter will hand this attachment to libass. Both halves are
        /// ffmpeg's: the mimetype has to be one it knows, and the filename tag has to be there for it to
        /// name the font by. </summary>
        public static bool ReachesRenderer(AttachmentStream a)
        {
            return IsFont(a) && HasKnownMimeType(a) && Tag(a.Filename).IsNotEmpty();
        }

        private static bool HasKnownMimeType(AttachmentStream a)
        {
            return FfmpegFontMimeTypes.Contains(Tag(a.MimeType).ToLower());
        }

        /// <summary> A container tag as ffprobe left it. Absent reads as "" rather than throwing: these
        /// are optional tags on an optional stream kind, and a file missing one is the case being asked
        /// about rather than an error. </summary>
        private static string Tag(string value)
        {
            return (value ?? "").Trim();
        }

        /// <summary> The file's font attachments the burn-in will never see, which is the set that has to
        /// be rescued. Empty for the ordinary case - mkvmerge writes mimetypes ffmpeg knows - and an
        /// empty answer is what keeps this costing nothing on a file that does not need it. </summary>
        public static List<AttachmentStream> GetSkippedFonts(MediaFile file)
        {
            if (file == null)
                return new List<AttachmentStream>();

            return file.AttachmentStreams.Where(a => IsFont(a) && !ReachesRenderer(a)).ToList();
        }

        /// <summary>
        /// Dumps <paramref name="file"/>'s attachments into this session's scratch folder and hands back
        /// the folder for "fontsdir", or "" when there is nothing to do or nothing came out. Only called
        /// where <see cref="GetSkippedFonts"/> found something, so a file whose fonts ffmpeg can already
        /// read costs no extra ffmpeg run at all.
        /// <para/>
        /// Every attachment, not only the skipped ones: it is one ffmpeg call either way, and the ones
        /// that were already reaching libass are handed to it twice rather than not at all. A font with
        /// no "filename" tag cannot be rescued this way - that tag is what "-dump_attachment" names the
        /// output after - so it is counted and mentioned rather than silently expected to work.
        /// </summary>
        public static async Task<string> ExtractFontsAsync(MediaFile file, int index)
        {
            string dir = Path.Combine(Paths.GetSessionDataPath(), $"burnInFonts{index}");

            try
            {
                IoUtils.TryDeleteIfExists(dir); // A batch reuses the session folder, so last file's fonts would still be in it
                Directory.CreateDirectory(dir);
            }
            catch (System.Exception e)
            {
                Logger.Log($"Could not create a folder for the burn-in's fonts: {e.Message}", true);
                return "";
            }

            await FfmpegExtract.ExtractAttachments(file.ImportPath, dir);

            if (IoUtils.GetFileInfosSorted(dir, false, "*").Length > 0)
                return dir;

            IoUtils.TryDeleteIfExists(dir); // An empty fontsdir is one more thing for the filter to be wrong about
            return "";
        }
    }
}
