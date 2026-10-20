namespace TwitchDownloaderWeb.Services.AudioRescue
{
    /// <summary>Presentation timestamps found at the start of an MPEG-TS segment, in 90 kHz ticks.</summary>
    /// <param name="FirstAudioPts">PTS of the first audio PES packet.</param>
    /// <param name="MinVideoPts">Lowest video PTS seen in the bytes read (B-frames can make the first one larger).</param>
    public readonly record struct TsTimestamps(long? FirstAudioPts, long? MinVideoPts)
    {
        public const double TicksPerSecond = 90_000;

        private const int PacketLength = 188;
        private const long PtsWrap = 1L << 33;

        /// <summary>
        /// The segment's start as ffmpeg sees it (the lowest first timestamp of its streams). Core concatenates segments
        /// relative to this, so it is where the segment's first audio lands in a downloaded file.
        /// </summary>
        public long? StartPts => (FirstAudioPts, MinVideoPts) switch
        {
            ({ } a, { } v) => Diff(a, v) < 0 ? a : v,
            ({ } a, null) => a,
            (null, { } v) => v,
            _ => null
        };

        /// <summary>Reads PTS values from the start of a transport stream. Only PES headers are parsed, so a partial segment works.</summary>
        public static TsTimestamps Read(ReadOnlySpan<byte> data)
        {
            long? audio = null;
            long? video = null;

            // Find the first sync byte; segments start on a packet boundary, but be tolerant
            var start = data.IndexOf((byte)0x47);
            if (start < 0)
                return default;

            for (var offset = start; offset + PacketLength <= data.Length; offset += PacketLength)
            {
                var packet = data.Slice(offset, PacketLength);
                if (packet[0] != 0x47)
                    break;

                var payloadUnitStart = (packet[1] & 0x40) != 0;
                if (!payloadUnitStart)
                    continue;

                var adaptationControl = (packet[3] >> 4) & 0x3;
                var payloadOffset = 4;
                if (adaptationControl is 2)
                    continue; // adaptation field only, no payload
                if (adaptationControl is 3)
                    payloadOffset += 1 + packet[4];

                if (payloadOffset + 14 > PacketLength)
                    continue;

                var pes = packet[payloadOffset..];
                if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1)
                    continue; // PSI table (PAT/PMT), not a PES packet

                var streamId = pes[3];
                var hasPts = (pes[7] & 0x80) != 0;
                if (!hasPts)
                    continue;

                var pts = ReadPts(pes.Slice(9, 5));

                if (streamId is >= 0xC0 and <= 0xDF)
                {
                    audio ??= pts;
                }
                else if (streamId is >= 0xE0 and <= 0xEF)
                {
                    if (video is not { } current || Diff(pts, current) < 0)
                        video = pts;
                }
            }

            return new TsTimestamps(audio, video);
        }

        /// <summary><paramref name="a"/> − <paramref name="b"/> in ticks, allowing for the 33-bit PTS counter wrapping (every ~26.5 hours).</summary>
        public static long Diff(long a, long b)
        {
            var diff = (a - b) % PtsWrap;
            if (diff > PtsWrap / 2) diff -= PtsWrap;
            if (diff < -PtsWrap / 2) diff += PtsWrap;
            return diff;
        }

        public static double DiffSeconds(long a, long b) => Diff(a, b) / TicksPerSecond;

        private static long ReadPts(ReadOnlySpan<byte> b) =>
            ((long)(b[0] & 0x0E) << 29)
            | ((long)b[1] << 22)
            | ((long)(b[2] & 0xFE) << 14)
            | ((long)b[3] << 7)
            | ((long)b[4] >> 1);
    }
}
