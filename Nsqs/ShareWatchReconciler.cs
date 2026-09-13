using System;
using System.Collections.Generic;
using System.IO;

namespace Nsqs
{
    internal static class ShareWatchReconciler
    {
        public static void CollectChanges(
            IEnumerable<FolderEntry> diskEntries,
            IReadOnlyList<string> indexedPaths,
            HashSet<string> seenPaths,
            List<FolderEntry> additions,
            List<string> removals)
        {
            var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in diskEntries)
            {
                diskPaths.Add(entry.Path);
                if (seenPaths.Add(entry.Path))
                    additions.Add(entry);
            }

            foreach (var indexedPath in indexedPaths)
            {
                if (diskPaths.Contains(indexedPath))
                    continue;

                var normalized = IndexStore.NormalizeDirectoryPath(indexedPath);
                if (normalized != null)
                    removals.Add(normalized);
            }
        }

        public static void CollectStalePaths(
            IReadOnlyList<string> indexedPaths,
            List<string> removals)
        {
            foreach (var indexedPath in indexedPaths)
            {
                if (Directory.Exists(indexedPath))
                    continue;

                var normalized = IndexStore.NormalizeDirectoryPath(indexedPath);
                if (normalized != null)
                    removals.Add(normalized);
            }
        }
    }
}
