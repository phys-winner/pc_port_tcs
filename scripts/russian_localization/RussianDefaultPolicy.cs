using System;

namespace ClosingShift.Localization
{
    // Pure decision function: no Unity or preference side effects.
    public static class RussianDefaultPolicy
    {
        public const int Version = 1;
        public const string LanguageName = "Русский (AI)";

        public static bool ShouldMigrate(bool hasRussian, bool osRussian, int appliedVersion,
                                         bool explicitChoice, string savedLanguage)
        {
            return hasRussian && osRussian && appliedVersion < Version && !explicitChoice &&
                   (String.IsNullOrEmpty(savedLanguage) || savedLanguage == "English");
        }
    }
}
