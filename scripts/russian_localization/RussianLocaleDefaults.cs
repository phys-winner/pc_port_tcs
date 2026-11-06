using System;
using UnityEngine;

namespace ClosingShift.Localization
{
    // This assembly references Unity and RussianFonts only, never Assembly-CSharp-firstpass.
    // It is called before I2 reads its startup preference and after real user selections.
    public static class RussianLocaleDefaults
    {
        public const string LanguageKey = "I2 Language";
        public const string MigrationKey = "ClosingShift.RussianDefaultVersion";
        public const string ExplicitChoiceKey = "ClosingShift.LanguageExplicit";

        public static void Prepare(bool hasRussian)
        {
            if (Application.platform != RuntimePlatform.Android || !hasRussian) return;
            int version = PlayerPrefs.GetInt(MigrationKey, 0);
            if (version >= RussianDefaultPolicy.Version) return;
            bool migrate = RussianDefaultPolicy.ShouldMigrate(hasRussian,
                Application.systemLanguage == SystemLanguage.Russian, version,
                PlayerPrefs.GetInt(ExplicitChoiceKey, 0) != 0,
                PlayerPrefs.GetString(LanguageKey, String.Empty));
            if (migrate) PlayerPrefs.SetString(LanguageKey, RussianDefaultPolicy.LanguageName);
            PlayerPrefs.SetInt(MigrationKey, RussianDefaultPolicy.Version);
            PlayerPrefs.Save();
            Debug.Log("PORT_RU_DEFAULT version=" + RussianDefaultPolicy.Version + " applied=" + migrate);
        }

        public static void MarkManualChoice()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            PlayerPrefs.SetInt(ExplicitChoiceKey, 1);
            PlayerPrefs.Save();
        }

        public static void AfterLocalize(string languageCode)
        {
            bool russian = languageCode == "ru";
            RussianUiPresentation.SetRussianActive(russian);
            RussianFontCompat.SetRussianActive(russian);
        }

        public static void AfterObjectLocalized(GameObject owner)
        {
            RussianUiPresentation.ApplyTo(owner);
            RussianFontCompat.ApplyTo(owner);
        }

        public static void AfterOverlayCreated(GameObject root)
        {
            RussianUiPresentation.ApplyOverlay(root);
            if (RussianUiPresentation.RussianActive && root != null)
                foreach (UnityEngine.UI.Text text in root.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                    RussianFontCompat.ApplyTo(text);
        }
    }
}
