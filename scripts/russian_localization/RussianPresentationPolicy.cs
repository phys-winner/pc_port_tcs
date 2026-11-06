using System;

namespace ClosingShift.Localization
{
    public static class RussianPresentationPolicy
    {
        public static string TranslateFps(string value)
        {
            const string prefix = "FPS limit: ";
            if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string cap = value.Substring(prefix.Length);
            if (cap == "30" || cap == "45" || cap == "60") return "Лимит FPS: " + cap;
            if (cap.StartsWith("Display (", StringComparison.Ordinal) && cap.EndsWith(")", StringComparison.Ordinal))
            {
                string number = cap.Substring(9, cap.Length - 10);
                int parsed;
                if (Int32.TryParse(number, out parsed) && parsed > 0) return "Лимит FPS: Экран (" + number + ")";
            }
            return null;
        }
    }
}
