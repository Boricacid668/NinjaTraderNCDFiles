namespace NinjaTrader
{
    public static class GlobalOptions
    {
        public static string HistoricalDataPath { get; set; } = string.Empty;
    }
}

namespace RespondClient.DomiKnow.NinjaTrader
{
    public static class DateTimeExtensions
    {
        public static bool Between(this DateTime value, DateTime startInclusive, DateTime endInclusive)
        {
            return value >= startInclusive && value <= endInclusive;
        }
    }
}
