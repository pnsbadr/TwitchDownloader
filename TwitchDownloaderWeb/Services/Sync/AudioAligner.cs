namespace TwitchDownloaderWeb.Services.Sync
{
    /// <summary>
    /// Lines up two streams of the same moment by their sound (badrfly 2026-11-20, "Sync with"). Two streamers in a collab don't share a
    /// microphone, but both streams carry the voice call, so the pattern of who talks, laughs or shouts when is the same: the alignment
    /// works on that loudness pattern (a 100-per-second envelope with its slow level taken out), not on the exact waveform.
    /// </summary>
    public static class AudioAligner
    {
        /// <summary>Envelope frames per second.</summary>
        public const int Rate = 100;

        /// <summary>Below this the match is too weak to trust.</summary>
        public const double MinCorrelation = 0.25;

        /// <summary>The best lag must stand out this much over the best one at least half a second away.</summary>
        public const double MinPeakRatio = 1.15;

        /// <summary>Sound wave levels per second (<see cref="Levels"/>).</summary>
        public const int WaveRate = 20;

        /// <summary>For drawing: the peak of each 1/<see cref="WaveRate"/> s, in dB from −54 (0) to 0 (255), so speech reads at any volume.</summary>
        public static byte[] Levels(ReadOnlySpan<short> samples, int sampleRate)
        {
            var frame = Math.Max(1, sampleRate / WaveRate);
            var count = samples.Length / frame;
            var result = new byte[count];
            for (var i = 0; i < count; i++)
            {
                var peak = 0;
                foreach (var s in samples.Slice(i * frame, frame))
                    peak = Math.Max(peak, Math.Abs((int)s));
                var db = 20 * Math.Log10(Math.Max(peak, 1) / 32768.0);
                result[i] = (byte)Math.Clamp(Math.Round((db + 54) / 54 * 255), 0, 255);
            }
            return result;
        }

        /// <summary>The loudness envelope of mono 16-bit samples: log RMS per 1/<see cref="Rate"/> s, minus a 1 s moving average.</summary>
        public static float[] Envelope(ReadOnlySpan<short> samples, int sampleRate)
        {
            var frame = Math.Max(1, sampleRate / Rate);
            var count = samples.Length / frame;
            var raw = new double[count];
            for (var i = 0; i < count; i++)
            {
                double sum = 0;
                var part = samples.Slice(i * frame, frame);
                foreach (var s in part)
                    sum += (double)s * s;
                raw[i] = Math.Log(1e-5 + Math.Sqrt(sum / frame) / 32768.0);
            }

            // Take the slow level out (different mics and volumes), keep the changes (speech, laughs)
            var result = new float[count];
            const int half = Rate / 2;
            double window = 0;
            int lo = 0, hi = 0;
            for (var i = 0; i < count; i++)
            {
                while (hi < count && hi <= i + half) window += raw[hi++];
                while (lo < i - half) window -= raw[lo++];
                result[i] = (float)(raw[i] - window / (hi - lo));
            }
            return result;
        }

        /// <param name="Lag">Frames into <c>b</c> where <c>a</c> starts.</param>
        /// <param name="Correlation">Pearson correlation at that lag (−1…1).</param>
        /// <param name="PeakRatio">How much it stands out over the best lag at least half a second away.</param>
        public sealed record Match(int Lag, double Correlation, double PeakRatio)
        {
            public bool Trusted => Correlation >= MinCorrelation && PeakRatio >= MinPeakRatio;

            /// <summary>0–1 for showing: how far over the trust thresholds it is.</summary>
            public double Confidence => Math.Clamp(Math.Min(Correlation / 0.6, (PeakRatio - 1) / 0.5), 0, 1);
        }

        /// <summary>
        /// Finds where <paramref name="a"/> sits inside the longer <paramref name="b"/> (lags 0 … b.Length − a.Length). Null when either is
        /// silent or too short.
        /// </summary>
        public static Match? Find(float[] a, float[] b)
        {
            var lags = b.Length - a.Length + 1;
            if (a.Length < Rate * 5 || lags < 1)
                return null;

            var n = a.Length;
            double meanA = 0;
            foreach (var x in a) meanA += x;
            meanA /= n;
            double varA = 0;
            foreach (var x in a) varA += (x - meanA) * (x - meanA);
            if (varA < 1e-6)
                return null;

            // Running sums of b over the window, for each lag's mean and variance
            var prefix = new double[b.Length + 1];
            var prefixSq = new double[b.Length + 1];
            for (var i = 0; i < b.Length; i++)
            {
                prefix[i + 1] = prefix[i] + b[i];
                prefixSq[i + 1] = prefixSq[i] + (double)b[i] * b[i];
            }

            var scores = new double[lags];
            for (var k = 0; k < lags; k++)
            {
                var sumB = prefix[k + n] - prefix[k];
                var varB = prefixSq[k + n] - prefixSq[k] - sumB * sumB / n;
                if (varB < 1e-6)
                {
                    scores[k] = 0;
                    continue;
                }
                double dot = 0;
                for (var i = 0; i < n; i++)
                    dot += (a[i] - meanA) * b[k + i];
                scores[k] = dot / Math.Sqrt(varA * varB);
            }

            var best = 0;
            for (var k = 1; k < lags; k++)
                if (scores[k] > scores[best]) best = k;
            var second = 0.0;
            for (var k = 0; k < lags; k++)
                if (Math.Abs(k - best) >= Rate / 2 && scores[k] > second) second = scores[k];

            var ratio = second <= 0.01 ? 10 : scores[best] / second;
            return new Match(best, scores[best], ratio);
        }
    }
}
