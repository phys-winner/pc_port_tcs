using System;

namespace ClosingShift.Android
{
    public static class PureScalePolicy
    {
        public const int MaximumShortSide = 720;
        public const int MinimumPercent = 25;
        public const int DefaultPercent = 75;

        public static bool TryChoosePercent(int width, int height, int percent, out int selectedWidth, out int selectedHeight)
        {
            selectedWidth = selectedHeight = 0;
            if (width < 2 || height < 2 || percent < MinimumPercent || percent > 100) return false;
            selectedWidth = RoundEvenWithinInput((long)width * percent, 100, width);
            selectedHeight = RoundEvenWithinInput((long)height * percent, 100, height);
            return true;
        }

        public static bool TryChoose(int width, int height, out int selectedWidth, out int selectedHeight)
        { return TryChoose(width, height, MaximumShortSide, out selectedWidth, out selectedHeight); }

        public static bool TryChoose(int width, int height, int maximumShortSide, out int selectedWidth, out int selectedHeight)
        {
            selectedWidth = selectedHeight = 0;
            if (width < 2 || height < 2 || maximumShortSide < 2 || maximumShortSide > MaximumShortSide) return false;
            int shortSide = Math.Min(width, height);
            long numerator = shortSide > maximumShortSide ? maximumShortSide : 1;
            long denominator = shortSide > maximumShortSide ? shortSide : 1;
            selectedWidth = RoundEvenWithinInput((long)width * numerator, denominator, width);
            selectedHeight = RoundEvenWithinInput((long)height * numerator, denominator, height);
            return true;
        }

        static int RoundEvenWithinInput(long numerator, long denominator, int input)
        {
            long nearestEven = ((numerator + denominator) / (2 * denominator)) * 2;
            return (int)Math.Max(2L, Math.Min(nearestEven, (long)(input & ~1)));
        }
    }
}
