namespace TruckingAdvisor.Audio.Dsp;

/// <summary>
/// Authentic CB Radio DSP filter chain:
/// OpenAI PCM Voice -> Bandpass (300Hz - 3.5kHz) -> Soft-Clipping Overdrive -> Static Hiss -> Squelch Tail Click.
/// Includes a Clean-Audio accessibility bypass option.
/// </summary>
public sealed class CbRadioDspFilter
{
    public bool CleanAudioAccessibilityBypass { get; set; }
    public float StaticLevel { get; set; } = 0.035f;
    public float OverdriveGain { get; set; } = 1.65f;
    private readonly Random _rng = new();

    private float _hpPrevIn;
    private float _hpPrevOut;
    private float _lpPrevOut;

    /// <summary>
    /// Processes normalized 32-bit float mono PCM samples in-place at the given sample rate (default 24,000 Hz).
    /// </summary>
    public void ProcessBuffer(Span<float> samples, int sampleRate = 24000)
    {
        if (CleanAudioAccessibilityBypass) return;

        // 1st-order High-Pass at 300 Hz and Low-Pass at 3400 Hz
        float rcHp = 1.0f / (2.0f * MathF.PI * 300.0f);
        float dt = 1.0f / sampleRate;
        float alphaHp = rcHp / (rcHp + dt);

        float rcLp = 1.0f / (2.0f * MathF.PI * 3400.0f);
        float alphaLp = dt / (rcLp + dt);

        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];

            // High-pass filter (cuts sub-300Hz bass rumble)
            float hp = alphaHp * (_hpPrevOut + x - _hpPrevIn);
            _hpPrevIn = x;
            _hpPrevOut = hp;

            // Low-pass filter (cuts >3.4kHz high frequencies)
            float lp = _lpPrevOut + alphaLp * (hp - _lpPrevOut);
            _lpPrevOut = lp;

            // Soft-clipping analog overdrive (tanh saturation)
            float driven = MathF.Tanh(lp * OverdriveGain);

            // Add subtle background carrier static
            float whiteNoise = ((float)_rng.NextDouble() * 2.0f - 1.0f) * StaticLevel;

            samples[i] = Math.Clamp(driven * 0.85f + whiteNoise, -0.98f, 0.98f);
        }
    }

    /// <summary>
    /// Generates a 90ms CB squelch tail + mic click burst to append at the start/end of transmissions.
    /// </summary>
    public float[] GenerateMicClickAndSquelchTail(int sampleRate = 24000)
    {
        int totalSamples = (int)(sampleRate * 0.09);
        var buf = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            float env = 1.0f - ((float)i / totalSamples);
            float clickTone = MathF.Sin(2.0f * MathF.PI * 1850.0f * i / sampleRate) * 0.25f;
            float burst = ((float)_rng.NextDouble() * 2.0f - 1.0f) * 0.22f;
            buf[i] = (clickTone + burst) * env;
        }
        return buf;
    }
}
