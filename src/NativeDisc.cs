using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DiscRipper
{
    internal static class NativeDisc
    {
        private const uint GenericRead = 0x80000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
        private const uint IoctlCdromReadToc = 0x00024000;
        private const uint IoctlStorageGetDeviceNumber = 0x002D1080;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint control, IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static DiscToc TryReadAudioToc(string letter)
        {
            IntPtr handle = Open(letter);
            if (handle == new IntPtr(-1)) return null;
            try
            {
                byte[] buffer = new byte[804]; uint returned;
                if (!DeviceIoControl(handle, IoctlCdromReadToc, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero) || returned < 12) return null;
                int first = buffer[2], last = buffer[3];
                if (first < 1 || last < first || last > 99) return null;
                var toc = new DiscToc { FirstTrack = first, LastTrack = last };
                int count = last - first + 1;
                for (int i = 0; i <= count; i++)
                {
                    int offset = 4 + (i * 8);
                    if (offset + 7 >= returned) return null;
                    int trackNumber = buffer[offset + 2];
                    int frames = (buffer[offset + 5] * 60 * 75) + (buffer[offset + 6] * 75) + buffer[offset + 7];
                    if (trackNumber == 0xAA) toc.LeadoutOffset = frames;
                    else
                    {
                        int control = buffer[offset + 1] & 0x0F;
                        if ((control & 0x04) != 0) return null; // Data track: this is not a standard audio CD.
                        toc.TrackOffsets.Add(frames);
                    }
                }
                if (toc.TrackOffsets.Count != count || toc.LeadoutOffset <= 0) return null;
                toc.DiscId = CalculateMusicBrainzDiscId(toc);
                toc.TocQuery = BuildTocQuery(toc);
                return toc;
            }
            finally { CloseHandle(handle); }
        }

        public static int GetStorageDeviceNumber(string letter)
        {
            IntPtr handle = Open(letter);
            if (handle == new IntPtr(-1)) return -1;
            try
            {
                byte[] buffer = new byte[12]; uint returned;
                if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero) || returned < 12) return -1;
                return BitConverter.ToInt32(buffer, 4);
            }
            finally { CloseHandle(handle); }
        }

        private static IntPtr Open(string letter)
        {
            return CreateFile(@"\\.\" + letter.TrimEnd(':') + ":", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }

        private static string CalculateMusicBrainzDiscId(DiscToc toc)
        {
            var text = new StringBuilder();
            text.Append(toc.FirstTrack.ToString("X2")); text.Append(toc.LastTrack.ToString("X2"));
            text.Append(toc.LeadoutOffset.ToString("X8"));
            for (int i = 0; i < 99; i++) text.Append((i < toc.TrackOffsets.Count ? toc.TrackOffsets[i] : 0).ToString("X8"));
            byte[] digest;
            using (SHA1 sha = SHA1.Create()) digest = sha.ComputeHash(Encoding.ASCII.GetBytes(text.ToString()));
            return Convert.ToBase64String(digest).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }

        private static string BuildTocQuery(DiscToc toc)
        {
            var parts = new List<string> { toc.FirstTrack.ToString(), toc.LastTrack.ToString(), toc.LeadoutOffset.ToString() };
            foreach (int offset in toc.TrackOffsets) parts.Add(offset.ToString());
            return string.Join("+", parts.ToArray());
        }
    }
}
