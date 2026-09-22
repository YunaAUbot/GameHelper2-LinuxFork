public static class VersionMarker
{
#if SECOND
    public static string Value => "second";
#else
    public static string Value => "first";
#endif
}
