import i18next from "i18next";
import ICU from "i18next-icu";
import { initReactI18next } from "react-i18next";
import ar from "./ar.json";
import en from "./en.json";

export const languages = ["en", "ar"] as const;
export type Language = (typeof languages)[number];

const STORAGE_KEY = "quicker.language";

function storedLanguage(): Language {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === "ar" || stored === "en") {
      return stored;
    }
  } catch {
    // storage unavailable (private mode): fall through
  }
  return navigator.language.toLowerCase().startsWith("ar") ? "ar" : "en";
}

/** Applies the language to the document: `lang` for fonts and hyphenation, `dir` for the whole layout (ADR-0027). */
export function applyLanguage(language: Language): void {
  document.documentElement.lang = language;
  document.documentElement.dir = language === "ar" ? "rtl" : "ltr";
  try {
    localStorage.setItem(STORAGE_KEY, language);
  } catch {
    // ignore
  }
}

export async function setLanguage(language: Language): Promise<void> {
  await i18next.changeLanguage(language);
  applyLanguage(language);
}

export function currentLanguage(): Language {
  return i18next.language === "ar" ? "ar" : "en";
}

void i18next
  .use(ICU)
  .use(initReactI18next)
  .init({
    resources: { en: { translation: en }, ar: { translation: ar } },
    lng: storedLanguage(),
    fallbackLng: "en",
    interpolation: { escapeValue: false },
    returnNull: false,
  });

applyLanguage(currentLanguage());

export { i18next as i18n };
