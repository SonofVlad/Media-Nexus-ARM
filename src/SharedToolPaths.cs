using System;
using System.IO;

namespace DiscRipper
{
    internal static class SharedToolPaths
    {
        public static string SharedRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Media Nexus",
                    "Shared");
            }
        }

        public static string FindFfmpeg()
        {
            string[] candidates =
            {
                Path.Combine(SharedRoot, "ffmpeg.exe"),
                Path.Combine(SharedRoot, "bin", "ffmpeg.exe")
            };

            foreach (string candidate in candidates)
                if (File.Exists(candidate)) return candidate;

            return null;
        }
    }
}
