namespace Versalia.Core;

/// <summary>Ajuste de sincronía «al oído»: el usuario pulsa cuando oye empezar una línea concreta.</summary>
public static class SyncCalibration
{
    /// <summary>Lo que se tarda de media en pulsar después de oír la línea (se descuenta).</summary>
    public const int ReactionMs = 100;
    /// <summary>La línea objetivo empieza al menos esto por delante de lo que se ve, para que aún no se haya cantado
    /// aunque la letra vaya varios segundos por detrás.</summary>
    public const int LeadMs = 4000;
    public const int MaxCorrectionMs = 10000;

    /// <summary>Índice de la primera línea que empieza al menos <paramref name="leadMs"/> después de la posición, o -1.</summary>
    public static int TargetLine(Lyrics lyrics, double positionMs, int leadMs = LeadMs)
    {
        for (int i = 0; i < lyrics.Lines.Count; i++)
            if (lyrics.Lines[i].StartMs >= positionMs + leadMs) return i;
        return -1;
    }

    /// <summary>Cuánto hay que sumar a la posición mostrada para que, al pulsar, la línea esté empezando.</summary>
    public static int TapCorrection(int lineStartMs, double displayedMs, int reactionMs = ReactionMs) =>
        (int)Math.Round(lineStartMs + reactionMs - displayedMs);

    /// <summary>Mediana de los ajustes guardados (null con menos de dos): sirve de punto de partida para canciones nuevas.</summary>
    public static int? Learned(IEnumerable<int> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count < 2) return null;
        int mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
    }
}
