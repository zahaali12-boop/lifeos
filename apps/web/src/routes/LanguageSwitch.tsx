import { Button } from "@quicker/ui";
import { useTranslation } from "react-i18next";
import { currentLanguage, setLanguage } from "../i18n";

/** EN/AR toggle for the anonymous screens (the signed-in shell has the full menu). */
export function LanguageSwitch() {
  const { t } = useTranslation();
  const language = currentLanguage();
  const next = language === "ar" ? "en" : "ar";
  return (
    <Button variant="ghost" size="sm" lang={next} onClick={() => { void setLanguage(next); }} aria-label={t("shell.language")} data-testid="language-toggle">
      {next === "ar" ? "العربية" : "English"}
    </Button>
  );
}
