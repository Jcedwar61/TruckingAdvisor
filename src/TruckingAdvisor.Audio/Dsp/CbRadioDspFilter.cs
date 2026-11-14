namespace TruckingAdvisor.Audio.Dsp;

/// <summary>
/// Colours a voice line the way a CB sounds, without burying it in noise or making it thin.
/// Warm band limit (rumble cut, a small presence lift, gentle top roll-off) -> light saturation -> soft
/// limiter, with hiss that ducks under the voice instead of sitting on top of it, plus key-up clicks.
/// The band is deliberately wider than a telephone band (145 Hz - 4.5 kHz): a 300 Hz - 3.2 kHz slice is
/// intelligible but sounds like a walkie-talkie and turns sibilance into fizz.
/// </summary>
public sealed class CbRadioDspFilter
{
    public bool CleanAudioAccessibilityBypass { get; set; }

    /// <summary>Hiss ceiling (peak, 0..1) reached in silence; the gate brings it down under speech.</summary>
    public float StaticLevel { get; set; } = 0.008f;
    /// <summary>How hard the voice is driven into the soft clipper (1 = clean).</summary>
    public float OverdriveGain { get; set; } = 1.02f;

    private readonly Random _rng = new();
    private Biquad _rumbleA, _rumbleB, _rumbleC, _presence, _topA, _topB, _topC, _hissHigh, _hissLow;
    private float _envelope, _hissZ, _deEss;
    private bool _ready;

