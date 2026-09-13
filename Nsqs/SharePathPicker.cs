using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace Nsqs
{
    internal static class SharePathPicker
    {
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        public static string? Browse(Window owner, string? initialPath = null)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select a network share folder",
                Multiselect = false
            };

            var seed = ShareIndexer.NormalizeUncRoot(initialPath ?? string.Empty)
                ?? TryGetUncPath(initialPath ?? string.Empty);
            if (!string.IsNullOrEmpty(seed))
                dialog.InitialDirectory = seed;

            if (dialog.ShowDialog(owner) != true)
                return null;

            return NormalizePickedPath(dialog.FolderName);
        }

        public static string? NormalizePickedPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var unc = TryGetUncPath(path.Trim());
            return ShareIndexer.NormalizeUncRoot(unc ?? string.Empty);
        }

        internal static string? TryGetUncPath(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return path;

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
                return null;

            var drive = root.TrimEnd('\\');
            var sb = new StringBuilder(512);
            var size = sb.Capacity;
            if (WNetGetConnection(drive, sb, ref size) != 0)
                return null;

            var uncRoot = sb.ToString().TrimEnd('\\');
            var relative = path.Substring(root.Length).TrimStart('\\');
            return string.IsNullOrEmpty(relative) ? uncRoot + "\\" : uncRoot + "\\" + relative;
        }
    }
}
