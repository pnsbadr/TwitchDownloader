namespace TwitchDownloaderWeb.Services.Playback
{
    /// <summary>The AAC format of the first audio frame in an MPEG-TS segment (from its ADTS header).</summary>
    public readonly record struct AdtsFormat(int Profile, int SampleRate, int Channels)
    {
        private const int PacketLength = 188;
        private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

        /// <summary>Reads the first audio PES packet's ADTS header, or null if there is none in <paramref name="data"/>.</summary>
        public static AdtsFormat? Read(ReadOnlySpan<byte> data)
        {
            var start = data.IndexOf((byte)0x47);
            if (start < 0)
                return null;

            for (var offset = start; offset + PacketLength <= data.Length; offset += PacketLength)
            {
                var packet = data.Slice(offset, PacketLength);
                if (packet[0] != 0x47)
                    return null;
                if ((packet[1] & 0x40) == 0)
                    continue; // not the start of a PES packet

                var adaptationControl = (packet[3] >> 4) & 0x3;
                var payloadOffset = 4;
                if (adaptationControl is 2)
                    continue;
                if (adaptationControl is 3)
                    payloadOffset += 1 + packet[4];
                if (payloadOffset + 9 > PacketLength)
                    continue;

                var pes = packet[payloadOffset..];
                if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1 || pes[3] is < 0xC0 or > 0xDF)
                    continue; // not an audio PES packet

                var adtsOffset = 9 + pes[8];
                if (adtsOffset + 4 > pes.Length)
                    return null;
                var adts = pes[adtsOffset..];
                if (adts[0] != 0xFF || (adts[1] & 0xF0) != 0xF0)
                    return null;

                var sampleRateIndex = (adts[2] >> 2) & 0xF;
                return new AdtsFormat(
                    (adts[2] >> 6) + 1,
                    sampleRateIndex < SampleRates.Length ? SampleRates[sampleRateIndex] : 0,
                    ((adts[2] & 0x1) << 2) | (adts[3] >> 6));
            }

            return null;
        }
    }
}
