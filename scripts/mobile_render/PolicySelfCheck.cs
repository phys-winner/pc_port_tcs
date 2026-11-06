using System;
using ClosingShift.Android;

static class PolicySelfCheck
{
    static void Check(int width, int height, int expectedWidth, int expectedHeight)
    {
        int resultWidth, resultHeight;
        if (!PureScalePolicy.TryChoose(width, height, out resultWidth, out resultHeight)
            || resultWidth != expectedWidth || resultHeight != expectedHeight)
            throw new Exception("Unexpected scale for " + width + "x" + height + ": " + resultWidth + "x" + resultHeight);
        if (resultWidth < 2 || resultHeight < 2 || resultWidth > width || resultHeight > height
            || (resultWidth & 1) != 0 || (resultHeight & 1) != 0 || Math.Min(resultWidth, resultHeight) > 720)
            throw new Exception("Scale invariant failed");
    }

    public static void Main()
    {
        Check(2400, 1080, 1600, 720);
        Check(2292, 1080, 1528, 720);
        Check(1280, 720, 1280, 720);
        Check(1080, 2400, 720, 1600);
        Check(640, 360, 640, 360);
        Check(101, 203, 100, 202);
        Check(2, 2, 2, 2);
        Check(3, 3, 2, 2);
        Check(2300, 1080, 1534, 720);
        Check(32000, 18000, 1280, 720);
        Check(int.MaxValue, int.MaxValue, 720, 720);
        Check(int.MaxValue, 2, int.MaxValue - 1, 2);
        int width, height;
        if (PureScalePolicy.TryChoose(0, 1080, out width, out height)
            || PureScalePolicy.TryChoose(1, 1, out width, out height)
            || PureScalePolicy.TryChoose(-1, 720, out width, out height))
            throw new Exception("Unready or invalid surface accepted");
        foreach (int cap in new[] { 360, 540 })
        {
            if (!PureScalePolicy.TryChoose(2400, 1080, cap, out width, out height) || height != cap || width != cap * 20 / 9)
                throw new Exception("Profile scale failed");
            if (!PureScalePolicy.TryChoose(320, 240, cap, out width, out height) || width != 320 || height != 240)
                throw new Exception("Profile upscaled small display");
        }
        if (PureScalePolicy.TryChoose(2400, 1080, 0, out width, out height) || PureScalePolicy.TryChoose(2400, 1080, 1080, out width, out height))
            throw new Exception("Invalid profile cap accepted");
        int percentCases = 0;
        foreach (int percent in new[] { 25, 33, 50, 67, 75, 100 })
        foreach (int[] size in new[] { new[] { 2340, 1080 }, new[] { 1080, 2340 }, new[] { 101, 203 }, new[] { 2, 2 }, new[] { Int32.MaxValue, Int32.MaxValue } })
        {
            if (!PureScalePolicy.TryChoosePercent(size[0], size[1], percent, out width, out height)
                || width < 2 || height < 2 || width > size[0] || height > size[1] || (width & 1) != 0 || (height & 1) != 0)
                throw new Exception("Percentage dimensions or overflow invariant failed");
            if (Math.Abs(width - size[0] * (percent / 100.0)) > 2 || Math.Abs(height - size[1] * (percent / 100.0)) > 2)
                throw new Exception("Percentage unexpectedly changed the requested scale");
            ++percentCases;
        }
        if (!PureScalePolicy.TryChoosePercent(2340, 1080, 50, out width, out height) || width != 1170 || height != 540
            || !PureScalePolicy.TryChoosePercent(2340, 1080, 25, out width, out height) || width != 586 || height != 270
            || !PureScalePolicy.TryChoosePercent(2340, 1080, 100, out width, out height) || width != 2340 || height != 1080
            || PureScalePolicy.TryChoosePercent(2340, 1080, 24, out width, out height)
            || PureScalePolicy.TryChoosePercent(2340, 1080, 101, out width, out height)
            || PureScalePolicy.TryChoosePercent(1, 1, 50, out width, out height))
            throw new Exception("Percentage boundary fixture failed");
        Console.WriteLine("PASS: legacy scale policy plus " + percentCases + " percentage/aspect/overflow fixtures, 25..100 bounds, even dimensions, native100%, invalid surfaces deferred");
    }
}
