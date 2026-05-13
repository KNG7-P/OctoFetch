using System;
using System.Text;

namespace OctoFetch.Helpers
{
    
    public static class UrlFileNameInferrer
    {
        public static string TryInferFilename(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            var segments = SplitPathSegments(url);
            if (segments.Length == 0) return string.Empty;

            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var seg = SafeUrlDecode(segments[i]);
                if (LooksLikeFilename(seg)) return seg;
            }

            for (int i = segments.Length - 1; i >= 0; i--)
            {
                if (TryHexDecode(segments[i], out var decoded) &&
                    LooksLikeFilename(decoded))
                {
                    return decoded;
                }
            }

            return string.Empty;
        }

        public static string Infer(string url, string fallback = "download")
        {
            var name = TryInferFilename(url);
            if (!string.IsNullOrEmpty(name)) return name;

            var segments = SplitPathSegments(url);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var seg = SafeUrlDecode(segments[i]);
                if (!string.IsNullOrWhiteSpace(seg)) return seg;
            }

            return fallback;
        }

        private static string[] SplitPathSegments(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return Array.Empty<string>();

            string path;
            try
            {
                var uri = new Uri(url, UriKind.Absolute);
                path = uri.AbsolutePath;
            }
            catch
            {
                var q = url.IndexOf('?');
                path = q >= 0 ? url[..q] : url;
            }
            return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        private static string SafeUrlDecode(string s)
        {
            try { return Uri.UnescapeDataString(s); }
            catch { return s; }
        }

        private static bool LooksLikeFilename(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.Length is < 3 or > 250) return false;

            int lastDot = s.LastIndexOf('.');
            if (lastDot <= 0 || lastDot >= s.Length - 1) return false;

            int extLen = s.Length - lastDot - 1;
            if (extLen is < 1 or > 6) return false;
            for (int i = lastDot + 1; i < s.Length; i++)
            {
                if (!char.IsLetterOrDigit(s[i])) return false;
            }

            bool bodyIsAllDigitsAndDots = true;
            for (int i = 0; i < lastDot; i++)
            {
                char c = s[i];
                if (c != '.' && !char.IsDigit(c)) { bodyIsAllDigitsAndDots = false; break; }
            }
            if (bodyIsAllDigitsAndDots) return false;

            return true;
        }

        private static bool TryHexDecode(string segment, out string decoded)
        {
            decoded = string.Empty;
            if (segment.Length < 16 || (segment.Length & 1) != 0) return false;

            for (int i = 0; i < segment.Length; i++)
            {
                char c = segment[i];
                bool isHex = (c >= '0' && c <= '9') ||
                             (c >= 'a' && c <= 'f') ||
                             (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }

            var bytes = new byte[segment.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexNibble(segment[i * 2]);
                int lo = HexNibble(segment[i * 2 + 1]);
                int b = (hi << 4) | lo;
                if (b < 0x20 || b > 0x7E) return false;
                bytes[i] = (byte)b;
            }

            decoded = Encoding.ASCII.GetString(bytes);
            return true;
        }

        private static int HexNibble(char c) =>
            c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 0,
            };
    }
}
