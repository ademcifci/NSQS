using System;
using System.Collections.Generic;

namespace Nsqs
{
    internal static class SharePathHelper
    {
        public static string? FindRootShare(string path, IReadOnlyList<string> roots)
        {
            string? best = null;

            foreach (var root in roots)
            {
                if (!IsUnderRoot(path, root))
                    continue;

                if (best == null || root.Length > best.Length)
                    best = root;
            }

            return best;
        }

        public static bool IsUnderRoot(string path, string normalizedRoot)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(normalizedRoot))
                return false;

            if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            if (path.Length == normalizedRoot.Length)
                return true;

            return normalizedRoot.EndsWith('\\') || path[normalizedRoot.Length] == '\\';
        }

        public static bool IsSafeIndexedPath(FolderEntry entry, IReadOnlyList<string> shareRoots)
        {
            if (IndexStore.NormalizeDirectoryPath(entry.Path) == null)
                return false;

            var root = ShareIndexer.NormalizeUncRoot(entry.RootShare);
            if (root == null)
                return false;

            if (!IsUnderRoot(entry.Path, root))
                return false;

            foreach (var configuredRoot in shareRoots)
            {
                var normalized = ShareIndexer.NormalizeUncRoot(configuredRoot);
                if (normalized == null)
                    continue;

                if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
