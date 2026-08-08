using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PubFerry
{
    // The same rules as shared/relative-path.ts, restated here.
    //
    // Not a duplication for its own sake: the page validated a byte string, and
    // this validates what is about to become a file on this machine. The page
    // could be wrong, could be an older build, or could have been replaced —
    // the host is the last thing between a manifest and the disk, so it decides
    // for itself. tests/pc-safe-path.test.ts holds the two implementations to
    // the same answers on the same inputs.
    public static class SafePath
    {
        public const int MaxPathLength = 200;
        public const int MaxSegmentLength = 180;
        public const int MaxDepth = 32;

        private static readonly HashSet<string> Reserved = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM0", "COM1", "COM2", "COM3", "COM4",
            "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4",
            "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static bool IsSafe(string path)
        {
            string reason;
            return Check(path, out reason);
        }

        public static bool Check(string path, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(path))
            {
                reason = "パスが空です。";
                return false;
            }
            if (path.Length > MaxPathLength)
            {
                reason = "パスが長すぎます。";
                return false;
            }
            if (path.IndexOf('\\') >= 0)
            {
                reason = "円記号を含むパスは受け取れません。";
                return false;
            }
            if (path[0] == '/')
            {
                reason = "絶対パスは受け取れません。";
                return false;
            }
            if (path.Length >= 2 && path[1] == ':' &&
                ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')))
            {
                reason = "ドライブ名付きのパスは受け取れません。";
                return false;
            }
            foreach (char c in path)
            {
                if (c < 0x20 || c == 0x7f)
                {
                    reason = "ファイル名に使えない文字が含まれています。";
                    return false;
                }
                if (c == '<' || c == '>' || c == ':' || c == '"' || c == '|' || c == '?' || c == '*')
                {
                    reason = "ファイル名に使えない文字が含まれています。";
                    return false;
                }
                if (IsDeceptive(c))
                {
                    reason = "表示を偽装できる不可視文字が含まれています。";
                    return false;
                }
            }
            // One spelling only. Without this, "á.txt" and "á.txt"
            // are two manifest entries that become one file on a filesystem
            // that normalises, and the second silently replaces the first.
            // An unpaired surrogate is not a character. It gets this far because
            // JavaScript carries one around happily; IsNormalized below THROWS
            // on it, and Linux maps it to U+FFFD on the way to the filesystem,
            // so two names differing only in their lone surrogate become one
            // file and the second write fails after the first has landed.
            // Caught by tests/path-parity.test.ts, which is the point of it.
            for (int i = 0; i < path.Length; i++)
            {
                if (!char.IsSurrogate(path[i]))
                {
                    continue;
                }
                bool paired =
                    char.IsHighSurrogate(path[i]) &&
                    i + 1 < path.Length &&
                    char.IsLowSurrogate(path[i + 1]);
                if (!paired)
                {
                    reason = "ファイル名に不正な文字が含まれています。";
                    return false;
                }
                i++;
            }

            bool normalized;
            try
            {
                normalized = path.IsNormalized(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                // Something IsNormalized will not even look at is something we
                // will not create.
                normalized = false;
            }
            if (!normalized)
            {
                reason = "文字の正規化形が Unicode NFC ではありません。";
                return false;
            }

            string[] segments = path.Split('/');
            if (segments.Length > MaxDepth)
            {
                reason = "フォルダーの階層が深すぎます。";
                return false;
            }
            foreach (string segment in segments)
            {
                if (segment.Length == 0)
                {
                    reason = "区切りが連続しているパスは受け取れません。";
                    return false;
                }
                if (segment.Length > MaxSegmentLength)
                {
                    reason = "名前が長すぎる部分があります。";
                    return false;
                }
                if (segment == "..")
                {
                    reason = "上位フォルダーを指すパスは受け取れません。";
                    return false;
                }
                if (segment == ".")
                {
                    reason = "「.」だけの部分を含むパスは受け取れません。";
                    return false;
                }
                char last = segment[segment.Length - 1];
                if (last == '.' || last == ' ')
                {
                    reason = "末尾がドットまたは空白の名前は受け取れません。";
                    return false;
                }
                if (segment[0] == ' ')
                {
                    reason = "先頭が空白の名前は受け取れません。";
                    return false;
                }
                string bare = segment.Split('.')[0];
                if (Reserved.Contains(bare))
                {
                    reason = "Windows が予約している名前が含まれています。";
                    return false;
                }
                if (bare.Length == 4 &&
                    (bare.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                     bare.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                    (bare[3] == '¹' || bare[3] == '²' || bare[3] == '³'))
                {
                    reason = "Windows が予約している名前が含まれています。";
                    return false;
                }
            }
            return true;
        }

        private static bool IsDeceptive(char c)
        {
            // Zero-width and bidi-override characters. `report.txt‮gpj.exe`
            // renders as `report.txtexe.jpg` in every file manager: the
            // extension somebody sees is not the extension they get.
            if (c >= 0x200b && c <= 0x200f) return true;
            if (c >= 0x202a && c <= 0x202e) return true;
            if (c >= 0x2066 && c <= 0x2069) return true;
            if (c == 0xfeff || c == 0x00ad || c == 0x2028 || c == 0x2029) return true;
            return false;
        }

        /// The key two entries collide on. Invariant lowering, not the current
        /// culture's: Turkish lowercases "I" to a dotless "ı", and a collision
        /// check that changes answer with the user's locale is not a check.
        public static string CollisionKey(string path)
        {
            return path.ToLowerInvariant();
        }

        /// Conflicts a SET of paths can have that no single path can: the same
        /// name twice, and a name used as both a file and a folder. Which of
        /// those wins depends on the order a writer happens to use, so neither
        /// does. Returns null when the set is fine.
        public static string FindSetConflict(IList<string> paths)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> directories = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < paths.Count; i++)
            {
                string key = CollisionKey(paths[i]);
                if (files.Contains(key))
                {
                    return "同じ名前のファイルが 2 つ以上含まれています。";
                }
                if (directories.Contains(key))
                {
                    return "同じ名前がファイルとフォルダーの両方に使われています。";
                }
                string[] segments = key.Split('/');
                StringBuilder prefix = new StringBuilder();
                for (int depth = 0; depth < segments.Length - 1; depth++)
                {
                    if (depth > 0) prefix.Append('/');
                    prefix.Append(segments[depth]);
                    string sofar = prefix.ToString();
                    if (files.Contains(sofar))
                    {
                        return "同じ名前がファイルとフォルダーの両方に使われています。";
                    }
                    directories.Add(sofar);
                }
                files.Add(key);
            }
            return null;
        }
    }
}
