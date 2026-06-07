namespace CrossPlatformMicStreamer.Controls;

internal static class LucideGlyphs
{
    private static readonly IReadOnlyDictionary<LucideKind, string[]> Paths =
        new Dictionary<LucideKind, string[]>
        {
            [LucideKind.RefreshCw] =
            [
                "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8",
                "M21 3v5h-5",
                "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16",
                "M8 16H3v5",
            ],
            [LucideKind.Plus] =
            [
                "M5 12h14",
                "M12 5v14",
            ],
            [LucideKind.CircleDot] =
            [
                "M12 12m-1 0a1 1 0 1 0 2 0a1 1 0 1 0 -2 0",
                "M12 12m-9 0a9 9 0 1 0 18 0a9 9 0 1 0 -18 0",
            ],
            [LucideKind.Link] =
            [
                "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71",
                "M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
            ],
            [LucideKind.Mic] =
            [
                "M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z",
                "M19 10v2a7 7 0 0 1-14 0v-2",
                "M12 19v3",
                "M8 22h8",
            ],
            [LucideKind.MicOff] =
            [
                "M9 9v3a3 3 0 0 0 5.12 2.12",
                "M15 9.34V5a3 3 0 0 0-5.94-.6",
                "M17 17a7 7 0 0 1-10-10",
                "M12 19v3",
                "M8 22h8",
                "M2 2l20 20",
            ],
            [LucideKind.Volume2] =
            [
                "M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z",
                "M16 9a5 5 0 0 1 0 6",
                "M19.364 4.636a9 9 0 0 1 0 12.728",
            ],
            [LucideKind.VolumeX] =
            [
                "M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z",
                "M16 9l4 4",
                "M20 9l-4 4",
            ],
            [LucideKind.ArrowRight] =
            [
                "M5 12h14",
                "M12 5l7 7-7 7",
            ],
            [LucideKind.Circle] =
            [
                "M12 12m-10 0a10 10 0 1 0 20 0a10 10 0 1 0 -20 0",
            ],
            [LucideKind.LoaderCircle] =
            [
                "M12 2v4",
                "m16.24 7.76 2.83-2.83",
                "M18 12h4",
                "m-2.83 7.07 2.83 2.83",
                "M12 18v4",
                "m-7.07-2.83-2.83 2.83",
                "M6 12H2",
                "m2.83-7.07-2.83-2.83",
            ],
            [LucideKind.CircleAlert] =
            [
                "M12 12m-10 0a10 10 0 1 0 20 0a10 10 0 1 0 -20 0",
                "M12 8v4",
                "M12 16h.01",
            ],
            [LucideKind.Pencil] =
            [
                "M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z",
                "M15 5l4 4",
            ],
            [LucideKind.Lock] =
            [
                "M7 11V7a5 5 0 0 1 10 0v4",
                "M5 11h14v10H5v-10",
            ],
            [LucideKind.LockOpen] =
            [
                "M7 11V7a5 5 0 0 1 9.9-1",
                "M5 11h14v10H5v-10",
            ],
            [LucideKind.Layers] =
            [
                "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83Z",
                "M22 12a1 1 0 0 0-.58-.91l-8.6-3.91a2 2 0 0 0-1.65 0l-8.58 3.9A1 1 0 0 0 2 12",
                "M22 17a1 1 0 0 0-.58-.91l-8.6-3.91a2 2 0 0 0-1.65 0l-8.58 3.9A1 1 0 0 0 2 17",
            ],
            [LucideKind.Trash2] =
            [
                "M3 6h18",
                "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6",
                "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
                "M10 11v6",
                "M14 11v6",
            ],
            [LucideKind.TrendingUp] =
            [
                "M16 7h6v6",
                "M22 7l-8.5 8.5-5-5L2 17",
            ],
            [LucideKind.TrendingDown] =
            [
                "M16 17h6v-6",
                "M22 17l-8.5-8.5-5 5L2 7",
            ],
        };

    public static IReadOnlyList<string> GetPaths(LucideKind kind) =>
        Paths.TryGetValue(kind, out var paths) ? paths : Paths[LucideKind.Circle];
}