    private void Prepare(int sampleRate)
    {
        // Rumble out below 72 Hz only (a deep trucker sits near 95 Hz, so a 145 Hz cut was pulling the bass
        // right out of them and leaving a thin radio). Presence is +2 dB at 1.7 kHz: more makes every S and
        // T sting. The top rolls from 4.5 kHz so codec grit and hiss stop reading as static.
        _rumbleA.SetHighPass(sampleRate, 72f);
        _rumbleB.SetHighPass(sampleRate, 72f);
        _rumbleC.SetHighPass(sampleRate, 72f);
        _presence.SetPeak(sampleRate, 1700f, 2f, 0.9f);
        _topA.SetLowPass(sampleRate, 4500f);
        _topB.SetLowPass(sampleRate, 4500f);
        _topC.SetLowPass(sampleRate, 5200f);
        _hissHigh.SetHighPass(sampleRate, 300f);
        _hissLow.SetLowPass(sampleRate, 2200f);
        _envelope = 0;
        _hissZ = 0;
        _deEss = 0;
        _ready = true;
    }
    /// <summary>
    /// Processes normalized mono PCM in place. <paramref name="sampleRate"/> may be anything; the filters
    /// are designed for it, so 22.05 kHz Windows voices and 44.1 kHz model output both keep their band shape.
    /// </summary>
    public void ProcessBuffer(Span<float> samples, int sampleRate = 24000)
    {
        if (CleanAudioAccessibilityBypass) return;
        if (sampleRate < 8000) return;
        if (!_ready) Prepare(sampleRate);

        // Envelope follower: fast attack (3 ms), slow release (120 ms) drives the noise gate.
        float attack = 1f - MathF.Exp(-1f / (0.003f * sampleRate));
        float release = 1f - MathF.Exp(-1f / (0.120f * sampleRate));
        // De-esser: a short high-band envelope on the difference between the signal and its smoothed self.
        float smooth = 1f - MathF.Exp(-1f / (0.0002f * sampleRate));

        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            float level = MathF.Abs(x);
            _envelope += (level - _envelope) * (level > _envelope ? attack : release);

            // Hiss: loud in the gaps, all but gone under a voice, so consonants stay clean and it reads as a
            // quiet carrier instead of a wash of static over every word.
            float open = 1f - Math.Clamp(_envelope * 22f, 0f, 1f);
            float gate = 0.08f + 0.92f * open;
            float white = (float)_rng.NextDouble() * 2f - 1f;
            _hissZ = 0.6f * _hissZ + 0.4f * white; // pink-ish, softer than white
            float hiss = _hissZ * StaticLevel * gate;
            hiss = _hissLow.Process(_hissHigh.Process(hiss));

            float shaped = _topC.Process(_topB.Process(_topA.Process(_presence.Process(_rumbleC.Process(_rumbleB.Process(_rumbleA.Process(x + hiss)))))));

            // Tame only the sibilant band: when the high band jumps above the smoothed signal, pull it back by
            // up to 8 dB. Sampled voices already hiss up here and the saturation below used to turn it to grit.
            float high = shaped - _deEss;
            _deEss += high * smooth;
            float band = MathF.Abs(high) - MathF.Abs(_deEss) * 0.6f;
            if (band > 0.02f) shaped -= high * Math.Clamp(band * 2.2f, 0f, 0.61f);

            // Gentle saturation, then a soft knee limiter instead of a hard clamp.
            float driven = OverdriveGain <= 1.001f ? shaped : MathF.Tanh(shaped * OverdriveGain) / MathF.Tanh(OverdriveGain);
            samples[i] = SoftLimit(driven);
        }
    }

    private static float SoftLimit(float v)
    {
        const float knee = 0.84f;
        float sign = v < 0 ? -1f : 1f;
        float a = MathF.Abs(v);
        if (a <= knee) return v;
        return sign * (knee + (1f - knee) * MathF.Tanh((a - knee) / (1f - knee)));
    }

    /// <summary>Short key-up tick at the start of a transmission (12 ms, fades in so it doesn't pop).</summary>
    public float[] GenerateKeyUpClick(int sampleRate = 24000)
    {
        int total = Math.Max(1, (int)(sampleRate * 0.012));
        var buf = new float[total];
        for (int i = 0; i < total; i++)
        {
            float t = (float)i / total;
            float attack = MathF.Min(1f, i / (sampleRate * 0.0012f));
            float env = attack * MathF.Exp(-5f * t) * (1f - t);
            float click = MathF.Sin(2f * MathF.PI * 1500f * i / sampleRate);
            buf[i] = click * env * 0.1f;
        }
        return buf;
    }

    /// <summary>Squelch tail at the end: a brief noise puff that decays away, like a carrier dropping.</summary>
    public float[] GenerateSquelchTail(int sampleRate = 24000)
    {
        int total = Math.Max(1, (int)(sampleRate * 0.026));
        var buf = new float[total];
        float low = 0;
        for (int i = 0; i < total; i++)
        {
            float t = (float)i / total;
            float env = MathF.Exp(-6f * t) * (1f - t);
            low = 0.6f * low + 0.4f * ((float)_rng.NextDouble() * 2f - 1f);
            buf[i] = low * env * 0.09f;
        }
        return buf;
    }

    /// <summary>Key-up and squelch together, for callers that use one burst at both ends.</summary>
    public float[] GenerateMicClickAndSquelchTail(int sampleRate = 24000) =>
        [.. GenerateKeyUpClick(sampleRate), .. GenerateSquelchTail(sampleRate)];

    /// <summary>Direct-form biquad (RBJ cookbook coefficients).</summary>
    private struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public void SetHighPass(int rate, float cutoff)
        {
            var (_, cos, _, alpha) = Design(rate, cutoff);
            float a0 = 1f + alpha;
            _b0 = (1f + cos) / 2f / a0; _b1 = -(1f + cos) / a0; _b2 = _b0;
            _a1 = -2f * cos / a0; _a2 = (1f - alpha) / a0;
        }

        public void SetLowPass(int rate, float cutoff)
        {
            var (_, cos, _, alpha) = Design(rate, cutoff);
            float a0 = 1f + alpha;
            _b0 = (1f - cos) / 2f / a0; _b1 = (1f - cos) / a0; _b2 = _b0;
            _a1 = -2f * cos / a0; _a2 = (1f - alpha) / a0;
        }

        /// <summary>Boost (or cut) a band by <paramref name="gainDb"/> with the given Q.</summary>
        public void SetPeak(int rate, float center, float gainDb, float q)
        {
            var (_, cos, _, alpha) = Design(rate, center);
            alpha /= MathF.Max(0.1f, q) * 1.414214f;
            float a = MathF.Pow(10f, gainDb / 40f);
            float a0 = 1f + alpha / a;
            _b0 = (1f + alpha * a) / a0;
            _b1 = -2f * cos / a0;
            _b2 = (1f - alpha * a) / a0;
            _a1 = -2f * cos / a0;
            _a2 = (1f - alpha / a) / a0;
        }

        private static (float W0, float Cos, float Sin, float Alpha) Design(int rate, float cutoff)
        {
            float w0 = 2f * MathF.PI * Math.Clamp(cutoff, 20f, rate * 0.45f) / rate;
            return (w0, MathF.Cos(w0), MathF.Sin(w0), MathF.Sin(w0) / (2f * 0.707f));
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
