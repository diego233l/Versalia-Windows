namespace Versalia.Core;

/// <summary>
/// Reloj interpolado: extrapola la última posición conocida con el reloj monotónico y corrige la deriva con una rampa
/// suave. Diferencias pequeñas se ignoran, las medianas se corrigen a medias y más de 1 s se trata como seek.
/// Tiempos en segundos monotónicos; posiciones en milisegundos.
/// </summary>
public sealed class InterpolatedClock
{
    public enum Outcome { Reset, Seek, Corrected, WithinNoise }

    public const double NoiseFloorMs = 20;
    public const double FullCorrectionMs = 250;
    public const double PartialGain = 0.5;
    public const double SeekThresholdMs = 1000;
    public const double MinCorrectionDuration = 0.4;
    public const double MaxCorrectionRate = 0.5;

    private double _basePositionMs;
    private double _baseTime;
    private bool _isPlaying;
    private double _correctionMs;
    private double _correctionDuration = MinCorrectionDuration;

    public double PositionMs(double now)
    {
        double elapsed = Math.Max(0, now - _baseTime);
        double position = _basePositionMs;
        if (_isPlaying) position += elapsed * 1000;
        if (_correctionMs != 0) position += _correctionMs * Math.Min(1, elapsed / _correctionDuration);
        return Math.Max(0, position);
    }

    public void Reset(double positionMs, double time, bool isPlaying)
    {
        _basePositionMs = Math.Max(0, positionMs);
        _baseTime = time;
        _isPlaying = isPlaying;
        _correctionMs = 0;
    }

    public Outcome Observe(double measuredMs, double time, bool playing, double uncertaintyMs = 0)
    {
        double predicted = PositionMs(time);
        double diff = measuredMs - predicted;
        if (!playing || !_isPlaying)
        {
            Reset(measuredMs, time, playing);
            return Outcome.Reset;
        }
        if (Math.Abs(diff) > SeekThresholdMs)
        {
            Reset(measuredMs, time, playing);
            return Outcome.Seek;
        }
        if (Math.Abs(diff) <= Math.Max(NoiseFloorMs, uncertaintyMs)) return Outcome.WithinNoise;
        double applied = Math.Abs(diff) >= FullCorrectionMs ? diff : diff * PartialGain;
        _basePositionMs = predicted;
        _baseTime = time;
        _correctionMs = applied;
        _correctionDuration = Math.Max(MinCorrectionDuration, Math.Abs(applied) / (1000 * MaxCorrectionRate));
        return Outcome.Corrected;
    }
}
