using TwitchDownloaderWeb.Services.AudioRescue;

namespace TwitchDownloaderWeb.Tests
{
    public class TsTimestampsTests
    {
        [Fact]
        public void DiffHandlesWrap()
        {
            const long wrap = 1L << 33;
            Assert.Equal(90_000, TsTimestamps.Diff(45_000, wrap - 45_000));
            Assert.Equal(-90_000, TsTimestamps.Diff(wrap - 45_000, 45_000));
            Assert.Equal(1, TsTimestamps.Diff(2, 1));
        }

        [Fact]
        public void ReadsPesTimestamps()
        {
            // One video PES (PTS 900000 = 10 s) and one audio PES (PTS 901800 = 10.02 s)
            var data = new byte[188 * 3];
            WritePes(data.AsSpan(0, 188), 0x100, 0xE0, 900_000);
            WritePat(data.AsSpan(188, 188));
            WritePes(data.AsSpan(376, 188), 0x101, 0xC0, 901_800);

            var ts = TsTimestamps.Read(data);

            Assert.Equal(900_000, ts.MinVideoPts);
            Assert.Equal(901_800, ts.FirstAudioPts);
            Assert.Equal(900_000, ts.StartPts);
        }

        private static void WritePat(Span<byte> packet)
        {
            packet.Fill(0xFF);
            packet[0] = 0x47; packet[1] = 0x40; packet[2] = 0x00; packet[3] = 0x10;
            packet[4] = 0x00; // pointer field, then a table: not a PES start code
            packet[5] = 0x00; packet[6] = 0xB0;
        }

        private static void WritePes(Span<byte> packet, int pid, byte streamId, long pts)
        {
            packet.Fill(0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)(0x40 | (pid >> 8));
            packet[2] = (byte)pid;
            packet[3] = 0x10; // payload only
            var pes = packet[4..];
            pes[0] = 0; pes[1] = 0; pes[2] = 1; pes[3] = streamId;
            pes[4] = 0; pes[5] = 0; pes[6] = 0x80; pes[7] = 0x80; pes[8] = 5;
            pes[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
            pes[10] = (byte)(pts >> 22);
            pes[11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
            pes[12] = (byte)(pts >> 7);
            pes[13] = (byte)(0x01 | ((pts << 1) & 0xFE));
        }
    }
}
